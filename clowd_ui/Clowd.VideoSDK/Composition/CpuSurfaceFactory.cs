using System;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// Raster (CPU) surface backend. Always available — this is the fallback when headless GPU
    /// context creation fails (RDP sessions, VMs, stale drivers, CI) and the reference
    /// implementation the GPU backend is equivalence-tested against.
    /// </summary>
    public sealed class CpuSurfaceFactory : ISurfaceFactory
    {
        private bool _disposed;

        public string BackendName => "CPU";

        /// <summary>Always null — null Context is the contract for "this backend is CPU".</summary>
        public GRContext Context => null;

        public SKSurface CreateSurface(int width, int height)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var surface = SKSurface.Create(SurfacePixels.Bgra(width, height));
            if (surface == null)
                throw new InvalidOperationException($"Failed to create {width}x{height} raster surface.");
            return surface;
        }

        public bool TryReadPixels(SKSurface surface, int width, int height, IntPtr dst, int rowBytes)
        {
            if (_disposed || surface == null || dst == IntPtr.Zero)
                return false;
            return surface.ReadPixels(SurfacePixels.Bgra(width, height), dst, rowBytes, 0, 0);
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
