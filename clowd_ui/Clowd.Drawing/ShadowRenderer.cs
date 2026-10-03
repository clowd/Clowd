using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
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

        /// <summary>The blur's sigma in sprite pixels at a bake scale.</summary>
        internal static double SigmaPx(double bakeScale) => RadiusToSigma(ShadowBlurRadius) * bakeScale;

        /// <summary>The pad around the ink, in canvas units, that covers the blur falloff plus any
        /// stroke drawn outside Bounds (the exact original formula, see <see cref="SpriteDims"/>).</summary>
        internal static int Pad(double lineWidth) => (int)Math.Ceiling(RadiusToSigma(ShadowBlurRadius) * 3 + lineWidth + 2);

        /// <summary>
        /// How far, in pixels, a source pixel reaches through the three box passes of
        /// <see cref="BoxBlur3"/> at this sigma: the sum of the box radii. A window re-blurred
        /// from a source padded by this much is exact inside the window.
        /// </summary>
        internal static int BlurReach(double sigmaPx)
        {
            int reach = 0;
            foreach (var size in BoxesForGauss(sigmaPx, 3))
                reach += Math.Max(0, (size - 1) / 2);
            return reach;
        }

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

            // padding must cover the blur falloff plus any stroke drawn outside Bounds. The pad
            // is computed in canvas units with the exact original formula (so bakeScale = 1
            // reproduces the original bake byte-for-byte), then converted to pixels.
            var pad = Pad(graphic.LineWidth);
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

            var alpha = new byte[w * h];
            using (var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96)))
            {
                RasterAlpha(rtb, new PixelRect(0, 0, w, h), ctx =>
                {
                    using (ctx.PushTransform(transform))
                        graphic.DrawShadowSilhouette(ctx);
                }, alpha);
            }

            // blur in pixel space: sigma scales with the bake (geometry and blur stay in step)
            BoxBlur3(alpha, w, h, SigmaPx(bakeScale));

            // tint with the (premultiplied) shadow color: black at ShadowAlpha opacity → B=G=R=0
            var shadow = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);
            using (var fb = shadow.Lock())
                Tint(alpha, w, new PixelRect(0, 0, w, h), fb);

            return shadow;
        }

        /// <summary>
        /// Rasterizes <paramref name="draw"/> (object only, no chrome, no effect) into
        /// <paramref name="rtb"/>, which is cleared first and may be larger than the
        /// <paramref name="rect"/> read back, and writes that rect's alpha plane — row-major,
        /// rect.Width per row — into <paramref name="alpha"/>. The alpha byte sits at offset 3 in
        /// both BGRA8888 and RGBA8888, so no per-platform branching is needed here.
        /// </summary>
        internal static void RasterAlpha(RenderTargetBitmap rtb, PixelRect rect, Action<DrawingContext> draw, byte[] alpha)
        {
            var size = rtb.PixelSize;
            var host = _bakeHost ??= new DrawDelegateVisual();
            host.Draw = draw;
            host.Width = size.Width;
            host.Height = size.Height;
            host.Measure(new Size(size.Width, size.Height));
            host.Arrange(new Rect(0, 0, size.Width, size.Height));
            rtb.Render(host);
            host.Draw = null; // drop the graphic reference held by the closure

            int w = rect.Width, h = rect.Height;
            var stride = w * 4;
            var buf = new byte[stride * h];
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                rtb.CopyPixels(rect, handle.AddrOfPinnedObject(), buf.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            for (int i = 0; i < w * h; i++)
                alpha[i] = buf[i * 4 + 3];
        }

        /// <summary>
        /// Writes the <paramref name="rect"/> of a blurred alpha plane (<paramref name="planeWidth"/>
        /// per row) into the same rect of a locked premultiplied BGRA sprite as the shadow color:
        /// black at ShadowAlpha opacity → B=G=R=0, A = alpha·ShadowAlpha/255. An
        /// <paramref name="inkAlpha"/> below 255 scales it further, for a plane whose silhouette
        /// was drawn opaque on behalf of translucent ink.
        /// </summary>
        internal static void Tint(byte[] alpha, int planeWidth, PixelRect rect, ILockedFramebuffer fb, int inkAlpha = 255)
        {
            var row = new byte[rect.Width * 4]; // BGR stay 0 (premultiplied black)
            for (int y = rect.Y; y < rect.Bottom; y++)
            {
                for (int x = 0; x < rect.Width; x++)
                    row[x * 4 + 3] = (byte)(alpha[y * planeWidth + rect.X + x] * inkAlpha / 255 * ShadowAlpha / 255);
                Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes + rect.X * 4, row.Length);
            }
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
