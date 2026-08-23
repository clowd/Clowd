using System;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// Headless GPU surface backend: a <see cref="GRContext"/> over Direct3D 12 on Windows or
    /// Metal on macOS (the backends SkiaSharp 3.119 exposes for those platforms), with offscreen
    /// render targets from <c>SKSurface.Create(context, ...)</c>.
    ///
    /// Creation is expected to fail in real deployments (RDP, VMs, stale drivers, CI, or a
    /// libSkiaSharp built without the D3D backend) — <see cref="TryCreate"/> returns null with a
    /// reason and the caller falls back to <see cref="CpuSurfaceFactory"/>. That fallback is a
    /// robustness requirement, not a nicety.
    ///
    /// Context affinity: create and use an instance on ONE thread only (see
    /// <see cref="ComposerThread"/>). All surfaces and textures made from this factory are bound
    /// to its context.
    /// </summary>
    public sealed class GpuSurfaceFactory : ISurfaceFactory
    {
        private readonly GRContext _context;
        private readonly string _backendName;
        // Native device objects backing the context; released after the context is disposed.
        private IntPtr _d3dAdapter, _d3dDevice, _d3dQueue;
        private IntPtr _mtlDevice, _mtlQueue;
        private bool _disposed;

        private GpuSurfaceFactory(GRContext context, string backendName)
        {
            _context = context;
            _backendName = backendName;
        }

        public string BackendName => _backendName;

        public GRContext Context => _context;

        /// <summary>
        /// Attempts to create a headless GPU factory for the current OS. Returns null (with
        /// <paramref name="failureReason"/> set) when no GPU backend is available — callers must
        /// fall back to CPU. Must be called on the thread that will own the context.
        /// </summary>
        public static GpuSurfaceFactory TryCreate(out string failureReason)
        {
            if (OperatingSystem.IsWindows())
                return TryCreateDirect3D(out failureReason);
            if (OperatingSystem.IsMacOS())
                return TryCreateMetal(out failureReason);

            failureReason = "No headless GPU backend for this OS.";
            return null;
        }

        private static GpuSurfaceFactory TryCreateDirect3D(out string failureReason)
        {
            if (!D3D12Backend.TryCreateDevice(out var adapter, out var device, out var queue, out failureReason))
                return null;

            GRContext context = null;
            try
            {
                var backend = new GRD3DBackendContext { Adapter = adapter, Device = device, Queue = queue };
                context = GRContext.CreateDirect3D(backend);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // libSkiaSharp without the D3D backend export.
                failureReason = "SkiaSharp native library lacks the Direct3D backend: " + ex.Message;
            }

            if (context == null)
            {
                failureReason ??= "GRContext.CreateDirect3D returned null.";
                D3D12Backend.Release(queue);
                D3D12Backend.Release(device);
                D3D12Backend.Release(adapter);
                return null;
            }

            // Smoke-test the context: some drivers hand out a context that cannot actually
            // allocate a render target (seen on RDP). Fail here so the CPU fallback engages
            // at startup rather than mid-composite.
            var factory = new GpuSurfaceFactory(context, "Direct3D 12")
            {
                _d3dAdapter = adapter,
                _d3dDevice = device,
                _d3dQueue = queue,
            };
            if (!SmokeTest(factory, out failureReason))
            {
                factory.Dispose();
                return null;
            }

            failureReason = null;
            return factory;
        }

        private static GpuSurfaceFactory TryCreateMetal(out string failureReason)
        {
            if (!OperatingSystem.IsMacOS())
            {
                failureReason = "Not macOS.";
                return null;
            }

            if (!MetalBackend.TryCreateDevice(out var device, out var queue, out failureReason))
                return null;

            GRContext context = null;
            try
            {
                var backend = new GRMtlBackendContext { DeviceHandle = device, QueueHandle = queue };
                context = GRContext.CreateMetal(backend);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // libSkiaSharp without the Metal backend export — the same shape of failure the
                // Direct3D branch above guards, and the same answer: fall back to the CPU rather
                // than taking the composer down.
                failureReason = "SkiaSharp native library lacks the Metal backend: " + ex.Message;
            }

            if (context == null)
            {
                failureReason ??= "GRContext.CreateMetal returned null.";
                MetalBackend.Release(queue);
                MetalBackend.Release(device);
                return null;
            }

            var factory = new GpuSurfaceFactory(context, "Metal")
            {
                _mtlDevice = device,
                _mtlQueue = queue,
            };
            if (!SmokeTest(factory, out failureReason))
            {
                factory.Dispose();
                return null;
            }

            failureReason = null;
            return factory;
        }

        private static bool SmokeTest(GpuSurfaceFactory factory, out string failureReason)
        {
            try
            {
                using var surface = factory.CreateSurface(4, 4);
                surface.Canvas.Clear(SKColors.Black);
                surface.Flush(submit: true, synchronous: true);
                failureReason = null;
                return true;
            }
            catch (Exception ex)
            {
                failureReason = "GPU context failed its smoke test: " + ex.Message;
                return false;
            }
        }

        public SKSurface CreateSurface(int width, int height)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var surface = SKSurface.Create(_context, budgeted: true, SurfacePixels.Bgra(width, height));
            if (surface == null)
                throw new InvalidOperationException(
                    $"Failed to create {width}x{height} {_backendName} surface.");
            return surface;
        }

        public bool TryReadPixels(SKSurface surface, int width, int height, IntPtr dst, int rowBytes)
        {
            if (_disposed || surface == null || dst == IntPtr.Zero)
                return false;

            // Make sure all recorded draws are submitted before the (synchronous) readback.
            surface.Flush(submit: true, synchronous: true);
            return surface.ReadPixels(SurfacePixels.Bgra(width, height), dst, rowBytes, 0, 0);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            // Order matters: the context references the native device objects.
            _context.Flush();
            _context.Submit(synchronous: true);
            _context.Dispose();

            if (_d3dQueue != IntPtr.Zero || _d3dDevice != IntPtr.Zero || _d3dAdapter != IntPtr.Zero)
            {
                D3D12Backend.Release(_d3dQueue);
                D3D12Backend.Release(_d3dDevice);
                D3D12Backend.Release(_d3dAdapter);
                _d3dQueue = _d3dDevice = _d3dAdapter = IntPtr.Zero;
            }

            if (OperatingSystem.IsMacOS() && (_mtlQueue != IntPtr.Zero || _mtlDevice != IntPtr.Zero))
            {
                MetalBackend.Release(_mtlQueue);
                MetalBackend.Release(_mtlDevice);
                _mtlQueue = _mtlDevice = IntPtr.Zero;
            }
        }
    }
}
