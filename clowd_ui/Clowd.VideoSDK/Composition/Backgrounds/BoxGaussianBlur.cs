using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// A Gaussian blur of a premultiplied RGBA float raster, in place: three box blurs in each
    /// direction, with widths chosen so their combined variance is the Gaussian's (Kovesi,
    /// "Fast Almost-Gaussian Filtering", 2010), which is also how Skia's own CPU blur
    /// approximates one. Beyond the raster's edge is transparent, as beyond an SVG filter
    /// region is (Skia's <c>Decal</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because Skia's raster blur engine works in 8 bits per channel whatever
    /// the surface's format: an <c>SKImageFilter</c> blur over an F16 or F32 linear surface is
    /// quantized to 8-bit linear values on the way in. The dark end of that scale re-encodes
    /// to sRGB in steps of 13, 9 and 6 levels (linear 1/255, 2/255 and 3/255 are sRGB 13, 22
    /// and 28), which shows as contour rings across any blend that passes near black in a
    /// channel: Breathing Field's red channel through its green-over-purple wash, and every
    /// channel of the mono palette. Chrome and resvg carry the same rings for the same
    /// reason. Blurring the floats and letting Skia convert to 8-bit sRGB only at the end
    /// gives the authored blend without them, at about the cost of Skia's own blur: each pass
    /// is one <see cref="Vector4"/> add and one subtract per pixel.
    /// </para>
    /// <para>
    /// <b>The edge.</b> Every pass treats what lies beyond the raster as transparent, which
    /// is the exact decal result for one box but not for three: the second pass cannot see
    /// the tail the first would have spread past the edge, so a step that runs off the raster
    /// comes out at about a third of its height on the edge pixel rather than a half, with
    /// the deficit gone by two sigma in (0.2% there, computed against the exact Gaussian at
    /// a sigma of 86; it is a fraction of sigma, so the same distance in sigmas holds at the
    /// 12 working pixels Breathing Field uses now). The reader pads a percentage filter
    /// region by exactly two sigma on every side, so the visible picture starts where the
    /// deficit ends, and Monterey's absolute regions reach 120 working pixels past the
    /// picture at a sigma of 11. Keeping the tails exactly would mean growing the raster by
    /// three radii a side,
    /// three times the pixels, for a difference confined to the padding.
    /// </para>
    /// <para>
    /// Float running sums are accurate enough here by a wide margin: a window of 173 pixels
    /// of premultiplied values in [0, 1] sums to at most 173, whose float ulp is 1.5e-5, and
    /// a row of 900 slides accumulates at worst 0.01 of it before the divide, or 6e-5 in the
    /// output against an 8-bit quantum of 3.9e-3.
    /// </para>
    /// </remarks>
    internal static class BoxGaussianBlur
    {
        /// <summary>
        /// Blurs <paramref name="pixels"/> (row-major premultiplied RGBA floats,
        /// <paramref name="width"/> x <paramref name="height"/>) in place by
        /// <paramref name="sigma"/> pixels, using <paramref name="scratch"/> (at least as
        /// large) as the ping-pong buffer. A sigma of zero or less is a no-op.
        /// </summary>
        internal static void Blur(float[] pixels, float[] scratch, int width, int height, float sigma)
        {
            if (width <= 0 || height <= 0 || sigma <= 0)
                return;
            int count = width * height * 4;
            if (pixels.Length < count || scratch.Length < count)
                throw new ArgumentException("the buffers are smaller than the raster");

            Span<int> radii = stackalloc int[3];
            BoxRadii(sigma, radii);

            var a = MemoryMarshal.Cast<float, Vector4>(pixels.AsSpan(0, count));
            var b = MemoryMarshal.Cast<float, Vector4>(scratch.AsSpan(0, count));
            // Six passes, ping-ponging, so the result lands back in `pixels`.
            for (int i = 0; i < 3; i++)
            {
                Horizontal(a, b, width, height, radii[i]);
                var t = a; a = b; b = t;
            }
            for (int i = 0; i < 3; i++)
            {
                Vertical(a, b, width, height, radii[i]);
                var t = a; a = b; b = t;
            }
        }

        /// <summary>
        /// The three box radii (half widths) for <paramref name="sigma"/>: windows of two odd
        /// widths, <c>wl</c> and <c>wl + 2</c>, <c>m</c> of the first and the rest of the
        /// second, with <c>m</c> the integer that brings the boxes' total variance closest to
        /// <c>sigma^2</c>. A sigma below about 0.6 rounds to three unit windows, the identity.
        /// </summary>
        internal static void BoxRadii(float sigma, Span<int> radii)
        {
            const int n = 3;
            double s2 = (double)sigma * sigma;
            double wIdeal = Math.Sqrt(12 * s2 / n + 1);
            int wl = (int)Math.Floor(wIdeal);
            if (wl % 2 == 0)
                wl--;
            wl = Math.Max(wl, 1);
            int wu = wl + 2;
            double mIdeal = (12 * s2 - n * (double)wl * wl - 4.0 * n * wl - 3.0 * n) / (-4.0 * wl - 4);
            int m = Math.Clamp((int)Math.Round(mIdeal), 0, n);
            for (int i = 0; i < n; i++)
                radii[i] = ((i < m ? wl : wu) - 1) / 2;
        }

        /// <summary>One box pass along the rows, <paramref name="src"/> to <paramref name="dst"/>:
        /// a running sum slides the window, the divisor is the full window since outside the
        /// raster is transparent.</summary>
        private static void Horizontal(ReadOnlySpan<Vector4> src, Span<Vector4> dst, int width, int height, int r)
        {
            float inv = 1f / (2 * r + 1);
            for (int y = 0; y < height; y++)
            {
                var row = src.Slice(y * width, width);
                var outRow = dst.Slice(y * width, width);
                var sum = Vector4.Zero;
                int prime = Math.Min(r, width - 1);
                for (int x = 0; x <= prime; x++)
                    sum += row[x];
                for (int x = 0; x < width; x++)
                {
                    outRow[x] = sum * inv;
                    int add = x + r + 1, remove = x - r;
                    if (add < width)
                        sum += row[add];
                    if (remove >= 0)
                        sum -= row[remove];
                }
            }
        }

        /// <summary>One box pass down the columns, walking the raster row by row with a running
        /// sum per column so the access pattern stays sequential.</summary>
        private static void Vertical(ReadOnlySpan<Vector4> src, Span<Vector4> dst, int width, int height, int r)
        {
            float inv = 1f / (2 * r + 1);
            var sums = new Vector4[width];
            int prime = Math.Min(r, height - 1);
            for (int y = 0; y <= prime; y++)
                Add(sums, src.Slice(y * width, width));
            for (int y = 0; y < height; y++)
            {
                var outRow = dst.Slice(y * width, width);
                for (int x = 0; x < width; x++)
                    outRow[x] = sums[x] * inv;
                int add = y + r + 1, remove = y - r;
                if (add < height)
                    Add(sums, src.Slice(add * width, width));
                if (remove >= 0)
                    Subtract(sums, src.Slice(remove * width, width));
            }
        }

        private static void Add(Vector4[] sums, ReadOnlySpan<Vector4> row)
        {
            for (int x = 0; x < row.Length; x++)
                sums[x] += row[x];
        }

        private static void Subtract(Vector4[] sums, ReadOnlySpan<Vector4> row)
        {
            for (int x = 0; x < row.Length; x++)
                sums[x] -= row[x];
        }
    }
}
