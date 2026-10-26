using System;
using System.Runtime.InteropServices;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="VideoEffect"/> rendering through <c>FrameComposer</c> on the CPU factory —
    /// the raster half of the WYSIWYG contract: whatever these pixels prove holds identically in
    /// preview and render, because both run this exact code. A synthetic frame + synthetic mask
    /// ride a fake <see cref="IFrameSource"/>, so the tests are pure pixel math with no FFmpeg.
    /// </summary>
    public class VideoEffectComposeTests
    {
        private const long Sec = 10_000_000;
        private const int W = 64, H = 64;

        // ---------------------------------------------------------------------------- builders

        private static Project NewProject(int width = W, int height = H) => new Project
        {
            Output = new OutputSettings { WidthPx = width, HeightPx = height, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
        };

        private static Item AddMediaItem(Project p, VideoEffect effect)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0 };
            p.Tracks.Add(track);
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = 10 * Sec,
                Content = new MediaContent { SourceId = Guid.NewGuid(), StreamIndex = 0 },
                Effect = effect,
            };
            p.Items.Add(item);
            return item;
        }

        private static byte[] Render(Project p, IFrameSource frames, int width = W, int height = H)
        {
            using var factory = new CpuSurfaceFactory();
            using var surface = factory.CreateSurface(width, height);
            FrameComposer.Compose(p, 5 * Sec, frames, surface.Canvas, width, height);

            int rowBytes = width * 4;
            var native = Marshal.AllocHGlobal(rowBytes * height);
            try
            {
                Assert.True(factory.TryReadPixels(surface, width, height, native, rowBytes));
                var pixels = new byte[rowBytes * height];
                Marshal.Copy(native, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        private static (byte B, byte G, byte R, byte A) Px(byte[] bgra, int x, int y, int width = W)
        {
            int i = y * width * 4 + x * 4;
            return (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
        }

        /// <summary>A raster image painted by <paramref name="paint"/> — frames and masks alike
        /// (a mask is gray in luma: white = subject, black = background).</summary>
        private static SKImage MakeImage(int width, int height, Action<SKCanvas> paint)
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            paint(surface.Canvas);
            return surface.Snapshot();
        }

        /// <summary>Solid red frame.</summary>
        private static SKImage RedFrame(int size = 32) =>
            MakeImage(size, size, c => c.Clear(new SKColor(255, 0, 0)));

        /// <summary>Vertical 2px black/white stripes — sharp detail a blur visibly destroys.</summary>
        private static SKImage StripeFrame(int size = 64) => MakeImage(size, size, c =>
        {
            c.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = SKColors.White };
            for (int x = 0; x < size; x += 4)
                c.DrawRect(SKRect.Create(x, 0, 2, size), paint);
        });

        /// <summary>Mask with the LEFT half subject (white) and the right half background
        /// (black) — deliberately a different resolution from every frame it is paired with,
        /// because the pairing must be fractional, never pixel-for-pixel.</summary>
        private static SKImage HalfMask(int size = 16) => MakeImage(size, size, c =>
        {
            c.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = SKColors.White };
            c.DrawRect(SKRect.Create(0, 0, size / 2f, size), paint);
        });

        private sealed class MaskedFrameSource : IFrameSource, IDisposable
        {
            private readonly SKImage _image;
            private readonly SKImage _mask;

            public MaskedFrameSource(SKImage image, SKImage mask)
            {
                _image = image;
                _mask = mask;
            }

            public bool TryGetFrame(Guid sourceId, int streamIndex, long sourceTimeTicks, out FrameRef frame)
            {
                frame = new FrameRef(_image, sourceTimeTicks, _mask);
                return true;
            }

            public void Dispose()
            {
                _image.Dispose();
                _mask?.Dispose();
            }
        }

        // ---------------------------------------------------------------------------- BgRemove

        [Fact]
        public void BgRemove_multiplies_alpha_per_the_mask()
        {
            // red frame, mask = left half subject: the right half vanishes onto the black canvas
            var p = NewProject();
            AddMediaItem(p, new VideoEffect { Kind = VideoEffectKind.BgRemove });

            using var frames = new MaskedFrameSource(RedFrame(), HalfMask());
            var px = Render(p, frames);

            AssertColor(Px(px, 16, H / 2), 0, 0, 255);      // subject side: full red
            AssertColor(Px(px, 48, H / 2), 0, 0, 0);        // background side: removed
        }

        [Fact]
        public void BgRemove_scales_alpha_by_the_mattes_gray_level()
        {
            // a uniform mid-gray matte halves the alpha: red over black composes to half red
            var p = NewProject();
            AddMediaItem(p, new VideoEffect { Kind = VideoEffectKind.BgRemove });

            using var gray = MakeImage(8, 8, c => c.Clear(new SKColor(128, 128, 128)));
            using var frames = new MaskedFrameSource(RedFrame(), gray);
            var px = Render(p, frames);

            AssertColor(Px(px, W / 2, H / 2), 0, 0, 128, tolerance: 6);
        }

        // ------------------------------------------------------------------------------ BgBlur

        [Fact]
        public void BgBlur_keeps_the_subject_sharp_and_changes_the_background()
        {
            var p = NewProject();
            AddMediaItem(p, new VideoEffect { Kind = VideoEffectKind.BgBlur, Amount = 0.5 });

            var plainProject = NewProject();
            AddMediaItem(plainProject, null);

            using var frames = new MaskedFrameSource(StripeFrame(), HalfMask());
            using var plainFrames = new MaskedFrameSource(StripeFrame(), null);
            var fx = Render(p, frames);
            var plain = Render(plainProject, plainFrames);

            // subject half (sampled away from the mask seam): pixel-identical to the sharp draw
            for (int x = 4; x < 24; x++)
            {
                var a = Px(fx, x, H / 2);
                var b = Px(plain, x, H / 2);
                Assert.InRange(a.G, Math.Max(0, b.G - 2), Math.Min(255, b.G + 2));
            }

            // background half: the stripes' extremes are gone — every sample sits mid-gray,
            // where the sharp draw alternates 0/255
            for (int x = 40; x < 60; x++)
            {
                var a = Px(fx, x, H / 2);
                Assert.InRange(a.G, 40, 215);
            }
        }

        // -------------------------------------------------------------------------------- blur

        [Fact]
        public void Blur_changes_pixels_and_needs_no_mask()
        {
            var p = NewProject();
            AddMediaItem(p, new VideoEffect { Kind = VideoEffectKind.Blur, Amount = 0.5 });

            using var frames = new MaskedFrameSource(StripeFrame(), null);
            var px = Render(p, frames);

            // stripes flattened everywhere, and the edges stay opaque (clamp tiling)
            for (int x = 4; x < 60; x += 3)
                Assert.InRange(Px(px, x, H / 2).G, 40, 215);
            Assert.Equal(255, Px(px, 0, H / 2).A);
            Assert.Equal(255, Px(px, W - 1, H / 2).A);
        }

        /// <summary>The WYSIWYG property of the blur dial: its sigma is canvas-relative, so
        /// composing at twice the size and scaling down lands on (approximately) the same
        /// picture — a preview window and the render cannot disagree about how blurred an item
        /// looks.</summary>
        [Fact]
        public void Blur_is_canvas_relative_across_compose_sizes()
        {
            var p = NewProject();
            AddMediaItem(p, new VideoEffect { Kind = VideoEffectKind.Blur, Amount = 0.5 });

            using var frames = new MaskedFrameSource(StripeFrame(), null);
            var small = Render(p, frames, W, H);
            var large = Render(p, frames, 2 * W, 2 * H);

            // box-average the large render down to the small grid and compare interiors
            double totalDiff = 0;
            int samples = 0;
            for (int y = 8; y < H - 8; y++)
            {
                for (int x = 8; x < W - 8; x++)
                {
                    var s = Px(small, x, y);
                    int g = (Px(large, 2 * x, 2 * y, 2 * W).G + Px(large, 2 * x + 1, 2 * y, 2 * W).G
                        + Px(large, 2 * x, 2 * y + 1, 2 * W).G + Px(large, 2 * x + 1, 2 * y + 1, 2 * W).G) / 4;
                    totalDiff += Math.Abs(s.G - g);
                    samples++;
                }
            }

            Assert.True(totalDiff / samples < 8,
                $"blur diverged across compose sizes (mean |diff| = {totalDiff / samples:F2})");
        }

        // ---------------------------------------------------------------------- graceful decay

        [Fact]
        public void A_segmented_kind_without_a_mask_draws_the_plain_picture()
        {
            using var frames = new MaskedFrameSource(StripeFrame(), null);

            foreach (var kind in new[] { VideoEffectKind.BgBlur, VideoEffectKind.BgRemove })
            {
                var p = NewProject();
                AddMediaItem(p, new VideoEffect { Kind = kind, Amount = 0.5 });

                var plainProject = NewProject();
                AddMediaItem(plainProject, null);

                Assert.Equal(Render(plainProject, frames), Render(p, frames));
            }
        }

        // ----------------------------------------------------------------------------- helpers

        private static void AssertColor((byte B, byte G, byte R, byte A) actual,
            byte b, byte g, byte r, int tolerance = 2)
        {
            Assert.InRange(actual.B, Math.Max(0, b - tolerance), Math.Min(255, b + tolerance));
            Assert.InRange(actual.G, Math.Max(0, g - tolerance), Math.Min(255, g + tolerance));
            Assert.InRange(actual.R, Math.Max(0, r - tolerance), Math.Min(255, r + tolerance));
        }
    }
}
