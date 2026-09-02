using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// A wallpaper boiled down to a tiny grid of colors: the frame the composer would draw,
    /// resampled to <see cref="Columns"/> x <see cref="Rows"/> cells. Stretched back up with
    /// smooth filtering the grid reads as a gradient mesh of the artwork's own colors in its own
    /// arrangement, which is what the timeline paints a background clip's body with — a card that
    /// says which wallpaper is on it without pretending to be a picture of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The frame, not the destination.</b> The scene is drawn into a 16:9 rectangle — the
    /// composition's own shape, so the mesh carries the crop the canvas shows — and never into
    /// the strip's shape, which is a 20:1 sliver whose cover-fit would sample a hairline band
    /// through the middle of the art and hand back one flat color.
    /// </para>
    /// <para>
    /// <b>Phase 0, always.</b> An animated style's card is a still: the timeline has no clock of
    /// its own (the inspector's tiles carry the only one in the feature, and pay for it), and a
    /// backdrop clip's body says WHICH wallpaper is here, which is the part that does not move.
    /// </para>
    /// <para>
    /// Cells are averaged in linear light. A cell that straddles a wave edge holds two very
    /// different colors, and averaging those as stored sRGB bytes gives a muddier, darker color
    /// than the eye sees blending them — visible as a dull seam exactly where the art is most
    /// characteristic.
    /// </para>
    /// <para>
    /// One render per (style, theme) for the life of the process, on the caller's thread and
    /// under the same lock discipline as <see cref="BackgroundAssets"/>: at most 59 entries, of
    /// which a session touches the handful it actually picks. The one costly row is Breathing
    /// Field, whose blur runs on a fixed 480px CPU raster whatever size it is asked for (~9 ms);
    /// it is paid once, the first time a clip of it is drawn.
    /// </para>
    /// </remarks>
    public static class BackgroundSwatch
    {
        /// <summary>Cells across. Six columns and four rows is as coarse as the mesh can be while
        /// still holding a corner-to-corner sweep and a hot spot in the middle of it; finer buys
        /// detail no one can see once it is stretched across a 24px strip.</summary>
        public const int Columns = 6;

        public const int Rows = 4;

        // 6 x 32 by 4 x 27 = 192 x 108, so the sampled frame is exactly 16:9 and every cell is a
        // whole number of pixels.
        private const int CellWidth = 32;
        private const int CellHeight = 27;

        private static readonly object Sync = new object();

        private static readonly Dictionary<(string Style, string Theme), uint[]> Cache
            = new Dictionary<(string, string), uint[]>();

        /// <summary>sRGB byte to linear light, so a cell's average is an average of the light and
        /// not of the encoding.</summary>
        private static readonly float[] ToLinear = BuildToLinear();

        /// <summary>
        /// The mesh cells for a (style, theme) pair, row-major from the top-left, as opaque
        /// <c>0xRRGGBB</c>. Both ids resolve through the catalog first, so an id this build does
        /// not know describes the wallpaper it actually draws; null only when the embedded file
        /// is missing, which the catalog tests rule out for every row. Safe to call from any
        /// thread; the array is a copy the caller may keep.
        ///
        /// <paramref name="color"/> is the item's own <c>BackgroundContent.Color</c> and is read
        /// only by the solid style, whose grid is that one color in every cell (over black, so a
        /// translucent fill reads on the card as it composes on the canvas). Nothing is cached for
        /// it: the grid is per item rather than per catalog row, and filling 24 cells costs less
        /// than the lookup would.
        /// </summary>
        public static uint[] Grid(string style, string theme, string color = null)
        {
            var resolved = BackgroundCatalog.Find(BackgroundCatalog.ResolveStyle(style));
            if (resolved.IsSolid)
                return Flat(BackgroundRenderer.SolidColorOf(color));

            var key = (resolved.Id, BackgroundCatalog.ResolveTheme(resolved.Id, theme) ?? string.Empty);

            lock (Sync)
            {
                if (!Cache.TryGetValue(key, out var cells))
                {
                    cells = Sample(resolved.Id, theme);
                    Cache[key] = cells;
                }
                return (uint[])cells?.Clone();
            }
        }

        /// <summary>One color in every cell, composited over the black ground the sampler clears
        /// to, so an alpha the composer would blend is blended here too.</summary>
        private static uint[] Flat(SKColor color)
        {
            double a = color.Alpha / 255.0;
            uint rgb = ((uint)Math.Round(color.Red * a) << 16)
                | ((uint)Math.Round(color.Green * a) << 8)
                | (uint)Math.Round(color.Blue * a);

            var cells = new uint[Columns * Rows];
            for (int i = 0; i < cells.Length; i++)
                cells[i] = rgb;
            return cells;
        }

        /// <summary>Draws the wallpaper once and folds it into the cell grid; null when there is
        /// no scene to draw.</summary>
        private static uint[] Sample(string style, string theme)
        {
            var scene = BackgroundRenderer.GetScene(style, theme);
            if (scene == null)
                return null;

            int width = Columns * CellWidth;
            int height = Rows * CellHeight;
            using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                // cleared to black so a scene with any transparency in it averages against the
                // same ground the composer's own canvas gives it
                canvas.Clear(SKColors.Black);
                BackgroundRenderer.DrawScene(canvas, SKRect.Create(0, 0, width, height), scene, phase: 0);
                canvas.Flush();
            }

            var pixels = bitmap.GetPixelSpan();
            var cells = new uint[Columns * Rows];
            for (int row = 0; row < Rows; row++)
            {
                for (int column = 0; column < Columns; column++)
                    cells[row * Columns + column] = Average(pixels, width, column * CellWidth, row * CellHeight);
            }
            return cells;
        }

        /// <summary>One cell's mean color, as <c>0xRRGGBB</c>. The buffer is BGRA and every pixel
        /// is opaque (the canvas was cleared), so the alpha byte is read past rather than
        /// unpremultiplied.</summary>
        private static uint Average(ReadOnlySpan<byte> bgra, int stride, int x0, int y0)
        {
            float b = 0, g = 0, r = 0;
            for (int y = y0; y < y0 + CellHeight; y++)
            {
                int i = (y * stride + x0) * 4;
                for (int x = 0; x < CellWidth; x++, i += 4)
                {
                    b += ToLinear[bgra[i]];
                    g += ToLinear[bgra[i + 1]];
                    r += ToLinear[bgra[i + 2]];
                }
            }

            float count = CellWidth * CellHeight;
            return ((uint)ToSrgb(r / count) << 16) | ((uint)ToSrgb(g / count) << 8) | ToSrgb(b / count);
        }

        private static float[] BuildToLinear()
        {
            var table = new float[256];
            for (int i = 0; i < table.Length; i++)
            {
                double v = i / 255.0;
                table[i] = (float)(v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4));
            }
            return table;
        }

        private static byte ToSrgb(float linear)
        {
            double v = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
            return (byte)Math.Clamp((int)Math.Round(v * 255.0), 0, 255);
        }
    }
}
