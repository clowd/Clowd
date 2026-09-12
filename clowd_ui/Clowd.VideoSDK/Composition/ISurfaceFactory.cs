using System;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// Creates the offscreen surfaces the compositor draws into. Surfaces are never constructed
    /// inline — everything goes through a factory so the GPU/CPU choice is made exactly once
    /// (see <see cref="SurfaceFactory.Create"/>) and the rest of the compositor is
    /// backend-agnostic.
    ///
    /// Threading: a factory (and every surface/texture created from it) is context-affine.
    /// GPU work for one <see cref="GRContext"/> must all happen on a single thread — own a
    /// factory through a <see cref="ComposerThread"/>. The CPU factory has no such requirement,
    /// but callers should not rely on that: treat every factory as single-threaded.
    /// </summary>
    public interface ISurfaceFactory : IDisposable
    {
        /// <summary>Human-readable backend name for diagnostics ("CPU", "Direct3D 12", "Metal").</summary>
        string BackendName { get; }

        /// <summary>The GPU context, or null when this is the CPU (raster) backend.</summary>
        GRContext Context { get; }

        /// <summary>Creates a BGRA8888 premultiplied surface of the given size.</summary>
        SKSurface CreateSurface(int width, int height);

        /// <summary>
        /// Reads the surface contents back to CPU memory as BGRA8888 premul. <paramref name="dst"/>
        /// must hold at least <paramref name="height"/> * <paramref name="rowBytes"/> bytes.
        /// For GPU surfaces this synchronizes with the GPU (flush + submit) — it is the
        /// perf-critical seam of the render loop, so callers pipeline it (two surfaces in flight).
        /// </summary>
        bool TryReadPixels(SKSurface surface, int width, int height, IntPtr dst, int rowBytes);

        /// <summary>
        /// Creates the render loop's readback ring (<see cref="IReadbackRing"/>): composition
        /// targets whose pixels are read back asynchronously where the backend can (Direct3D 12),
        /// and synchronously otherwise. <paramref name="slots"/> is the depth an asynchronous ring
        /// gets; a synchronous ring has no copy in flight to cover and is capped at
        /// <see cref="SyncReadbackRing.MaxUsefulSlots"/>, so callers size their bookkeeping from
        /// <see cref="IReadbackRing.SlotCount"/>, not from what they asked for. The default is the
        /// synchronous <see cref="SyncReadbackRing"/> over <see cref="CreateSurface"/> and
        /// <see cref="TryReadPixels"/> — every implementation gets a working ring for free and
        /// only a backend with a faster path overrides it. Must be called on the factory's
        /// thread; the ring is disposed there too, before the factory.
        /// </summary>
        /// <param name="diagnosticLog">Receives a line when an asynchronous ring was wanted but
        /// could not be created (the reason, and that the synchronous ring is used instead).</param>
        IReadbackRing CreateReadbackRing(int width, int height, int slots, Action<string> diagnosticLog = null)
            => new SyncReadbackRing(this, width, height, SyncReadbackRing.UsefulSlots(slots));
    }

    internal static class SurfacePixels
    {
        /// <summary>The one pixel format of the composition pipeline: BGRA8888 premultiplied —
        /// matching both the sws_scale output of the decode workers and Avalonia's swapchain.</summary>
        public static SKImageInfo Bgra(int width, int height)
            => new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
    }
}
