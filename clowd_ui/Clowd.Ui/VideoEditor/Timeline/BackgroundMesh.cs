using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Clowd.VideoSDK.Composition;

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>
    /// The wallpaper cards' body art: one <see cref="BackgroundSwatch"/> grid per (style, theme)
    /// as a <see cref="BackgroundSwatch.Columns"/> x <see cref="BackgroundSwatch.Rows"/> bitmap,
    /// which the surface stretches across the clip under smooth filtering. Upscaling a grid of
    /// colors IS a gradient mesh — one image draw, no gradient brushes to rebuild per item and
    /// per zoom — and because the cells come out of the composer's own render the mesh is the
    /// wallpaper's colors in the wallpaper's arrangement rather than a decorative guess.
    /// </summary>
    /// <remarks>
    /// Built on the UI thread on first use of a theme and kept for the life of the process: the
    /// grid behind it is a fixed 24 colors, the bitmap is 96 bytes, and a session touches only
    /// the themes it actually puts on the timeline. The render behind the first ask costs a few
    /// milliseconds (see <see cref="BackgroundSwatch"/>), paid once per theme.
    /// </remarks>
    internal static class BackgroundMesh
    {
        /// <summary>
        /// The part of the bitmap a card draws: inset half a texel, so the destination runs from
        /// the first cell's center to the last one's. Drawing the whole bitmap instead would put
        /// the outer half-cell of every edge into clamped territory, which reads as a flat band
        /// of color around a mesh rather than as a mesh.
        /// </summary>
        public static readonly Rect Source =
            new Rect(0.5, 0.5, BackgroundSwatch.Columns - 1, BackgroundSwatch.Rows - 1);

        private static readonly Dictionary<(string Style, string Theme), Bitmap> Cache
            = new Dictionary<(string, string), Bitmap>();

        /// <summary>The mesh for a (style, theme) pair, or null when there is no artwork to
        /// sample — in which case the card keeps its plain row fill. Ids resolve through the
        /// catalog, so the key is the pair actually drawn.</summary>
        public static Bitmap Get(string style, string theme)
        {
            var styleId = BackgroundCatalog.ResolveStyle(style);
            var key = (styleId, BackgroundCatalog.ResolveTheme(styleId, theme) ?? String.Empty);
            if (Cache.TryGetValue(key, out var cached))
                return cached;

            var bitmap = Build(styleId, theme);
            Cache[key] = bitmap;
            return bitmap;
        }

        private static Bitmap Build(string style, string theme)
        {
            var cells = BackgroundSwatch.Grid(style, theme);
            if (cells == null)
                return null;

            var bitmap = new WriteableBitmap(new PixelSize(BackgroundSwatch.Columns, BackgroundSwatch.Rows),
                new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

            using (var buffer = bitmap.Lock())
            {
                // laid out on the framebuffer's own stride rather than a packed 4 * Columns:
                // a 6px row is padded on most backends.
                int stride = buffer.RowBytes;
                var bytes = new byte[stride * BackgroundSwatch.Rows];
                for (int row = 0; row < BackgroundSwatch.Rows; row++)
                {
                    for (int column = 0; column < BackgroundSwatch.Columns; column++)
                    {
                        uint rgb = cells[row * BackgroundSwatch.Columns + column];
                        int i = row * stride + column * 4;
                        bytes[i + 0] = (byte)rgb;
                        bytes[i + 1] = (byte)(rgb >> 8);
                        bytes[i + 2] = (byte)(rgb >> 16);
                        bytes[i + 3] = 0xFF;
                    }
                }
                Marshal.Copy(bytes, 0, buffer.Address, bytes.Length);
            }

            return bitmap;
        }
    }
}
