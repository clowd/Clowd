using System;

namespace Clowd.Drawing.Rendering
{
    /// <summary>CPU gaussian approximation for GraphicImage's Blur obscure mode.</summary>
    internal static class BoxBlur
    {
        /// <summary>
        /// 3 successive box blurs ≈ gaussian (standard boxesForGauss derivation), in place, over
        /// <paramref name="channels"/> interleaved 8-bit channels (4 for a BGRA region). Channels
        /// are blurred independently, which is only correct for premultiplied color.
        /// </summary>
        internal static void Blur3(byte[] data, int w, int h, double sigma, int channels = 1)
        {
            var tmp = new byte[data.Length];
            foreach (var size in BoxesForGauss(sigma, 3))
            {
                var r = (size - 1) / 2;
                if (r <= 0) continue;
                BoxBlurH(data, tmp, w, h, r, channels);
                BoxBlurV(tmp, data, w, h, r, channels);
            }
        }

        private static int[] BoxesForGauss(double sigma, int n)
        {
            var wIdeal = Math.Sqrt(12 * sigma * sigma / n + 1);
            var wl = (int)Math.Floor(wIdeal);
            if (wl % 2 == 0) wl--;
            var wu = wl + 2;
            var mIdeal = (12 * sigma * sigma - n * (double)wl * wl - 4.0 * n * wl - 3.0 * n) / (-4.0 * wl - 4);
            var m = (int)Math.Round(mIdeal);
            var sizes = new int[n];
            for (int i = 0; i < n; i++)
                sizes[i] = i < m ? wl : wu;
            return sizes;
        }

        // sliding-window box blurs; samples outside the image count as zero (fully transparent),
        // so a caller blurring a cut-out region must pad it by the blur reach
        private static void BoxBlurH(byte[] src, byte[] dst, int w, int h, int r, int channels)
        {
            var div = 2 * r + 1;
            for (int c = 0; c < channels; c++)
            {
                for (int y = 0; y < h; y++)
                {
                    var row = y * w;
                    var sum = 0;
                    for (int x = 0; x < Math.Min(r, w); x++)
                        sum += src[(row + x) * channels + c];
                    for (int x = 0; x < w; x++)
                    {
                        if (x + r < w) sum += src[(row + x + r) * channels + c];
                        dst[(row + x) * channels + c] = (byte)(sum / div);
                        if (x - r >= 0) sum -= src[(row + x - r) * channels + c];
                    }
                }
            }
        }

        private static void BoxBlurV(byte[] src, byte[] dst, int w, int h, int r, int channels)
        {
            var div = 2 * r + 1;
            for (int c = 0; c < channels; c++)
            {
                for (int x = 0; x < w; x++)
                {
                    var sum = 0;
                    for (int y = 0; y < Math.Min(r, h); y++)
                        sum += src[(y * w + x) * channels + c];
                    for (int y = 0; y < h; y++)
                    {
                        if (y + r < h) sum += src[((y + r) * w + x) * channels + c];
                        dst[(y * w + x) * channels + c] = (byte)(sum / div);
                        if (y - r >= 0) sum -= src[((y - r) * w + x) * channels + c];
                    }
                }
            }
        }
    }
}
