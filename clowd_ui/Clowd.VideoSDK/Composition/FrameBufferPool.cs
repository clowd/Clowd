using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// A pooled native BGRA staging buffer. Opaque to managed code: the address is handed to
    /// native calls (sws_scale writes into it, Skia reads from it) — pixels are never touched
    /// from managed loops. Call <see cref="Return"/> exactly once when done; extra calls are
    /// harmless no-ops (the buffer may already be back in circulation, so never touch it after
    /// the first Return).
    /// </summary>
    public sealed class FrameBuffer
    {
        private readonly FrameBufferPool _pool;
        internal bool Pooled; // guarded by the pool's lock

        internal FrameBuffer(FrameBufferPool pool, IntPtr address, int capacity)
        {
            _pool = pool;
            Address = address;
            Capacity = capacity;
        }

        public IntPtr Address { get; }
        public int Capacity { get; }

        /// <summary>Returns the buffer to its pool.</summary>
        public void Return() => _pool.Return(this);
    }

    /// <summary>
    /// Pool of native-allocated (64-byte aligned, sws_scale-friendly) CPU frame buffers shared
    /// between decode writers (<see cref="PooledFrameSink"/>) and the composer's texture upload
    /// (<see cref="FrameTextureCache"/>). Renting never blocks — a new buffer is allocated when
    /// none of sufficient capacity is pooled. Thread-safe.
    /// </summary>
    public sealed class FrameBufferPool : IDisposable
    {
        private readonly object _sync = new object();
        private readonly List<FrameBuffer> _free = new List<FrameBuffer>();
        private bool _disposed;
        private int _totalAllocated;

        /// <summary>Buffers currently sitting in the pool (test/diagnostic).</summary>
        internal int PooledCount
        {
            get { lock (_sync) return _free.Count; }
        }

        /// <summary>Total buffers ever allocated by this pool (test/diagnostic).</summary>
        internal int TotalAllocated
        {
            get { lock (_sync) return _totalAllocated; }
        }

        /// <summary>Rents a buffer of at least <paramref name="sizeBytes"/> bytes.</summary>
        public unsafe FrameBuffer Rent(int sizeBytes)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sizeBytes, 0);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                for (int i = _free.Count - 1; i >= 0; i--)
                {
                    if (_free[i].Capacity >= sizeBytes)
                    {
                        var buffer = _free[i];
                        _free.RemoveAt(i);
                        buffer.Pooled = false;
                        return buffer;
                    }
                }

                var address = (IntPtr)NativeMemory.AlignedAlloc((nuint)sizeBytes, 64);
                _totalAllocated++;
                return new FrameBuffer(this, address, sizeBytes);
            }
        }

        internal unsafe void Return(FrameBuffer buffer)
        {
            lock (_sync)
            {
                if (buffer.Pooled)
                    return; // double-return: already back in the pool (or freed) — no-op

                buffer.Pooled = true;
                if (_disposed)
                {
                    NativeMemory.AlignedFree((void*)buffer.Address);
                    return;
                }

                _free.Add(buffer);
            }
        }

        /// <summary>
        /// Frees all pooled buffers. Buffers still outstanding are freed when returned.
        /// </summary>
        public unsafe void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                foreach (var buffer in _free)
                    NativeMemory.AlignedFree((void*)buffer.Address);
                _free.Clear();
            }
        }
    }
}
