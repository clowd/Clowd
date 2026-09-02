using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clowd.VideoSDK.Composition;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The renderer's pixels and its clock: every style draws a full, opaque, non-uniform
    /// picture; the three loops change with time, repeat exactly one period later and close
    /// seamlessly; a still ignores time; cover placement is resolution-independent; the phase
    /// is a pure integer function of project ticks; and concurrent draws of one shared scene
    /// agree byte for byte.
    /// </summary>
    public class BackgroundRendererTests
    {
        private const int W = 64, H = 64;
        private const long Sec = 10_000_000;

        /// <summary>The BGRA bytes of a style drawn to fill a black W x H bitmap at a project time.</summary>
        internal static byte[] RenderPixels(string style, string theme, double timeSeconds, int w = W, int h = H,
            double speed = 1.0, string color = null)
        {
            using var bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Black);
                BackgroundRenderer.Draw(canvas, SKRect.Create(0, 0, w, h), style, theme, timeSeconds, speed, color);
                canvas.Flush();
            }
            return bitmap.Bytes.ToArray();
        }

        private static byte[] RenderScene(BackgroundScene scene, double phase, SKRect dest, double opacity = 1.0,
            int w = W, int h = H)
        {
            using var bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Black);
                BackgroundRenderer.DrawScene(canvas, dest, scene, phase, opacity);
                canvas.Flush();
            }
            return bitmap.Bytes.ToArray();
        }

        private static (byte B, byte G, byte R, byte A) Px(byte[] bgra, int x, int y, int w = W)
        {
            int i = y * w * 4 + x * 4;
            return (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
        }

        private static int DistinctColors(byte[] bgra)
        {
            var seen = new HashSet<uint>();
            for (int i = 0; i < bgra.Length; i += 4)
                seen.Add(BitConverter.ToUInt32(bgra, i));
            return seen.Count;
        }

        private static int MaxChannelDifference(byte[] a, byte[] b)
        {
            int max = 0;
            for (int i = 0; i < a.Length; i++)
                max = Math.Max(max, Math.Abs(a[i] - b[i]));
            return max;
        }

        public static IEnumerable<object[]> AllSpecs() => BackgroundCatalogTests.AllSpecs();

        public static IEnumerable<object[]> AnimatedStyles()
            => BackgroundCatalog.Styles.Where(s => s.IsAnimated).Select(s => new object[] { s.Id });

        public static IEnumerable<object[]> StaticStyles()
            => BackgroundCatalog.Styles.Where(s => !s.IsAnimated).Select(s => new object[] { s.Id });

        // ---------------------------------------------------------------------------- pixels

        [Theory]
        [MemberData(nameof(AllSpecs))]
        public void Every_spec_fills_the_box_with_opaque_varied_pixels(string style, string theme)
        {
            var pixels = RenderPixels(style, theme, 0);

            // Covers the whole box: every pixel opaque and nothing left at the cleared black.
            int black = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                Assert.Equal(255, pixels[i + 3]);
                if (pixels[i] < 8 && pixels[i + 1] < 8 && pixels[i + 2] < 8)
                    black++;
            }
            Assert.True(black < pixels.Length / 4 / 10, $"{style}/{theme}: {black} near-black pixels");

            // Real art, not a flat fill.
            Assert.True(DistinctColors(pixels) >= 8, $"{style}/{theme}: only {DistinctColors(pixels)} distinct colors");
        }

        [Fact]
        public void A_few_known_colors_land_where_the_art_puts_them()
        {
            // Moving Blob at t=0: the ground rect is #FF0066 and the blob (#BB004B) sits around
            // the group's translate (486, 261) of 900x600, i.e. near the center of the box.
            var blob = RenderPixels("moving-blob", "source", 0);
            var corner = Px(blob, 1, 1);
            Assert.Equal((0x66, 0x00, 0xFF), (corner.B, corner.G, corner.R));
            var center = Px(blob, 34, 28);
            Assert.Equal((0x4B, 0x00, 0xBB), (center.B, center.G, center.R));

            // Its Ember theme maps #FF0066 -> Ramp1 (#FCA13F) and #BB004B -> Ramp3 (#DE4B3C).
            var ember = RenderPixels("moving-blob", "ember", 0);
            corner = Px(ember, 1, 1);
            Assert.Equal((0x3F, 0xA1, 0xFC), (corner.B, corner.G, corner.R));
            center = Px(ember, 34, 28);
            Assert.Equal((0x3C, 0x4B, 0xDE), (center.B, center.G, center.R));

            // Layered Steps' ground (#140021) shows top-right, where no step reaches.
            var steps = RenderPixels("layered-steps", "source", 0);
            var ground = Px(steps, 62, 1);
            Assert.Equal((0x21, 0x00, 0x14), (ground.B, ground.G, ground.R));
        }

        [Fact]
        public void Monterey_dark_is_darker_than_light_and_not_black()
        {
            var light = RenderPixels("monterey", "light", 0);
            var dark = RenderPixels("monterey", "dark", 0);
            long lightSum = 0, darkSum = 0;
            for (int i = 0; i < light.Length; i += 4)
            {
                lightSum += light[i] + light[i + 1] + light[i + 2];
                darkSum += dark[i] + dark[i + 1] + dark[i + 2];
            }
            // Saturation-blended black leaves the luminance and takes the saturation to zero;
            // the soft-light black at 60% then darkens. So: noticeably darker, nowhere near
            // black, and close to grayscale, which a renderer that ignored the blend modes
            // (identical to light) or composited them as plain alpha (black) would both fail.
            Assert.True(darkSum < lightSum * 3 / 4, $"dark {darkSum} vs light {lightSum}");
            Assert.True(darkSum > lightSum / 20, $"dark {darkSum} is nearly black");
            long chroma = 0;
            for (int i = 0; i < dark.Length; i += 4)
                chroma += Math.Abs(dark[i] - dark[i + 1]) + Math.Abs(dark[i + 1] - dark[i + 2]);
            Assert.True(chroma < dark.Length / 4 * 6, $"dark is not desaturated: mean chroma {chroma / (dark.Length / 4.0):F1}");
            Assert.True(DistinctColors(dark) >= 8);
        }

        [Fact]
        public void Breathing_field_blur_is_soft_and_carries_both_colors()
        {
            var field = RenderPixels("breathing-field", "source", 0, 128, 128);
            // No hard edges: neighboring pixels differ by little everywhere.
            int maxStep = 0;
            for (int y = 0; y < 128; y++)
            {
                for (int x = 1; x < 128; x++)
                {
                    var a = Px(field, x - 1, y, 128);
                    var b = Px(field, x, y, 128);
                    maxStep = Math.Max(maxStep, Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B));
                }
            }
            Assert.True(maxStep < 40, $"hard edge of {maxStep} in a sigma-161 blur");
            Assert.True(DistinctColors(field) >= 64);
        }

        // ------------------------------------------------------------------------------ time

        [Theory]
        [MemberData(nameof(AnimatedStyles))]
        public void Animated_styles_move_and_repeat_exactly_one_period_later(string styleId)
        {
            var style = BackgroundCatalog.Find(styleId);
            double period = style.PeriodSeconds;

            var t0 = RenderPixels(styleId, "source", 0);
            var third = RenderPixels(styleId, "source", period / 3);
            var wrapped = RenderPixels(styleId, "source", period);
            var again = RenderPixels(styleId, "source", 0);

            Assert.False(t0.AsSpan().SequenceEqual(third), styleId + " does not move between t=0 and t=period/3");
            Assert.True(t0.AsSpan().SequenceEqual(wrapped), styleId + " differs between t=0 and t=period");
            Assert.True(t0.AsSpan().SequenceEqual(again), styleId + " is not deterministic at one instant");
        }

        /// <summary>
        /// The frame just before the wrap is the frame at the wrap. The geometry is checked
        /// exactly through the tracks (every value the sampler interpolates toward at phase 1 is
        /// the value it returns at phase 0); the pixels are checked with an allowance for
        /// Skia's supersampled antialiasing, which quantizes an edge pixel's coverage and can
        /// flip it by a step when an edge that moved a ten-thousandth of a unit crosses a
        /// sub-scanline. A real seam would move whole shapes by pixels.
        /// </summary>
        [Theory]
        [MemberData(nameof(AnimatedStyles))]
        public void Loop_closes_seamlessly(string styleId)
        {
            var scene = (SvgBackgroundScene)BackgroundRenderer.GetScene(styleId, "source");
            int tracks = 0;
            void Check(SvgNode node)
            {
                switch (node)
                {
                    case SvgGroup g:
                        if (g.TranslateTrack != null) CheckTrack(g.TranslateTrack);
                        foreach (var child in g.Children) Check(child);
                        break;
                    case SvgCircle c:
                        if (c.CxTrack != null) CheckTrack(c.CxTrack);
                        if (c.CyTrack != null) CheckTrack(c.CyTrack);
                        if (c.RTrack != null) CheckTrack(c.RTrack);
                        break;
                    case SvgShape s:
                        if (s.DTrack != null) CheckTrack(s.DTrack);
                        break;
                }
            }
            void CheckTrack(SmilTrack track)
            {
                tracks++;
                var first = new float[track.Stride];
                var last = new float[track.Stride];
                track.Sample(0, first);
                track.Sample(1 - 1e-7, last);
                for (int i = 0; i < track.Stride; i++)
                    Assert.InRange(Math.Abs(first[i] - last[i]), 0, 0.01f);
            }
            Check(scene.Scene.Root);
            Assert.True(tracks > 0, styleId + " has no animation tracks");

            var dest = SKRect.Create(0, 0, W, H);
            var a = RenderScene(scene, 0, dest);
            var b = RenderScene(scene, 1 - 1e-7, dest);
            int worst = 0, changed = 0;
            for (int i = 0; i < a.Length; i += 4)
            {
                int d = Math.Max(Math.Abs(a[i] - b[i]), Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
                worst = Math.Max(worst, d);
                if (d > 2) changed++;
            }
            Assert.True(worst <= 64 && changed <= W * H / 100,
                $"{styleId} jumps at the loop point: {changed} pixels changed, worst by {worst}");
        }

        [Theory]
        [MemberData(nameof(StaticStyles))]
        public void Static_styles_ignore_time(string styleId)
        {
            var style = BackgroundCatalog.Find(styleId);
            var theme = style.Specs[0].Id;
            var t0 = RenderPixels(styleId, theme, 0);
            var later = RenderPixels(styleId, theme, 37);
            Assert.True(t0.AsSpan().SequenceEqual(later));
            Assert.Equal(0.0, BackgroundRenderer.PhaseOf(style, 37 * Sec));
        }

        /// <summary>The solid style has no scene to fetch: the entry point every tile and the
        /// composer share fills the destination with the color it is handed, and falls back to
        /// Clowd blue for a color it cannot read.</summary>
        [Fact]
        public void Solid_style_draws_the_color_it_is_handed()
        {
            var red = RenderPixels(BackgroundCatalog.SolidStyle, null, 0, color: "#FFFF0000");
            Assert.Equal(1, DistinctColors(red));
            var px = Px(red, 10, 10);
            Assert.Equal((0, 0, 255, 255), (px.B, px.G, px.R, px.A));

            var fallback = RenderPixels(BackgroundCatalog.SolidStyle, null, 0);
            Assert.Equal(fallback, RenderPixels(BackgroundCatalog.SolidStyle, "no-such-theme", 37, color: "  "));
            var blue = Px(fallback, 10, 10);
            Assert.Equal((0xF0, 0xAF, 0x00, 255), (blue.B, blue.G, blue.R, blue.A));

            Assert.Equal(SKColor.Parse("#FF00AFF0"), BackgroundRenderer.SolidColorOf(null));
            Assert.Equal(SKColor.Parse("#FF00AFF0"), BackgroundRenderer.SolidColorOf("nonsense"));
            Assert.Equal(SKColor.Parse("#8012AB34"), BackgroundRenderer.SolidColorOf("#8012AB34"));

            // opacity fades the fill the way it fades a scene: half over black is half the color
            using var bitmap = new SKBitmap(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Black);
                BackgroundRenderer.DrawSolid(canvas, SKRect.Create(0, 0, W, H), SKColors.Red, 0.5);
                canvas.Flush();
            }
            var faded = Px(bitmap.Bytes.ToArray(), 32, 32);
            Assert.InRange(faded.R, 126, 130);
        }

        [Fact]
        public void Phase_is_an_integer_tick_function_of_project_time()
        {
            var blob = BackgroundCatalog.Find("moving-blob");
            Assert.Equal(600_000_000, blob.PeriodTicks);
            Assert.Equal(0.0, BackgroundRenderer.PhaseOf(blob, 0));
            Assert.Equal(0.0, BackgroundRenderer.PhaseOf(blob, 60 * Sec));
            Assert.Equal(0.0, BackgroundRenderer.PhaseOf(blob, 120 * Sec));
            Assert.Equal(0.5, BackgroundRenderer.PhaseOf(blob, 30 * Sec));
            Assert.Equal(0.5, BackgroundRenderer.PhaseOf(blob, 90 * Sec));
            Assert.Equal(1.0 / 600_000_000, BackgroundRenderer.PhaseOf(blob, 1));
            Assert.Equal(1 - 1.0 / 600_000_000, BackgroundRenderer.PhaseOf(blob, 60 * Sec - 1));
            // A negative instant wraps into the period rather than going negative.
            Assert.Equal(0.75, BackgroundRenderer.PhaseOf(blob, -15 * Sec));

            // The speed dial scales ticks before the modulo; a bad speed reads as 1.
            Assert.Equal(BackgroundRenderer.PhaseOf(blob, 20 * Sec), BackgroundRenderer.PhaseOf(blob, 10 * Sec, 2.0));
            Assert.Equal(BackgroundRenderer.PhaseOf(blob, 5 * Sec), BackgroundRenderer.PhaseOf(blob, 20 * Sec, 0.25));
            Assert.Equal(BackgroundRenderer.PhaseOf(blob, 7 * Sec), BackgroundRenderer.PhaseOf(blob, 7 * Sec, double.NaN));
            Assert.Equal(BackgroundRenderer.PhaseOf(blob, 7 * Sec), BackgroundRenderer.PhaseOf(blob, 7 * Sec, 0));

            var field = BackgroundCatalog.Find("breathing-field");
            Assert.Equal(900_000_000, field.PeriodTicks);
            Assert.Equal(0.0, BackgroundRenderer.PhaseOf(field, 90 * Sec));
            Assert.Equal(0.0, BackgroundRenderer.PhaseOf(field, 90 * Sec, 1.0));

            // The string overload resolves an unknown id to the (static) default.
            Assert.Equal(0.0, BackgroundRenderer.PhaseOf("no-such-style", 12345));
            Assert.Equal(0.5, BackgroundRenderer.PhaseOf("moving-corners", 30 * Sec));
        }

        [Fact]
        public void Animation_speed_two_at_t_equals_speed_one_at_two_t()
        {
            var fast = RenderPixels("moving-corners", "source", 7, speed: 2.0);
            var slow = RenderPixels("moving-corners", "source", 14);
            Assert.True(fast.AsSpan().SequenceEqual(slow));
        }

        // ------------------------------------------------------------------------- placement

        [Fact]
        public void Unknown_style_id_draws_the_default()
        {
            var fallback = RenderPixels("not-a-style", "not-a-theme", 0);
            var expected = RenderPixels("big-sur", "default", 0);
            Assert.True(fallback.AsSpan().SequenceEqual(expected));
            Assert.True(RenderPixels(null, null, 0).AsSpan().SequenceEqual(expected));
        }

        [Fact]
        public void Cover_placement_is_resolution_independent()
        {
            // The 128 render box-downsampled 2x2 must match the 64 render: same picture, more pixels.
            foreach (var (style, theme) in new[] { ("gradient", "sunrise"), ("stacked-waves", "source"), ("explode", null) })
            {
                var small = RenderPixels(style, theme, 0, 64, 64);
                var large = RenderPixels(style, theme, 0, 128, 128);
                int worst = 0;
                long total = 0;
                for (int y = 0; y < 64; y++)
                {
                    for (int x = 0; x < 64; x++)
                    {
                        var s = Px(small, x, y, 64);
                        var a = Px(large, 2 * x, 2 * y, 128);
                        var b = Px(large, 2 * x + 1, 2 * y, 128);
                        var c = Px(large, 2 * x, 2 * y + 1, 128);
                        var d = Px(large, 2 * x + 1, 2 * y + 1, 128);
                        int r = (a.R + b.R + c.R + d.R) / 4, g = (a.G + b.G + c.G + d.G) / 4, bl = (a.B + b.B + c.B + d.B) / 4;
                        int diff = Math.Max(Math.Abs(r - s.R), Math.Max(Math.Abs(g - s.G), Math.Abs(bl - s.B)));
                        worst = Math.Max(worst, diff);
                        total += diff;
                    }
                }
                // Antialiased edges between strongly different colors can differ by tens of
                // levels on the edge pixel itself; a placement error would shift whole shapes
                // by pixels and move the mean by far more than this.
                double mean = total / (64.0 * 64.0);
                Assert.True(worst <= 96 && mean < 2.0, $"{style}/{theme}: box-downsampled 128 differs from 64 by up to {worst}, mean {mean:F2}");
            }
        }

        [Fact]
        public void Cover_matrix_is_xMidYMid_slice()
        {
            // A 900x600 viewBox into a 64x64 square: scale by the larger ratio (64/600), the
            // 96-wide result centered so 16 units hang off each side.
            var m = BackgroundRenderer.CoverMatrix(SKRect.Create(0, 0, 64, 64), SKRect.Create(0, 0, 900, 600));
            float s = 64f / 600f;
            Assert.Equal(s, m.ScaleX, 5);
            Assert.Equal(s, m.ScaleY, 5);
            Assert.Equal(32 - 900 * s / 2, m.TransX, 4);
            Assert.Equal(0, m.TransY, 4);

            // A 1600x1000 viewBox into a 200x100 letterbox: width-limited, vertical overflow.
            m = BackgroundRenderer.CoverMatrix(SKRect.Create(10, 20, 200, 100), SKRect.Create(0, 0, 1600, 1000));
            Assert.Equal(0.125f, m.ScaleX, 5);
            Assert.Equal(10, m.TransX, 4);
            Assert.Equal(20 + 50 - 1000 * 0.125f / 2, m.TransY, 4);
        }

        [Fact]
        public void DrawScene_clips_to_dest_and_restores_the_canvas()
        {
            var scene = BackgroundRenderer.GetScene("gradient", "abyss");
            using var bitmap = new SKBitmap(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
            int before = canvas.SaveCount;
            var matrixBefore = canvas.TotalMatrix;
            BackgroundRenderer.DrawScene(canvas, SKRect.Create(16, 16, 32, 32), scene, 0);
            Assert.Equal(before, canvas.SaveCount);
            Assert.Equal(matrixBefore, canvas.TotalMatrix);
            canvas.Flush();

            var pixels = bitmap.Bytes.ToArray();
            Assert.Equal((0, 0, 0, 255), Px(pixels, 2, 2));
            Assert.Equal((0, 0, 0, 255), Px(pixels, 61, 61));
            var inside = Px(pixels, 32, 32);
            Assert.True(inside.R + inside.G + inside.B > 30, "the inner box was not painted");
        }

        [Fact]
        public void Opacity_halves_the_color_and_fades_the_art_as_one()
        {
            var scene = BackgroundRenderer.GetScene("stacked-waves", "source");
            var dest = SKRect.Create(0, 0, W, H);
            var full = RenderScene(scene, 0, dest, 1.0);
            var half = RenderScene(scene, 0, dest, 0.5);
            // Over black, half opacity is half the color everywhere (within rounding).
            for (int i = 0; i < full.Length; i += 4)
            {
                for (int c = 0; c < 3; c++)
                    Assert.InRange(half[i + c], full[i + c] / 2 - 2, full[i + c] / 2 + 2);
                Assert.Equal(255, half[i + 3]);
            }
            Assert.True(RenderScene(scene, 0, dest, 0).AsSpan().SequenceEqual(RenderScene(null, 0, dest)));
        }

        // ------------------------------------------------------------------------- threading

        [Fact]
        public async Task Concurrent_draws_of_one_scene_agree_byte_for_byte()
        {
            // One shared scene of each kind (recorded picture, live loop with the raster blur,
            // bitmap), drawn from several threads at once, must give the same pixels as alone.
            foreach (var (style, theme, seconds) in new[] { ("gradient", "orchid", 0.0), ("breathing-field", "forest", 31.0), ("moving-blob", "grape", 12.5), ("big-sur", "teal", 0.0) })
            {
                var expected = RenderPixels(style, theme, seconds);
                var tasks = Enumerable.Range(0, 8)
                    .Select(_ => Task.Run(() => RenderPixels(style, theme, seconds)))
                    .ToArray();
                var results = await Task.WhenAll(tasks);
                foreach (var pixels in results)
                    Assert.True(pixels.AsSpan().SequenceEqual(expected), $"{style}/{theme} drew differently under contention");
            }
        }
    }
}
