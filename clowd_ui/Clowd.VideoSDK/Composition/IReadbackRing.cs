using System;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>One slot's pixels after <see cref="IReadbackRing.Wait"/>: BGRA8888 premultiplied,
    /// top-down rows of <see cref="RowBytes"/> bytes (a GPU readback heap pads rows to the
    /// hardware's pitch, so it is not necessarily <c>width * 4</c>). The memory belongs to the
    /// slot and is valid until that slot's next <see cref="IReadbackRing.Begin"/>.</summary>
    public readonly struct ReadbackPixels
    {
        public ReadbackPixels(IntPtr address, int rowBytes)
        {
            Address = address;
            RowBytes = rowBytes;
        }

        public IntPtr Address { get; }

        public int RowBytes { get; }
    }

    /// <summary>
    /// A fixed ring of composition targets whose pixels come back to CPU memory without the
    /// drawing thread ever waiting for the GPU — the asynchronous readback contract of
    /// <see cref="ISurfaceFactory.CreateReadbackRing"/>. It replaces the synchronous
    /// <see cref="ISurfaceFactory.TryReadPixels"/> in the render loop, where a single
    /// <c>SKSurface.ReadPixels</c> (flush, wait for the GPU, copy through an uncached path) cost
    /// 2 ms per frame at 1240x1166 and 8 ms at 2480x2332 — the whole render's ceiling.
    ///
    /// <para>Per-slot protocol, in this order: <see cref="Begin"/> (draw into the returned
    /// canvas) → <see cref="Submit"/> → <see cref="Wait"/> → read the pixels → <see cref="Begin"/>
    /// again. <see cref="Begin"/>, <see cref="Submit"/> and <see cref="Dispose"/> run on the
    /// factory's thread (the <see cref="ComposerThread"/>); <see cref="Wait"/> may run on any
    /// thread, which is the point: one thread composes slot <c>n+1</c> while another waits for
    /// and consumes slot <c>n</c>. The ring never blocks <see cref="Begin"/> on an in-flight slot
    /// — a slot must have been waited for, and its pixels finished with, before it is reused;
    /// the caller's handshake (a free-slot queue) provides that ordering, and misuse throws.</para>
    /// </summary>
    public interface IReadbackRing : IDisposable
    {
        /// <summary>Canvas width in pixels.</summary>
        int Width { get; }

        /// <summary>Canvas height in pixels.</summary>
        int Height { get; }

        /// <summary>Number of slots in the ring.</summary>
        int SlotCount { get; }

        /// <summary>Human-readable description for diagnostics, e.g. "Direct3D 12 copy-queue
        /// readback, 4 slots" or "synchronous readback, 4 slots".</summary>
        string Description { get; }

        /// <summary>Makes <paramref name="slot"/> the current drawing target and returns its
        /// canvas (valid until <see cref="Submit"/>). The slot must be idle: never used, or
        /// waited for since its last submit.</summary>
        SKCanvas Begin(int slot);

        /// <summary>Ends drawing on <paramref name="slot"/> and starts its readback. Returns as
        /// soon as the work is queued; on a GPU backend nothing has been read back yet.</summary>
        void Submit(int slot);

        /// <summary>Blocks until the pixels of <paramref name="slot"/> are in CPU memory and
        /// returns them. The slot is idle afterwards: the memory stays valid until the slot's
        /// next <see cref="Begin"/>.</summary>
        ReadbackPixels Wait(int slot);
    }
}
