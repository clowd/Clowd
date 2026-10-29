using System;
using System.Collections.Generic;
using Prisc.Interop;
using Prisc.VideoSDK.Composition;

namespace Prisc.UI.Harness
{
    internal static partial class InteropProbe
    {
        /// <summary>The wrapper-level items of this area: one success, one documented failure and one
        /// round trip per export family, keyed <c>&lt;area&gt;.&lt;item&gt;</c>.</summary>
        static partial void ProbeGpu(ProbeRun run)
        {
            // Success: the device for this OS, with its borrowed pointers (whether they are set, never
            // their values, so two runs compare equal) and the adapter name.
            run.Item("gpu.device.create", () =>
            {
                using var device = GpuDevice.TryCreate(GpuDeviceFlags.None, out var reason);
                if (device == null)
                    return ProbeOutcome.Unavailable(reason);
                return new Dictionary<string, object>
                {
                    ["backend"] = device.Backend.ToString(),
                    ["adapter"] = device.AdapterName,
                    ["pointers"] = OperatingSystem.IsWindows()
                        ? D3D12PointersSet(device)
                        : OperatingSystem.IsMacOS() && MetalPointersSet(device),
                };
            }, anyThread: true);

            // Documented failure: an unknown flag bit is an argument error.
            run.Item("gpu.device.bad_flags", () =>
            {
                var device = GpuDevice.TryCreate((GpuDeviceFlags)0x10, out var reason);
                device?.Dispose();
                if (device == null && reason.Contains("unknown bits", StringComparison.Ordinal))
                    return "InvalidArg";
                return new ProbeOutcome("error", Error: "an unknown flag bit was not refused: " + reason);
            }, anyThread: true);

            // The ring and the converter: a real round trip on Windows (the crate self-checks both),
            // a truthful Unsupported elsewhere.
            run.Item("gpu.ring.create", () =>
            {
                using var device = GpuDevice.TryCreate(GpuDeviceFlags.None, out var reason);
                if (device == null)
                    return ProbeOutcome.Unavailable(reason);
                using var ring = GpuReadbackRing.TryCreate(device, 64, 48, 2, out reason, out var status);
                if (ring == null)
                    return status == InteropStatus.Unsupported ? ProbeOutcome.Unsupported(reason) : ProbeOutcome.Unavailable(reason);
                // Creation runs the conversion self-check, so a converter that comes back is checked;
                // a capable GPU on which it failed is an error, as a failed check always was.
                using var converter = GpuNv12Converter.TryCreate(device, ring, out var converterReason, out var converterStatus);
                if (converter == null && converterStatus != InteropStatus.Capability)
                    return new ProbeOutcome("error", Error: "the NV12 converter failed on a capable GPU: " + converterReason);
                string nv12 = converter != null ? "self-check ok" : "unavailable: " + converterReason;
                return new Dictionary<string, object>
                {
                    ["rowPitch"] = ring.RowPitch,
                    ["shareable"] = ring.Shareable,
                    ["nv12"] = nv12,
                };
            }, anyThread: true);

            // Round trip through Skia: the composer's factory on this device, and the readback ring it
            // picks (asynchronous Direct3D 12 on Windows, synchronous on Metal).
            run.Item("gpu.surface_factory", () =>
            {
                var log = new List<string>();
                using var factory = SurfaceFactory.Create(preferGpu: true, log.Add);
                using var ring = factory.CreateReadbackRing(64, 48, 2, log.Add);
                return new Dictionary<string, object>
                {
                    ["backend"] = factory.BackendName,
                    ["ring"] = ring.Description,
                    ["log"] = log,
                };
            }, anyThread: true);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static bool D3D12PointersSet(GpuDevice device)
            => device.D3D12Adapter != IntPtr.Zero && device.D3D12Device != IntPtr.Zero && device.D3D12Queue != IntPtr.Zero;

        [System.Runtime.Versioning.SupportedOSPlatform("macos")]
        private static bool MetalPointersSet(GpuDevice device)
            => device.MetalDevice != IntPtr.Zero && device.MetalQueue != IntPtr.Zero;
    }
}
