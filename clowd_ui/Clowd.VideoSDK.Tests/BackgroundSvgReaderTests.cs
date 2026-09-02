using System;
using System.Linq;
using System.Xml.Linq;
using Clowd.VideoSDK.Composition;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The reader's rules that the shipped files exercise only in passing, each on a small
    /// synthetic file so one assertion is about one rule: a blur's effective sigma is the
    /// authored one (the working canvas's scale must not be applied to it twice), a blur
    /// mixes in the color space its filter declares (linear light by default), a
    /// <c>&lt;use&gt;</c> of a preceding sibling group is cloned with its clip and blur while
    /// every other <c>&lt;use&gt;</c> is a named skip, and a shape's own <c>transform</c> is
    /// honored rather than silently dropped.
    /// </summary>
    public class BackgroundSvgReaderTests
    {
        private static SvgScene Parse(string svg) => BackgroundSvgReader.Read(XElement.Parse(svg));

        private static byte[] Render(SvgScene scene, int w, int h, SKColor clear)
        {
            using var bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(clear);
                scene.Draw(canvas, 0);
                canvas.Flush();
            }
            return bitmap.Bytes.ToArray();
        }

        private static (int R, int G, int B, int A) Px(byte[] bgra, int w, int x, int y)
        {
            int i = (y * w + x) * 4;
            return (bgra[i + 2], bgra[i + 1], bgra[i], bgra[i + 3]);
        }

        // -------------------------------------------------------------------------- blur

        /// <summary>
        /// A white half-plane blurred at sigma 60 in Breathing Field's 900x600 box (so the
        /// working scale is the 12 pixels per sigma cap, 12/60, under the 480/900 ceiling):
        /// the 16%-84% crossing of the blurred edge spans two sigma. Skia maps an image
        /// filter's sigma through the working canvas's CTM, so the
        /// scale must be applied exactly once; handing Skia <c>sigma * k</c> under
        /// <c>Scale(k, k)</c> measured 32 here, and 86 for Breathing Field's authored 161.
        /// The allowance covers Skia's box-blur approximation (a few percent low) and the
        /// fixed-resolution raster; the double scaling misses by a factor of two.
        /// </summary>
        [Fact]
        public void Blur_sigma_is_the_authored_user_units()
        {
            const int W = 900, H = 600;
            var scene = Parse(@"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 900 600'>
                <defs><filter id='f'><feGaussianBlur stdDeviation='60'/></filter></defs>
                <g filter='url(#f)'><rect x='-400' y='-400' width='850' height='1400' fill='white'/></g></svg>");
            Assert.Empty(scene.Skipped);

            var pixels = Render(scene, W, H, SKColors.Black);
            int x84 = -1, x16 = -1;
            for (int x = 0; x < W; x++)
            {
                int v = Px(pixels, W, x, H / 2).R;
                if (x84 < 0 && v < 0.84 * 255) x84 = x;
                if (x16 < 0 && v < 0.16 * 255) x16 = x;
            }
            Assert.True(x84 > 0 && x16 > x84, $"no blurred edge found ({x84}, {x16})");
            double sigma = (x16 - x84) / 2.0;
            Assert.InRange(sigma, 60 * 0.85, 60 * 1.15);
        }

        /// <summary>
        /// A purple ground and a green half-plane, both under the blur, so the blurred edge is
        /// a 50/50 mix of the two made in the filter's working space. Linear light gives the
        /// teal-blue a browser shows for Breathing Field; sRGB the darker, more purple mix. The
        /// expected values are the two mixes computed by hand: in linear light
        /// <c>(lin(66) + 0) / 2, (0 + lin(CC)) / 2, (lin(FF) + lin(99)) / 2</c> re-encoded;
        /// in sRGB simply the channel means.
        /// </summary>
        [Theory]
        [InlineData("", "", 73, 149, 212)]                                                  // SVG's initial value: linearRGB
        [InlineData("", "color-interpolation-filters='linearRGB'", 73, 149, 212)]
        [InlineData("", "color-interpolation-filters='auto'", 73, 149, 212)]
        [InlineData("", "color-interpolation-filters='sRGB'", 51, 102, 204)]
        [InlineData("color-interpolation-filters='sRGB'", "", 51, 102, 204)]               // inherited from the root
        [InlineData("color-interpolation-filters='sRGB'", "color-interpolation-filters='inherit'", 51, 102, 204)]
        [InlineData("color-interpolation-filters='sRGB'", "color-interpolation-filters='linearRGB'", 73, 149, 212)]
        public void Blur_mixes_in_the_color_space_the_filter_declares(string rootAttr, string filterAttr, int r, int g, int b)
        {
            const int W = 200, H = 100;
            var scene = Parse($@"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 200 100' {rootAttr}>
                <defs><filter id='f' filterUnits='userSpaceOnUse' x='-40' y='-40' width='280' height='180' {filterAttr}>
                    <feGaussianBlur stdDeviation='10'/></filter></defs>
                <g filter='url(#f)'>
                    <rect width='200' height='100' fill='#6600FF'/>
                    <rect width='100' height='100' fill='#00CC99'/>
                </g></svg>");
            Assert.Empty(scene.Skipped);

            // The two pixels either side of the edge at x=100 are symmetric about it; their mean
            // is the mix at the edge itself.
            var pixels = Render(scene, W, H, SKColors.Black);
            var left = Px(pixels, W, 99, 50);
            var right = Px(pixels, W, 100, 50);
            var mid = ((left.R + right.R) / 2, (left.G + right.G) / 2, (left.B + right.B) / 2);
            Assert.InRange(mid.Item1, r - 6, r + 6);
            Assert.InRange(mid.Item2, g - 6, g + 6);
            Assert.InRange(mid.Item3, b - 6, b + 6);
            Assert.Equal(255, left.A);
        }

        [Fact]
        public void A_filter_in_an_unknown_color_space_is_a_skip()
        {
            var scene = Parse(@"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'>
                <defs><filter id='f' color-interpolation-filters='bogus'><feGaussianBlur stdDeviation='1'/></filter></defs>
                <g filter='url(#f)'><rect width='10' height='10'/></g></svg>");
            var skip = Assert.Single(scene.Skipped);
            Assert.Equal("g: filter #f color-interpolation-filters 'bogus' is not supported", skip);
        }

        // --------------------------------------------------------------------------- use

        /// <summary>
        /// Monterey's shape: a stack, then a <c>&lt;use&gt;</c> of it clipped to a region and
        /// put through a blur with an absolute region. The original is untouched, the clone's
        /// blurred edge shows through the clip just beyond it and fades out five sigma in, and
        /// nothing is skipped.
        /// </summary>
        [Fact]
        public void Use_of_a_preceding_sibling_group_clones_it_clipped_and_blurred()
        {
            const int W = 200, H = 100;
            const string body = @"
                <g id='stack'><rect width='100' height='100' fill='white'/></g>
                {USE}
                <defs>
                    <clipPath id='c'><rect x='100' y='0' width='100' height='100'/></clipPath>
                    <filter id='f' filterUnits='userSpaceOnUse' x='-50' y='-50' width='300' height='200' color-interpolation-filters='sRGB'>
                        <feGaussianBlur stdDeviation='10'/></filter>
                </defs>";
            const string head = "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' viewBox='0 0 200 100'>";

            var with = Parse(head + body.Replace("{USE}", "<use xlink:href='#stack' clip-path='url(#c)' filter='url(#f)'/>") + "</svg>");
            Assert.Empty(with.Skipped);
            var pixels = Render(with, W, H, SKColors.Black);
            Assert.Equal(255, Px(pixels, W, 50, 50).R);
            Assert.InRange(Px(pixels, W, 102, 50).R, 40, 200);
            Assert.InRange(Px(pixels, W, 160, 50).R, 0, 4);

            // Without the use the right half is bare, which is what the clone adds.
            var without = Parse(head + body.Replace("{USE}", "") + "</svg>");
            Assert.Equal(0, Px(Render(without, W, H, SKColors.Black), W, 102, 50).R);

            // `href` without the xlink prefix, and x/y, are the same clone moved.
            var moved = Parse(head + body.Replace("{USE}", "<use href='#stack' x='100' y='0'/>") + "</svg>");
            Assert.Empty(moved.Skipped);
            Assert.Equal(255, Px(Render(moved, W, H, SKColors.Black), W, 150, 50).R);
        }

        [Fact]
        public void Monterey_reads_its_backdrop_blur_layers()
        {
            foreach (var asset in new[] { "monterey/light.svg", "monterey/dark.svg" })
            {
                var scene = BackgroundSvgReader.Read(BackgroundAssets.TryReadXml(asset));
                Assert.Empty(scene.Skipped);
                // The three blurred clones exist as groups with a blur, nested one in the next.
                int blurred = 0;
                void Count(SvgNode node)
                {
                    if (node is SvgGroup g)
                    {
                        if (g.BlurStdDeviation > 0) blurred++;
                        foreach (var child in g.Children) Count(child);
                    }
                }
                Count(scene.Root);
                // The stacks nest and each clone shares the subtree built for its stack, so the
                // blurred groups on a full walk are: use#bgstack1 inside bgstack2; use#bgstack2
                // and, through its clone of bgstack2, use#bgstack1 again; then use#bgstack3,
                // whose clone of bgstack3 holds all four of those once more. Seven.
                Assert.Equal(7, blurred);
            }
        }

        [Theory]
        [InlineData("<use href='#later'/><g id='later'/>", "not a <g> read earlier")]
        [InlineData("<rect id='r' width='1' height='1'/><use href='#r'/>", "not a <g> read earlier")]
        [InlineData("<g><g id='inner'/></g><use href='#inner'/>", "outside the group's own parent")]
        [InlineData("<g id='s'/><use href='#s' fill='red'/>", "its own fill")]
        [InlineData("<g id='s'/><use href='#s' style='stroke:red'/>", "its own stroke")]
        [InlineData("<g id='s'/><use href='other.svg#s'/>", "local #id href")]
        [InlineData("<g id='s'/><use/>", "local #id href")]
        public void A_use_outside_the_supported_form_is_a_skip(string body, string reason)
        {
            var scene = Parse($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'>{body}</svg>");
            var skip = Assert.Single(scene.Skipped);
            Assert.StartsWith("use: ", skip);
            Assert.Contains(reason, skip);
        }

        // ----------------------------------------------------------------------- transform

        /// <summary>
        /// A shape's own <c>transform</c> places it (it used to be read by groups only, and a
        /// shape carrying one drew at the untransformed spot with nothing in the skip list,
        /// the one failure the contract exists to prevent). The circle checks the SVG order:
        /// <c>translate(0,200) scale(2)</c> scales first, so (50,50) r 25 lands at (100,300) r 50.
        /// </summary>
        [Fact]
        public void A_shape_transform_is_honored()
        {
            const int W = 400, H = 400;
            var scene = Parse(@"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 400 400'>
                <path transform='translate(200,0)' d='M0 0 L100 0 L100 100 L0 100 Z' fill='#ff0000'/>
                <circle transform='translate(0,200) scale(2)' cx='50' cy='50' r='25' fill='#0000ff'/>
                <rect transform='rotate(90 300 300)' x='250' y='290' width='100' height='20' fill='#00ff00'/></svg>");
            Assert.Empty(scene.Skipped);

            var pixels = Render(scene, W, H, SKColors.White);
            Assert.Equal((255, 255, 255, 255), Px(pixels, W, 50, 50));     // neither the path nor the circle, untransformed
            Assert.Equal((255, 0, 0, 255), Px(pixels, W, 250, 50));
            Assert.Equal((0, 0, 255, 255), Px(pixels, W, 100, 300));
            Assert.Equal((255, 255, 255, 255), Px(pixels, W, 160, 300));   // r 50, not 25 scaled by nothing
            Assert.Equal((0, 255, 0, 255), Px(pixels, W, 300, 340));       // the bar stood upright about its center
            Assert.Equal((255, 255, 255, 255), Px(pixels, W, 340, 300));
        }

        /// <summary>A clip-path on a transformed shape is in the shape's own (transformed)
        /// space, as it is on a group.</summary>
        [Fact]
        public void A_shape_clip_is_in_the_transformed_space()
        {
            const int W = 200, H = 100;
            var scene = Parse(@"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 200 100'>
                <defs><clipPath id='c'><rect x='0' y='0' width='50' height='100'/></clipPath></defs>
                <rect transform='translate(100,0)' clip-path='url(#c)' width='100' height='100' fill='#ff0000'/></svg>");
            Assert.Empty(scene.Skipped);
            var pixels = Render(scene, W, H, SKColors.White);
            Assert.Equal((255, 255, 255, 255), Px(pixels, W, 25, 50));     // clip in root space would show here
            Assert.Equal((255, 0, 0, 255), Px(pixels, W, 125, 50));
            Assert.Equal((255, 255, 255, 255), Px(pixels, W, 175, 50));    // clipped off in the shape's space
        }
    }
}
