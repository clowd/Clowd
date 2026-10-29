using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Prisc.Interop;
using Prisc.Interop.Testing;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Media;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The prisc_interop GPU wrappers (<see cref="GpuDevice"/>, <see cref="GpuReadbackRing"/>,
    /// <see cref="GpuNv12Converter"/>) and the places the video SDK consumes them. Tests that need a
    /// real device skip with the reason when the machine has none (CI runners, VMs, RDP); the
    /// Windows halves run on CI with <c>PRISC_GPU_ALLOW_WARP=1</c>.
    /// </summary>
    public unsafe class GpuInteropTests
    {
        private static GpuDeviceFlags Flags =>
            Environment.GetEnvironmentVariable(EnvVars.GpuAllowWarp) == "1" ? GpuDeviceFlags.AllowSoftwareAdapter : GpuDeviceFlags.None;

        private static GpuDevice RequireDevice()
        {
            InteropTestGate.Require();
            var device = GpuDevice.TryCreate(Flags, out var reason);
            Assert.SkipWhen(device == null, "no GPU device: " + reason);
            return device;
        }

        /// <summary>The C# view of the two structs equals the Rust-pinned layout (ffi/gpu.rs).</summary>
        [Fact]
        public void Struct_layouts_match_the_rust_pins()
        {
            Assert.Equal(64, sizeof(PriscGpuDeviceInfo));
            PriscGpuDeviceInfo info = default;
            byte* basePtr = (byte*)&info;
            Assert.Equal(0, (byte*)&info.backend - basePtr);
            Assert.Equal(4, (byte*)&info._reserved - basePtr);
            Assert.Equal(8, (byte*)&info.adapter_name - basePtr);
            Assert.Equal(24, (byte*)&info.d3d12_adapter - basePtr);
            Assert.Equal(32, (byte*)&info.d3d12_device - basePtr);
            Assert.Equal(40, (byte*)&info.d3d12_queue - basePtr);
            Assert.Equal(48, (byte*)&info.metal_device - basePtr);
            Assert.Equal(56, (byte*)&info.metal_queue - basePtr);

            Assert.Equal(24, sizeof(PriscGpuRingInfo));
            PriscGpuRingInfo ring = default;
            byte* ringBase = (byte*)&ring;
            Assert.Equal(12, (byte*)&ring.row_pitch - ringBase);
            Assert.Equal(16, (byte*)&ring.shareable - ringBase);
        }

        [Fact]
        public void Device_create_reports_the_backend_borrowed_pointers_and_the_adapter_name()
        {
            using var device = RequireDevice();
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(GpuBackend.D3D12, device.Backend);
                Assert.NotEqual(IntPtr.Zero, device.D3D12Adapter);
                Assert.NotEqual(IntPtr.Zero, device.D3D12Device);
                Assert.NotEqual(IntPtr.Zero, device.D3D12Queue);
                Assert.NotEqual(device.D3D12Device, device.D3D12Queue);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Assert.Equal(GpuBackend.Metal, device.Backend);
                Assert.NotEqual(IntPtr.Zero, device.MetalDevice);
                Assert.NotEqual(IntPtr.Zero, device.MetalQueue);
                Assert.NotEqual(device.MetalDevice, device.MetalQueue);
            }

            Assert.False(String.IsNullOrEmpty(device.AdapterName), "every adapter and every Metal device has a name");
        }

        /// <summary>The Windows-only families answer truthfully elsewhere: the factories return null
        /// with <see cref="InteropStatus.Unsupported"/>, the acquire throws it, and the texture
        /// release is a quiet no-op.</summary>
        [Fact]
        public void Windows_only_families_answer_unsupported_elsewhere()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "this is the off-Windows answer");
            using var device = RequireDevice();

            var ring = GpuReadbackRing.TryCreate(device, 16, 16, 2, out var reason, out var status);
            Assert.Null(ring);
            Assert.Equal(InteropStatus.Unsupported, status);
            Assert.Contains("prisc_gpu_readback_ring_create_windows", reason, StringComparison.Ordinal);

            var e = Assert.Throws<InteropException>(() => device.AcquireD3D11Device());
            Assert.Equal(InteropStatus.Unsupported, e.Status);

            GpuNv12Converter.ReleaseTexture(new IntPtr(0x1234)); // never throws
            Assert.Equal((int)InteropStatus.Unsupported, NativeMethods.prisc_gpu_nv12_texture_release_windows((void*)0x1234));
        }

        [Fact]
        public void A_disposed_device_refuses_its_pointers()
        {
            var device = RequireDevice();
            var backend = device.Backend;
            device.Dispose();
            device.Dispose(); // idempotent, as every SafeHandle is
            Assert.Equal(backend, device.Backend); // read at creation
            if (OperatingSystem.IsWindows())
                AssertD3D12GetterThrows(device);
            else if (OperatingSystem.IsMacOS())
                AssertMetalGetterThrows(device);
        }

        [SupportedOSPlatform("windows")]
        private static void AssertD3D12GetterThrows(GpuDevice device)
            => Assert.Throws<ObjectDisposedException>(() => { _ = device.D3D12Device; });

        [SupportedOSPlatform("macos")]
        private static void AssertMetalGetterThrows(GpuDevice device)
            => Assert.Throws<ObjectDisposedException>(() => { _ = device.MetalDevice; });

        /// <summary>The transferred reference: the D3D11 device is created once and every acquire
        /// hands out the same object with one reference for the caller.</summary>
        [Fact]
        public void Acquired_d3d11_device_is_the_same_object_every_time()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Direct3D 11 is Windows only.");
            using var device = RequireDevice();
            IntPtr first = device.AcquireD3D11Device();
            IntPtr second = device.AcquireD3D11Device();
            Assert.NotEqual(IntPtr.Zero, first);
            Assert.Equal(first, second);
            // Both are real references: release them the way FFmpeg releases its own.
            Marshal.Release(second);
            Marshal.Release(first);
        }

        /// <summary>FFmpeg's device context releases exactly the one reference the acquire transfers:
        /// twenty frames contexts over the device leave its reference count where it started.</summary>
        [Fact]
        public void Acquired_d3d11_device_is_released_exactly_once_by_ffmpeg()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Direct3D 11 is Windows only.");
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);
            using var device = RequireDevice();
            IntPtr d3d11 = device.AcquireD3D11Device();
            try
            {
                try
                {
                    using var probe = new HardwareFrames(device, 64, 48, StubAllocate, StubRelease, "probe");
                }
                catch (InvalidOperationException ex)
                {
                    Assert.Skip("FFmpeg has no usable D3D11VA support here: " + ex.Message);
                }

                int before = ReferenceCount(d3d11);
                for (int n = 0; n < 20; n++)
                {
                    using var frames = new HardwareFrames(device, 64, 48, StubAllocate, StubRelease, "refcount guard");
                    Assert.Equal(0, frames.PoolTextures);
                }
                Assert.Equal(before, ReferenceCount(d3d11));
            }
            finally
            {
                Marshal.Release(d3d11);
            }
        }

        private static IntPtr StubAllocate() => IntPtr.Zero;

        private static void StubRelease(IntPtr texture)
        {
        }

        private static int ReferenceCount(IntPtr unknown)
        {
            Marshal.AddRef(unknown);
            return Marshal.Release(unknown);
        }

        /// <summary>The ring holds its device inside the crate: disposing the device wrapper first
        /// is untidy, not a use after free. The crate's creation self-check spent fence value 1.</summary>
        [Fact]
        public void Ring_outlives_a_disposed_device_wrapper()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "The Direct3D 12 readback ring is Windows only.");
            var device = RequireDevice();
            var ring = GpuReadbackRing.TryCreate(device, 64, 48, 2, out var reason, out _);
            Assert.True(ring != null, "a machine with a Direct3D 12 device must build a readback ring: " + reason);
            using (ring)
            {
                Assert.Equal((64, 48, 2), (ring.Width, ring.Height, ring.SlotCount));
                Assert.True(ring.RowPitch >= 64 * 4 && ring.RowPitch % 256 == 0, $"row pitch {ring.RowPitch}");

                device.Dispose();

                IntPtr texture = ring.Begin(0);
                Assert.NotEqual(IntPtr.Zero, texture);
                ring.CancelBegin(0);
                Assert.Equal(texture, ring.Begin(0));
                Assert.Throws<InvalidOperationException>(() => ring.Begin(0)); // composing

                ulong value = ring.Submit(0);
                Assert.Equal(2UL, value);
                var (address, rowBytes) = ring.Wait(0, TimeSpan.FromSeconds(30));
                Assert.NotEqual(IntPtr.Zero, address);
                Assert.Equal(ring.RowPitch, rowBytes);
                ring.WaitConsumed(value, TimeSpan.Zero);
                Assert.Throws<TimeoutException>(() => ring.WaitConsumed(value + 10, TimeSpan.FromMilliseconds(20)));
            }
        }

        /// <summary>Critique F9 in C#: a texture released after its converter, ring and device
        /// wrappers were all disposed (FFmpeg's free path at teardown) is released, not a crash.</summary>
        [Fact]
        public void Nv12_texture_release_after_the_converter_is_disposed_is_safe()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "The NV12 converter is Windows only.");
            var device = RequireDevice();
            var ring = GpuReadbackRing.TryCreate(device, 32, 32, 2, out var reason, out _);
            Assert.True(ring != null, reason);
            var converter = GpuNv12Converter.TryCreate(device, ring, out reason, out var status);
            if (converter == null)
            {
                ring.Dispose();
                device.Dispose();
                Assert.SkipWhen(status == InteropStatus.Capability, "this GPU cannot run the NV12 converter: " + reason);
                Assert.Fail("the converter did not come up over a shareable ring: " + reason);
            }

            IntPtr texture = converter.AcquireTexture();
            Assert.NotEqual(IntPtr.Zero, texture);
            converter.Dispose();
            ring.Dispose();
            device.Dispose();
            GpuNv12Converter.ReleaseTexture(texture);
            GpuNv12Converter.ReleaseTexture(texture); // the second is a no-op
        }

        /// <summary>Every null return carries a reason: the fallback line SurfaceFactory logs is all a
        /// field report about a CPU fallback has to go on.</summary>
        [Fact]
        public void A_failed_gpu_factory_always_explains_itself()
        {
            var factory = GpuSurfaceFactory.TryCreate(out var reason);
            if (factory == null)
            {
                Assert.False(String.IsNullOrEmpty(reason), "a null GPU factory says why it is null");
                return;
            }
            factory.Dispose();
        }

        /// <summary>The adapter name reaches the composition backend log line.</summary>
        [Fact]
        public void Gpu_backend_diagnostic_line_names_the_adapter()
        {
            var logged = new List<string>();
            using var factory = SurfaceFactory.Create(preferGpu: true, logged.Add);
            Assert.SkipWhen(factory is not GpuSurfaceFactory, "no GPU surface factory: " + String.Join(" ", logged));
            var gpu = (GpuSurfaceFactory)factory;
            Assert.False(String.IsNullOrEmpty(gpu.GpuName));
            Assert.Contains(logged, line => line == $"Composition surface backend: {gpu.BackendName} ({gpu.GpuName})");
        }
    }
}
