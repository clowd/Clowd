using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Clowd.VideoSDK.Composition;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // The asynchronous readback contract (IReadbackRing): the synchronous ring on the CPU
    // backend everywhere, the Direct3D 12 copy-queue ring when a GPU is present. Scenes are
    // flat-colour and non-anti-aliased so GPU and CPU rasterization are bit-identical and every
    // comparison can be exact.
    public class ReadbackRingTests
    {
        private static SKColor FrameColor(int n) =>
            new SKColor((byte)((n * 37) & 0xff), (byte)((n * 91) & 0xff), (byte)((n * 53) & 0xff));

        /// <summary>Frame <paramref name="n"/> of the test sequence: a per-frame background, a
        /// wandering white square and a centre block in the frame colour — enough to tell any two
        /// frames (and any slot mix-up) apart.</summary>
        private static void DrawFrame(SKCanvas canvas, int n, int w, int h)
        {
            canvas.Clear(FrameColor(n));
            using var paint = new SKPaint { IsAntialias = false, Color = SKColors.White };
            canvas.DrawRect((n * 7) % Math.Max(1, w - 16), (n * 11) % Math.Max(1, h - 16), 16, 16, paint);
            for (int i = 0; i < 40; i++)
            {
                paint.Color = new SKColor((byte)(i * 3), (byte)(i * 5), (byte)(i * 7));
                canvas.DrawRect((i * 37 + n) % Math.Max(1, w - 8), (i * 53) % Math.Max(1, h - 8), 8, 8, paint);
            }
            paint.Color = FrameColor(n);
            canvas.DrawRect(w / 2 - 8, h / 2 - 8, 16, 16, paint);
        }

        /// <summary>The CPU raster of frame <paramref name="n"/>, tightly packed BGRA.</summary>
        private static byte[] Reference(int n, int w, int h)
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var surface = SKSurface.Create(info, bitmap.GetPixels(), bitmap.RowBytes);
            DrawFrame(surface.Canvas, n, w, h);
            var pixels = new byte[w * 4 * h];
            for (int y = 0; y < h; y++)
                Marshal.Copy(bitmap.GetPixels() + y * bitmap.RowBytes, pixels, y * w * 4, w * 4);
            return pixels;
        }

        /// <summary>Copies a slot's (possibly row-padded) pixels into a tightly packed array.</summary>
        private static byte[] Pack(ReadbackPixels pixels, int w, int h)
        {
            Assert.True(pixels.RowBytes >= w * 4, $"row bytes {pixels.RowBytes} < {w * 4}");
            var packed = new byte[w * 4 * h];
            for (int y = 0; y < h; y++)
                Marshal.Copy(pixels.Address + y * pixels.RowBytes, packed, y * w * 4, w * 4);
            return packed;
        }

        private static void AssertFrame(byte[] expected, byte[] actual, int frame, int slot)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                    Assert.Fail($"frame {frame} (slot {slot}): byte {i} is {actual[i]}, expected {expected[i]}");
            }
        }

        /// <summary>Runs <paramref name="frames"/> frames through the ring the way RenderJob does:
        /// slot n % SlotCount, waiting for a slot's previous occupant (on another thread, as the
        /// convert stage does) before drawing into it, and checks every frame that comes back.</summary>
        private static void RunPipelined(IReadbackRing ring, int frames)
        {
            int w = ring.Width, h = ring.Height, slots = ring.SlotCount;
            var occupant = new int[slots];
            Array.Fill(occupant, -1);

            void Consume(int slot)
            {
                var pixels = Task.Run(() => ring.Wait(slot)).GetAwaiter().GetResult();
                AssertFrame(Reference(occupant[slot], w, h), Pack(pixels, w, h), occupant[slot], slot);
                occupant[slot] = -1;
            }

            for (int n = 0; n < frames; n++)
            {
                int slot = n % slots;
                if (occupant[slot] >= 0)
                    Consume(slot);
                DrawFrame(ring.Begin(slot), n, w, h);
                ring.Submit(slot);
                occupant[slot] = n;
            }

            for (int slot = 0; slot < slots; slot++)
            {
                if (occupant[slot] >= 0)
                    Consume(slot);
            }
        }

        // -------------------------------------------------------------------------- CPU / sync

        [Fact]
        public void Cpu_factory_ring_is_synchronous_and_round_trips_every_slot()
        {
            using var factory = new CpuSurfaceFactory();
            // the render loop's request: a synchronous ring has no copy in flight to cover, so
            // the factory holds only the two slots it can use (one composing, one being read)
            using var ring = factory.CreateReadbackRing(33, 21, RenderJobSlots);
            Assert.IsType<SyncReadbackRing>(ring);
            Assert.Equal(33, ring.Width);
            Assert.Equal(21, ring.Height);
            Assert.Equal(SyncReadbackRing.MaxUsefulSlots, ring.SlotCount);
            Assert.Contains("synchronous", ring.Description);

            RunPipelined(ring, 11); // more frames than slots, an odd count: every slot is reused
        }

        [Fact]
        public void Sync_ring_depth_is_capped_by_the_factory_but_exact_from_the_constructor()
        {
            using var factory = new CpuSurfaceFactory();
            foreach (var (requested, expected) in new[] { (1, 1), (2, 2), (3, 2), (RenderJobSlots, 2) })
            {
                using var ring = factory.CreateReadbackRing(8, 8, requested);
                Assert.Equal(expected, ring.SlotCount);
            }
            Assert.Equal(1, SyncReadbackRing.UsefulSlots(1));
            Assert.Equal(SyncReadbackRing.MaxUsefulSlots, SyncReadbackRing.UsefulSlots(RenderJobSlots));
            Assert.Throws<ArgumentOutOfRangeException>(() => SyncReadbackRing.UsefulSlots(0));

            // the constructor honours its argument: a caller with its own reason for depth
            using var deep = new SyncReadbackRing(factory, 8, 8, 4);
            Assert.Equal(4, deep.SlotCount);
            RunPipelined(deep, 9);
        }

        [Fact]
        public void Sync_ring_pixels_match_try_read_pixels()
        {
            using var factory = new CpuSurfaceFactory();
            using var ring = factory.CreateReadbackRing(40, 24, 2);

            DrawFrame(ring.Begin(1), 5, 40, 24);
            ring.Submit(1);
            var viaRing = Pack(ring.Wait(1), 40, 24);

            using var surface = factory.CreateSurface(40, 24);
            DrawFrame(surface.Canvas, 5, 40, 24);
            var native = Marshal.AllocHGlobal(40 * 4 * 24);
            try
            {
                Assert.True(factory.TryReadPixels(surface, 40, 24, native, 40 * 4));
                var viaFactory = new byte[40 * 4 * 24];
                Marshal.Copy(native, viaFactory, 0, viaFactory.Length);
                Assert.Equal(viaFactory, viaRing);
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        [Fact]
        public void Ring_rejects_out_of_order_use()
        {
            using var factory = new CpuSurfaceFactory();
            using var ring = factory.CreateReadbackRing(8, 8, 2);

            Assert.Throws<InvalidOperationException>(() => ring.Wait(0));   // never begun
            Assert.Throws<InvalidOperationException>(() => ring.Submit(0)); // never begun
            ring.Begin(0);
            Assert.Throws<InvalidOperationException>(() => ring.Begin(0));  // begun twice
            Assert.Throws<InvalidOperationException>(() => ring.Wait(0));   // not submitted
            ring.Submit(0);
            Assert.Throws<InvalidOperationException>(() => ring.Begin(0));  // in flight
            ring.Wait(0);
            ring.Begin(0); // idle again after Wait
            Assert.Throws<ArgumentOutOfRangeException>(() => ring.Begin(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => ring.Begin(-1));
        }

        [Fact]
        public void Ring_slots_are_independent()
        {
            using var factory = new CpuSurfaceFactory();
            // built directly: a factory caps the synchronous ring at two slots
            using var ring = new SyncReadbackRing(factory, 16, 16, 3);

            // submit three different frames, then read them back in a different order
            for (int slot = 0; slot < 3; slot++)
            {
                DrawFrame(ring.Begin(slot), 100 + slot, 16, 16);
                ring.Submit(slot);
            }

            foreach (int slot in new[] { 2, 0, 1 })
                AssertFrame(Reference(100 + slot, 16, 16), Pack(ring.Wait(slot), 16, 16), 100 + slot, slot);
        }

        /// <summary>A factory that does not override CreateReadbackRing (the shape of the app's
        /// Avalonia-lease adapter) gets the synchronous ring from the interface default.</summary>
        private sealed class BareFactory : ISurfaceFactory
        {
            private readonly CpuSurfaceFactory _inner = new CpuSurfaceFactory();

            public string BackendName => "bare";

            public GRContext Context => null;

            public SKSurface CreateSurface(int width, int height) => _inner.CreateSurface(width, height);

            public bool TryReadPixels(SKSurface surface, int width, int height, IntPtr dst, int rowBytes)
                => _inner.TryReadPixels(surface, width, height, dst, rowBytes);

            public void Dispose() => _inner.Dispose();
        }

        [Fact]
        public void Interface_default_ring_is_the_synchronous_one()
        {
            using ISurfaceFactory factory = new BareFactory(); // default members bind through the interface
            using var ring = factory.CreateReadbackRing(12, 10, 2);
            Assert.IsType<SyncReadbackRing>(ring);
            RunPipelined(ring, 5);
        }

        [Fact]
        public void Composer_thread_creates_and_disposes_a_ring_on_thread()
        {
            using var composer = ComposerThread.Start(preferGpu: false);
            IReadbackRing ring = null;
            composer.Send(() => ring = composer.Factory.CreateReadbackRing(20, 20, 2));
            Assert.NotNull(ring);

            // draw on the composer thread, wait on this one — the render loop's split
            composer.Send(() =>
            {
                DrawFrame(ring.Begin(0), 3, 20, 20);
                ring.Submit(0);
            });
            AssertFrame(Reference(3, 20, 20), Pack(ring.Wait(0), 20, 20), 3, 0);
            composer.Send(() => ring.Dispose());
        }

        // ---------------------------------------------------------------------- Direct3D 12

        private static GpuSurfaceFactory RequireD3D12()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Direct3D 12 is Windows-only.");
            var gpu = GpuSurfaceFactory.TryCreate(out var reason);
            Assert.SkipWhen(gpu == null, "GPU backend unavailable: " + reason);
            return gpu;
        }

        [Fact]
        [System.Runtime.Versioning.SupportedOSPlatform("windows")] // RequireD3D12 skips elsewhere; this tells the analyzer
        public void D3d12_ring_is_asynchronous_and_matches_the_cpu_raster_bit_exactly()
        {
            var gpu = RequireD3D12();
            try
            {
                var log = new List<string>();
                // an odd width whose rows do not fill a 256-byte pitch: the readback heap pads them
                using var ring = gpu.CreateReadbackRing(333, 97, RenderJobSlots, log.Add);
                Assert.True(ring.Description.StartsWith("Direct3D 12", StringComparison.Ordinal),
                    $"expected the asynchronous ring on a working D3D12 device, got '{ring.Description}' " +
                    string.Join(" | ", log));
                Assert.Equal(RenderJobSlots, ring.SlotCount);
                // the flag the zero-copy path keys off and the diagnostic line must agree, whether
                // or not this driver granted the shared-handle flags
                var d3d = Assert.IsType<D3D12ReadbackRing>(ring);
                Assert.Equal(d3d.Shareable, ring.Description.EndsWith(", shareable", StringComparison.Ordinal));

                DrawFrame(ring.Begin(0), 0, 333, 97);
                ring.Submit(0);
                var pixels = ring.Wait(0);
                Assert.True(pixels.RowBytes >= 333 * 4 && pixels.RowBytes % 256 == 0,
                    $"readback rows should be 256-byte aligned, got {pixels.RowBytes}");
                AssertFrame(Reference(0, 333, 97), Pack(pixels, 333, 97), 0, 0);

                RunPipelined(ring, 3 * RenderJobSlots + 1); // every slot reused several times, cross-thread waits
            }
            finally
            {
                gpu.Dispose();
            }
        }

        [Fact]
        public void D3d12_ring_matches_the_factory_surface_readback()
        {
            var gpu = RequireD3D12();
            try
            {
                const int w = 128, h = 96;
                using var ring = gpu.CreateReadbackRing(w, h, 2);
                DrawFrame(ring.Begin(0), 9, w, h);
                ring.Submit(0);
                var viaRing = Pack(ring.Wait(0), w, h);

                // the pre-existing synchronous path on the same GPU context
                using var surface = gpu.CreateSurface(w, h);
                DrawFrame(surface.Canvas, 9, w, h);
                var native = Marshal.AllocHGlobal(w * 4 * h);
                try
                {
                    Assert.True(gpu.TryReadPixels(surface, w, h, native, w * 4));
                    var viaSurface = new byte[w * 4 * h];
                    Marshal.Copy(native, viaSurface, 0, viaSurface.Length);
                    Assert.Equal(viaSurface, viaRing);
                }
                finally
                {
                    Marshal.FreeHGlobal(native);
                }
            }
            finally
            {
                gpu.Dispose();
            }
        }

        /// <summary>Anti-aliased content, where rasterization is sensitive to the surface origin:
        /// the ring's wrapped texture is TopLeft while a Skia-owned surface is BottomLeft, so this
        /// pins down whether the two readback paths can differ at anti-aliased edges.</summary>
        private static void DrawAntialiased(SKCanvas canvas, int w, int h)
        {
            canvas.Clear(new SKColor(30, 40, 50));
            using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(220, 180, 40) };
            canvas.DrawCircle(w * 0.37f, h * 0.61f, Math.Min(w, h) * 0.3f, paint);
            paint.Color = new SKColor(40, 200, 120, 160);
            canvas.DrawRoundRect(SKRect.Create(w * 0.2f + 0.3f, h * 0.15f + 0.7f, w * 0.5f, h * 0.4f), 9.5f, 9.5f, paint);
            using var path = new SKPath();
            path.MoveTo(3.2f, 4.7f);
            path.LineTo(w - 7.1f, h * 0.33f);
            path.LineTo(w * 0.5f, h - 2.5f);
            path.Close();
            paint.Color = new SKColor(255, 255, 255, 90);
            canvas.DrawPath(path, paint);
        }

        [Fact]
        public void D3d12_ring_matches_the_factory_surface_readback_for_antialiased_content()
        {
            var gpu = RequireD3D12();
            try
            {
                const int w = 160, h = 120;
                using var ring = gpu.CreateReadbackRing(w, h, 2);
                DrawAntialiased(ring.Begin(1), w, h);
                ring.Submit(1);
                var viaRing = Pack(ring.Wait(1), w, h);

                using var surface = gpu.CreateSurface(w, h);
                DrawAntialiased(surface.Canvas, w, h);
                var native = Marshal.AllocHGlobal(w * 4 * h);
                try
                {
                    Assert.True(gpu.TryReadPixels(surface, w, h, native, w * 4));
                    var viaSurface = new byte[w * 4 * h];
                    Marshal.Copy(native, viaSurface, 0, viaSurface.Length);
                    int worst = 0, differing = 0;
                    for (int i = 0; i < viaSurface.Length; i++)
                    {
                        int d = Math.Abs(viaSurface[i] - viaRing[i]);
                        worst = Math.Max(worst, d);
                        if (d != 0)
                            differing++;
                    }
                    Assert.True(worst == 0,
                        $"ring and surface readback differ: worst {worst}, {differing} of {viaSurface.Length} bytes");
                }
                finally
                {
                    Marshal.FreeHGlobal(native);
                }
            }
            finally
            {
                gpu.Dispose();
            }
        }

        /// <summary>A texture drawn scaled at fractional offsets — bilinear sampling, where the
        /// two readback paths could plausibly disagree: the ring's wrapped texture is a
        /// TopLeft-origin render target, a Skia-owned surface is BottomLeft, and the flipped
        /// projection could evaluate the sample positions with different rounding. Measured:
        /// it does not; sampled content is bit-exact like the geometry above, so the ring changes
        /// nothing about what a frame looks like.</summary>
        [Fact]
        public void D3d12_ring_and_factory_surface_agree_on_sampled_images()
        {
            var gpu = RequireD3D12();
            try
            {
                const int w = 160, h = 120;
                using var source = SKImage.FromPixelCopy(
                    new SKImageInfo(64, 48, SKColorType.Bgra8888, SKAlphaType.Premul), NoiseBitmap(64, 48), 64 * 4);
                using var texture = source.ToTextureImage(gpu.Context);
                Assert.NotNull(texture);
                using var paint = new SKPaint { IsAntialias = true };
                var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
                void Draw(SKCanvas canvas)
                {
                    canvas.Clear(SKColors.Black);
                    canvas.DrawImage(texture, SKRect.Create(7.3f, 11.6f, 64 * 1.37f, 48 * 1.37f), sampling, paint);
                    canvas.DrawImage(texture, SKRect.Create(90.5f, 20.25f, 64 * 0.83f, 48 * 0.83f), sampling, paint);
                }

                using var ring = gpu.CreateReadbackRing(w, h, 2);
                Draw(ring.Begin(0));
                ring.Submit(0);
                var viaRing = Pack(ring.Wait(0), w, h);

                using var surface = gpu.CreateSurface(w, h);
                Draw(surface.Canvas);
                var native = Marshal.AllocHGlobal(w * 4 * h);
                try
                {
                    Assert.True(gpu.TryReadPixels(surface, w, h, native, w * 4));
                    var viaSurface = new byte[w * 4 * h];
                    Marshal.Copy(native, viaSurface, 0, viaSurface.Length);
                    int worst = 0, differing = 0;
                    for (int i = 0; i < viaSurface.Length; i++)
                    {
                        int d = Math.Abs(viaSurface[i] - viaRing[i]);
                        worst = Math.Max(worst, d);
                        if (d != 0)
                            differing++;
                    }
                    Assert.True(worst == 0,
                        $"sampled content differs by up to {worst} in {differing} of {viaSurface.Length} bytes");
                }
                finally
                {
                    Marshal.FreeHGlobal(native);
                }
            }
            finally
            {
                gpu.Dispose();
            }
        }

        private static byte[] NoiseBitmap(int w, int h)
        {
            var rng = new Random(w * h);
            var pixels = new byte[w * h * 4];
            rng.NextBytes(pixels);
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255; // opaque, so premultiplication does not enter into it
            return pixels;
        }

        /// <summary>The shared protocol's escape hatch: a consumer that failed before it could
        /// signal gives the slot up through AbandonShared — no wait, the slot is idle again, the
        /// consumed fence is brought up to the abandoned value from the CPU so nothing that
        /// counts on it (WaitConsumed, the ring's own teardown) can hang, and the readback
        /// protocol carries on over the same slot with the next value.</summary>
        [Fact]
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public void D3d12_abandoned_shared_slot_goes_idle_and_the_fence_moves_on()
        {
            var gpu = RequireD3D12();
            try
            {
                const int w = 24, h = 16;
                using var ring = gpu.CreateReadbackRing(w, h, 2);
                var d3d = Assert.IsType<D3D12ReadbackRing>(ring);

                DrawFrame(ring.Begin(0), 1, w, h);
                ulong value = d3d.SubmitShared(0);
                Assert.Throws<InvalidOperationException>(() => ring.Begin(0)); // in flight
                d3d.AbandonShared(0);
                d3d.WaitConsumed(value); // reached from the CPU: returns at once instead of timing out
                Assert.Throws<InvalidOperationException>(() => d3d.AbandonShared(0)); // idle: nothing to abandon
                Assert.Throws<InvalidOperationException>(() => d3d.ReleaseShared(0));

                // readback over the same slot, with the next fence value, still comes back right
                DrawFrame(ring.Begin(0), 2, w, h);
                ring.Submit(0);
                AssertFrame(Reference(2, w, h), Pack(ring.Wait(0), w, h), 2, 0);

                // a slot submitted for readback is not the shared protocol's to abandon
                DrawFrame(ring.Begin(1), 3, w, h);
                ring.Submit(1);
                Assert.Throws<InvalidOperationException>(() => d3d.AbandonShared(1));
                AssertFrame(Reference(3, w, h), Pack(ring.Wait(1), w, h), 3, 1);
            }
            finally
            {
                gpu.Dispose();
            }
        }

        [Fact]
        public void D3d12_ring_survives_many_frames_and_dispose_before_factory()
        {
            var gpu = RequireD3D12();
            try
            {
                var ring = gpu.CreateReadbackRing(64, 64, 3);
                RunPipelined(ring, 200);
                ring.Dispose();
                ring.Dispose(); // idempotent
                Assert.Throws<ObjectDisposedException>(() => ring.Begin(0));

                // the factory still works after its ring is gone
                using var surface = gpu.CreateSurface(8, 8);
                surface.Canvas.Clear(SKColors.White);
                surface.Flush(submit: true, synchronous: true);
            }
            finally
            {
                gpu.Dispose();
            }
        }

        private const int RenderJobSlots = Render.RenderJob.ReadbackSlots;
    }
}
