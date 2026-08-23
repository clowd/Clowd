using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>
    /// A filmstrip request for one video stream: the thumbnails covering
    /// <c>[SourceInTicks, SourceInTicks + DurationTicks)</c>, one every
    /// <see cref="IntervalTicks"/>. The interval is a hint — the provider quantizes it (see
    /// <see cref="QuantizeInterval"/>) and reports what it actually used on the strip.
    /// </summary>
    public readonly record struct ThumbnailRequest(Guid SourceId, int StreamIndex, long SourceInTicks,
        long DurationTicks, long IntervalTicks, int ThumbHeightPx)
    {
        /// <summary>The finest thumbnail grid any zoom level resolves to. Asking for more than four
        /// thumbnails a second buys nothing on a timeline row.</summary>
        public const long BaseIntervalTicks = TimeSpan.TicksPerMillisecond * 250;

        /// <summary>
        /// Snaps an interval to a power-of-two multiple of <see cref="BaseIntervalTicks"/>, rounding
        /// <b>down</b> so the strip is never sparser than the caller asked for. Because every grid
        /// is anchored at source time 0, a coarser grid's timestamps are a subset of a finer one's —
        /// which is what lets zooming out reuse the thumbnails zooming in already decoded.
        /// </summary>
        public static long QuantizeInterval(long intervalTicks)
        {
            if (intervalTicks <= BaseIntervalTicks)
                return BaseIntervalTicks;

            var step = BaseIntervalTicks;
            while (step <= Int64.MaxValue / 2 && step * 2 <= intervalTicks)
                step *= 2;
            return step;
        }
    }

    /// <summary>One filmstrip frame: the source time it was decoded at, and the decoded image.</summary>
    public readonly record struct TimelineThumbnail(long SourceTicks, Bitmap Image);

    /// <summary>
    /// The thumbnails a provider has ready for a <see cref="ThumbnailRequest"/> right now. Requests
    /// never block: whatever is decoded comes back immediately, the misses are queued, and
    /// <see cref="ITimelinePreviewProvider.PreviewReady"/> tells the timeline to ask again. Rows
    /// draw the nearest available thumbnail across a gap, so a partial strip still looks like a
    /// filmstrip rather than a hole.
    /// </summary>
    public sealed class ThumbnailStrip
    {
        public static readonly ThumbnailStrip Empty =
            new ThumbnailStrip(ThumbnailRequest.BaseIntervalTicks, Array.Empty<TimelineThumbnail>(), false);

        public ThumbnailStrip(long intervalTicks, IReadOnlyList<TimelineThumbnail> thumbnails, bool isComplete)
        {
            IntervalTicks = intervalTicks;
            Thumbnails = thumbnails ?? Array.Empty<TimelineThumbnail>();
            IsComplete = isComplete;
        }

        /// <summary>The interval actually used, after quantization.</summary>
        public long IntervalTicks { get; }

        /// <summary>Ready thumbnails, ascending by <see cref="TimelineThumbnail.SourceTicks"/>.
        /// Immutable — providers hand out snapshots, never live collections.</summary>
        public IReadOnlyList<TimelineThumbnail> Thumbnails { get; }

        /// <summary>True when nothing is still queued for this span, so the timeline can stop
        /// re-asking on <see cref="ITimelinePreviewProvider.PreviewReady"/>.</summary>
        public bool IsComplete { get; }
    }

    /// <summary>A waveform request for one audio stream, bucketed at
    /// <see cref="TicksPerBucket"/> — the timeline picks the bucket size from the current zoom so
    /// one bucket is roughly one pixel.</summary>
    public readonly record struct AudioPeaksRequest(Guid SourceId, int StreamIndex, long SourceInTicks,
        long DurationTicks, long TicksPerBucket);

    /// <summary>
    /// Mono-folded min/max peaks for a span of one audio stream. <see cref="MinMax"/> is
    /// <b>interleaved</b> — <c>[min0, max0, min1, max1, …]</c>, two entries per bucket — as floats
    /// normalized to <c>[-1, 1]</c>: the UI multiplies straight by half the row height, with no
    /// scaling step to get wrong. (The SDK stores them more compactly; converting once here keeps
    /// the drawing code trivial.)
    /// </summary>
    public sealed class AudioPeaks
    {
        public AudioPeaks(long startTicks, long ticksPerBucket, IReadOnlyList<float> minMax, bool isComplete)
        {
            StartTicks = startTicks;
            TicksPerBucket = ticksPerBucket;
            MinMax = minMax ?? Array.Empty<float>();
            IsComplete = isComplete;
        }

        /// <summary>Silence covering the request — what a provider returns for a stream it cannot
        /// (or will not) analyze. Draws as a flat line, never as a missing row.</summary>
        public static AudioPeaks Silent(in AudioPeaksRequest request)
        {
            var perBucket = Math.Max(1, request.TicksPerBucket);
            var buckets = (int)Math.Clamp((request.DurationTicks + perBucket - 1) / perBucket, 0, Int32.MaxValue / 2);
            return new AudioPeaks(request.SourceInTicks, perBucket, new float[buckets * 2], true);
        }

        /// <summary>Source time of bucket 0.</summary>
        public long StartTicks { get; }

        public long TicksPerBucket { get; }

        public IReadOnlyList<float> MinMax { get; }

        public int BucketCount => MinMax.Count / 2;

        /// <summary>True when the whole span has been analyzed; a false value means the waveform is
        /// still being built and <see cref="ITimelinePreviewProvider.PreviewReady"/> will fire
        /// again.</summary>
        public bool IsComplete { get; }

        /// <summary>Reads one bucket. Returns false (and silence) outside the analyzed range, so
        /// callers can walk a pixel range without bounds-checking every step.</summary>
        public bool TryGetBucket(int index, out float min, out float max)
        {
            if (index < 0 || index >= BucketCount)
            {
                min = 0;
                max = 0;
                return false;
            }

            min = MinMax[index * 2];
            max = MinMax[index * 2 + 1];
            return true;
        }
    }

    /// <summary>A cursor-activity request for one recording's input capture (the source's
    /// <c>InputCapturePath</c>), in <b>source</b> ticks and bucketed like
    /// <see cref="AudioPeaksRequest"/>: one bucket is roughly one pixel.</summary>
    public readonly record struct CursorActivityRequest(Guid SourceId, long SourceInTicks,
        long DurationTicks, long TicksPerBucket);

    /// <summary>One button press in source ticks: the down and the matching up (equal for a
    /// release the capture never saw).</summary>
    public readonly record struct CursorClickSpan(long DownTicks, long UpTicks);

    /// <summary>
    /// The pointer's activity over a span of one recording, for the cursor row: per-bucket peak
    /// speed in <c>[0, 1]</c> (see <c>CursorMotion.Normalize</c> — the UI multiplies straight by
    /// half the row height, like a waveform) and every click whose press lands in the span.
    /// </summary>
    public sealed class CursorActivity
    {
        public CursorActivity(long startTicks, long ticksPerBucket, IReadOnlyList<float> motion,
            IReadOnlyList<CursorClickSpan> clicks, bool isComplete)
        {
            StartTicks = startTicks;
            TicksPerBucket = ticksPerBucket;
            Motion = motion ?? Array.Empty<float>();
            Clicks = clicks ?? Array.Empty<CursorClickSpan>();
            IsComplete = isComplete;
        }

        /// <summary>A still pointer and no clicks over the request — what a source without
        /// capture data gets. Complete: nothing will arrive.</summary>
        public static CursorActivity None(in CursorActivityRequest request) => Flat(request, true);

        /// <summary>Nothing yet: the capture is still loading and
        /// <see cref="ITimelinePreviewProvider.PreviewReady"/> will fire.</summary>
        public static CursorActivity Pending(in CursorActivityRequest request) => Flat(request, false);

        private static CursorActivity Flat(in CursorActivityRequest request, bool complete)
        {
            var perBucket = Math.Max(1, request.TicksPerBucket);
            var buckets = (int)Math.Clamp((request.DurationTicks + perBucket - 1) / perBucket, 0, Int32.MaxValue / 2);
            return new CursorActivity(request.SourceInTicks, perBucket, new float[buckets], null, complete);
        }

        /// <summary>Source time of bucket 0.</summary>
        public long StartTicks { get; }

        public long TicksPerBucket { get; }

        /// <summary>Peak normalized speed per bucket.</summary>
        public IReadOnlyList<float> Motion { get; }

        /// <summary>Presses whose down lies in the span, ascending.</summary>
        public IReadOnlyList<CursorClickSpan> Clicks { get; }

        public int BucketCount => Motion.Count;

        /// <summary>False while the capture is still being read.</summary>
        public bool IsComplete { get; }
    }

    /// <summary>A keystroke-run request for one recording's input capture, in <b>source</b>
    /// ticks. The pause-break and filter are the item's own: runs are segmented exactly as the
    /// overlay will show them, so each one here is one row on the output.</summary>
    public readonly record struct KeyRunsRequest(Guid SourceId, long SourceInTicks, long DurationTicks,
        int PauseBreakMs, KeystrokeFilter Filter);

    /// <summary>One keystroke run in source ticks: from its first key-down to its last, and how
    /// many keys it carries.</summary>
    public readonly record struct TimelineKeyRun(long StartTicks, long EndTicks, int KeyCount);

    /// <summary>The keystroke runs intersecting a span of one recording, ascending.</summary>
    public sealed class KeyRuns
    {
        public static readonly KeyRuns None = new KeyRuns(Array.Empty<TimelineKeyRun>(), true);

        /// <summary>Nothing yet: the capture is still loading and
        /// <see cref="ITimelinePreviewProvider.PreviewReady"/> will fire.</summary>
        public static readonly KeyRuns Pending = new KeyRuns(Array.Empty<TimelineKeyRun>(), false);

        public KeyRuns(IReadOnlyList<TimelineKeyRun> runs, bool isComplete)
        {
            Runs = runs ?? Array.Empty<TimelineKeyRun>();
            IsComplete = isComplete;
        }

        public IReadOnlyList<TimelineKeyRun> Runs { get; }

        /// <summary>False while the capture is still being read.</summary>
        public bool IsComplete { get; }
    }

    /// <summary>
    /// Where the timeline gets item visuals it cannot compute itself: video filmstrips, audio
    /// waveforms, and the input-capture activity the cursor and keys rows preview. Every call is
    /// non-blocking and returns what is ready <i>now</i>; misses are queued
    /// on the provider's own background workers and announced through <see cref="PreviewReady"/>.
    /// The timeline never learns how the pixels are produced — which is what lets it ship (and be
    /// driven manually) against <see cref="NullTimelinePreviewProvider"/> before the SDK's decoding
    /// services exist.
    ///
    /// All members are called on the UI thread, and <see cref="PreviewReady"/> is raised there too
    /// (the implementation marshals): the timeline redraws in response, so an off-thread event would
    /// be a bug at every consumer.
    /// </summary>
    public interface ITimelinePreviewProvider
    {
        /// <summary>Raised — coalesced, on the UI thread — when previously missing thumbnails or
        /// peaks have become available. The timeline throttles its redraw off this.</summary>
        event EventHandler PreviewReady;

        /// <summary>The filmstrip that is ready for <paramref name="request"/>; queues whatever is
        /// missing.</summary>
        ThumbnailStrip GetThumbnails(in ThumbnailRequest request);

        /// <summary>The waveform peaks that are ready for <paramref name="request"/>; queues the
        /// analysis when it has not run yet.</summary>
        AudioPeaks GetAudioPeaks(in AudioPeaksRequest request);

        /// <summary>The pointer activity that is ready for <paramref name="request"/>; starts
        /// reading the capture when it has not been read yet.</summary>
        CursorActivity GetCursorActivity(in CursorActivityRequest request);

        /// <summary>The keystroke runs that are ready for <paramref name="request"/>; starts
        /// reading the capture when it has not been read yet.</summary>
        KeyRuns GetKeyRuns(in KeyRunsRequest request);

        /// <summary>Tells the provider which timeline span is on screen so it can prioritize (and
        /// abandon) decoding work as the user scrolls and zooms. Advisory: a provider is free to
        /// ignore it.</summary>
        void SetViewport(long startTicks, long endTicks);
    }

    /// <summary>
    /// The do-nothing provider: empty filmstrips, silent waveforms, a still pointer, no events. The timeline's
    /// default, so it is never null-checked, and the placeholder until the SDK's thumbnail and
    /// waveform services land.
    /// </summary>
    public sealed class NullTimelinePreviewProvider : ITimelinePreviewProvider
    {
        public static readonly NullTimelinePreviewProvider Instance = new NullTimelinePreviewProvider();

        /// <summary>Never raised — the accessors are empty so subscribers cannot leak onto a
        /// singleton that will outlive every editor window.</summary>
        public event EventHandler PreviewReady
        {
            add { }
            remove { }
        }

        public ThumbnailStrip GetThumbnails(in ThumbnailRequest request) => ThumbnailStrip.Empty;

        public AudioPeaks GetAudioPeaks(in AudioPeaksRequest request) => AudioPeaks.Silent(request);

        public CursorActivity GetCursorActivity(in CursorActivityRequest request) => CursorActivity.None(request);

        public KeyRuns GetKeyRuns(in KeyRunsRequest request) => KeyRuns.None;

        public void SetViewport(long startTicks, long endTicks)
        {
        }
    }
}
