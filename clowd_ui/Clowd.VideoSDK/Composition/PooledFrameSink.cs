using System;
using Clowd.VideoSDK.Playback;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// A completed frame acquired from a <see cref="PooledFrameSink"/>. The consumer owns the
    /// underlying buffer: either hand it to <see cref="FrameTextureCache.Upload"/> (which
    /// recycles it) or call <see cref="Return"/> — exactly one of the two.
    /// </summary>
    public sealed class PooledFrame
    {
        internal PooledFrame(FrameBuffer buffer, int width, int height, int rowBytes, TimeSpan pts)
        {
            Buffer = buffer;
            Width = width;
            Height = height;
            RowBytes = rowBytes;
            Pts = pts;
        }

        public FrameBuffer Buffer { get; }
        public int Width { get; }
        public int Height { get; }
        public int RowBytes { get; }
        public TimeSpan Pts { get; }

        /// <summary>Returns the buffer to the pool without consuming the frame.</summary>
        public void Return() => Buffer.Return();
    }

    /// <summary>
    /// The SDK-side <see cref="IFrameSink"/>: decode threads sws_scale BGRA directly into pooled
    /// native buffers (no managed pixel access, no UI dependency), and the consumer — the
    /// composer thread, or Avalonia's render thread inside a draw operation — takes the latest
    /// completed frame for texture upload.
    ///
    /// Latest-frame discipline: completing a frame replaces the previous unconsumed latest, whose
    /// buffer goes straight back to the pool; <see cref="TryAcquireLatest"/> transfers ownership
    /// of the newest frame to the caller and yields nothing until another frame completes. An
    /// acquired frame's buffer is never reused until the consumer releases it.
    ///
    /// BeginFrame never blocks (the pool allocates on demand), which satisfies the
    /// <see cref="IFrameSink"/> contract's "may block briefly" as a no-op.
    /// </summary>
    public sealed class PooledFrameSink : IFrameSink, IDisposable
    {
        private readonly object _sync = new object();
        private readonly FrameBufferPool _pool;
        private readonly bool _ownsPool;
        private PooledFrame _latest;
        private bool _disposed;

        /// <summary>Creates a sink with its own private buffer pool.</summary>
        public PooledFrameSink()
            : this(new FrameBufferPool(), ownsPool: true)
        {
        }

        /// <summary>Creates a sink over a shared pool (several sinks can recycle through one
        /// pool when their frames are consumed by the same composer).</summary>
        public PooledFrameSink(FrameBufferPool pool)
            : this(pool, ownsPool: false)
        {
            ArgumentNullException.ThrowIfNull(pool);
        }

        private PooledFrameSink(FrameBufferPool pool, bool ownsPool)
        {
            _pool = pool;
            _ownsPool = ownsPool;
        }

        /// <summary>The buffer pool frames are rented from (shared with the texture cache).</summary>
        public FrameBufferPool Pool => _pool;

        /// <summary>Raised after a frame completes (on the decode/present thread). Typical use:
        /// schedule an invalidate/compose pass. Handlers must not block.</summary>
        public event Action FrameCompleted;

        public FrameTarget BeginFrame(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return default;

            int rowBytes = checked(width * 4);
            int size = checked(rowBytes * height);
            FrameBuffer buffer;
            lock (_sync)
            {
                if (_disposed)
                    return default;
                buffer = _pool.Rent(size);
            }

            return new FrameTarget(buffer.Address, rowBytes, width, height, buffer);
        }

        public void CompleteFrame(in FrameTarget target, TimeSpan pts)
        {
            if (target.Token is not FrameBuffer buffer)
                return;

            PooledFrame replaced;
            lock (_sync)
            {
                if (_disposed)
                {
                    buffer.Return();
                    return;
                }

                replaced = _latest;
                _latest = new PooledFrame(buffer, target.Width, target.Height, target.RowBytes, pts);
            }

            // The superseded frame was never consumed — recycle it (outside the lock).
            replaced?.Return();
            FrameCompleted?.Invoke();
        }

        /// <summary>
        /// Takes ownership of the newest completed frame. Returns false when no frame has
        /// completed since the last acquisition.
        /// </summary>
        public bool TryAcquireLatest(out PooledFrame frame)
        {
            lock (_sync)
            {
                frame = _latest;
                _latest = null;
            }

            return frame != null;
        }

        public void Dispose()
        {
            PooledFrame latest;
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                latest = _latest;
                _latest = null;
            }

            latest?.Return();

            // In-flight writer buffers (BeginFrame'd but not completed) drain through
            // CompleteFrame's disposed path; the pool frees late returns after its own Dispose.
            if (_ownsPool)
                _pool.Dispose();
        }
    }
}
