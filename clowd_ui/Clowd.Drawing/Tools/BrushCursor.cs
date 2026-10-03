using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Clowd.Drawing.Tools
{
    /// <summary>
    /// The brush's cursor: a ring the size of the dot a click leaves (2·LineWidth across, in device
    /// pixels at the current zoom), drawn as a 1px white ring inside a 1px black ring so it reads on
    /// any artwork. Rasterised by hand with analytic coverage rather than through a DrawingContext,
    /// so the result is exact and needs no render pass; the cursor for the last diameter is cached.
    /// </summary>
    internal static class BrushCursor
    {
        /// <summary>Below this diameter the ring would close up into a blob.</summary>
        internal const int MinDiameter = 7;

        /// <summary>Windows draws larger cursors unreliably; a bigger brush keeps a 256px ring.</summary>
        internal const int MaxSize = 256;

        private static int _cachedDiameter = -1;
        private static Cursor _cached;

        /// <summary>The cursor for a brush <paramref name="diameterPx"/> device pixels across.</summary>
        public static Cursor Get(double diameterPx)
        {
            int diameter = Quantize(diameterPx);
            if (diameter == _cachedDiameter && _cached != null)
                return _cached;

            var pixels = Rasterize(diameter, out int size);
            var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = bitmap.Lock())
            {
                for (int y = 0; y < size; y++)
                    Marshal.Copy(pixels, y * size * 4, fb.Address + y * fb.RowBytes, size * 4);
            }

            var previous = _cached;
            _cached = new Cursor(bitmap, new PixelPoint(size / 2, size / 2));
            _cachedDiameter = diameter;
            previous?.Dispose();
            return _cached;
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
