using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Playback
{
    /// <summary>
    /// The precomputed timeline-time ↔ source-time mapping <see cref="CompositionPlayer"/> plays a
    /// <see cref="Project"/>'s <b>video</b> streams through. Immutable — rebuilt on every
    /// <c>UpdateProject</c> and swapped atomically, so decode/present threads read it lock-free.
    /// Audio has no mapping here: it is mixed in timeline time by <see cref="AudioMixWorker"/>
    /// (every unmuted stream audible, gaps silenced, per-sample gain — the render mixer's own
    /// semantics), so audio tracks contribute only to <see cref="DurationTicks"/>.
    ///
    /// Per referenced video stream it holds the item spans as <see cref="Segment"/>s
    /// (timeline-ordered), giving the same timeline→source mapping <c>FrameComposer</c> uses
    /// (<c>src = SourceIn + (t - TimelineStart)</c>), plus the inverse used to pace source-stamped
    /// frames against the timeline clock, and the stream's interior source cuts as a
    /// <see cref="SkipRangeSchedule"/> (the source spans no item covers between consecutive
    /// segments — a cut made in the editor).
    ///
    /// Assumptions (documented, not enforced) about video streams: items of one stream appear in
    /// source order (trim/cut/split never reorder), and their timelines are gapless. The mapping
    /// degrades gracefully otherwise — a source instant inside a cut maps to the seam, a timeline
    /// instant in a gap maps to the next segment's in-point.
    /// </summary>
    internal sealed class ProjectTimelineMap
    {
        /// <summary>One item's span: timeline [TlStart, TlEnd) filled from source [SrcIn, SrcEnd),
        /// consumed at <see cref="Speed"/> source ticks per timeline tick (1 = realtime).</summary>
        internal readonly struct Segment
        {
            public Segment(long tlStart, long tlEnd, long srcIn, double speed = 1.0)
            {
                TlStart = tlStart;
                TlEnd = tlEnd;
                SrcIn = srcIn;
                Speed = speed > 0 ? speed : 1.0;
            }

            public long TlStart { get; }
            public long TlEnd { get; }
            public long SrcIn { get; }
            public double Speed { get; }

            public long SrcEnd => SrcIn + ToSource(TlEnd - TlStart);

            /// <summary>Source minus timeline at the segment's start. No longer the per-instant
            /// offset once <see cref="Speed"/> ≠ 1 — it serves as the segment's identity for seam
            /// detection (the player only ever compares it across instants), never arithmetic.</summary>
            public long Offset => SrcIn - TlStart;

            /// <summary>A timeline span inside this segment, rendered into source ticks.</summary>
            public long ToSource(long timelineTicks) =>
                Speed == 1.0 ? timelineTicks : (long)Math.Round(timelineTicks * Speed);

            /// <summary>A source span inside this segment, rendered into timeline ticks.</summary>
            public long ToTimeline(long sourceTicks) =>
                Speed == 1.0 ? sourceTicks : (long)Math.Round(sourceTicks / Speed);
        }

        /// <summary>The mapping for one (sourceId, streamIndex).</summary>
        internal sealed class StreamMap
        {
            private readonly Segment[] _segments; // timeline order

            public StreamMap(List<Segment> segments)
            {
                segments.Sort((a, b) => a.TlStart.CompareTo(b.TlStart));
                _segments = segments.ToArray();
                SourceCuts = BuildCuts(_segments);
            }

            public IReadOnlyList<Segment> Segments => _segments;

            /// <summary>Interior source spans not covered by any segment (the cut-out material),
            /// in source time. Frames whose pts land here must never surface in the preview.</summary>
            public SkipRangeSchedule SourceCuts { get; }

            /// <summary>Timeline instant → source instant, mirroring the composer's per-item
            /// mapping. Clamps: before the first segment → its in-point, inside a timeline gap →
            /// the next segment's in-point, past the last segment → its last kept source
            /// instant (source end − 1).</summary>
            public long TimelineToSource(long tlTicks)
            {
                if (_segments.Length == 0)
                    return tlTicks;

                foreach (var seg in _segments)
                {
                    if (tlTicks < seg.TlStart)
                        return seg.SrcIn; // before this segment (leading edge or a gap)
                    if (tlTicks < seg.TlEnd)
                        return seg.SrcIn + seg.ToSource(tlTicks - seg.TlStart);
                }

                // Past the last segment: clamp to the last KEPT source instant, not to SrcEnd.
                // SrcEnd is the exclusive out-point — the first instant the edit removed — and
                // seeking a decode pipeline there presents the first trimmed-away frame (the
                // preview would show material a render never contains). SrcEnd − 1 combined
                // with the workers' floor-to-covering-frame exact-seek discard resolves to the
                // last kept frame instead.
                return Math.Max(_segments[^1].SrcIn, _segments[^1].SrcEnd - 1);
            }

            /// <summary>Source instant → timeline instant (the pacing map handed to the decode
            /// workers). A source instant inside a cut clamps to the seam (the end of the segment
            /// preceding the cut); before all segments clamps to the first segment's start.</summary>
            public long SourceToTimeline(long srcTicks)
            {
                long best = long.MinValue;
                long bestSrcEnd = long.MinValue;
                foreach (var seg in _segments)
                {
                    if (srcTicks >= seg.SrcIn && srcTicks < seg.SrcEnd)
                        return seg.TlStart + seg.ToTimeline(srcTicks - seg.SrcIn);
                    if (seg.SrcEnd <= srcTicks && seg.SrcEnd > bestSrcEnd)
                    {
                        bestSrcEnd = seg.SrcEnd;
                        best = seg.TlEnd;
                    }
                }

                if (best != long.MinValue)
                    return best;
                return _segments.Length > 0 ? _segments[0].TlStart : srcTicks;
            }

            /// <summary>The source-timeline offset of the segment covering the timeline instant,
            /// or <see cref="long.MinValue"/> when nothing covers it (a gap, or outside the
            /// stream's span). A change in this value across a played seam is exactly a source
            /// discontinuity — the player's cue to hop the decode pipeline.</summary>
            public long OffsetAtTimeline(long tlTicks)
            {
                foreach (var seg in _segments)
                {
                    if (tlTicks >= seg.TlStart && tlTicks < seg.TlEnd)
                        return seg.Offset;
                }

                return long.MinValue;
            }

            private static SkipRangeSchedule BuildCuts(Segment[] segments)
            {
                List<TimeRange> ranges = null;
                for (int i = 1; i < segments.Length; i++)
                {
                    long cutStart = segments[i - 1].SrcEnd;
                    long cutEnd = segments[i].SrcIn;
                    if (cutEnd > cutStart)
                    {
                        ranges ??= new List<TimeRange>();
                        ranges.Add(new TimeRange(new TimeSpan(cutStart), new TimeSpan(cutEnd)));
                    }
                }

                return ranges == null ? SkipRangeSchedule.Empty : new SkipRangeSchedule(ranges);
            }
        }

        private readonly Dictionary<(Guid SourceId, int StreamIndex), StreamMap> _video;

        private ProjectTimelineMap(
            Dictionary<(Guid, int), StreamMap> video,
            (Guid, int)? primaryVideo,
            long durationTicks)
        {
            _video = video;
            PrimaryVideo = primaryVideo;
            DurationTicks = durationTicks;
        }

        public IReadOnlyDictionary<(Guid SourceId, int StreamIndex), StreamMap> VideoStreams => _video;

        /// <summary>The stream whose immediate-present frames define the paused position and whose
        /// segment offsets drive seam detection: the first media item on the lowest-order visible
        /// video track (the screen recording, in a recording project).</summary>
        public (Guid SourceId, int StreamIndex)? PrimaryVideo { get; }

        /// <summary>Timeline length — the whole project's, so audio (or text/image) items running
        /// past the last video frame still count.</summary>
        public long DurationTicks { get; }

        public bool TryGetVideo((Guid, int) key, out StreamMap map) => _video.TryGetValue(key, out map);

        public static ProjectTimelineMap Build(Project project)
        {
            ArgumentNullException.ThrowIfNull(project);

            var tracks = new List<Track>(project.Tracks ?? new List<Track>());
            tracks.Sort((a, b) =>
            {
                int byOrder = a.Order.CompareTo(b.Order);
                return byOrder != 0 ? byOrder : a.Id.CompareTo(b.Id);
            });

            var items = project.Items ?? new List<Item>();

            var videoSegments = new Dictionary<(Guid, int), List<Segment>>();
            (Guid, int)? primary = null;

            foreach (var track in tracks)
            {
                if (track.Kind != TrackKind.Video || track.Hidden)
                    continue;

                // items of this track in timeline order (Project.Normalize sorts, but do not rely on it)
                var trackItems = new List<Item>();
                foreach (var item in items)
                {
                    if (item.TrackId == track.Id && item.Content is MediaContent && item.DurationTicks > 0)
                        trackItems.Add(item);
                }

                trackItems.Sort((a, b) => a.TimelineStartTicks.CompareTo(b.TimelineStartTicks));

                foreach (var item in trackItems)
                {
                    var media = (MediaContent)item.Content;
                    var key = (media.SourceId, media.StreamIndex);
                    var segment = new Segment(item.TimelineStartTicks, item.TimelineEndTicks,
                        media.SourceInTicks, TimelineOps.SpeedOf(media));

                    if (!videoSegments.TryGetValue(key, out var list))
                        videoSegments[key] = list = new List<Segment>();
                    list.Add(segment);
                    primary ??= key;
                }
            }

            var video = new Dictionary<(Guid, int), StreamMap>();
            foreach (var (key, segments) in videoSegments)
                video[key] = new StreamMap(segments);

            return new ProjectTimelineMap(video, primary, project.GetDurationTicks());
        }
    }
}
