using System;
using System.Runtime.Versioning;
using System.Threading;

namespace Prisc.Interop
{
    /// <summary>
    /// The headless GPU device the compositor renders on: a Direct3D 12 device, its adapter and a
    /// direct command queue on Windows, an <c>MTLDevice</c> and an <c>MTLCommandQueue</c> on macOS
    /// (<c>prisc_gpu_device_create</c>).
    /// </summary>
    /// <remarks>
    /// <para>Everything the creation reports is read once into fields, so the properties cost no
    /// native call. The raw pointers are <b>borrowed</b> from this handle: valid until it is
    /// disposed, never AddRef'd or released by C#. <see cref="AcquireD3D11Device"/> is the one
    /// exception and says so.</para>
    /// <para>Disposing this handle does not pull the device out from under anything still using it:
    /// Skia takes its own references, and a readback ring or NV12 converter built from it holds one
    /// inside the crate. The order Skia context, rings and converters, then this device is the one
    /// that releases things promptly, not a safety requirement.</para>
    /// </remarks>
    public sealed unsafe class GpuDevice : InteropHandle
    {
        private readonly IntPtr _d3d12Adapter, _d3d12Device, _d3d12Queue, _metalDevice, _metalQueue;

        private GpuDevice(PriscGpuDevice* handle, in PriscGpuDeviceInfo info)
        {
            Adopt((IntPtr)handle);
            Backend = (GpuBackend)info.backend;
            AdapterName = Utf8.Read(info.adapter_name);
            _d3d12Adapter = (IntPtr)info.d3d12_adapter;
            _d3d12Device = (IntPtr)info.d3d12_device;
            _d3d12Queue = (IntPtr)info.d3d12_queue;
            _metalDevice = (IntPtr)info.metal_device;
            _metalQueue = (IntPtr)info.metal_queue;
        }

        /// <summary>Which backend this device is.</summary>
        public GpuBackend Backend { get; }

        /// <summary>The adapter description (Windows) or <c>MTLDevice.name</c> (macOS), for
        /// diagnostics: it names the GPU in the composition backend log line.</summary>
        public string AdapterName { get; }

        /// <summary>
        /// Creates the device for this OS, or returns null with the reason. Never throws: a machine
        /// with no GPU, a driver that refused, and a missing native library are all answered the
        /// same way, by compositing on the CPU.
        /// </summary>
        /// <param name="flags"><see cref="GpuDeviceFlags.AllowSoftwareAdapter"/> lets Windows pick
        /// WARP (tests on GPU-less runners); ignored elsewhere.</param>
        /// <param name="failureReason">Why there is no device; empty on success.</param>
        public static GpuDevice? TryCreate(GpuDeviceFlags flags, out string failureReason)
        {
            if (!InteropLibrary.IsAvailable)
            {
                failureReason = InteropLibrary.UnavailableReason;
                return null;
            }

            PriscGpuDevice* created = null;
            PriscGpuDeviceInfo info = default;
            int status = NativeMethods.prisc_gpu_device_create((uint)flags, &created, &info);
            if (status != (int)InteropStatus.Ok || created == null)
            {
                failureReason = GpuStatus.Describe(status, "prisc_gpu_device_create");
                return null;
            }

            failureReason = String.Empty;
            return new GpuDevice(created, info);
        }

        /// <summary>The borrowed <c>IDXGIAdapter1*</c> for <c>GRD3DBackendContext.Adapter</c>.</summary>
        [SupportedOSPlatform("windows")]
        public IntPtr D3D12Adapter => Live(_d3d12Adapter);

        /// <summary>The borrowed <c>ID3D12Device*</c> for <c>GRD3DBackendContext.Device</c>.</summary>
        [SupportedOSPlatform("windows")]
        public IntPtr D3D12Device => Live(_d3d12Device);

        /// <summary>The borrowed direct <c>ID3D12CommandQueue*</c> for <c>GRD3DBackendContext.Queue</c>.</summary>
        [SupportedOSPlatform("windows")]
        public IntPtr D3D12Queue => Live(_d3d12Queue);

        /// <summary>The borrowed <c>id&lt;MTLDevice&gt;</c> for <c>GRMtlBackendContext.DeviceHandle</c>.</summary>
        [SupportedOSPlatform("macos")]
        public IntPtr MetalDevice => Live(_metalDevice);

        /// <summary>The borrowed <c>id&lt;MTLCommandQueue&gt;</c> for <c>GRMtlBackendContext.QueueHandle</c>.</summary>
        [SupportedOSPlatform("macos")]
        public IntPtr MetalQueue => Live(_metalQueue);

        /// <summary>
        /// The Direct3D 11 device on the same adapter, <b>with a reference transferred to the
        /// caller</b>: it goes into <c>AVD3D11VADeviceContext.device</c>, and FFmpeg releases it
        /// exactly once when the device context is freed (on every path, a failed init included), so
        /// nothing here calls AddRef or Release. Created on the first call and shared with the NV12
        /// converter. A refused <c>D3D11CreateDevice</c> or an adapter LUID mismatch throws: those
        /// are hard failures, not a GPU that cannot do this. Windows only (elsewhere an
        /// <see cref="InteropException"/> with <see cref="InteropStatus.Unsupported"/>).
        /// </summary>
        public IntPtr AcquireD3D11Device()
        {
            using var lease = Lease();
            void* pointer = null;
            GpuStatus.Check(NativeMethods.prisc_gpu_d3d11_device_acquire_windows((PriscGpuDevice*)lease.Handle, &pointer),
                "prisc_gpu_d3d11_device_acquire_windows");
            return (IntPtr)pointer;
        }

        protected override int Destroy(IntPtr h) => NativeMethods.prisc_gpu_device_destroy((PriscGpuDevice*)h);

        private IntPtr Live(IntPtr pointer)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            return pointer;
        }
    }

    /// <summary>The GPU area's status mapping, shared by the three GPU wrappers.</summary>
    /// <remarks>The video SDK tells three outcomes apart: a misuse of the slot protocol
    /// (<see cref="InvalidOperationException"/>, which <c>IReadbackRing</c> documents), a fence
    /// that did not arrive in time (<see cref="TimeoutException"/>), and a failure of the driver or
    /// the crate (<see cref="InteropException"/>, with the HRESULT in its native code).</remarks>
    internal static class GpuStatus
    {
        /// <summary>Returns for a success (<c>Ok</c> or <c>None</c>); throws for the rest. A benign
        /// <c>Timeout</c> is thrown as a <see cref="TimeoutException"/> because the callers have no
        /// other way to report it.</summary>
        internal static void Check(int status, string operation, string? timeoutMessage = null)
        {
            switch ((InteropStatus)status)
            {
                case InteropStatus.Ok:
                case InteropStatus.None:
                    return;
                case InteropStatus.Timeout:
                    throw new TimeoutException(timeoutMessage ?? $"{operation} timed out.");
                case InteropStatus.State:
                    // The crate's sentence is the message (it is what the ring's misuse reported
                    // in C# before); the InteropException with the operation is the inner one.
                    string detail = Native.LastErrorMessage();
                    var failure = Native.Failure(status, operation);
                    throw new InvalidOperationException(detail.Length > 0 ? detail : failure.Message, failure);
                default:
                    throw Native.Failure(status, operation);
            }
        }

        /// <summary>The reason a <c>TryCreate</c> hands back: the crate's own message (it names the
        /// step and carries the HRESULT), or the operation and status when there is none.</summary>
        internal static string Describe(int status, string operation)
        {
            string detail = InteropLibrary.IsAvailable ? Native.LastErrorMessage() : String.Empty;
            return String.IsNullOrEmpty(detail) ? $"{operation} failed: {(InteropStatus)status}" : detail;
        }
    }

    /// <summary>The one timeout conversion every GPU wrapper uses.</summary>
    internal static class GpuTimeout
    {
        /// <summary>A timeout as the milliseconds the crate takes: <see cref="Timeout.InfiniteTimeSpan"/>
        /// is <c>u32::MAX</c> (INFINITE), anything else negative is zero (one poll), and a very large
        /// finite timeout is clamped below INFINITE.</summary>
        internal static uint Milliseconds(TimeSpan timeout)
        {
            if (timeout == Timeout.InfiniteTimeSpan)
                return UInt32.MaxValue;

            double milliseconds = timeout.TotalMilliseconds;
            if (milliseconds <= 0)
                return 0;

            return milliseconds >= UInt32.MaxValue ? UInt32.MaxValue - 1 : (uint)milliseconds;
        }
    }
}
