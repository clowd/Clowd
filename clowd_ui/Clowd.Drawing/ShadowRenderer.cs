using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing
{
    /// <summary>
    /// CPU drop-shadow builder — the ONLY shadow implementation (final-design §A.3).
    /// Visual.Effect is never used; both the screen (via ShadowSpriteCache sprites blitted by
    /// SceneRenderer) and the export path bake shadows here, so they match by construction.
    /// The pipeline rasterizes the graphic alone, blurs its alpha channel with a 3-pass box
    /// blur (gaussian approximation) and tints it with the shadow color.
    ///
    /// Two properties of the bake (refactors over the original export-only version — the
    /// constants and the blur formula are unchanged, this is the spec look):
    /// (a) sprites are baked RELATIVE to the graphic's bounds origin, so a pure translation
    ///     reuses the bitmap at its new position;
    /// (b) a bakeScale parameter scales geometry AND sigma, so the zoom-bucketed sprite cache
    ///     can bake crisper sprites at high zoom. bakeScale = 1 is byte-identical to the
    ///     original absolute-space bake.
    /// </summary>
    internal static class ShadowRenderer
    {
        // drop shadow parameters (§2.5) — the spec look, shared by screen sprites and export
        internal const double ShadowOffsetX = 1.414;
        internal const double ShadowOffsetY = 1.414;
        internal const double ShadowBlurRadius = 5;
        internal const byte ShadowAlpha = 0x80;

        // skia's blur-radius → gaussian sigma conversion, matching what the compositor
        // DropShadowEffect used to do on screen
        private static double RadiusToSigma(double radius) => radius > 0 ? 0.57735 * radius + 0.5 : 0;

        // one reusable bake host (UI-thread only — bakes run on the dispatcher)
        [ThreadStatic] private static DrawDelegateVisual _bakeHost;

        /// <summary>
        /// Pixel size of the sprite <see cref="Render"/> would produce for this graphic at the
        /// given bake scale (used by the sprite cache to enforce dimension caps up front).
        /// </summary>
        internal static PixelSize MeasureSprite(GraphicBase graphic, double bakeScale)
        {
            var (w, h, _) = SpriteDims(graphic, bakeScale);
            return new PixelSize(w, h);
        }

        /// <summary>
        /// Largest scale ≤ <paramref name="desiredScale"/> whose sprite fits
        /// <paramref name="maxDimension"/> pixels on both sides.
        /// </summary>
        internal static double ClampBakeScale(GraphicBase graphic, double desiredScale, int maxDimension)
        {
            var scale = desiredScale;
            for (int i = 0; i < 4; i++) // the ceil() in SpriteDims makes this non-exact; iterate
            {
                var px = MeasureSprite(graphic, scale);
                var max = Math.Max(px.Width, px.Height);
                if (max <= maxDimension)
                    break;
                scale *= (double)maxDimension / max;
            }

            return scale;
        }

        private static (int w, int h, int padPx) SpriteDims(GraphicBase graphic, double bakeScale)
        {
            var bounds = graphic.Bounds;
            var sigma = RadiusToSigma(ShadowBlurRadius);

            // padding must cover the blur falloff plus any stroke drawn outside Bounds. The pad
            // is computed in canvas units with the exact original formula (so bakeScale = 1
            // reproduces the original bake byte-for-byte), then converted to pixels.
            var pad = (int)Math.Ceiling(sigma * 3 + graphic.LineWidth + 2);
            var padPx = (int)Math.Ceiling(pad * bakeScale);
            var w = (int)Math.Ceiling(bounds.Width * bakeScale) + padPx * 2;
            var h = (int)Math.Ceiling(bounds.Height * bakeScale) + padPx * 2;
            return (w, h, padPx);
        }

        /// <summary>
        /// Bakes the shadow bitmap for a single graphic. The bitmap is <paramref name="bakeScale"/>
        /// pixels per canvas unit; <paramref name="originFromBoundsTopLeft"/> receives the sprite's
        /// top-left relative to the graphic's Bounds top-left, in canvas units, shadow offset
        /// included — so the caller positions it with <c>Bounds.TopLeft + origin</c> and a pure
        /// translation of the graphic moves the sprite for free.
        /// </summary>
        public static WriteableBitmap Render(GraphicBase graphic, double bakeScale, out Vector originFromBoundsTopLeft)
        {
            var bounds = graphic.Bounds;
            var (w, h, padPx) = SpriteDims(graphic, bakeScale);

            originFromBoundsTopLeft = new Vector(-padPx / bakeScale + ShadowOffsetX,
                                                 -padPx / bakeScale + ShadowOffsetY);

            // rasterize the graphic alone (object only, no selection chrome, no effect),
            // bounds-relative: p → (p - bounds.TopLeft) · bakeScale + padPx
            var transform = Matrix.CreateTranslation(-bounds.Left, -bounds.Top)
                            * Matrix.CreateScale(bakeScale, bakeScale)
                            * Matrix.CreateTranslation(padPx, padPx);

            var host = _bakeHost ??= new DrawDelegateVisual();
            host.Draw = ctx =>
            {
                using (ctx.PushTransform(transform))
                    graphic.DrawObject(ctx);
            };
            host.Width = w;
            host.Height = h;
            host.Measure(new Size(w, h));
            host.Arrange(new Rect(0, 0, w, h));

            // read back the silhouette alpha. the alpha byte sits at offset 3 in both BGRA8888
            // and RGBA8888, so no per-platform branching is needed here.
            var alpha = new byte[w * h];
            using (var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96)))
            {
                rtb.Render(host);

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

            host.Draw = null; // drop the graphic reference held by the closure

            // blur in pixel space: sigma scales with the bake (geometry and blur stay in step)
            BoxBlur3(alpha, w, h, RadiusToSigma(ShadowBlurRadius) * bakeScale);

            // tint with the (premultiplied) shadow color: black at ShadowAlpha opacity → B=G=R=0
            var shadow = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);
            using (var fb = shadow.Lock())
            {
                var row = new byte[w * 4]; // BGR stay 0 (premultiplied black)
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                        row[x * 4 + 3] = (byte)(alpha[y * w + x] * ShadowAlpha / 255);
                    Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
                }
            }

            return shadow;
        }

        /// <summary>
        /// 3 successive box blurs ≈ gaussian (standard boxesForGauss derivation), in place, over
        /// <paramref name="channels"/> interleaved 8-bit channels: 1 for the shadow alpha plane,
        /// 4 for a BGRA region (GraphicImage's Blur obscure mode). Channels are blurred
        /// independently, which is only correct for premultiplied color.
        /// </summary>
        internal static void BoxBlur3(byte[] data, int w, int h, double sigma, int channels = 1)
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

        // sliding-window box blurs; samples outside the image count as zero (fully transparent for
        // the shadow plane), so a caller blurring a cut-out region must pad it by the blur reach
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
