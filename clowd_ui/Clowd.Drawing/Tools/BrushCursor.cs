using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Clowd.Drawing.Tools
{
    /// <summary>
    /// The brush's cursor: a ring the size of the dot a click leaves (GraphicBrush.SizePerLineWidth·LineWidth across, in device
    /// pixels at the current zoom), drawn as a 1px white ring inside a 1px black ring so it reads on
    /// any artwork. Rasterised by hand with analytic coverage rather than through a DrawingContext,
    /// so the result is exact and needs no render pass. Ring cursors are cached per diameter and never
    /// disposed: several canvases (one per monitor, each at its own scaling) hold rings of different
    /// sizes at once, and disposing the previous diameter's cursor broke the one another canvas was
    /// still showing. Diameters are whole pixels below <see cref="MaxSize"/>, so the cache is bounded.
    /// The highlighter's is the same two bands around its rectangular tip (<see cref="GetRect"/>).
    /// </summary>
    internal static class BrushCursor
    {
        /// <summary>Below this diameter the ring would close up into a blob.</summary>
        internal const int MinDiameter = 7;

        /// <summary>Windows draws larger cursors unreliably; a bigger brush keeps a 256px ring.</summary>
        internal const int MaxSize = 256;

        /// <summary>Below this width a rectangle's two white sides would touch.</summary>
        internal const int MinRectWidth = 3;

        private static readonly Dictionary<int, Cursor> _rings = new Dictionary<int, Cursor>();

        private static PixelSize _cachedRectSize;
        private static Cursor _cachedRect;

        /// <summary>The cursor for a brush <paramref name="diameterPx"/> device pixels across.</summary>
        public static Cursor Get(double diameterPx)
        {
            int diameter = Quantize(diameterPx);
            if (_rings.TryGetValue(diameter, out var cached))
                return cached;

            var pixels = Rasterize(diameter, out int size);
            var cursor = CreateCursor(pixels, size, size);
            _rings[diameter] = cursor;
            return cursor;
        }

        /// <summary>The cursor for a rectangular tip <paramref name="widthPx"/> by
        /// <paramref name="heightPx"/> device pixels, hotspot at its centre.</summary>
        public static Cursor GetRect(double widthPx, double heightPx)
        {
            var tip = QuantizeRect(widthPx, heightPx);
            if (tip == _cachedRectSize && _cachedRect != null)
                return _cachedRect;

            var pixels = RasterizeRect(tip.Width, tip.Height, out int w, out int h);
            var previous = _cachedRect;
            _cachedRect = CreateCursor(pixels, w, h);
            _cachedRectSize = tip;
            previous?.Dispose();
            return _cachedRect;
        }

        private static Cursor CreateCursor(byte[] pixels, int w, int h)
        {
            var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = bitmap.Lock())
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(pixels, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
            }

            return new Cursor(bitmap, new PixelPoint(w / 2, h / 2));
        }

        /// <summary>Whole pixels, each side clamped like <see cref="Quantize"/> (the width to
        /// <see cref="MinRectWidth"/>, so a thin tip still shows a white core).</summary>
        internal static PixelSize QuantizeRect(double widthPx, double heightPx) =>
            new PixelSize(Math.Clamp((int)Math.Round(widthPx), MinRectWidth, MaxSize - 4),
                          Math.Clamp((int)Math.Round(heightPx), MinDiameter, MaxSize - 4));

        /// <summary>
        /// Premultiplied BGRA pixels of a bitmap centred on a <paramref name="width"/> by
        /// <paramref name="height"/> rectangle: its outermost pixel row/column white, and a 1px
        /// black band just outside. The rectangle is pixel-aligned, so both bands are crisp.
        /// </summary>
        internal static byte[] RasterizeRect(int width, int height, out int w, out int h)
        {
            w = width + 4; // the black band and one pixel of slack on each side, like the ring
            h = height + 4;
            var pixels = new byte[w * h * 4];

            // the rectangle covers [2, 2 + width) × [2, 2 + height)
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    bool inside = x >= 2 && x < 2 + width && y >= 2 && y < 2 + height;
                    bool edge = inside && (x == 2 || x == 1 + width || y == 2 || y == 1 + height);
                    if (inside && !edge)
                        continue;

                    byte grey = edge ? (byte)255 : (byte)0; // outside the rect (within 1px): the black band
                    int i = (y * w + x) * 4;
                    pixels[i] = grey;
                    pixels[i + 1] = grey;
                    pixels[i + 2] = grey;
                    pixels[i + 3] = 255;
                }
            }

            return pixels;
        }

        /// <summary>Whole pixels, clamped so the ring is visible and the bitmap stays within
        /// <see cref="MaxSize"/> (the black ring sits outside the diameter, plus one pixel of AA).</summary>
        internal static int Quantize(double diameterPx) =>
            Math.Clamp((int)Math.Round(diameterPx), MinDiameter, MaxSize - 4);

        /// <summary>
        /// Premultiplied BGRA pixels of a square bitmap centred on the ring. The white ring covers
        /// radii [r − 1, r] and the black ring [r, r + 1], r = diameter / 2; coverage is the overlap
        /// of each band with a 1px box filter on the distance. The bitmap's size has the diameter's
        /// parity, so the centre lands on a pixel corner (even) or a pixel centre (odd) and every
        /// band edge falls between pixels where the ring crosses the axes: crisp, not half-blurred.
        /// The hotspot is the pixel at or just below-right of the centre.
        /// </summary>
        internal static byte[] Rasterize(int diameter, out int size)
        {
            double r = diameter / 2.0;
            size = diameter + 4; // the black ring's outer pixel on each side, plus one of AA
            double c = size / 2.0;
            var pixels = new byte[size * size * 4];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double dx = x + 0.5 - c, dy = y + 0.5 - c;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    double white = Band(d, r - 1, r);
                    double black = Band(d, r, r + 1);
                    double alpha = white + black;
                    if (alpha <= 0)
                        continue;

                    byte grey = (byte)Math.Round(255 * white); // premultiplied: white contributes its coverage
                    int i = (y * size + x) * 4;
                    pixels[i] = grey;
                    pixels[i + 1] = grey;
                    pixels[i + 2] = grey;
                    pixels[i + 3] = (byte)Math.Round(255 * Math.Min(1, alpha));
                }
            }

            return pixels;
        }

        // coverage of the band [inner, outer] by a pixel whose centre is d from the ring's centre
        private static double Band(double d, double inner, double outer) =>
            Math.Clamp(outer - d + 0.5, 0, 1) - Math.Clamp(inner - d + 0.5, 0, 1);
    }
}
