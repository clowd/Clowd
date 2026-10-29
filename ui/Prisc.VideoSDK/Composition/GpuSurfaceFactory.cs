using System;
using SkiaSharp;
using Interop = Prisc.Interop;

namespace Prisc.VideoSDK.Composition
{
    /// <summary>
    /// Headless GPU surface backend: a <see cref="GRContext"/> over Direct3D 12 on Windows or
    /// Metal on macOS (the backends SkiaSharp 3.119 exposes for those platforms), with offscreen
    /// render targets from <c>SKSurface.Create(context, ...)</c>. The device itself comes from
    /// prisc_interop (<see cref="Interop.GpuDevice"/>); this class only builds the Skia context on it.
    ///
    /// Creation is expected to fail in real deployments (RDP, VMs, stale drivers, CI, or a
    /// libSkiaSharp built without the D3D backend): <see cref="TryCreate"/> returns null with a
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
        // The device, its adapter and its queue, owned by one handle and released after the context.
        private readonly Interop.GpuDevice _device;
        private bool _disposed;

        private GpuSurfaceFactory(GRContext context, string backendName, Interop.GpuDevice device)
        {
            _context = context;
            _backendName = backendName;
            _device = device;
        }

        public string BackendName => _backendName;

        /// <summary>The adapter description or MTLDevice name behind this context, for diagnostics.</summary>
        public string GpuName => _device.AdapterName;

        public GRContext Context => _context;

        /// <summary>The device this factory renders on, which the readback ring and the encode
        /// bridge build on.</summary>
        internal Interop.GpuDevice Device => _device;

        /// <summary>
        /// Attempts to create a headless GPU factory for the current OS. Returns null (with
        /// <paramref name="failureReason"/> set) when no GPU backend is available: callers must
        /// fall back to CPU. Must be called on the thread that will own the context.
        /// </summary>
        public static GpuSurfaceFactory TryCreate(out string failureReason)
        {
            // Software adapters (WARP) are slower than the CPU compositor; tests on a GPU-less CI
            // runner opt in with PRISC_GPU_ALLOW_WARP=1 to exercise this path anyway.
            var flags = Environment.GetEnvironmentVariable(Interop.EnvVars.GpuAllowWarp) == "1"
                ? Interop.GpuDeviceFlags.AllowSoftwareAdapter
                : Interop.GpuDeviceFlags.None;
            var device = Interop.GpuDevice.TryCreate(flags, out failureReason);
            if (device == null)
                return null;

            string backendName;
            GRContext context = null;
            try
            {
                // The backend the device reports is the real condition; the OS term is what the
                // platform-compatibility analyzer reads for the Windows- and macOS-only getters.
                if (device.Backend == Interop.GpuBackend.D3D12 && OperatingSystem.IsWindows())
                {
                    backendName = "Direct3D 12";
                    context = GRContext.CreateDirect3D(new GRD3DBackendContext
                    {
                        Adapter = device.D3D12Adapter,
                        Device = device.D3D12Device,
                        Queue = device.D3D12Queue,
                    });
                }
                else if (device.Backend == Interop.GpuBackend.Metal && OperatingSystem.IsMacOS())
                {
                    backendName = "Metal";
                    context = GRContext.CreateMetal(new GRMtlBackendContext
                    {
                        DeviceHandle = device.MetalDevice,
                        QueueHandle = device.MetalQueue,
                    });
                }
                else
                {
                    failureReason = "No headless GPU backend for this OS.";
                    device.Dispose();
                    return null;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // libSkiaSharp without the backend export: fall back to the CPU rather than taking
                // the composer down.
                failureReason = "SkiaSharp native library lacks the " +
                    (device.Backend == Interop.GpuBackend.Metal ? "Metal" : "Direct3D") + " backend: " + ex.Message;
                device.Dispose();
                return null;
            }

            if (context == null)
            {
                failureReason = $"GRContext.Create for {backendName} returned null.";
                device.Dispose();
                return null;
            }

            // Smoke-test the context: some drivers hand out a context that cannot actually
            // allocate a render target (seen on RDP). Fail here so the CPU fallback engages
            // at startup rather than mid-composite.
            var factory = new GpuSurfaceFactory(context, backendName, device);
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

        /// <summary>
        /// Direct3D 12: the asynchronous <see cref="D3D12ReadbackRing"/> (copy-queue readback,
        /// no GPU wait on this thread), falling back to the synchronous ring, with the reason
        /// logged, when the driver refuses any part of it or the ring fails its self-check. Metal:
        /// the synchronous ring; an IOSurface-backed equivalent is a later, mac-side piece of work.
        /// </summary>
        public IReadbackRing CreateReadbackRing(int width, int height, int slots, Action<string> diagnosticLog = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_device.Backend == Interop.GpuBackend.D3D12 && OperatingSystem.IsWindows())
            {
                var ring = D3D12ReadbackRing.TryCreate(_context, _device, width, height, slots, out var reason);
                if (ring != null)
                    return ring;
                diagnosticLog?.Invoke(
                    $"Direct3D 12 asynchronous readback unavailable ({reason}); using synchronous readback.");
            }

            return new SyncReadbackRing(this, width, height, SyncReadbackRing.UsefulSlots(slots));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            // Order: the context references the native device objects. Disposing the device first
            // would not free them under Skia (it holds its own references), but this order
            // releases everything promptly.
            _context.Flush();
            _context.Submit(synchronous: true);
            _context.Dispose();
            _device.Dispose();
        }
    }
}
