using System;
using System.Collections.Generic;
using System.Threading;

namespace Clowd.VideoSDK.Thumbs
{
    /// <summary>
    /// The bands <see cref="ThumbWorkScheduler"/> orders work by, lowest value first — named for
    /// how badly a timeline row needs the result. A waveform is one cheap forward pass that makes a
    /// whole audio row real, a keyframe pass sketches a video row end to end at near-demux speed,
    /// and refinement fills in the frames between keyframes for the span the user is looking at:
    /// useful, but never at the cost of a row that is still blank.
    ///
    /// <para>
    /// The values are the same integers <see cref="FilmstripProvider.KeyframePassPriority"/> and
    /// <see cref="FilmstripProvider.RefinePriority"/> use, so the enum and the raw-int
    /// <see cref="IThumbWorkQueue"/> overload order against each other correctly.
    /// </para>
    /// </summary>
    internal enum ThumbWorkPriority
    {
        Waveform = 10,
        Keyframes = 20,
        Refinement = 30,
    }

    /// <summary>
    /// A queued (or running) unit of work. <see cref="Cancel"/> (and <see cref="Dispose"/>, which
    /// is the same thing) unqueues the item if it has not started; whether it also stops work that
    /// is <i>already running</i> depends on how it was enqueued — items given their own token
    /// (<see cref="ThumbWorkScheduler.Enqueue(int, Action{CancellationToken})"/>) are canceled
    /// outright, items sharing a caller's token stop when that caller cancels it.
    /// </summary>
    internal sealed class ThumbWorkHandle : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Action<CancellationToken> _work;
        private readonly CancellationToken _token;

        /// <summary>Set only for items the scheduler minted a token for; never disposed — it holds
        /// no registrations and no wait handle, so letting the GC take it costs nothing and cannot
        /// race with a <see cref="Cancel"/> from another thread.</summary>
        private readonly CancellationTokenSource _ownedSource;

        private bool _started;
        private bool _finished;
        private bool _canceled;
        private Exception _error;

        internal ThumbWorkHandle(Action<CancellationToken> work, CancellationToken token,
            CancellationTokenSource ownedSource = null)
        {
            _work = work;
            _token = token;
            _ownedSource = ownedSource;
        }

        /// <summary>True once the work has run, thrown, or been unqueued.</summary>
        public bool IsFinished
        {
            get
            {
                lock (_gate)
                    return _finished;
            }
        }

        /// <summary>True when the item never ran because it was unqueued first.</summary>
        public bool IsCanceled
        {
            get
            {
                lock (_gate)
                    return _canceled;
            }
        }

        /// <summary>What the work threw, if anything. A throwing item is recorded here and
        /// dropped — it never takes the scheduler thread down with it.</summary>
        public Exception Error
        {
            get
            {
                lock (_gate)
                    return _error;
            }
        }

        /// <summary>Abandons the item: drops it if it is still queued, and cancels its token if the
        /// scheduler minted one for it.</summary>
        public void Cancel()
        {
            lock (_gate)
            {
                if (!_started && !_finished)
                {
                    _canceled = true;
                    _finished = true;
                    Monitor.PulseAll(_gate);
                }
            }

            _ownedSource?.Cancel();
        }

        public void Dispose() => Cancel();

        /// <summary>Blocks until the item is finished (test/diagnostic helper). Returns false on
        /// timeout.</summary>
        public bool Wait(int millisecondsTimeout)
        {
            long deadline = Environment.TickCount64 + Math.Max(0, millisecondsTimeout);
            lock (_gate)
            {
                while (!_finished)
                {
                    int remaining = (int)Math.Min(Int32.MaxValue, Math.Max(0, deadline - Environment.TickCount64));
                    if (remaining == 0 || !Monitor.Wait(_gate, remaining))
                        return _finished;
                }

                return true;
            }
        }

        /// <summary>Scheduler thread only.</summary>
        internal void Run()
        {
            lock (_gate)
            {
                if (_finished)
                    return;
                _started = true;
            }

            Exception error = null;
            try
            {
                if (!_token.IsCancellationRequested)
                    _work(_token);
            }
            catch (OperationCanceledException)
            {
                // the token fired mid-work: an ordinary stop, not a fault
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                lock (_gate)
                {
                    _error = error;
                    _finished = true;
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }

    /// <summary>
    /// The single background thread every preview-visual service decodes on:
    /// <see cref="WaveformProvider"/> and <see cref="FilmstripProvider"/> both enqueue here, so
    /// timeline visuals can never out-compete playback (the thread runs
    /// <see cref="ThreadPriority.BelowNormal"/>) and can never fan out into N decoders at once.
    /// Ordering is strictly by priority, FIFO within a band, so the work that makes a blank row
    /// real always goes first.
    ///
    /// <para>
    /// One thread, no reentrancy: a work delegate runs to completion before the next item starts,
    /// and it runs on a thread nobody else owns — it may block on decoding for as long as it needs.
    /// It must, however, honor the cancellation token it is handed, or a closing editor waits on
    /// it. Exceptions are captured on the handle and dropped; the thread never dies.
    /// </para>
    ///
    /// <para>
    /// The thread is started on demand and retires itself after <see cref="IdleTimeoutMs"/> with an
    /// empty queue, so <see cref="Shared"/> costs nothing between edits.
    /// </para>
    /// </summary>
    internal sealed class ThumbWorkScheduler : IThumbWorkQueue, IDisposable
    {
        private const int IdleTimeoutMs = 10_000;

        private static readonly Lazy<ThumbWorkScheduler> LazyShared =
            new Lazy<ThumbWorkScheduler>(() => new ThumbWorkScheduler(), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>The process-wide scheduler. Never disposed — it holds no unmanaged state and
        /// its thread is a background one that retires when idle.</summary>
        public static ThumbWorkScheduler Shared => LazyShared.Value;

        private readonly object _gate = new object();
        private readonly PriorityQueue<ThumbWorkHandle, (int Band, long Sequence)> _queue =
            new PriorityQueue<ThumbWorkHandle, (int Band, long Sequence)>();

        private long _sequence;
        private Thread _thread;
        private bool _disposed;

        /// <summary>Items waiting to start (test/diagnostic).</summary>
        public int PendingCount
        {
            get
            {
                lock (_gate)
                    return _queue.Count;
            }
        }

        /// <summary>
        /// True when work more urgent than <paramref name="priorityBand"/> is waiting for the
        /// thread. Long-running items (the filmstrip's whole-stream pass and its refinement loop)
        /// poll this and park themselves so the priority bands mean something once the single
        /// thread is busy — without a yield point, ordering only applies to items that have not
        /// started yet.
        /// </summary>
        public bool HasPendingBelow(int priorityBand)
        {
            lock (_gate)
                return _queue.TryPeek(out _, out var head) && head.Band < priorityBand;
        }

        /// <summary>
        /// Queues <paramref name="work"/> in the <paramref name="priority"/> band. The delegate is
        /// handed <paramref name="cancellationToken"/> — the same token also drops the item before
        /// it starts, so a disposed provider's queued work evaporates.
        /// </summary>
        public ThumbWorkHandle Enqueue(ThumbWorkPriority priority, Action<CancellationToken> work,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(work);
            return Enqueue(new ThumbWorkHandle(work, cancellationToken), (int)priority);
        }

        /// <summary>
        /// Queues <paramref name="work"/> at a raw priority (lower runs first) with a token of its
        /// own: the returned handle's <see cref="ThumbWorkHandle.Cancel"/>/
        /// <see cref="ThumbWorkHandle.Dispose"/> both unqueues it and cancels it mid-flight. This
        /// is the <see cref="IThumbWorkQueue"/> shape the filmstrip abandons stale viewport work
        /// through.
        /// </summary>
        public ThumbWorkHandle Enqueue(int priority, Action<CancellationToken> work)
        {
            ArgumentNullException.ThrowIfNull(work);

            var source = new CancellationTokenSource();
            return Enqueue(new ThumbWorkHandle(work, source.Token, source), priority);
        }

        IDisposable IThumbWorkQueue.Enqueue(int priority, Action<CancellationToken> work) =>
            Enqueue(priority, work);

        private ThumbWorkHandle Enqueue(ThumbWorkHandle handle, int band)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                _queue.Enqueue(handle, (band, _sequence++));
                EnsureThread();
                Monitor.Pulse(_gate);
            }

            return handle;
        }

        /// <summary>Caller holds <see cref="_gate"/>. The new thread's first act is to take the
        /// same lock, so it does not observe a half-filled queue.</summary>
        private void EnsureThread()
        {
            if (_thread != null)
                return;

            _thread = new Thread(WorkLoop)
            {
                Name = "clowd-thumbs",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            _thread.Start();
        }

        private void WorkLoop()
        {
            while (true)
            {
                ThumbWorkHandle handle;
                lock (_gate)
                {
                    while (!_disposed && _queue.Count == 0)
                    {
                        // a timed-out wait with nothing queued retires the thread; the next
                        // Enqueue starts a fresh one (all under the lock, so the two cannot race).
                        if (!Monitor.Wait(_gate, IdleTimeoutMs) && _queue.Count == 0)
                        {
                            _thread = null;
                            return;
                        }
                    }

                    if (_disposed)
                    {
                        _thread = null;
                        return;
                    }

                    handle = _queue.Dequeue();
                }

                handle.Run();
            }
        }

        public void Dispose()
        {
            Thread thread;
            List<ThumbWorkHandle> queued;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;

                queued = new List<ThumbWorkHandle>(_queue.Count);
                while (_queue.Count > 0)
                    queued.Add(_queue.Dequeue());

                thread = _thread;
                Monitor.PulseAll(_gate);
            }

            foreach (var handle in queued)
                handle.Cancel();

            // a work item already running owns its own token; do not block the caller on it
            thread?.Join(2000);
        }
    }
}
