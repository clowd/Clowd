using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Clowd.UI.Preview.Producers;
using Clowd.VideoSDK.Thumbs;

namespace Clowd.UI.Preview
{
    /// <summary>
    /// One coalesced batch of finished previews. The engine raises a single event for everything it
    /// installed in a drain rather than one per preview, because the listener is every realized tile
    /// on the page and a per-result event would be a quadratic broadcast during a scroll.
    /// </summary>
    public sealed class PreviewReadyEventArgs : EventArgs
    {
        public PreviewReadyEventArgs(IReadOnlyList<PreviewKey> keys)
        {
            Keys = keys ?? Array.Empty<PreviewKey>();
        }

        /// <summary>The keys installed in this batch, at most
        /// <see cref="SessionPreviewEngine.MaxDrainPerTick"/> of them.</summary>
        public IReadOnlyList<PreviewKey> Keys { get; }

        /// <summary>Whether this batch contains a key — the whole question a tile has. A linear
        /// scan of at most four entries, which is cheaper than any set would be.</summary>
        public bool Contains(in PreviewKey key)
        {
            for (int i = 0; i < Keys.Count; i++)
            {
                if (Keys[i].Equals(key))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// The preview engine: the one thing that decides what a session's tile shows, when the work to
    /// produce it happens, and which thread pays for it.
    ///
    /// <para>
    /// <b>The UI thread never does file I/O here.</b> Its entire involvement is three dictionary
    /// probes (hot cache, live jobs, negative cache), wrapping at most four finished buffers into
    /// bitmaps per dispatcher tick, and running a 250 ms timer. Everything that stats, decodes,
    /// composes or writes happens on <see cref="PreviewWorkQueue"/> (Lane A) or
    /// <see cref="ThumbWork.Shared"/> (Lane B).
    /// </para>
    ///
    /// <para>
    /// <b>Identity is the session directory plus a content stamp</b>, never a container and never a
    /// <c>SessionInfo</c> instance. The Recent page destroys and recreates every container whenever
    /// any property on any session changes, so anything keyed on a container would throw away
    /// perfectly good work several times a second; and a worker holding a session would fault the
    /// moment SessionManager disposed it. Producers only ever see the immutable
    /// <see cref="PreviewRequest"/>, which is also what structurally guarantees the engine can never
    /// write to a session and re-trigger itself.
    /// </para>
    ///
    /// <para>
    /// <b>The grace window is the single most important anti-thrash detail.</b> See
    /// <see cref="Release"/>.
    /// </para>
    /// </summary>
    public sealed class SessionPreviewEngine
    {
        /// <summary>
        /// How long a key survives with no subscribers before its work is abandoned. Deliberately
        /// the same 250 ms as the Recent page's own regroup debounce: a rebuild detaches every tile
        /// and reattaches it within that window, and the grace list is what turns that into a no-op
        /// instead of a cancel-and-restart of every visible preview.
        /// </summary>
        public const int GraceMs = 250;

        /// <summary>
        /// How long a source that failed to produce is left alone. A recording still being written,
        /// a render part-way through its output file and a GIF mid-encode all fail the same way and
        /// all keep failing for a while; without this, a visible row would retry the failing decode
        /// on every scroll tick that re-requested it.
        /// </summary>
        public const int NegativeTtlMs = 5 * 60 * 1000;

        /// <summary>
        /// Results wrapped into bitmaps per dispatcher tick. Each wrap is a <c>WriteableBitmap</c>
        /// allocation plus a ~130 KB copy — around 30 µs — so four bounds the engine's cost on the
        /// UI thread at roughly a tenth of a millisecond per frame no matter how much landed at
        /// once. Whatever is left over re-arms and lands on the next tick.
        /// </summary>
        public const int MaxDrainPerTick = 4;

        /// <summary>The cache sweep's Lane A band: below every tile band (40/50/60), so a sweep can
        /// never delay a preview the user is waiting for.</summary>
        public const int SweepBand = 100;

        /// <summary>How long after the engine first runs the sweep is scheduled. Long enough that
        /// it cannot land inside the app's cold start, which is the window the standing rule about
        /// disk scanning exists to protect.</summary>
        public const int SweepDelayMs = 30_000;

        /// <summary>
        /// How many times a Lane B item will step aside for an open editor before it runs anyway.
        /// The check exists because Lane B is one non-preemptive thread, so a composite that has
        /// started cannot be interrupted; stepping aside <i>before</i> starting is the only way an
        /// editor's timeline avoids starving behind recents work. The cap is what keeps that from
        /// becoming a livelock while a busy editor keeps its own bands full.
        /// </summary>
        public const int MaxLaneBDeferrals = 32;

        private static readonly Lazy<SessionPreviewEngine> LazyCurrent =
            new Lazy<SessionPreviewEngine>(() => new SessionPreviewEngine(),
                LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// The process-wide engine, constructed on the first tile's first request rather than at
        /// startup. The constructor touches no disk and starts no thread: it wires two dispatcher
        /// timers and nothing else.
        /// </summary>
        public static SessionPreviewEngine Current => LazyCurrent.Value;

        private readonly PreviewMemoryCache _memory = new PreviewMemoryCache();

        /// <summary>In-flight work by key. An entry here means "somebody is producing this"; a
        /// finished preview lives in <see cref="_memory"/> and its job is gone.</summary>
        private readonly Dictionary<PreviewKey, Job> _jobs = new Dictionary<PreviewKey, Job>();

        /// <summary>Keys whose subscribers have all left, with the tick they may be reaped at.</summary>
        private readonly Dictionary<PreviewKey, long> _grace = new Dictionary<PreviewKey, long>();

        /// <summary>Keys whose source could not be produced, with the tick the ban expires at.</summary>
        private readonly Dictionary<PreviewKey, long> _negative = new Dictionary<PreviewKey, long>();

        /// <summary>Finished pixels waiting to be wrapped on the UI thread. Written by both lanes,
        /// read only by <see cref="Drain"/>.</summary>
        private readonly ConcurrentQueue<Completion> _completions = new ConcurrentQueue<Completion>();

        private readonly DispatcherTimer _graceTimer;
        private readonly DispatcherTimer _sweepTimer;

        private int _drainArmed;

        private SessionPreviewEngine()
        {
            _graceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(GraceMs),
            };
            _graceTimer.Tick += OnGraceTick;

            // One-shot. Scheduling the sweep from a timer rather than from the constructor keeps
            // the engine's construction free of even an enqueue: nothing starts a Lane A thread
            // until a tile actually asks for a preview.
            _sweepTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(SweepDelayMs),
            };
            _sweepTimer.Tick += OnSweepTick;
            _sweepTimer.Start();
        }

        /// <summary>Raised once per drain with every key installed in that batch.</summary>
        public event EventHandler<PreviewReadyEventArgs> PreviewReady;

        /// <summary>Live entries in the hot cache. Diagnostics only.</summary>
        public int HotCount => _memory.Count;

        /// <summary>Keys currently being produced. Diagnostics only.</summary>
        public int PendingJobCount => _jobs.Count;

        /// <summary>Keys currently banned from re-producing. Diagnostics only — but the one number
        /// worth looking at when a list is drawing icons where it should be drawing posters.</summary>
        public int NegativeCount => _negative.Count;

        /// <summary>
        /// UI thread. The tile's per-render question: is this preview already decoded? One
        /// dictionary probe and no I/O whatsoever, which is what lets a tile call it from layout
        /// and from <c>Render</c>.
        /// </summary>
        public bool TryGetHot(in PreviewKey key, out Bitmap bmp, out PreviewKind kind)
        {
            VerifyUiThread();
            return _memory.TryGet(key, out bmp, out kind);
        }

        /// <summary>
        /// UI thread. Registers <paramref name="subscriber"/>'s interest in a key and starts (or
        /// re-prioritizes) the work behind it. Dedupes by key: a hundred rebuilt containers asking
        /// for the same session share one job, and a key that already has a decoded preview does no
        /// work at all.
        /// </summary>
        /// <param name="priority">The band the work runs in. A lower band than the job already has
        /// promotes it in place; a higher one is ignored, because a tile that scrolled out of view
        /// has not become less worth finishing.</param>
        public void Request(in PreviewKey key, PreviewRequest request, PreviewPriority priority, object subscriber)
        {
            VerifyUiThread();

            if (request == null)
                return;

            // a key claimed again is a key that is no longer abandoned, whatever the reaper thought
            _grace.Remove(key);

            // already decoded: the tile will get it from TryGetHot, and re-producing it would only
            // evict something else to install an identical picture.
            if (_memory.TryGet(key, out _, out _))
                return;

            if (_jobs.TryGetValue(key, out var job))
            {
                if (subscriber != null)
                    job.Subscribers.Add(subscriber);

                int band = (int)priority;
                if (band < job.Band)
                {
                    job.Band = band;
                    PreviewWorkQueue.Shared.Promote(job.KeyBox, band);
                }

                return;
            }

            job = new Job(key, request, (int)priority);
            if (subscriber != null)
                job.Subscribers.Add(subscriber);

            _jobs.Add(key, job);

            // A negatively-cached key still gets a tile: the expensive producer is skipped and the
            // file-type icon is drawn instead. Read here, on the UI thread, so the worker needs no
            // shared state of its own — the ban is written back in Drain, also on the UI thread.
            bool skipExpensive = IsNegative(key);
            var token = job.Cancellation.Token;

            job.Handle = PreviewWorkQueue.Shared.Enqueue(job.Band, job.KeyBox,
                _ => RunLaneA(job, skipExpensive, token));

            // The queue evicts its oldest pending item when it overflows, which is the one case
            // where a job's work disappears without ever running. Retiring the job then lets the
            // next request rebuild it rather than dedupe against a job nothing is producing.
            job.Handle.Token.Register(() => OnLaneAAbandoned(job));
        }

        /// <summary>
        /// UI thread. Drops a subscriber. Reaching zero does <b>not</b> cancel: the key goes on a
        /// grace list and is only abandoned if nothing has claimed it
        /// <see cref="GraceMs"/> later.
        /// </summary>
        /// <remarks>
        /// This is the detail the whole design hangs on. Any property change on any session sends a
        /// Reset through the page's observable collection, which rebuilds every group and therefore
        /// destroys and recreates every container on the page — several times a second while a
        /// recording's duration is ticking up. Every one of those rebuilds releases every visible
        /// key and re-requests it a few milliseconds later. Cancelling on zero would mean each of
        /// them tore down and restarted every visible preview, including FFmpeg opens already
        /// half-way through, and the list would never finish drawing at all.
        /// </remarks>
        public void Release(in PreviewKey key, object subscriber)
        {
            VerifyUiThread();

            if (!_jobs.TryGetValue(key, out var job))
                return;

            if (subscriber != null)
                job.Subscribers.Remove(subscriber);

            if (job.Subscribers.Count > 0)
                return;

            _grace[key] = Environment.TickCount64 + GraceMs;
            if (!_graceTimer.IsEnabled)
                _graceTimer.Start();
        }

        /// <summary>
        /// UI thread. Stops Lane A from dequeuing for a window — the fling gate. Requests keep
        /// arriving and keep being ordered while the list moves; they are simply not produced,
        /// because every row about to be requested is one the user is scrolling past. Hot-cache
        /// hits are unaffected: they never reach a queue.
        /// </summary>
        public void SuspendFor(TimeSpan window)
        {
            if (window <= TimeSpan.Zero)
                return;

            PreviewWorkQueue.Shared.SuspendUntil(Environment.TickCount64 + (long)window.TotalMilliseconds);
        }

        /// <summary>
        /// UI thread. Forgets everything about a session directory — called by
        /// <c>SessionManager.DeleteSession</c> before the session is disposed, so a directory reused
        /// under the same name can never serve the deleted session's picture.
        /// </summary>
        /// <remarks>
        /// No disk work is needed or done. The disk cache is keyed by the source file's own path,
        /// modification time and length, so a new file at a recycled path hashes to a different
        /// token and is simply a miss; the superseded entries are unreferenced files that the sweep
        /// reclaims on its own schedule.
        /// </remarks>
        public void PurgeSession(string sessionDir)
        {
            VerifyUiThread();

            if (String.IsNullOrEmpty(sessionDir))
                return;

            var target = PreviewKey.NormalizeDir(sessionDir);
            if (String.IsNullOrEmpty(target))
                return;

            _memory.PurgeSessionDir(target);

            List<PreviewKey> doomed = null;
            foreach (var pair in _jobs)
            {
                if (String.Equals(pair.Key.SessionDir, target, StringComparison.Ordinal))
                    (doomed ??= new List<PreviewKey>()).Add(pair.Key);
            }

            if (doomed != null)
            {
                foreach (var key in doomed)
                    Abandon(key);
            }

            List<PreviewKey> banned = null;
            foreach (var pair in _negative)
            {
                if (String.Equals(pair.Key.SessionDir, target, StringComparison.Ordinal))
                    (banned ??= new List<PreviewKey>()).Add(pair.Key);
            }

            if (banned != null)
            {
                foreach (var key in banned)
                    _negative.Remove(key);
            }
        }

        private void OnGraceTick(object sender, EventArgs e)
        {
            long now = Environment.TickCount64;

            List<PreviewKey> expired = null;
            foreach (var pair in _grace)
            {
                if (pair.Value <= now)
                    (expired ??= new List<PreviewKey>()).Add(pair.Key);
            }

            if (expired != null)
            {
                foreach (var key in expired)
                {
                    _grace.Remove(key);

                    // it may have been claimed again since — Request removes the grace entry, but a
                    // job that regained a subscriber through some other path is checked here too.
                    if (_jobs.TryGetValue(key, out var job) && job.Subscribers.Count == 0)
                        Abandon(key);
                }
            }

            if (_grace.Count == 0)
                _graceTimer.Stop();

            PruneNegative(now);
        }

        private void OnSweepTick(object sender, EventArgs e)
        {
            _sweepTimer.Stop();

            // Lowest band, so it queues behind every tile that is waiting; and its own key, so it
            // can never be confused with (or promoted alongside) a preview.
            PreviewWorkQueue.Shared.Enqueue(SweepBand, null, ct =>
            {
                try
                {
                    PreviewDiskCache.EnsureRoot();
                    PreviewDiskCache.Sweep(ct);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("SessionPreviewEngine: cache sweep failed — " + ex.Message);
                }
            });
        }

        /// <summary>UI thread. Cancels a job's work in both lanes and retires it, so the next
        /// request starts fresh.</summary>
        private void Abandon(in PreviewKey key)
        {
            if (!_jobs.Remove(key, out var job))
                return;

            _grace.Remove(key);

            // one authority for both lanes: the Lane A handle unqueues the item, and the job's own
            // token is what a Lane B item (which the engine holds no handle for) checks.
            job.Cancellation.Cancel();
            job.Handle?.Dispose();
        }

        /// <summary>
        /// Any thread. Fired when a Lane A item's token is cancelled, which happens both when the
        /// engine abandons a job and when the queue evicts it under the 512-item cap. Only the
        /// second case needs anything: the job is still in the dictionary with no work behind it.
        /// </summary>
        private void OnLaneAAbandoned(Job job)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_jobs.TryGetValue(job.Key, out var live) && ReferenceEquals(live, job)
                    && !job.HandedOff && !job.Completed)
                {
                    _jobs.Remove(job.Key);
                    _grace.Remove(job.Key);
                }
            }, DispatcherPriority.Background);
        }

        private bool IsNegative(in PreviewKey key)
        {
            if (!_negative.TryGetValue(key, out long deadline))
                return false;

            if (deadline > Environment.TickCount64)
                return true;

            _negative.Remove(key);
            return false;
        }

        /// <summary>Drops expired bans, and — if the table has grown past anything a real session
        /// list would produce — the whole thing, since a ban is only ever an optimization.</summary>
        private void PruneNegative(long now)
        {
            if (_negative.Count == 0)
                return;

            if (_negative.Count > 1024)
            {
                _negative.Clear();
                return;
            }

            List<PreviewKey> expired = null;
            foreach (var pair in _negative)
            {
                if (pair.Value <= now)
                    (expired ??= new List<PreviewKey>()).Add(pair.Key);
            }

            if (expired == null)
                return;

            foreach (var key in expired)
                _negative.Remove(key);
        }

        // ---- worker side ----------------------------------------------------------------------

        /// <summary>
        /// Lane A. Resolves what the session's content actually is, serves the disk cache when it
        /// can, produces the cheap kinds here, and hands the expensive kinds to Lane B.
        /// </summary>
        private void RunLaneA(Job job, bool skipExpensive, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return;

            // The first Lane A item is what pays for resolving the cache root, which creates
            // directories. Forcing it here means no later call can accidentally be the first one
            // and pay for it somewhere the cost is not allowed.
            PreviewDiskCache.EnsureRoot();

            var source = SessionContentResolver.Resolve(job.Request);
            if (ct.IsCancellationRequested)
                return;

            bool expensive = source.Kind is PreviewSourceKind.Video or PreviewSourceKind.Project;

            if (expensive && !skipExpensive)
            {
                // The disk probe stays on Lane A deliberately: a cached poster is one file open and
                // a small PNG decode, and making it wait behind the single Lane B thread would put
                // it behind whatever composite is running there.
                var token = PreviewDiskCache.TokenFor(source);
                if (token != null && PreviewDiskCache.TryLoad(token, out var cached))
                {
                    Complete(job, cached, degraded: false);
                    return;
                }

                if (ct.IsCancellationRequested)
                    return;

                job.HandedOff = true;
                int band = job.Band;
                ThumbWork.Shared.Enqueue(band, _ => RunLaneB(job, source, token, band, 0, ct));
                return;
            }

            PreviewPixels pixels = skipExpensive ? null : ProduceCheap(source, job.Request, ct);
            if (ct.IsCancellationRequested)
                return;

            bool degraded = pixels == null && !skipExpensive && source.Kind != PreviewSourceKind.Icon
                && source.Kind != PreviewSourceKind.None;

            pixels ??= FileIconPreviewProducer.Produce(source, job.Request, ct);
            if (pixels == null || ct.IsCancellationRequested)
            {
                // A null from the terminal producer means Skia could not give us a raster surface
                // at all — a process-level condition, not a property of this session — so it is
                // deliberately not banned. Banning it would leave the row permanently iconless.
                return;
            }

            Complete(job, pixels, degraded);
        }

        /// <summary>
        /// Lane B. Everything that opens FFmpeg or runs the composition stack, on the one thread
        /// those are affine to. The whole create/use/dispose cycle of a composite lives inside a
        /// single item, which is what satisfies that affinity for free.
        /// </summary>
        private void RunLaneB(Job job, PreviewSource source, string diskToken, int band, int deferrals,
            CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return;

            // Step aside for an open editor's timeline rather than making it wait behind a decode
            // that cannot be interrupted once it starts. Re-enqueuing rather than giving up matters:
            // abandoning here would look to the engine like a producer failure and would ban the
            // key for five minutes over what is only a scheduling conflict.
            if (deferrals < MaxLaneBDeferrals
                && ThumbWork.Shared.HasPendingBelow(ThumbWork.RecentsVisiblePriority))
            {
                ThumbWork.Shared.Enqueue(band, _ => RunLaneB(job, source, diskToken, band, deferrals + 1, ct));
                return;
            }

            var pixels = source.Kind == PreviewSourceKind.Project
                ? VideoProjectPreviewProducer.Produce(source, job.Request, ct)
                : VideoPosterProducer.Produce(source, job.Request, ct);

            if (ct.IsCancellationRequested)
                return;

            if (pixels != null)
            {
                if (diskToken != null)
                    PreviewDiskCache.Store(diskToken, pixels);

                Complete(job, pixels, degraded: false);
                return;
            }

            // A growing mp4, a partial render, a composition whose media has gone missing: fall
            // through to the file-type icon and ban the key for a while so a visible row does not
            // pay for the same failed open on every scroll tick.
            var icon = FileIconPreviewProducer.Produce(source, job.Request, ct);
            if (icon != null && !ct.IsCancellationRequested)
                Complete(job, icon, degraded: true);
        }

        private static PreviewPixels ProduceCheap(in PreviewSource source, PreviewRequest request,
            CancellationToken ct)
        {
            return source.Kind switch
            {
                PreviewSourceKind.Image => ImagePreviewProducer.Produce(source, request, ct),
                PreviewSourceKind.Text => TextExcerptProducer.Produce(source, request, ct),
                _ => null,
            };
        }

        /// <summary>Any thread. Hands a finished preview to the UI thread and makes sure exactly one
        /// drain is armed for it.</summary>
        private void Complete(Job job, PreviewPixels pixels, bool degraded)
        {
            job.Completed = true;
            _completions.Enqueue(new Completion(job.Key, pixels, degraded));

            if (Interlocked.CompareExchange(ref _drainArmed, 1, 0) == 0)
                Dispatcher.UIThread.Post(Drain, DispatcherPriority.Background);
        }

        /// <summary>
        /// UI thread. Wraps up to <see cref="MaxDrainPerTick"/> finished buffers into bitmaps,
        /// installs them, and raises one event for the batch. Avalonia bitmap objects are
        /// constructed here and nowhere else — this is the only place in the engine that is allowed
        /// to make one.
        /// </summary>
        private void Drain()
        {
            List<PreviewKey> ready = null;

            for (int i = 0; i < MaxDrainPerTick && _completions.TryDequeue(out var completion); i++)
            {
                _jobs.Remove(completion.Key);
                _grace.Remove(completion.Key);

                if (completion.Degraded)
                    _negative[completion.Key] = Environment.TickCount64 + NegativeTtlMs;
                else
                    _negative.Remove(completion.Key);

                var bitmap = ToBitmap(completion.Pixels);
                if (bitmap == null)
                    continue;

                // Installed whether or not anyone is still watching: the work is already paid for,
                // and a row the user scrolls back to should not have to pay for it a second time.
                _memory.Set(completion.Key, bitmap, completion.Pixels.Kind, completion.Pixels.Bgra.LongLength);
                (ready ??= new List<PreviewKey>(MaxDrainPerTick)).Add(completion.Key);
            }

            // Cleared after draining and re-checked after clearing: a completion pushed in between
            // fails to arm a drain of its own, and this is what catches it.
            Volatile.Write(ref _drainArmed, 0);
            if (!_completions.IsEmpty && Interlocked.CompareExchange(ref _drainArmed, 1, 0) == 0)
                Dispatcher.UIThread.Post(Drain, DispatcherPriority.Background);

            if (ready != null)
                PreviewReady?.Invoke(this, new PreviewReadyEventArgs(ready));
        }

        /// <summary>The verified <c>TimelinePreviewProvider.ToBitmap</c> idiom. Unpremultiplied,
        /// because that is what every producer hands over — an image preview keeps its real alpha so
        /// the tile's checkerboard shows through it.</summary>
        private static Bitmap ToBitmap(PreviewPixels pixels)
        {
            if (pixels == null || pixels.Bgra == null || pixels.Width <= 0 || pixels.Height <= 0)
                return null;

            int rowBytes = pixels.Width * 4;
            if ((long)pixels.Stride * pixels.Height > pixels.Bgra.LongLength)
                return null;

            try
            {
                var bitmap = new WriteableBitmap(new PixelSize(pixels.Width, pixels.Height),
                    new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);

                using (var buffer = bitmap.Lock())
                {
                    for (int y = 0; y < pixels.Height; y++)
                    {
                        Marshal.Copy(pixels.Bgra, y * pixels.Stride,
                            IntPtr.Add(buffer.Address, y * buffer.RowBytes), rowBytes);
                    }
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SessionPreviewEngine: could not wrap a preview — " + ex.Message);
                return null;
            }
        }

        [Conditional("DEBUG")]
        private static void VerifyUiThread() => Dispatcher.UIThread.VerifyAccess();

        /// <summary>
        /// One key's in-flight work. Created, mutated and retired on the UI thread only, with two
        /// exceptions that are written by a worker and read by the drain:
        /// <see cref="HandedOff"/> and <see cref="Completed"/>, both of which are one-way latches
        /// whose worst-case stale read costs a redundant dictionary probe.
        /// </summary>
        private sealed class Job
        {
            internal Job(PreviewKey key, PreviewRequest request, int band)
            {
                Key = key;
                KeyBox = key;
                Request = request;
                Band = band;
            }

            internal readonly PreviewKey Key;

            /// <summary>The same key, boxed once, so the queue's key dictionary and every
            /// <c>Promote</c> for this job share one allocation.</summary>
            internal readonly object KeyBox;

            internal readonly PreviewRequest Request;

            /// <summary>
            /// The job's cancellation authority, covering both lanes. Never disposed: it carries no
            /// registrations of its own beyond the one the queue handle adds, and disposing it while
            /// a worker still holds its token is the kind of race that buys nothing.
            /// </summary>
            internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();

            /// <summary>Subscribers by reference — a tile is not value-comparable and two tiles
            /// showing the same session are two distinct claims on the key. Refcounting them as a
            /// set rather than an int is what makes a double Request from one tile idempotent: a
            /// tile re-requests whenever its band changes, and an int would drift upward until the
            /// key could never be released.</summary>
            internal readonly HashSet<object> Subscribers = new HashSet<object>(ByReference.Instance);

            internal int Band;

            internal PreviewWorkHandle Handle;

            /// <summary>Set once the work has moved to Lane B, where the engine holds no handle.
            /// Distinguishes "the Lane A item is gone because it finished its part" from "the Lane A
            /// item is gone because the queue evicted it".</summary>
            internal volatile bool HandedOff;

            internal volatile bool Completed;
        }

        /// <summary>
        /// Identity comparison for the subscriber set. Hand-rolled because
        /// <c>System.Collections.ReferenceEqualityComparer</c> is not visible from this project's
        /// reference set; the type is four lines and has no behaviour worth sharing.
        /// </summary>
        private sealed class ByReference : IEqualityComparer<object>
        {
            internal static readonly ByReference Instance = new ByReference();

            public new bool Equals(object x, object y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private readonly struct Completion
        {
            internal Completion(PreviewKey key, PreviewPixels pixels, bool degraded)
            {
                Key = key;
                Pixels = pixels;
                Degraded = degraded;
            }

            internal readonly PreviewKey Key;
            internal readonly PreviewPixels Pixels;

            /// <summary>The real producer failed and this is the file-type icon standing in for it.
            /// Recorded as a ban when it lands, on the UI thread, so no shared state is needed.</summary>
            internal readonly bool Degraded;
        }
    }
}
