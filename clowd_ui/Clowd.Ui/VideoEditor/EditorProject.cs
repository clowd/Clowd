using System;
using System.Collections.Generic;
using Clowd.UI.Services;
using Clowd.VideoSDK;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// The seam between the editor's Phase-1 edit surface (<see cref="VideoEditDocument"/>: one
    /// trim range, a cut list, one webcam overlay — the single-row timeline and the sidebar) and
    /// the v2 <see cref="Project"/> the compositor plays and renders. The project <b>is</b> the
    /// document; this class is the projection in both directions:
    ///
    /// <list type="bullet">
    /// <item><see cref="Rebuild"/> maps the document onto a fresh project — the keep segments
    /// (<see cref="VideoEditDocument.GetKeepSegments"/>) become one item per row per segment,
    /// placed back to back, exactly as <c>RenderArgsCompat</c> maps a v1 render-args file. That is
    /// the "segments-to-items" form of <c>TimelineOps.Split</c> + <c>RippleDelete</c>: the timeline
    /// control edits a normalized cut list rather than individual items, so rebuilding the item set
    /// from that list is both simpler and exactly equivalent, and it keeps the control's math (and
    /// its tests) untouched.</item>
    /// <item><see cref="ApplyToDocument"/> reads a saved project back into a document, so a v2
    /// <c>videoedit.json</c> reopens in the same single-row UI.</item>
    /// </list>
    ///
    /// Ids are minted once per editor window (<see cref="RecordingIds"/>) and reused by every
    /// rebuild: <c>CompositionPlayer.UpdateProject</c> only reopens decoders when the set of
    /// referenced (source, stream) pairs changes, so a trim drag that mints new source ids would
    /// tear the pipeline down on every pointer move.
    ///
    /// The webcam placement is computed through <see cref="VideoRenderManager.ComputeWebcamRect"/> —
    /// the very pixel rect handed to the render tool — and normalized back by
    /// <see cref="RecordingProject.WebcamTransform"/>, so what the preview composes is what the
    /// render produces, rounding and edge-clamping included.
    /// </summary>
    internal sealed class EditorProject
    {
        private const long TicksPerMs = TimeSpan.TicksPerMillisecond;

        private readonly string _path;
        private readonly VideoStreamProbe _screen;
        private readonly VideoStreamProbe _webcam;
        private readonly AudioStreamProbe _audio;
        private readonly RecordingIds _ids = RecordingIds.New();
        private readonly int _fpsNum;
        private readonly int _fpsDen;

        /// <param name="path">The recording being edited.</param>
        /// <param name="probe">Its probe — the project's output canvas, streams and duration.</param>
        /// <param name="document">The live edit document; every <see cref="Rebuild"/> reads it.</param>
        public EditorProject(string path, MediaProbeResult probe, VideoEditDocument document)
        {
            ArgumentNullException.ThrowIfNull(probe);
            ArgumentNullException.ThrowIfNull(document);

            var videoStreams = probe.VideoStreams ?? Array.Empty<VideoStreamProbe>();
            if (videoStreams.Count == 0)
                throw new InvalidOperationException("The recording has no video stream.");

            _path = path;
            _screen = videoStreams[0];

            // the webcam is the second video stream, the same "track 1" rule the recorder writes
            // and the render tool reads.
            var cam = videoStreams.Count > 1 ? videoStreams[1] : null;
            _webcam = cam is { Width: > 0, Height: > 0 } ? cam : null;

            _audio = probe.AudioStreams is { Count: > 0 } ? probe.AudioStreams[0] : null;
            (_fpsNum, _fpsDen) = RecordingProject.ChooseFrameRate(_screen);

            Document = document;
            // the container's duration, exactly as the export path has always taken it; a file
            // whose container declares none falls back to the screen stream's own.
            DurationMs = (probe.DurationTicks > 0 ? probe.DurationTicks : _screen.DurationTicks) / TicksPerMs;
            RenderSource = new VideoRenderSource(
                DurationMs,
                _screen.Width, _screen.Height,
                _webcam?.StreamIndex,
                _webcam?.Width ?? 0,
                _webcam?.Height ?? 0);

            TimeMap = EditTimeMap.Whole(DurationMs);
        }

        public VideoEditDocument Document { get; }

        /// <summary>Source (recording) duration in milliseconds — the span the timeline shows.</summary>
        public long DurationMs { get; }

        /// <summary>Screen frame size; the project's output canvas and the preview's aspect.</summary>
        public int ScreenWidth => _screen.Width;

        public int ScreenHeight => _screen.Height;

        public bool HasWebcam => _webcam != null;

        /// <summary>Webcam frame aspect as height/width, 0 when there is no webcam track.</summary>
        public double WebcamAspect => _webcam != null ? _webcam.Height / (double)_webcam.Width : 0;

        /// <summary>What the (still v1) render path needs to know about the source media.</summary>
        public VideoRenderSource RenderSource { get; }

        /// <summary>The project as of the last successful <see cref="Rebuild"/>; null before the
        /// first one.</summary>
        public Project Current { get; private set; }

        /// <summary>Maps between the timeline control's source time and the project's output
        /// timeline time (which is what the player's transport speaks). Matches
        /// <see cref="Current"/>.</summary>
        public EditTimeMap TimeMap { get; private set; }

        /// <summary>
        /// Rebuilds <see cref="Current"/> from the document. Returns false — leaving the previous
        /// project in place — when the edit keeps nothing of the recording: there is no such thing
        /// as an empty project to preview, and the render path already refuses that edit with a
        /// message of its own.
        /// </summary>
        public bool Rebuild()
        {
            var keepMs = Document.GetKeepSegments(DurationMs);
            if (keepMs.Count == 0)
                return false;

            var segments = new List<KeepSegment>(keepMs.Count);
            foreach (var seg in keepMs)
                segments.Add(new KeepSegment(seg.StartMs * TicksPerMs, seg.DurationMs * TicksPerMs));

            Current = RecordingProject.Build(new RecordingProjectSpec
            {
                InputPath = _path,
                Screen = _screen,
                Webcam = _webcam,
                Audio = _audio,
                FpsNum = _fpsNum,
                FpsDen = _fpsDen,
                Segments = segments,
                WebcamTransform = BuildWebcamTransform(),
                WebcamHidden = !Document.Webcam.Enabled,
                Ids = _ids,
            });

            TimeMap = EditTimeMap.FromKeepSegments(keepMs);
            return true;
        }

        /// <summary>The webcam items' placement, or null when the recording has no camera track.</summary>
        private Transform BuildWebcamTransform()
        {
            if (_webcam == null)
                return null;

            var overlay = Document.Webcam;
            var rect = VideoRenderManager.ComputeWebcamRect(overlay, RenderSource);
            var mask = new Mask
            {
                Shape = overlay.Shape == WebcamOverlayShape.Circle ? MaskShape.Circle : MaskShape.RoundedRect,
                // kept even for a circle (where it has no effect) so switching shapes back and
                // forth — including across a save/reload — does not lose the user's radius.
                CornerRadius = overlay.CornerRadius,
            };

            return RecordingProject.WebcamTransform(rect.X, rect.Y, rect.W, rect.H,
                _screen.Width, _screen.Height, mask);
        }

        /// <summary>
        /// Reads a saved project back into the editor's single-row edit surface: the lowest-order
        /// video row's items are the keep segments (their source spans), so the trim range is the
        /// first item's in-point to the last item's out-point and the cuts are the source spans
        /// between them; the webcam row's hidden flag and its first item's transform are the
        /// overlay. Values go through the document's own setters, so anything out of range in the
        /// file is clamped exactly as a live edit would be.
        ///
        /// This is a projection, not a full load: the Phase-1 UI can only express what it can
        /// express, so a project carrying anything else (extra rows, per-item transforms, text or
        /// image items) keeps only the part shown above, and the next save writes that back.
        /// </summary>
        public static void ApplyToDocument(Project project, VideoEditDocument document, long sourceDurationMs)
        {
            ArgumentNullException.ThrowIfNull(project);
            ArgumentNullException.ThrowIfNull(document);

            var tracks = new List<Track>(project.Tracks ?? new List<Track>());
            tracks.Sort((a, b) =>
            {
                int byOrder = a.Order.CompareTo(b.Order);
                return byOrder != 0 ? byOrder : a.Id.CompareTo(b.Id);
            });

            Track screenTrack = null;
            Track webcamTrack = null;
            foreach (var track in tracks)
            {
                if (track.Kind != TrackKind.Video)
                    continue;
                if (screenTrack == null)
                    screenTrack = track;
                else if (webcamTrack == null)
                    webcamTrack = track;
            }

            if (screenTrack == null)
                return;

            var items = MediaItemsOf(project, screenTrack.Id);
            if (items.Count == 0)
                return;

            long firstIn = SourceIn(items[0]);
            long lastEnd = SourceIn(items[items.Count - 1]) + items[items.Count - 1].DurationTicks;

            document.TrimStartMs = firstIn / TicksPerMs;
            // 0 is the document's "to the end" sentinel: an untrimmed tail must round-trip as the
            // sentinel so the edit survives a re-probe that reports a slightly different duration.
            long lastEndMs = lastEnd / TicksPerMs;
            document.TrimEndMs = sourceDurationMs > 0 && lastEndMs >= sourceDurationMs ? 0 : lastEndMs;

            var cuts = new List<CutRegion>();
            for (int i = 1; i < items.Count; i++)
            {
                long cutStart = SourceIn(items[i - 1]) + items[i - 1].DurationTicks;
                long cutEnd = SourceIn(items[i]);
                if (cutEnd > cutStart)
                    cuts.Add(new CutRegion(cutStart / TicksPerMs, cutEnd / TicksPerMs));
            }

            document.SetCuts(cuts);

            if (webcamTrack == null)
                return;

            document.Webcam.Enabled = !webcamTrack.Hidden;

            var camItems = MediaItemsOf(project, webcamTrack.Id);
            var transform = camItems.Count > 0 ? camItems[0].Transform : null;
            if (transform == null)
                return;

            document.Webcam.CenterX = transform.X;
            document.Webcam.CenterY = transform.Y;
            document.Webcam.Width = transform.Scale;

            if (transform.Mask is { } mask)
            {
                document.Webcam.Shape = mask.Shape == MaskShape.Circle
                    ? WebcamOverlayShape.Circle
                    : WebcamOverlayShape.RoundedRect;
                document.Webcam.CornerRadius = mask.CornerRadius;
            }
        }

        private static List<Item> MediaItemsOf(Project project, Guid trackId)
        {
            var items = new List<Item>();
            foreach (var item in project.Items ?? new List<Item>())
            {
                if (item.TrackId == trackId && item.Content is MediaContent && item.DurationTicks > 0)
                    items.Add(item);
            }

            items.Sort((a, b) => a.TimelineStartTicks.CompareTo(b.TimelineStartTicks));
            return items;
        }

        private static long SourceIn(Item item) => ((MediaContent)item.Content).SourceInTicks;
    }

    /// <summary>
    /// Maps between the two time domains the editor speaks: the <b>source</b> time of the
    /// recording (what the single-row timeline draws, scrubs and reports — the whole recording
    /// including the parts the edit removes) and the <b>output timeline</b> time of the project
    /// (what <c>CompositionPlayer</c>'s transport runs in, where the kept segments are back to
    /// back). Pure integer millisecond math, so it can be unit-tested without a player.
    ///
    /// Mapping a source instant that the edit removed clamps to the seam — the boundary the cut
    /// collapsed to, i.e. the end of the last kept segment before it. That is the same behaviour
    /// as the old preview, where seeking into a skip range made the player hop to its end, and it
    /// agrees with the SDK's own source-to-timeline mapping.
    /// </summary>
    internal sealed class EditTimeMap
    {
        private readonly long[] _sourceStart;
        private readonly long[] _timelineStart;
        private readonly long[] _length;

        private EditTimeMap(long[] sourceStart, long[] timelineStart, long[] length, long durationMs)
        {
            _sourceStart = sourceStart;
            _timelineStart = timelineStart;
            _length = length;
            TimelineDurationMs = durationMs;
        }

        /// <summary>Length of the edited (output) timeline in milliseconds.</summary>
        public long TimelineDurationMs { get; }

        /// <summary>The identity map over a whole, unedited recording.</summary>
        public static EditTimeMap Whole(long durationMs)
        {
            durationMs = Math.Max(0, durationMs);
            return new EditTimeMap(new[] { 0L }, new[] { 0L }, new[] { durationMs }, durationMs);
        }

        public static EditTimeMap FromKeepSegments(IReadOnlyList<CutRegion> keep)
        {
            ArgumentNullException.ThrowIfNull(keep);
            if (keep.Count == 0)
                return Whole(0);

            var sourceStart = new long[keep.Count];
            var timelineStart = new long[keep.Count];
            var length = new long[keep.Count];

            long cursor = 0;
            for (int i = 0; i < keep.Count; i++)
            {
                sourceStart[i] = keep[i].StartMs;
                timelineStart[i] = cursor;
                length[i] = Math.Max(0, keep[i].DurationMs);
                cursor += length[i];
            }

            return new EditTimeMap(sourceStart, timelineStart, length, cursor);
        }

        /// <summary>Output-timeline instant → source instant. Clamped into the edit.</summary>
        public long ToSourceMs(long timelineMs)
        {
            if (timelineMs <= 0)
                return _sourceStart[0];

            for (int i = 0; i < _length.Length; i++)
            {
                if (timelineMs < _timelineStart[i] + _length[i])
                    return _sourceStart[i] + Math.Max(0, timelineMs - _timelineStart[i]);
            }

            int last = _length.Length - 1;
            return _sourceStart[last] + _length[last];
        }

        /// <summary>Source instant → output-timeline instant; a removed instant maps to the seam
        /// it collapsed to.</summary>
        public long ToTimelineMs(long sourceMs)
        {
            long seam = 0;
            for (int i = 0; i < _length.Length; i++)
            {
                if (sourceMs < _sourceStart[i])
                    return seam; // before this segment: a trimmed head, or inside a cut
                if (sourceMs < _sourceStart[i] + _length[i])
                    return _timelineStart[i] + (sourceMs - _sourceStart[i]);
                seam = _timelineStart[i] + _length[i];
            }

            return seam;
        }
    }
}
