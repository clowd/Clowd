using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing
{
    /// <summary>
    /// CPU drop-shadow builder for the export path (§2.10). RenderTargetBitmap.Render does not
    /// honor Visual.Effect (it is a compositor-only feature), so exports rasterize each shadowed
    /// graphic alone, blur its alpha channel with a 3-pass box blur (gaussian approximation) and
    /// tint it with the shadow color. The result is composited under the graphic by GraphicVisual.
    /// </summary>
    internal static class ShadowRenderer
    {
        // skia's blur-radius → gaussian sigma conversion, matching what the compositor does on screen
        private static double RadiusToSigma(double radius) => radius > 0 ? 0.57735 * radius + 0.5 : 0;

        /// <summary>
        /// Renders the shadow bitmap for a single graphic at 96 dpi. <paramref name="position"/>
        /// receives the top-left of the shadow in graphic (canvas) space, shadow offset included.
        /// </summary>
        public static WriteableBitmap Render(GraphicBase graphic, out Point position)
        {
            var bounds = graphic.Bounds;
            var sigma = RadiusToSigma(GraphicVisual.ShadowBlurRadius);

            // padding must cover the blur falloff plus any stroke drawn outside Bounds
            var pad = (int)Math.Ceiling(sigma * 3 + graphic.LineWidth + 2);
            var w = (int)Math.Ceiling(bounds.Width) + pad * 2;
            var h = (int)Math.Ceiling(bounds.Height) + pad * 2;

            position = new Point(bounds.Left - pad + GraphicVisual.ShadowOffsetX,
                                 bounds.Top - pad + GraphicVisual.ShadowOffsetY);

            // rasterize the graphic alone (object only, no selection chrome, no effect)
            var vis = new GraphicVisual(graphic)
            {
                ObjectOnly = true,
                Width = w,
                Height = h,
                ObjectOffset = new Vector(-bounds.Left + pad, -bounds.Top + pad),
                Effect = null,
            };
            vis.Measure(new Size(w, h));
            vis.Arrange(new Rect(0, 0, w, h));

            // read back the silhouette alpha. the alpha byte sits at offset 3 in both BGRA8888
            // and RGBA8888, so no per-platform branching is needed here.
            var alpha = new byte[w * h];
            using (var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96)))
            {
                rtb.Render(vis);

                var stride = w * 4;
                var buf = new byte[stride * h];
                var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
                try
                {
                    rtb.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buf.Length, stride);
                }
                finally
                {
                    handle.Free();
                }

                for (int i = 0; i < alpha.Length; i++)
                    alpha[i] = buf[i * 4 + 3];
            }

            BoxBlur3(alpha, w, h, sigma);

            // tint with the (premultiplied) shadow color: black at ShadowAlpha opacity → B=G=R=0
            var shadow = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);
            using (var fb = shadow.Lock())
            {
                var row = new byte[w * 4]; // BGR stay 0 (premultiplied black)
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                        row[x * 4 + 3] = (byte)(alpha[y * w + x] * GraphicVisual.ShadowAlpha / 255);
                    Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
                }
            }

            return shadow;
        }

        /// <summary>3 successive box blurs ≈ gaussian (standard boxesForGauss derivation).</summary>
        private static void BoxBlur3(byte[] data, int w, int h, double sigma)
        {
            var tmp = new byte[data.Length];
            foreach (var size in BoxesForGauss(sigma, 3))
            {
                var r = (size - 1) / 2;
                if (r <= 0) continue;
                BoxBlurH(data, tmp, w, h, r);
                BoxBlurV(tmp, data, w, h, r);
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

        // sliding-window box blurs; pixels outside the image count as fully transparent
        private static void BoxBlurH(byte[] src, byte[] dst, int w, int h, int r)
        {
            var div = 2 * r + 1;
            for (int y = 0; y < h; y++)
            {
                var row = y * w;
                var sum = 0;
                for (int x = 0; x < Math.Min(r, w); x++)
                    sum += src[row + x];
                for (int x = 0; x < w; x++)
                {
                    if (x + r < w) sum += src[row + x + r];
                    dst[row + x] = (byte)(sum / div);
                    if (x - r >= 0) sum -= src[row + x - r];
                }
            }
        }

        private static void BoxBlurV(byte[] src, byte[] dst, int w, int h, int r)
        {
            var div = 2 * r + 1;
            for (int x = 0; x < w; x++)
            {
                var sum = 0;
                for (int y = 0; y < Math.Min(r, h); y++)
                    sum += src[y * w + x];
                for (int y = 0; y < h; y++)
                {
                    if (y + r < h) sum += src[(y + r) * w + x];
                    dst[y * w + x] = (byte)(sum / div);
                    if (y - r >= 0) sum -= src[(y - r) * w + x];
                }
            }
        }
    }
}
