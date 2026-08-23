using System;
using System.Collections.Generic;
using System.Threading;

namespace Clowd.VideoSDK.Thumbs
{
    /// <summary>
    /// One filmstrip frame: the source instant it stands for and its pixels, BGRA top-down with
    /// <see cref="Stride"/> bytes per row. Deliberately <i>not</i> an SKImage or a platform bitmap —
    /// the SDK has no idea what the UI draws with, and a plain buffer has no lifetime to manage.
    /// The array is never written to after it is published, so consumers may hold it (and any
    /// bitmap they build from it) for as long as they like.
    /// <para>
    /// <see cref="SourceTicks"/> is the frame's own presentation time for thumbnails from the
    /// keyframe pass, and the <i>grid</i> instant for refined ones (the frame covering it) — which
    /// is what makes a coarse grid a subset of a finer one, and therefore reusable across zooms.
    /// </para>
    /// </summary>
    public readonly record struct FilmstripThumbnail(long SourceTicks, byte[] Pixels, int Width, int Height, int Stride);

    /// <summary>
    /// What a filmstrip has decoded so far: an immutable, ascending-by-time view handed out by
    /// <see cref="FilmstripProvider.GetOrStart"/>. Never blocks and never waits for decoding — a
    /// partial strip is normal, and <see cref="FilmstripProvider.Changed"/> says when to ask again.
    /// </summary>
    public sealed class FilmstripSnapshot
    {
        public static readonly FilmstripSnapshot Empty = new FilmstripSnapshot(
            FilmstripProvider.BaseIntervalTicks, 0, Array.Empty<FilmstripThumbnail>(), true, 0, null);

        internal FilmstripSnapshot(long intervalTicks, long durationTicks,
            IReadOnlyList<FilmstripThumbnail> thumbnails, bool isComplete, double progress, string error)
        {
            IntervalTicks = intervalTicks;
            DurationTicks = durationTicks;
            Thumbnails = thumbnails ?? Array.Empty<FilmstripThumbnail>();
            IsComplete = isComplete;
            Progress = progress;
            Error = error;
        }

        /// <summary>The grid the refinement pass is currently filling, after quantization
        /// (<see cref="FilmstripProvider.QuantizeInterval"/>).</summary>
        public long IntervalTicks { get; }

        /// <summary>Source duration once the decoder has opened the file; 0 before that.</summary>
        public long DurationTicks { get; }

        /// <summary>Ready thumbnails, ascending by <see cref="FilmstripThumbnail.SourceTicks"/>.</summary>
        public IReadOnlyList<FilmstripThumbnail> Thumbnails { get; }

        /// <summary>True when the keyframe pass has finished and no refinement work is outstanding,
        /// so the caller can stop re-asking on <see cref="FilmstripProvider.Changed"/>.</summary>
        public bool IsComplete { get; }

        /// <summary>Keyframe-pass progress in [0,1]; 1 once the pass is done.</summary>
        public double Progress { get; }

        /// <summary>Why decoding stopped, or null. A failed strip is <see cref="IsComplete"/>:
        /// nothing more is coming, and whatever was decoded before the failure stays usable.</summary>
        public string Error { get; }

        /// <summary>
        /// The thumbnail closest to <paramref name="sourceTicks"/>, which is what a row draws for a
        /// grid slot that has not been refined yet — a filmstrip with slightly stale frames rather
        /// than a hole. False only when nothing has been decoded at all.
        /// </summary>
        public bool TryGetNearest(long sourceTicks, out FilmstripThumbnail thumbnail)
        {
            var list = Thumbnails;
            int n = list.Count;
            if (n == 0)
            {
                thumbnail = default;
                return false;
            }

            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (list[mid].SourceTicks < sourceTicks)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            // `lo` is the first entry at or after the target; its predecessor may be closer.
            var best = list[lo];
            if (lo > 0)
            {
                var prev = list[lo - 1];
                if (Math.Abs(sourceTicks - prev.SourceTicks) <= Math.Abs(best.SourceTicks - sourceTicks))
                    best = prev;
            }

            thumbnail = best;
            return true;
        }
    }

    /// <summary>
    /// The slice of the shared thumbnail work scheduler the filmstrip needs: a single BelowNormal
    /// worker with a priority queue, so decoding never competes with playback. Lower priorities run
    /// first; the returned handle removes the item when it is still queued and cancels the token
    /// when it is already running, which is how a viewport change abandons stale work.
    /// </summary>
    public interface IThumbWorkQueue
    {
        IDisposable Enqueue(int priority, Action<CancellationToken> work);

        /// <summary>True when work more urgent than <paramref name="priorityBand"/> is queued.
        /// Long items poll this and park themselves so a lower band never starves behind a
        /// non-preemptive whole-stream decode; a queue with no notion of urgency (the inline test
        /// queues) just keeps the default.</summary>
        bool HasPendingBelow(int priorityBand) => false;
    }

    /// <summary>
    /// Video filmstrips for the timeline, decoded off the UI thread and cached in memory.
    ///
    /// <para><b>Two tiers.</b> (1) A keyframe pass over the whole stream — <c>AVDISCARD_NONKEY</c>
    /// at near demux speed — gives every row a complete, if coarse, strip almost immediately;
    /// rows draw the nearest thumbnail across the gaps. (2) Refinement fills the exact grid the
    /// visible rows ask for, seeking to the keyframe at or before each slot and decoding forward at
    /// most one GOP. <see cref="SetViewport"/>
    /// aims tier 2 at what the user is looking at — one span per visible item, each filled outward
    /// from its own middle, so a trimmed recording's kept segments are refined rather than the cut
    /// material between them — and abandons slots that scrolled away. Both tiers park themselves
    /// whenever a lower band (a waveform) is waiting for the shared thread, resuming afterwards.</para>
    ///
    /// <para><b>Grid.</b> Slots are anchored at source time 0 on a power-of-two multiple of
    /// <see cref="BaseIntervalTicks"/> (<see cref="QuantizeInterval"/>), so every zoom level's grid
    /// is a subset of every finer one and zooming out reuses what zooming in decoded. This mirrors
    /// the timeline's <c>ThumbnailRequest.QuantizeInterval</c> exactly — the UI type cannot be
    /// referenced from the SDK, so the two must stay in step.</para>
    ///
    /// <para><b>Memory.</b> A hard cap on retained thumbnails (~2000 by default, ~29 MB at 48 px
    /// tall 16:9), evicting the ones farthest from the viewport first and oldest-first among
    /// equals. No disk cache in v1.</para>
    ///
    /// <para>Thread-safe. <see cref="Changed"/> is throttled and raised on a thread-pool thread —
    /// UI consumers marshal it themselves.</para>
    /// </summary>
    public sealed class FilmstripProvider : IDisposable
    {
        /// <summary>The finest grid any zoom resolves to (250 ms) — see
        /// <see cref="QuantizeInterval"/>.</summary>
        public const long BaseIntervalTicks = TimeSpan.TicksPerMillisecond * 250;

        public const int DefaultThumbHeightPx = ThumbnailDecoder.DefaultThumbHeightPx;

        /// <summary>~29 MB of 48 px 16:9 thumbnails — enough for a long recording's keyframe pass
        /// plus several screens of refined grid.</summary>
        public const int DefaultMaxThumbnails = 2000;

        /// <summary>Scheduler priority of the whole-stream keyframe pass. Lower runs first;
        /// waveform analysis is expected to sit below both of these.</summary>
        public const int KeyframePassPriority = 20;

        /// <summary>Scheduler priority of viewport refinement — behind the keyframe pass, which is
        /// what gives every row something to draw first.</summary>
        public const int RefinePriority = 30;

        /// <summary>Most grid slots one viewport may queue. Keeps a pathological zoom (or a very
        /// wide row) from filling the whole cache with one strip and thrashing eviction.</summary>
        private const int MaxViewportSlots = 400;

        /// <summary>Frame budget for the forward decode after a refinement seek: one GOP plus
        /// slack. A stream whose keyframes are further apart than this falls back to the keyframe
        /// pass thumbnails, which is exactly what tier 1 is for.</summary>
        private const int MaxForwardFrames = 600;

        private const int NotifyThrottleMs = 100;
        private const int DisposeDrainMs = 5000;

        private readonly IThumbWorkQueue _queue;
        private readonly int _maxThumbnails;
        private readonly int _evictBatch;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private readonly object _lock = new object();
        private readonly Dictionary<string, Strip> _strips = new Dictionary<string, Strip>(StringComparer.OrdinalIgnoreCase);
        private int _count;
        private long _stamp;
        private bool _disposed;

        private readonly object _notifyLock = new object();
        private readonly Timer _notifyTimer;
        private bool _notifyScheduled;
        private long _lastNotifyMs = Int64.MinValue;

        private readonly object _busyLock = new object();
        private readonly ManualResetEventSlim _idle = new ManualResetEventSlim(true);
        private int _busy;

        /// <summary>Decodes on the process-wide <see cref="ThumbWorkScheduler"/> — the shape every
        /// caller outside this assembly uses, since the scheduler itself is internal.</summary>
        public FilmstripProvider()
            : this(ThumbWorkScheduler.Shared)
        {
        }

        public FilmstripProvider(IThumbWorkQueue queue, int maxThumbnails = DefaultMaxThumbnails)
        {
            ArgumentNullException.ThrowIfNull(queue);
            if (maxThumbnails <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxThumbnails), maxThumbnails, "The cache must hold at least one thumbnail.");

            _queue = queue;
            _maxThumbnails = maxThumbnails;
            _evictBatch = Math.Max(1, maxThumbnails / 32);
            _notifyTimer = new Timer(OnNotifyTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Raised — coalesced to at most one per 100 ms, on a thread-pool thread — when
        /// new thumbnails have landed.</summary>
        public event EventHandler Changed;

        /// <summary>
        /// Snaps an interval to a power-of-two multiple of <see cref="BaseIntervalTicks"/>, rounding
        /// <b>down</b> so a strip is never sparser than asked for. Every grid is anchored at source
        /// time 0, so a coarser grid's instants are a subset of a finer one's. Must stay identical
        /// to the timeline's <c>ThumbnailRequest.QuantizeInterval</c>.
        /// </summary>
        public static long QuantizeInterval(long intervalTicks)
        {
            if (intervalTicks <= BaseIntervalTicks)
                return BaseIntervalTicks;

            long step = BaseIntervalTicks;
            while (step <= Int64.MaxValue / 2 && step * 2 <= intervalTicks)
                step *= 2;
            return step;
        }

        /// <summary>
        /// The filmstrip for one video stream, starting the keyframe pass the first time it is
        /// asked for. Returns immediately with whatever is decoded — including nothing at all on
        /// the first call.
        /// </summary>
        public FilmstripSnapshot GetOrStart(string sourcePath, int streamIndex,
            int thumbHeightPx = DefaultThumbHeightPx)
        {
            ArgumentNullException.ThrowIfNull(sourcePath);

            Strip strip;
            FilmstripSnapshot snapshot;
            bool startPass = false;
            lock (_lock)
            {
                if (_disposed)
                    return FilmstripSnapshot.Empty;

                strip = GetOrCreateLocked(sourcePath, streamIndex, thumbHeightPx);
                if (!strip.PassStarted)
                {
                    strip.PassStarted = true;
                    startPass = true;
                }
                snapshot = SnapshotLocked(strip);
            }

            // Enqueue outside the lock: the scheduler is free to run the item inline, and no work
            // item may ever find the provider's lock held by the thread that queued it.
            if (startPass)
                StartPass(strip);
            return snapshot;
        }

        /// <summary>
        /// Tells the provider which span of one stream is on screen and how finely it is drawn, in
        /// source ticks — the one-span shape of the list overload below.
        /// </summary>
        public void SetViewport(string sourcePath, int streamIndex, int thumbHeightPx,
            long startTicks, long endTicks, long intervalTicks)
            => SetViewport(sourcePath, streamIndex, thumbHeightPx,
                new[] { (startTicks, endTicks) }, intervalTicks);

        /// <summary>
        /// Tells the provider which spans of one stream are on screen and how finely they are
        /// drawn, in source ticks — one span per visible item, because source time is not
        /// contiguous once a recording has been cut: a min/max union would aim refinement (and
        /// anchor eviction) at the removed material between the kept segments. Refinement fills
        /// each span outward from its own middle, sharing the slot budget across spans in
        /// proportion to their length; slots that fell out of view are abandoned, and their
        /// thumbnails become the first eviction candidates. <paramref name="intervalTicks"/> is a
        /// hint — it is quantized (<see cref="QuantizeInterval"/>) and reported back on the
        /// snapshot.
        /// </summary>
        public void SetViewport(string sourcePath, int streamIndex, int thumbHeightPx,
            IReadOnlyList<(long StartTicks, long EndTicks)> spans, long intervalTicks)
        {
            ArgumentNullException.ThrowIfNull(sourcePath);
            ArgumentNullException.ThrowIfNull(spans);

            var normalized = NormalizeSpans(spans);
            if (normalized.Length == 0)
            {
                ClearViewport(sourcePath, streamIndex, thumbHeightPx);
                return;
            }

            Strip strip;
            bool startRefine;
            IDisposable abandoned = null;
            lock (_lock)
            {
                if (_disposed)
                    return;

                strip = GetOrCreateLocked(sourcePath, streamIndex, thumbHeightPx);

                // A refinement item in flight re-reads the viewport between slots, so scrolling
                // needs no cancellation. A jump to spans that share nothing with the old ones is
                // different: the seek+GOP decode it is inside right now is for a slot nobody can
                // see, so it is dropped outright and a fresh item starts on the new spans.
                if (strip.RefineActive && strip.HasViewport && !SpansOverlap(normalized, strip.ViewSpans))
                {
                    strip.RefineCts?.Cancel();
                    abandoned = strip.RefineHandle;
                    strip.RefineHandle = null;
                    strip.RefineCts = null;
                    strip.RefineActive = false;
                }

                long interval = QuantizeInterval(intervalTicks);
                if (!strip.HasViewport || interval != strip.ViewInterval || !SpansEqual(normalized, strip.ViewSpans))
                    strip.Attempted.Clear();

                strip.HasViewport = true;
                strip.ViewportRetired = false;
                strip.ViewSpans = normalized;
                strip.ViewInterval = interval;
                strip.Snapshot = null; // the reported interval is part of the snapshot
                startRefine = EnsureRefineLocked(strip);
            }

            if (abandoned != null)
            {
                try { abandoned.Dispose(); }
                catch { /* a scheduler that already forgot the item is fine */ }
            }
            if (startRefine)
                StartRefine(strip);
        }

        /// <summary>
        /// Retires a strip's viewport: nothing of the stream is drawn any more (its items scrolled
        /// off screen or were deleted). Any in-flight refinement is canceled — it would be
        /// decoding slots nobody can see, on the one thread every visible strip shares — and the
        /// strip's thumbnails become the cache's <i>first</i> eviction candidates instead of
        /// squatting at their last viewport's priority. A later <see cref="SetViewport"/> revives
        /// the strip untouched.
        /// </summary>
        public void ClearViewport(string sourcePath, int streamIndex, int thumbHeightPx)
        {
            ArgumentNullException.ThrowIfNull(sourcePath);

            IDisposable abandoned = null;
            lock (_lock)
            {
                if (_disposed)
                    return;

                int height = Math.Clamp(thumbHeightPx, ThumbnailDecoder.MinThumbHeightPx, ThumbnailDecoder.MaxThumbHeightPx);
                if (!_strips.TryGetValue(KeyOf(sourcePath, streamIndex, height), out var strip) || !strip.HasViewport)
                    return;

                if (strip.RefineActive)
                {
                    strip.RefineCts?.Cancel();
                    abandoned = strip.RefineHandle;
                    strip.RefineHandle = null;
                    strip.RefineCts = null;
                    strip.RefineActive = false;
                }

                strip.HasViewport = false;
                strip.ViewportRetired = true;
                strip.ViewSpans = Array.Empty<(long, long)>();
                strip.Attempted.Clear();
                strip.Snapshot = null;
            }

            if (abandoned != null)
            {
                try { abandoned.Dispose(); }
                catch { /* a scheduler that already forgot the item is fine */ }
            }
        }

        /// <summary>Clamps each span to non-negative time, sorts and merges overlapping or
        /// touching ones — the shape every consumer below assumes (disjoint, ascending).</summary>
        private static (long Start, long End)[] NormalizeSpans(IReadOnlyList<(long StartTicks, long EndTicks)> spans)
        {
            var list = new List<(long Start, long End)>(spans.Count);
            foreach (var span in spans)
            {
                long start = Math.Max(0, Math.Min(span.StartTicks, span.EndTicks));
                long end = Math.Max(0, Math.Max(span.StartTicks, span.EndTicks));
                list.Add((start, end));
            }

            if (list.Count > 1)
            {
                list.Sort((a, b) => a.Start.CompareTo(b.Start));
                int w = 0;
                for (int i = 1; i < list.Count; i++)
                {
                    if (list[i].Start <= list[w].End)
                        list[w] = (list[w].Start, Math.Max(list[w].End, list[i].End));
                    else
                        list[++w] = list[i];
                }
                list.RemoveRange(w + 1, list.Count - w - 1);
            }

            return list.ToArray();
        }

        /// <summary>Both lists sorted and disjoint; touching counts as overlap, mirroring the old
        /// single-span disjointness test.</summary>
        private static bool SpansOverlap((long Start, long End)[] a, (long Start, long End)[] b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (a[i].End < b[j].Start)
                    i++;
                else if (b[j].End < a[i].Start)
                    j++;
                else
                    return true;
            }
            return false;
        }

        private static bool SpansEqual((long Start, long End)[] a, (long Start, long End)[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        public void Dispose()
        {
            List<IDisposable> handles;
            List<ThumbnailDecoder> parked = null;
            lock (_lock)
            {
                if (_disposed)
                    return;
                _disposed = true;

                handles = new List<IDisposable>();
                foreach (var strip in _strips.Values)
                {
                    if (strip.PassHandle != null)
                        handles.Add(strip.PassHandle);
                    if (strip.RefineHandle != null)
                        handles.Add(strip.RefineHandle);
                    strip.PassHandle = null;
                    strip.RefineHandle = null;

                    // a pass parked between chunks owns an FFmpeg context nobody will resume now
                    if (strip.PassDecoder != null)
                    {
                        (parked ??= new List<ThumbnailDecoder>()).Add(strip.PassDecoder);
                        strip.PassDecoder = null;
                    }
                }
                _strips.Clear();
                _count = 0;
            }

            _cts.Cancel();
            foreach (var handle in handles)
            {
                try { handle.Dispose(); }
                catch { /* a scheduler that already forgot the item is fine */ }
            }

            if (parked != null)
            {
                foreach (var decoder in parked)
                {
                    try { decoder.Dispose(); }
                    catch { /* teardown is best effort */ }
                }
            }

            _notifyTimer.Dispose();

            // Work items own FFmpeg contexts; give the running one a moment to unwind so the
            // decoders are closed by the time the editor window tears the pipeline down. The event
            // itself is deliberately not disposed: an item the scheduler starts anyway would touch
            // it and take the scheduler thread down with an ObjectDisposedException.
            _idle.Wait(DisposeDrainMs);
        }

        // ------------------------------------------------------------------------------- strips

        private sealed class Strip
        {
            public string Path;
            public int StreamIndex;
            public int ThumbHeight;

            public readonly SortedList<long, Entry> Thumbs = new SortedList<long, Entry>();
            public readonly HashSet<long> Unfillable = new HashSet<long>();

            /// <summary>Slots refinement has already decoded (or tried to) for the CURRENT
            /// viewport, cleared whenever it moves. Without it a viewport asking for more slots
            /// than the cache may hold never terminates: eviction drops a slot the refinement pass
            /// just filled, the pass picks it again, and the two spin against each other forever.</summary>
            public readonly HashSet<long> Attempted = new HashSet<long>();

            public long DurationTicks;
            public long ProgressTicks;
            public bool PassStarted;
            public bool PassDone;
            public string Error;

            public bool HasViewport;

            /// <summary>Aimed at least once and then cleared: nobody is drawing this strip, so its
            /// thumbnails go first when the cache is over budget. A strip that was simply never
            /// aimed stays neutral (distance 0) — its keyframe pass may be publishing before the
            /// first viewport lands.</summary>
            public bool ViewportRetired;

            /// <summary>The visible source spans, ascending and disjoint (see
            /// <see cref="NormalizeSpans"/>); empty unless <see cref="HasViewport"/>.</summary>
            public (long Start, long End)[] ViewSpans = Array.Empty<(long, long)>();

            public long ViewInterval = BaseIntervalTicks;

            /// <summary>A keyframe pass parked mid-stream because a lower band was waiting for the
            /// scheduler thread; the re-enqueued pass resumes it where the demux left off.</summary>
            public ThumbnailDecoder PassDecoder;

            public IDisposable PassHandle;
            public IDisposable RefineHandle;
            public CancellationTokenSource RefineCts;
            public bool RefineActive;

            public FilmstripSnapshot Snapshot;
        }

        private sealed class Entry
        {
            public byte[] Pixels;
            public int Width;
            public int Height;
            public bool Refined;
            public long Stamp;
        }

        private static string KeyOf(string path, int streamIndex, int thumbHeight) =>
            path + "|" + streamIndex.ToString() + "|" + thumbHeight.ToString();

        private Strip GetOrCreateLocked(string path, int streamIndex, int thumbHeightPx)
        {
            int height = Math.Clamp(thumbHeightPx, ThumbnailDecoder.MinThumbHeightPx, ThumbnailDecoder.MaxThumbHeightPx);
            string key = KeyOf(path, streamIndex, height);
            if (!_strips.TryGetValue(key, out var strip))
            {
                strip = new Strip { Path = path, StreamIndex = streamIndex, ThumbHeight = height };
                _strips[key] = strip;
            }
            return strip;
        }

        private FilmstripSnapshot SnapshotLocked(Strip strip)
        {
            if (strip.Snapshot != null)
                return strip.Snapshot;

            var thumbs = new FilmstripThumbnail[strip.Thumbs.Count];
            var keys = strip.Thumbs.Keys;
            var values = strip.Thumbs.Values;
            for (int i = 0; i < thumbs.Length; i++)
            {
                var e = values[i];
                thumbs[i] = new FilmstripThumbnail(keys[i], e.Pixels, e.Width, e.Height, e.Width * 4);
            }

            bool done = strip.PassDone || strip.Error != null;
            double progress = done ? 1
                : strip.DurationTicks > 0 ? Math.Clamp(strip.ProgressTicks / (double)strip.DurationTicks, 0, 1)
                : 0;

            strip.Snapshot = new FilmstripSnapshot(strip.ViewInterval, strip.DurationTicks, thumbs,
                done && !strip.RefineActive, progress, strip.Error);
            return strip.Snapshot;
        }

        // -------------------------------------------------------------------------- work items

        private void StartPass(Strip strip)
        {
            IDisposable handle = null;
            try
            {
                handle = _queue.Enqueue(KeyframePassPriority, ct => RunWork(() => RunPass(strip, ct)));
            }
            finally
            {
                bool cancel;
                lock (_lock)
                {
                    cancel = _disposed;
                    if (!cancel)
                        strip.PassHandle = handle;
                }
                if (cancel)
                    handle?.Dispose();
            }
        }

        private void StartRefine(Strip strip)
        {
            CancellationTokenSource cts;
            lock (_lock)
            {
                if (_disposed)
                    return;
                cts = strip.RefineCts;
            }

            IDisposable handle = null;
            try
            {
                handle = _queue.Enqueue(RefinePriority, ct => RunWork(() => RunRefine(strip, cts, ct)));
            }
            finally
            {
                bool cancel;
                lock (_lock)
                {
                    cancel = _disposed || strip.RefineCts != cts;
                    if (!cancel)
                        strip.RefineHandle = handle;
                }
                if (cancel)
                    handle?.Dispose();
            }
        }

        /// <summary>Runs one scheduler item, tracking it so <see cref="Dispose"/> can wait for the
        /// FFmpeg contexts it owns to be released.</summary>
        private void RunWork(Action work)
        {
            lock (_busyLock)
            {
                if (_busy++ == 0)
                    _idle.Reset();
            }
            try
            {
                work();
            }
            finally
            {
                lock (_busyLock)
                {
                    if (--_busy == 0)
                        _idle.Set();
                }
            }
        }

        /// <summary>Tier 1: every keyframe of the stream, start to end, at near demux speed. When
        /// a lower band (a waveform) is waiting for the shared thread, the pass parks its decoder
        /// on the strip and re-enqueues itself, so priority ordering holds even against an item
        /// that is minutes long — <c>PassDone</c> still only flips at the real end of the stream.</summary>
        private void RunPass(Strip strip, CancellationToken queueToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(queueToken, _cts.Token);
            var ct = linked.Token;
            if (ct.IsCancellationRequested)
                return;

            ThumbnailDecoder decoder = null;
            bool parked = false;
            try
            {
                lock (_lock)
                {
                    decoder = strip.PassDecoder;
                    strip.PassDecoder = null;
                }

                if (decoder == null)
                {
                    decoder = new ThumbnailDecoder(strip.Path, strip.StreamIndex, strip.ThumbHeight);
                    decoder.KeyframesOnly = true;
                }

                lock (_lock)
                {
                    strip.DurationTicks = decoder.DurationTicks;
                    strip.Snapshot = null;
                }

                while (!ct.IsCancellationRequested && decoder.DecodeNext(out long pts))
                {
                    Publish(strip, pts, decoder.CopyThumb(), decoder.ThumbWidth, decoder.ThumbHeight, refined: false);
                    lock (_lock)
                    {
                        if (pts > strip.ProgressTicks)
                            strip.ProgressTicks = pts;
                        strip.Snapshot = null;
                    }

                    // one keyframe is milliseconds of work, so this is a fine-grained yield point;
                    // the parked decoder carries the demux position across the requeue.
                    if (_queue.HasPendingBelow(KeyframePassPriority))
                    {
                        lock (_lock)
                        {
                            if (!_disposed)
                            {
                                strip.PassDecoder = decoder;
                                parked = true;
                            }
                        }

                        if (parked)
                        {
                            StartPass(strip);
                            return;
                        }
                    }
                }

                if (ct.IsCancellationRequested)
                    return;

                lock (_lock)
                {
                    strip.PassDone = true;
                    strip.Snapshot = null;
                }
            }
            catch (Exception ex)
            {
                SetError(strip, ex);
            }
            finally
            {
                if (!parked)
                    decoder?.Dispose();
                NotifyChanged();
            }
        }

        /// <summary>Tier 2: the grid the visible rows ask for, each span filled outward from its
        /// own middle. Re-reads the viewport between slots, so scrolling redirects the work in
        /// flight instead of finishing a strip nobody is looking at; a lower band waiting for the
        /// thread makes it bail between slots — the finally block re-enqueues it (still at
        /// <see cref="RefinePriority"/>), so it resumes exactly where it left off once the more
        /// urgent work has drained.</summary>
        private void RunRefine(Strip strip, CancellationTokenSource stripCts, CancellationToken queueToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                queueToken, stripCts?.Token ?? CancellationToken.None, _cts.Token);
            var ct = linked.Token;

            ThumbnailDecoder decoder = null;
            byte[] scratch = null;
            try
            {
                while (!ct.IsCancellationRequested && !_queue.HasPendingBelow(RefinePriority) &&
                       TryPickSlot(strip, out long slot))
                {
                    // claim the slot before decoding it: a slot the cache evicts again immediately
                    // must not be picked a second time for the same viewport (see Strip.Attempted).
                    lock (_lock)
                        strip.Attempted.Add(slot);

                    if (decoder == null)
                    {
                        decoder = new ThumbnailDecoder(strip.Path, strip.StreamIndex, strip.ThumbHeight);
                        scratch = new byte[decoder.ThumbByteCount];
                        lock (_lock)
                        {
                            if (strip.DurationTicks == 0)
                            {
                                strip.DurationTicks = decoder.DurationTicks;
                                strip.Snapshot = null;
                            }
                        }
                    }

                    if (TryDecodeAt(decoder, slot, scratch, ct, out byte[] pixels))
                    {
                        Publish(strip, slot, pixels, decoder.ThumbWidth, decoder.ThumbHeight, refined: true);
                    }
                    else if (!ct.IsCancellationRequested)
                    {
                        // Nothing decodable there (past the last frame, or a broken GOP): remember
                        // it, or the slot would be picked again forever.
                        lock (_lock)
                            strip.Unfillable.Add(slot);
                    }
                }
            }
            catch (Exception ex)
            {
                SetError(strip, ex);
            }
            finally
            {
                decoder?.Dispose();

                bool restart = false;
                lock (_lock)
                {
                    if (strip.RefineCts == stripCts)
                    {
                        strip.RefineActive = false;
                        strip.RefineHandle = null;
                        strip.RefineCts = null;
                        strip.Snapshot = null;
                        // The viewport may have moved while this item was unwinding.
                        if (!_disposed && !ct.IsCancellationRequested)
                            restart = EnsureRefineLocked(strip);
                    }
                }

                NotifyChanged();
                if (restart)
                    StartRefine(strip);
            }
        }

        /// <summary>Seeks to the keyframe at or before <paramref name="target"/> and decodes
        /// forward to the frame that covers it (at most one GOP). Copies exactly once: the running
        /// frame goes into the caller's scratch buffer, and only the winner is allocated.</summary>
        private static bool TryDecodeAt(ThumbnailDecoder decoder, long target, byte[] scratch,
            CancellationToken ct, out byte[] pixels)
        {
            pixels = null;
            decoder.Seek(target);

            bool havePrevious = false;
            for (int i = 0; i < MaxForwardFrames; i++)
            {
                if (ct.IsCancellationRequested)
                    return false;

                if (!decoder.DecodeNext(out long pts))
                    break;

                if (pts >= target)
                {
                    // The frame covering `target` is the previous one, unless this lands on it or
                    // the seek already put us past it (start of stream, keyframe-less prefix).
                    pixels = havePrevious && pts > target ? (byte[])scratch.Clone() : decoder.CopyThumb();
                    return true;
                }

                decoder.CopyThumbTo(scratch);
                havePrevious = true;
            }

            if (havePrevious)
            {
                // End of stream (or the GOP budget) before reaching the target: the last frame we
                // saw is the one on screen at that instant.
                pixels = (byte[])scratch.Clone();
                return true;
            }

            return false;
        }

        // ------------------------------------------------------------------------- slot picking

        /// <summary>True when a refinement item was (re)started for the strip; the caller enqueues
        /// it outside the lock.</summary>
        private bool EnsureRefineLocked(Strip strip)
        {
            if (_disposed || strip.RefineActive || !TryPickSlotLocked(strip, out _))
                return false;

            strip.RefineActive = true;
            strip.RefineCts = new CancellationTokenSource();
            return true;
        }

        private bool TryPickSlot(Strip strip, out long slot)
        {
            lock (_lock)
                return TryPickSlotLocked(strip, out slot);
        }

        /// <summary>The unfilled grid slot nearest its own span's middle — every visible span is
        /// enumerated (never the gaps between them), so a cut recording's kept segments refine
        /// while the deleted material stays untouched.</summary>
        private bool TryPickSlotLocked(Strip strip, out long slot)
        {
            slot = 0;
            if (_disposed || !strip.HasViewport || strip.Error != null)
                return false;

            long interval = Math.Max(BaseIntervalTicks, strip.ViewInterval);
            var spans = strip.ViewSpans;

            // the slot budget is shared across the spans in proportion to their length, so one
            // huge span cannot starve the short kept segment the user is also looking at.
            long totalLength = 0;
            for (int i = 0; i < spans.Length; i++)
            {
                long start = Math.Max(0, spans[i].Start);
                long end = spans[i].End;
                if (strip.DurationTicks > 0)
                    end = Math.Min(end, strip.DurationTicks);
                if (end >= start)
                    totalLength += end - start + interval; // + interval: a sub-slot span still owns a share
            }

            long bestDistance = Int64.MaxValue;
            bool found = false;
            for (int i = 0; i < spans.Length; i++)
            {
                long start = Math.Max(0, spans[i].Start);
                long end = spans[i].End;
                if (strip.DurationTicks > 0)
                    end = Math.Min(end, strip.DurationTicks);
                if (end < start)
                    continue;

                long first = start / interval * interval;
                long center = start + (end - start) / 2;

                // A very wide span (or a very fine grid) would otherwise queue thousands of slots
                // and evict itself; keep the window around what the eye is actually on.
                long budget = totalLength <= 0
                    ? MaxViewportSlots
                    : Math.Max(1, (long)((double)MaxViewportSlots * (end - start + interval) / totalLength));
                long slots = (end - first) / interval + 1;
                if (slots > budget)
                {
                    long centerSlot = center / interval * interval;
                    first = Math.Max(first, centerSlot - budget / 2 * interval);
                    end = Math.Min(end, first + (budget - 1) * interval);
                }

                for (long t = first; t <= end; t += interval)
                {
                    if (strip.Unfillable.Contains(t) || strip.Attempted.Contains(t) || IsFilledLocked(strip, t, interval))
                        continue;

                    long distance = Math.Abs(t - center);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        slot = t;
                        found = true;
                    }
                }
            }

            return found;
        }

        /// <summary>A slot counts as filled by any thumbnail within a quarter of the grid step —
        /// a keyframe that close is the same frame at filmstrip size, and re-decoding it would burn
        /// a seek for nothing.</summary>
        private static bool IsFilledLocked(Strip strip, long slotTicks, long interval)
        {
            var keys = strip.Thumbs.Keys;
            int n = keys.Count;
            if (n == 0)
                return false;

            long tolerance = interval / 4;
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (keys[mid] < slotTicks)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            if (Math.Abs(keys[lo] - slotTicks) <= tolerance)
                return true;
            return lo > 0 && Math.Abs(keys[lo - 1] - slotTicks) <= tolerance;
        }

        // ------------------------------------------------------------------------------ publish

        private void Publish(Strip strip, long ticks, byte[] pixels, int width, int height, bool refined)
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                if (strip.Thumbs.TryGetValue(ticks, out var existing))
                {
                    if (existing.Refined && !refined)
                        return; // never trade an exact grid frame for a nearby keyframe
                }
                else
                {
                    _count++;
                }

                strip.Thumbs[ticks] = new Entry
                {
                    Pixels = pixels,
                    Width = width,
                    Height = height,
                    Refined = refined,
                    Stamp = ++_stamp,
                };
                strip.Snapshot = null;
                EvictLocked();
            }

            NotifyChanged();
        }

        private void SetError(Strip strip, Exception ex)
        {
            lock (_lock)
            {
                strip.Error ??= ex.Message;
                strip.PassDone = true;
                strip.Snapshot = null;
            }
        }

        /// <summary>Trims the cache back under the cap, dropping the thumbnails furthest outside
        /// their strip's viewport first and, among equally distant ones, the oldest. A batch at a
        /// time so a long keyframe pass does not re-scan on every frame.</summary>
        private void EvictLocked()
        {
            if (_count <= _maxThumbnails)
                return;

            var candidates = new List<(long Distance, long Stamp, Strip Strip, long Key)>(_count);
            foreach (var strip in _strips.Values)
            {
                var keys = strip.Thumbs.Keys;
                var values = strip.Thumbs.Values;
                for (int i = 0; i < keys.Count; i++)
                    candidates.Add((DistanceToViewport(strip, keys[i]), values[i].Stamp, strip, keys[i]));
            }

            candidates.Sort((a, b) =>
            {
                int c = b.Distance.CompareTo(a.Distance);
                return c != 0 ? c : a.Stamp.CompareTo(b.Stamp);
            });

            int remove = Math.Min(candidates.Count, _count - Math.Max(1, _maxThumbnails - _evictBatch));
            for (int i = 0; i < remove; i++)
            {
                var c = candidates[i];
                if (c.Strip.Thumbs.Remove(c.Key))
                {
                    _count--;
                    c.Strip.Snapshot = null;
                }
            }
        }

        /// <summary>Distance to the nearest visible span — 0 only <i>inside</i> one, so thumbnails
        /// over the cut-out material between two kept segments lose ties against the pixels being
        /// drawn. A retired strip (nobody draws it) is infinitely far; one that was never aimed is
        /// neutral, since its keyframe pass may publish before the first viewport lands.</summary>
        private static long DistanceToViewport(Strip strip, long ticks)
        {
            if (!strip.HasViewport)
                return strip.ViewportRetired ? Int64.MaxValue : 0;

            long best = Int64.MaxValue;
            var spans = strip.ViewSpans;
            for (int i = 0; i < spans.Length; i++)
            {
                long distance = Math.Max(0, Math.Max(spans[i].Start - ticks, ticks - spans[i].End));
                if (distance < best)
                    best = distance;
                if (best == 0)
                    break;
            }
            return best;
        }

        // ------------------------------------------------------------------------------- notify

        private void NotifyChanged()
        {
            lock (_notifyLock)
            {
                if (_disposed || _notifyScheduled)
                    return;

                long now = Environment.TickCount64;
                long due = _lastNotifyMs == Int64.MinValue ? 0 : Math.Max(0, _lastNotifyMs + NotifyThrottleMs - now);
                _notifyScheduled = true;
                try
                {
                    _notifyTimer.Change(due, Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                    _notifyScheduled = false; // disposed underneath us; nothing left to announce
                }
            }
        }

        private void OnNotifyTimer(object state)
        {
            lock (_notifyLock)
            {
                _notifyScheduled = false;
                _lastNotifyMs = Environment.TickCount64;
                if (_disposed)
                    return;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
