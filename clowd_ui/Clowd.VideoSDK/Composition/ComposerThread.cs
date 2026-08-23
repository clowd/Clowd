using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The context-affine thread that owns an <see cref="ISurfaceFactory"/> (and therefore the
    /// <c>GRContext</c>, when the GPU backend is active). Skia GPU contexts are single-threaded:
    /// every surface creation, texture upload (<see cref="FrameTextureCache"/>), compose and
    /// readback for one context must run on this thread. Decode threads stay entirely
    /// context-free — they write BGRA into pooled CPU buffers (<see cref="PooledFrameSink"/>)
    /// and this thread uploads.
    ///
    /// The factory is created ON the composer thread (context creation is itself affine) and
    /// disposed on it when the queue drains at <see cref="Dispose"/>.
    /// </summary>
    public sealed class ComposerThread : IDisposable
    {
        private readonly Thread _thread;
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim();
        private readonly Action<string> _log;
        private ISurfaceFactory _factory;
        private Exception _startError;
        private bool _disposed;

        private ComposerThread(bool preferGpu, Action<string> log)
        {
            _log = log;
            _thread = new Thread(() => ThreadProc(preferGpu))
            {
                Name = "Clowd.VideoSDK Composer",
                IsBackground = true,
            };
            _thread.Start();
        }

        /// <summary>
        /// Starts the composer thread and blocks until backend selection has completed on it.
        /// </summary>
        public static ComposerThread Start(bool preferGpu, Action<string> diagnosticLog = null)
        {
            var composer = new ComposerThread(preferGpu, diagnosticLog);
            composer._ready.Wait();
            if (composer._startError != null)
            {
                var error = composer._startError;
                composer.Dispose();
                throw new InvalidOperationException("Failed to start the composer thread.", error);
            }

            return composer;
        }

        /// <summary>The factory owned by this thread. Use it only from work posted here.</summary>
        public ISurfaceFactory Factory => _factory;

        /// <summary>Convenience for diagnostics: the selected backend name.</summary>
        public string BackendName => _factory?.BackendName;

        /// <summary>True when the calling code is already running on the composer thread.</summary>
        public bool IsCurrent => Thread.CurrentThread == _thread;

        private void ThreadProc(bool preferGpu)
        {
            try
            {
                _factory = SurfaceFactory.Create(preferGpu, _log);
            }
            catch (Exception ex)
            {
                _startError = ex;
                _ready.Set();
                return;
            }

            _ready.Set();

            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    // Post() work has no caller to observe the failure; Send() wraps its own
                    // exception marshalling and never reaches this handler.
                    _log?.Invoke("Composer work item failed: " + ex);
                }
            }

            _factory?.Dispose();
            _factory = null;
        }

        /// <summary>Queues work onto the composer thread (fire and forget). Failures are
        /// reported to the diagnostic log; use <see cref="Send"/> to observe exceptions.</summary>
        public void Post(Action work)
        {
            ArgumentNullException.ThrowIfNull(work);
            try
            {
                _queue.Add(work);
            }
            catch (InvalidOperationException)
            {
                throw new ObjectDisposedException(nameof(ComposerThread));
            }
        }

        /// <summary>Runs work on the composer thread and blocks until it completes, rethrowing
        /// its exception here. Calls from the composer thread itself execute inline (no deadlock).</summary>
        public void Send(Action work)
        {
            ArgumentNullException.ThrowIfNull(work);
            if (IsCurrent)
            {
                work();
                return;
            }

            using var done = new ManualResetEventSlim();
            ExceptionDispatchInfo error = null;
            Post(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    error = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    done.Set();
                }
            });
            done.Wait();
            error?.Throw();
        }

        /// <summary>Runs a function on the composer thread and returns its result.</summary>
        public T Send<T>(Func<T> work)
        {
            ArgumentNullException.ThrowIfNull(work);
            T result = default;
            Send(() => { result = work(); });
            return result;
        }

        /// <summary>
        /// Completes the queue, joins the thread (pending work runs to completion, then the
        /// factory is disposed on the composer thread) and releases resources. Must not be
        /// called from the composer thread itself.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            if (IsCurrent)
                throw new InvalidOperationException("ComposerThread.Dispose must not be called from the composer thread.");
            _disposed = true;

            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
            _ready.Dispose();
        }
    }
}
