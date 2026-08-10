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
    }

    internal static class SurfacePixels
    {
        /// <summary>The one pixel format of the composition pipeline: BGRA8888 premultiplied —
        /// matching both the sws_scale output of the decode workers and Avalonia's swapchain.</summary>
        public static SKImageInfo Bgra(int width, int height)
            => new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
    }
}
