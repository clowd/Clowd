using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Clowd.UI.Preview
{
    /// <summary>
    /// A queued (or running) Lane A item. Disposing it unqueues the item if it has not started and
    /// cancels its token if it has, which is the whole contract the engine needs: a preview whose
    /// row went away stops costing something, whichever of those two states it happened to be in.
    /// </summary>
    /// <remarks>
    /// Every mutable field here is guarded by the owning queue's lock rather than one of its own.
    /// That is deliberate: settling an item and updating the queue's pending count have to happen
    /// together or the 512-item cap drifts, and two locks taken in two orders is how that kind of
    /// bookkeeping acquires a deadlock. The one thing that happens outside the lock is
    /// <see cref="CancellationTokenSource.Cancel"/>, which runs registered callbacks on the calling
    /// thread and must never do so underneath a lock the callback might want.
    /// </remarks>
    public sealed class PreviewWorkHandle : IDisposable
    {
        private readonly PreviewWorkQueue _owner;
        private readonly Action<CancellationToken> _work;

        /// <summary>Minted by the queue, never disposed. It holds no registrations and no wait
        /// handle, so letting the GC take it costs nothing and cannot race with a
        /// <see cref="Cancel"/> from another thread — the <c>ThumbWorkHandle</c> convention.</summary>
        private readonly CancellationTokenSource _source = new CancellationTokenSource();

        internal PreviewWorkHandle(PreviewWorkQueue owner, object key, int band, long sequence,
            Action<CancellationToken> work)
        {
            _owner = owner;
            _work = work;
            Key = key;
            Band = band;
            Sequence = sequence;
        }

        /// <summary>The dedupe/promote identity this item was enqueued under, or null.</summary>
        internal object Key { get; }

        /// <summary>Arrival order, monotonic across the whole queue. Negated in the priority tuple
        /// so ordering is LIFO within a band, and used unnegated to pick the eviction victim when
        /// the cap is hit.</summary>
        internal long Sequence { get; }

        /// <summary>The item's current band. <see cref="PreviewWorkQueue.Promote"/> lowers it and
        /// pushes a second copy into the priority queue; the copy whose band no longer matches this
        /// field is the stale one and is skipped at dequeue.</summary>
        internal int Band { get; set; }

        internal bool Started { get; set; }

        /// <summary>Ran, threw, or was unqueued — in every case it will never run (again).</summary>
        internal bool Settled { get; set; }

        internal CancellationToken Token => _source.Token;

        /// <summary>True when the item never ran because it was dropped first — by its owner, by
        /// the 512-item cap, or by a re-enqueue under the same key.</summary>
        public bool IsCanceled { get; internal set; }

        /// <summary>True once the item has run, thrown, or been unqueued.</summary>
        public bool IsFinished => _owner.IsSettled(this);

        /// <summary>What the work threw, if anything. A throwing item is recorded here and dropped;
        /// it never takes a worker thread down with it.</summary>
        public Exception Error { get; private set; }

        /// <summary>Abandons the item: unqueues it if it has not started, cancels its token if it
        /// has. Idempotent, and safe from any thread.</summary>
        public void Cancel()
        {
            _owner.TryAbandon(this);

            // outside the queue lock, always: Cancel runs continuations inline.
            try
            {
                _source.Cancel();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("PreviewWorkQueue: cancellation callback threw — " + ex.Message);
            }
        }

        public void Dispose() => Cancel();

        /// <summary>Worker thread only, and only for an item the queue has already marked
        /// started.</summary>
        internal void Run()
        {
            Exception error = null;
            try
            {
                if (!_source.IsCancellationRequested)
                    _work(_source.Token);
            }
            catch (OperationCanceledException)
            {
                // the token fired mid-work: an ordinary stop, not a fault
            }
            catch (Exception ex)
            {
                error = ex;
                Debug.WriteLine("PreviewWorkQueue: work item threw — " + ex);
            }

            Error = error;
            _owner.Finish(this);
        }
    }

    /// <summary>
    /// Lane A: the preview engine's own pool for everything that is cheap but still I/O — statting
    /// a session's content, reading and writing the disk cache, decoding an image, rasterizing an
    /// icon, typesetting a text excerpt, and the once-per-process cache sweep. Anything that opens
    /// FFmpeg or runs <c>FrameComposer</c> goes to Lane B (<c>ThumbWork.Shared</c>) instead, where
    /// a single thread gives the composition stack the thread affinity it requires.
    ///
    /// <para>
    /// <b>Deliberately not the ThreadPool.</b> Two reasons, both of which show up exactly when the
    /// list is being flung. The pool grows on demand, so a thousand queued previews become a
    /// thousand concurrent decodes fighting over the same disk instead of two orderly ones; and it
    /// is the same pool Avalonia runs its own async work on, so preview decoding would compete with
    /// the layout and input work that has to stay ahead of the user. Two owned threads at
    /// <see cref="ThreadPriority.BelowNormal"/> cannot do either.
    /// </para>
    ///
    /// <para>
    /// <b>LIFO within a band.</b> Ordering is <c>(Band, -Sequence)</c>, so the newest request in the
    /// most urgent band runs first. During a fling across thousands of rows the newest requests are
    /// where the user actually landed; strict FIFO would spend the whole queue drawing rows they
    /// flew past before it ever reached the screen they are looking at. Across bands the ordering is
    /// still strictly by urgency, so a visible row always beats a buffered one no matter when it
    /// arrived.
    /// </para>
    ///
    /// <para>
    /// The threads start on the first enqueue and retire themselves after
    /// <see cref="IdleTimeoutMs"/> with nothing pending, so <see cref="Shared"/> costs nothing
    /// between scrolls — and, more to the point, costs nothing at app startup, where the engine is
    /// not even constructed yet.
    /// </para>
    /// </summary>
    public sealed class PreviewWorkQueue
    {
        /// <summary>Two, not one and not four. One would serialize a slow decode in front of every
        /// icon behind it; four would have all of them contending for the same disk and would make
        /// the BelowNormal priority meaningless against a foreground app. Two lets a stalled item
        /// hide behind a running one without ever becoming a fan-out.</summary>
        public const int WorkerCount = 2;

        /// <summary>Longer than the editor's own 10 s, because scrolling a list is bursty in a way
        /// that scrubbing a timeline is not: a user reading a list of sessions comes back.</summary>
        public const int IdleTimeoutMs = 30_000;

        /// <summary>Hard cap on queued items. A fling across a very long list can request faster
        /// than two threads retire work, and an unbounded queue would keep every one of those
        /// requests alive long after the rows they belong to were recycled.</summary>
        public const int MaxQueued = 512;

        private static readonly Lazy<PreviewWorkQueue> LazyShared =
            new Lazy<PreviewWorkQueue>(() => new PreviewWorkQueue(), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>The process-wide Lane A queue. Never disposed — it owns no unmanaged state and
        /// its threads are background ones that retire when idle.</summary>
        public static PreviewWorkQueue Shared => LazyShared.Value;

        private readonly object _gate = new object();

        private readonly PriorityQueue<PreviewWorkHandle, (int Band, long NegSequence)> _queue =
            new PriorityQueue<PreviewWorkHandle, (int Band, long NegSequence)>();

        /// <summary>Live items by enqueue key, for <see cref="Promote"/>. A boxed key is fine: the
        /// engine's <see cref="PreviewKey"/> is a record struct, so its boxed equality is
        /// structural.</summary>
        private readonly Dictionary<object, PreviewWorkHandle> _byKey = new Dictionary<object, PreviewWorkHandle>();

        /// <summary>The same items in arrival order. The priority queue cannot answer "which is
        /// oldest" — its head is the most urgent, which under LIFO ordering is close to the
        /// youngest — so eviction reads from here instead, in amortized constant time.</summary>
        private readonly Queue<PreviewWorkHandle> _arrival = new Queue<PreviewWorkHandle>();

        private readonly Thread[] _threads = new Thread[WorkerCount];

        private long _sequence;
        private int _pending;
        private int _liveThreads;
        private long _suspendUntilTicks;

        /// <summary>Items waiting to start. Diagnostics only.</summary>
        public int PendingCount
        {
            get
            {
                lock (_gate)
                    return _pending;
            }
        }

        /// <summary>Worker threads currently alive. Diagnostics only.</summary>
        public int WorkerThreadCount
        {
            get
            {
                lock (_gate)
                    return _liveThreads;
            }
        }

        /// <summary>
        /// Queues <paramref name="work"/> in <paramref name="band"/> (lower runs first). The
        /// returned handle unqueues the item while it is still pending and cancels its token once
        /// it is running.
        /// </summary>
        /// <param name="key">Identity for <see cref="Promote"/>, or null for an item that will
        /// never be repositioned (the cache sweep). Re-enqueuing a live key abandons the previous
        /// item — the engine dedupes by key and never does this, but a queue that silently kept two
        /// items under one identity would make <see cref="Promote"/> a coin flip.</param>
        public PreviewWorkHandle Enqueue(int band, object key, Action<CancellationToken> work)
        {
            ArgumentNullException.ThrowIfNull(work);

            PreviewWorkHandle displaced = null;
            PreviewWorkHandle evicted = null;
            PreviewWorkHandle handle;

            lock (_gate)
            {
                if (key != null && _byKey.TryGetValue(key, out displaced))
                    AbandonLocked(displaced);

                handle = new PreviewWorkHandle(this, key, band, _sequence++, work);
                _queue.Enqueue(handle, (band, -handle.Sequence));
                _arrival.Enqueue(handle);
                _pending++;

                if (key != null)
                    _byKey[key] = handle;

                // settled items at the front of the arrival queue are pure bookkeeping; dropping
                // them here keeps it from growing without bound in a session that never overflows.
                while (_arrival.Count > 0 && _arrival.Peek().Settled)
                    _arrival.Dequeue();

                if (_pending > MaxQueued)
                    evicted = EvictOldestLocked();

                EnsureThreadsLocked();
                Monitor.Pulse(_gate);
            }

            // both of these cancel a token, which must not happen under the lock
            displaced?.Cancel();
            evicted?.Cancel();
            return handle;
        }

        /// <summary>
        /// Moves a live item into a more urgent band in place. Raising an item's band is ignored:
        /// a tile that scrolled from visible to buffered has not become less worth finishing, and
        /// demoting it would only make the queue churn while the user scrolls back and forth.
        /// </summary>
        public void Promote(object key, int band)
        {
            if (key == null)
                return;

            lock (_gate)
            {
                if (!_byKey.TryGetValue(key, out var handle) || handle.Settled || handle.Started)
                    return;

                if (band >= handle.Band)
                    return;

                // PriorityQueue has no reprioritize, so the item goes in a second time at its new
                // band and the old copy is skipped at dequeue by the band mismatch. Sequence is
                // kept, so a promoted item does not jump the LIFO order of its new band.
                handle.Band = band;
                _queue.Enqueue(handle, (band, -handle.Sequence));
                Monitor.Pulse(_gate);
            }
        }

        /// <summary>
        /// Pauses dequeuing until <paramref name="tickCount"/> (an
        /// <see cref="Environment.TickCount64"/> value). The fling gate: while the list is moving,
        /// requests keep arriving and keep being ordered, but nothing is produced — every row about
        /// to be requested is one the user is scrolling past. Memory-cache hits are unaffected,
        /// because they never reach this queue at all. A later call can only extend the window,
        /// never shorten one already in effect.
        /// </summary>
        public void SuspendUntil(long tickCount)
        {
            lock (_gate)
            {
                if (tickCount > _suspendUntilTicks)
                    _suspendUntilTicks = tickCount;

                // waiters recompute how long they have left to sleep
                Monitor.PulseAll(_gate);
            }
        }

        internal bool IsSettled(PreviewWorkHandle handle)
        {
            lock (_gate)
                return handle.Settled;
        }

        /// <summary>Marks a still-pending item dropped. Returns true when this call is what dropped
        /// it — an item that has already started, or already been dropped, is left alone.</summary>
        internal bool TryAbandon(PreviewWorkHandle handle)
        {
            lock (_gate)
            {
                if (handle.Settled || handle.Started)
                    return false;

                AbandonLocked(handle);
                return true;
            }
        }

        /// <summary>Called by an item once its delegate has returned. The pending count was already
        /// decremented when the item started, so this only retires its identity.</summary>
        internal void Finish(PreviewWorkHandle handle)
        {
            lock (_gate)
            {
                handle.Settled = true;
                ForgetKeyLocked(handle);
            }
        }

        private void AbandonLocked(PreviewWorkHandle handle)
        {
            handle.Settled = true;
            handle.IsCanceled = true;
            _pending--;
            ForgetKeyLocked(handle);
        }

        /// <summary>Drops the item's key registration, but only when it is still the item that key
        /// points at — a re-enqueue under the same key has already replaced it, and that newer item
        /// must keep its slot.</summary>
        private void ForgetKeyLocked(PreviewWorkHandle handle)
        {
            if (handle.Key == null)
                return;

            if (_byKey.TryGetValue(handle.Key, out var live) && ReferenceEquals(live, handle))
                _byKey.Remove(handle.Key);
        }

        /// <summary>
        /// Drops the longest-waiting pending item. The oldest, not the least urgent: an item that
        /// has been queued through a whole fling belongs to a row the user left far behind, whereas
        /// the newest item in the least urgent band is on screen in a second. Returns the victim so
        /// the caller can cancel it outside the lock, or null if everything queued is already
        /// settled.
        /// </summary>
        private PreviewWorkHandle EvictOldestLocked()
        {
            while (_arrival.Count > 0)
            {
                var candidate = _arrival.Dequeue();
                if (candidate.Settled || candidate.Started)
                    continue;

                AbandonLocked(candidate);
                return candidate;
            }

            return null;
        }

        /// <summary>Caller holds <see cref="_gate"/>. A new thread's first act is to take the same
        /// lock, so it cannot observe a half-filled queue.</summary>
        private void EnsureThreadsLocked()
        {
            // Both threads, not one per pending item. Sizing the pool to the queue depth reads as
            // thrift but is wrong in the case that matters: a steady trickle of one-item-at-a-time
            // work would never grow past a single worker, so a slow decode would block every cheap
            // icon behind it — which is the exact thing two workers exist to prevent. They retire
            // together after the idle timeout, so the cost of being wrong the other way is a thread
            // that parks for thirty seconds.
            for (int i = 0; i < _threads.Length; i++)
            {
                if (_threads[i] != null)
                    continue;

                int slot = i;
                var thread = new Thread(() => WorkLoop(slot))
                {
                    Name = "clowd-preview-" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    IsBackground = true,

                    // the same priority the editor's thumbnail worker runs at: a preview is never
                    // worth a frame of the app the user is actually driving.
                    Priority = ThreadPriority.BelowNormal,
                };

                _threads[slot] = thread;
                _liveThreads++;
                thread.Start();
            }
        }

        private void WorkLoop(int slot)
        {
            while (true)
            {
                PreviewWorkHandle handle;

                lock (_gate)
                {
                    while (true)
                    {
                        long now = Environment.TickCount64;
                        bool suspended = _suspendUntilTicks > now;

                        if (!suspended)
                        {
                            handle = TakeNextLocked();
                            if (handle != null)
                                break;
                        }

                        // while the gate is down, sleep only until it lifts — otherwise a fling
                        // that ends quietly would leave the queue parked for the full idle timeout.
                        int wait = IdleTimeoutMs;
                        if (suspended && _pending > 0)
                            wait = (int)Math.Clamp(_suspendUntilTicks - now, 1, IdleTimeoutMs);

                        if (!Monitor.Wait(_gate, wait) && _pending == 0)
                        {
                            // a full idle timeout with nothing to do: retire. The next Enqueue
                            // starts a fresh thread, and both happen under this lock, so the two
                            // cannot race into a queue with no thread behind it.
                            _threads[slot] = null;
                            _liveThreads--;
                            return;
                        }
                    }
                }

                handle.Run();
            }
        }

        /// <summary>
        /// Caller holds <see cref="_gate"/>. Pops the most urgent runnable item, discarding the two
        /// kinds of dead entry the priority queue can hold: items abandoned since they were queued
        /// (this is the "drop it before running" check — the engine's grace reaper is what sets it,
        /// so a row whose subscribers left and never came back costs nothing but a dequeue), and
        /// stale copies left behind by <see cref="Promote"/>.
        /// </summary>
        private PreviewWorkHandle TakeNextLocked()
        {
            while (_queue.TryDequeue(out var handle, out var priority))
            {
                if (handle.Settled || handle.Started)
                    continue;

                // a copy from before a promotion; the item is still queued under its new band
                if (handle.Band != priority.Band)
                    continue;

                handle.Started = true;
                _pending--;
                return handle;
            }

            return null;
        }
    }
}
