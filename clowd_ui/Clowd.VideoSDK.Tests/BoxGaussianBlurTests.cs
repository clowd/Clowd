using System;
using Clowd.VideoSDK.Composition;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The float blur behind every wallpaper filter: it conserves mass, its spread is the
    /// sigma it was asked for, it is symmetric and channel-independent, it leaves a uniform
    /// field alone, and it treats the raster's edge as transparent.
    /// </summary>
    public class BoxGaussianBlurTests
    {
        private static float[] Impulse(int w, int h, int x, int y, float r, float g, float b, float a)
        {
            var pixels = new float[w * h * 4];
            int i = (y * w + x) * 4;
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
            return pixels;
        }

        /// <summary>Three integer-width boxes cannot hit a variance exactly; Kovesi's choice
        /// of widths lands within a couple of percent from sigma 8 up (the same class as
        /// Skia's own blur, which measures about 3% low), and coarser below that, where a
        /// wallpaper blur never is: the smallest shipped is Monterey's 30 units, 7 working
        /// pixels.</summary>
        [Theory]
        [InlineData(8f)]
        [InlineData(12f)]
        [InlineData(40f)]
        [InlineData(86f)]
        public void An_impulse_spreads_to_the_requested_sigma_and_keeps_its_mass(float sigma)
        {
            const int W = 601, H = 601, C = 300;
            var pixels = Impulse(W, H, C, C, 1f, 0.5f, 0.25f, 1f);
            BoxGaussianBlur.Blur(pixels, new float[pixels.Length], W, H, sigma);

            double mass = 0, varX = 0, varY = 0, massG = 0, massB = 0;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    double v = pixels[i + 3];
                    mass += v;
                    varX += v * (x - C) * (x - C);
                    varY += v * (y - C) * (y - C);
                    massG += pixels[i + 1];
                    massB += pixels[i + 2];
                    Assert.Equal(pixels[i], pixels[i + 3]);            // R carried 1, as A did
                    Assert.Equal(pixels[(y * W + (2 * C - x)) * 4 + 3], v, 5);   // mirror in x
                    Assert.Equal(pixels[((2 * C - y) * W + x) * 4 + 3], v, 5);   // mirror in y
                }
            }
            Assert.InRange(mass, 0.999, 1.001);
            Assert.InRange(massG, 0.499, 0.501);
            Assert.InRange(massB, 0.249, 0.251);
            Assert.InRange(Math.Sqrt(varX / mass), sigma * 0.97, sigma * 1.03);
            Assert.InRange(Math.Sqrt(varY / mass), sigma * 0.97, sigma * 1.03);
        }

        [Fact]
        public void A_uniform_field_is_unchanged_away_from_the_edge_and_fades_at_it()
        {
            const int W = 200, H = 120;
            var pixels = new float[W * H * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 0.25f;
                pixels[i + 1] = 0.5f;
                pixels[i + 2] = 0.75f;
                pixels[i + 3] = 1f;
            }
            BoxGaussianBlur.Blur(pixels, new float[pixels.Length], W, H, 8f);

            // Three boxes of radius at most 14 reach 42 pixels; beyond that nothing changes.
            for (int y = 45; y < H - 45; y++)
            {
                for (int x = 45; x < W - 45; x++)
                {
                    int i = (y * W + x) * 4;
                    Assert.Equal(0.25f, pixels[i], 4);
                    Assert.Equal(0.5f, pixels[i + 1], 4);
                    Assert.Equal(0.75f, pixels[i + 2], 4);
                    Assert.Equal(1f, pixels[i + 3], 4);
                }
            }
            // The edge pixel sees transparent beyond the raster. One box pass leaves it just
            // over half covered; each later pass sees the previous pass's tail cut off at the
            // edge as well, so three passes compound to about a third (see the blur's remarks
            // on why that stays out of the picture).
            Assert.InRange(pixels[(60 * W + 0) * 4 + 3], 0.25f, 0.45f);
            Assert.InRange(pixels[(0 * W + 100) * 4 + 3], 0.25f, 0.45f);
        }

        [Fact]
        public void Zero_sigma_is_a_no_op_and_small_buffers_are_refused()
        {
            var pixels = Impulse(5, 5, 2, 2, 1f, 1f, 1f, 1f);
            var copy = (float[])pixels.Clone();
            BoxGaussianBlur.Blur(pixels, new float[pixels.Length], 5, 5, 0f);
            Assert.Equal(copy, pixels);
            Assert.Throws<ArgumentException>(() => BoxGaussianBlur.Blur(pixels, new float[10], 5, 5, 1f));
        }

        [Fact]
        public void Box_radii_follow_kovesi()
        {
            // sigma 86 (Breathing Field's working sigma): ideal width 172.0, so 171 and 173,
            // two of the first; total variance 2*(171^2-1)/12 + (173^2-1)/12 = 7367 vs 7396.
            Span<int> radii = stackalloc int[3];
            BoxGaussianBlur.BoxRadii(86f, radii);
            Assert.Equal(new[] { 85, 85, 86 }, radii.ToArray());

            BoxGaussianBlur.BoxRadii(0.1f, radii);
            Assert.Equal(new[] { 0, 0, 0 }, radii.ToArray());
        }
    }
}
