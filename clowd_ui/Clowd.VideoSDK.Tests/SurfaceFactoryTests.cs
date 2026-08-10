using System;
using System.Runtime.InteropServices;
using System.Threading;
using Clowd.VideoSDK.Composition;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    public class SurfaceFactoryTests
    {
        // A flat-color scene with no anti-aliased edges: rasterization must be bit-identical
        // across backends, so the GPU/CPU equivalence check can be exact.
        private static void DrawTestScene(SKCanvas canvas)
        {
            canvas.Clear(new SKColor(255, 0, 0)); // red
            using var green = new SKPaint { Color = new SKColor(0, 255, 0), IsAntialias = false };
            canvas.DrawRect(SKRect.Create(8, 8, 16, 16), green);
            using var blue = new SKPaint { Color = new SKColor(0, 0, 255, 128), IsAntialias = false };
            canvas.DrawRect(SKRect.Create(16, 16, 32, 32), blue);
        }

        private static byte[] RenderAndReadback(ISurfaceFactory factory, int w, int h)
        {
            using var surface = factory.CreateSurface(w, h);
            DrawTestScene(surface.Canvas);

            int rowBytes = w * 4;
            var native = Marshal.AllocHGlobal(rowBytes * h);
            try
            {
                Assert.True(factory.TryReadPixels(surface, w, h, native, rowBytes));
                var pixels = new byte[rowBytes * h];
                Marshal.Copy(native, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        private static (byte B, byte G, byte R, byte A) PixelAt(byte[] bgra, int rowBytes, int x, int y)
        {
            int i = y * rowBytes + x * 4;
            return (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
        }

        [Fact]
        public void Cpu_factory_draw_and_readback()
        {
            using var factory = new CpuSurfaceFactory();
            Assert.Equal("CPU", factory.BackendName);
            Assert.Null(factory.Context);

            const int w = 64, h = 64;
            var pixels = RenderAndReadback(factory, w, h);
            int rowBytes = w * 4;

            Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), PixelAt(pixels, rowBytes, 2, 2));   // red bg
            Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)255), PixelAt(pixels, rowBytes, 10, 10)); // green rect
            // premul srcover of 50% blue over red: R = 255-127 = 128, B = 128
            var blended = PixelAt(pixels, rowBytes, 40, 40);
            Assert.Equal(255, blended.A);
            Assert.InRange(blended.B, 126, 130);
            Assert.InRange(blended.R, 126, 130);
            Assert.Equal(0, blended.G);
        }

        [Fact]
        public void Create_prefer_cpu_returns_cpu()
        {
            string logged = null;
            using var factory = SurfaceFactory.Create(preferGpu: false, m => logged = m);
            Assert.IsType<CpuSurfaceFactory>(factory);
            Assert.Null(factory.Context);
            Assert.Contains("CPU", logged);
        }

        [Fact]
        public void Create_prefer_gpu_always_yields_a_working_factory()
        {
            // On dev boxes this selects the GPU; on RDP/CI it must fall back to CPU — either
            // way Create never returns null and the result can draw.
            using var factory = SurfaceFactory.Create(preferGpu: true);
            Assert.NotNull(factory);
            var pixels = RenderAndReadback(factory, 16, 16);
            Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), PixelAt(pixels, 16 * 4, 2, 2));
        }

        [Fact]
        public void Gpu_factory_matches_cpu_pixels()
        {
            var gpu = GpuSurfaceFactory.TryCreate(out var reason);
            if (gpu == null)
                Assert.Skip("GPU backend unavailable: " + reason);

            try
            {
                Assert.NotNull(gpu.Context);

                const int w = 64, h = 64;
                var gpuPixels = RenderAndReadback(gpu, w, h);
                using var cpu = new CpuSurfaceFactory();
                var cpuPixels = RenderAndReadback(cpu, w, h);

                // Equivalence seed test: flat-color scene must match exactly (tolerance 1 to
                // allow for rounding differences in blend units).
                Assert.Equal(cpuPixels.Length, gpuPixels.Length);
                for (int i = 0; i < cpuPixels.Length; i++)
                {
                    if (Math.Abs(cpuPixels[i] - gpuPixels[i]) > 1)
                        Assert.Fail($"Pixel byte {i} differs: cpu={cpuPixels[i]} gpu={gpuPixels[i]}");
                }
            }
            finally
            {
                gpu.Dispose();
            }
        }

        [Fact]
        public void Composer_thread_owns_factory_and_runs_work_on_its_thread()
        {
            using var composer = ComposerThread.Start(preferGpu: false);
            Assert.NotNull(composer.Factory);
            Assert.Equal("CPU", composer.BackendName);
            Assert.False(composer.IsCurrent);

            int composerThreadId = composer.Send(() => Environment.CurrentManagedThreadId);
            Assert.NotEqual(Environment.CurrentManagedThreadId, composerThreadId);

            // Post executes on the same thread.
            int postedThreadId = 0;
            using var done = new ManualResetEventSlim();
            composer.Post(() =>
            {
                postedThreadId = Environment.CurrentManagedThreadId;
                done.Set();
            });
            Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(composerThreadId, postedThreadId);

            // Send from within the composer thread executes inline without deadlocking.
            bool nestedRan = composer.Send(() =>
            {
                Assert.True(composer.IsCurrent);
                bool inner = false;
                composer.Send(() => inner = true);
                return inner;
            });
            Assert.True(nestedRan);

            // Factory work (surface creation) is legal on the composer thread.
            bool surfaceOk = composer.Send(() =>
            {
                using var s = composer.Factory.CreateSurface(8, 8);
                s.Canvas.Clear(SKColors.White);
                return true;
            });
            Assert.True(surfaceOk);
        }

        [Fact]
        public void Composer_thread_send_propagates_exceptions()
        {
            using var composer = ComposerThread.Start(preferGpu: false);
            var ex = Assert.Throws<InvalidOperationException>(
                () => composer.Send(() => throw new InvalidOperationException("boom")));
            Assert.Equal("boom", ex.Message);

            // The thread survives a failed work item.
            Assert.Equal(42, composer.Send(() => 42));
        }

        [Fact]
        public void Composer_thread_dispose_joins_and_rejects_new_work()
        {
            var composer = ComposerThread.Start(preferGpu: false);
            composer.Dispose();
            Assert.Throws<ObjectDisposedException>(() => composer.Post(() => { }));
            composer.Dispose(); // idempotent
        }
    }
}
