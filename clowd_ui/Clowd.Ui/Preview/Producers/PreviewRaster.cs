using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Clowd.UI.Preview.Producers
{
    /// <summary>
    /// The two pieces of pixel plumbing every raster producer needs: how big the result should be,
    /// and how to get it out of Skia in the shape <see cref="PreviewPixels"/> promises.
    /// </summary>
    /// <remarks>
    /// Shared rather than copied because the two halves have to agree. <see cref="Fit"/> is what
    /// makes an image producer and a poster producer hand the tile the same kind of thing — a
    /// picture at its own aspect ratio, never padded — and <see cref="ToPixels"/> is the one place
    /// that knows the composite/readback split: Skia will only render into a premultiplied surface,
    /// while <see cref="PreviewPixels"/> is unpremultiplied because the engine wraps it in a
    /// WriteableBitmap created with <c>AlphaFormat.Unpremul</c>. Doing the conversion on the way
    /// out is the same idiom <c>FileIconRenderer</c> and <c>PreviewDiskCache</c> already use.
    /// </remarks>
    internal static class PreviewRaster
    {
        /// <summary>
        /// The largest size with <paramref name="srcWidth"/>:<paramref name="srcHeight"/>'s aspect
        /// that fits inside the tile. Never enlarges: a 60x40 thumbnail of a 60x40 source is the
        /// honest picture, and upscaling it here would only bloat the cache entry and hand the tile
        /// a blurrier version of what it can already draw crisply at any size it likes.
        /// </summary>
        internal static (int Width, int Height) Fit(int srcWidth, int srcHeight, int maxWidth, int maxHeight)
        {
            if (srcWidth <= 0 || srcHeight <= 0 || maxWidth <= 0 || maxHeight <= 0)
                return (0, 0);

            double scale = Math.Min((double)maxWidth / srcWidth, (double)maxHeight / srcHeight);
            if (scale >= 1.0)
                return (srcWidth, srcHeight);

            // Rounded, then floored into the box: rounding alone can push a dimension one pixel
            // past the bound on a near-exact fit, and a 221px buffer in a 220px tile is a silent
            // extra resample every time it is drawn.
            int w = Math.Clamp((int)Math.Round(srcWidth * scale), 1, maxWidth);
            int h = Math.Clamp((int)Math.Round(srcHeight * scale), 1, maxHeight);
            return (w, h);
        }

        /// <summary>
        /// Copies a Skia pixmap out as tightly packed, top-down, unpremultiplied BGRA, converting
        /// as it goes. Null when the read failed — which for a pixmap we just rendered into means
        /// an out-of-memory condition, not a bad input.
        /// </summary>
        internal static PreviewPixels ToPixels(SKPixmap pixmap, int width, int height, PreviewKind kind)
        {
            if (pixmap == null || width <= 0 || height <= 0)
                return null;

            var bgra = new byte[(long)width * height * 4];
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                var target = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                if (!pixmap.ReadPixels(target, pin.AddrOfPinnedObject(), width * 4))
                    return null;
            }
            finally
            {
                pin.Free();
            }

            return new PreviewPixels(bgra, width, height, kind);
        }

        /// <summary>
        /// <see cref="ToPixels(SKPixmap,int,int,PreviewKind)"/> over a bitmap's own pixels. Skia is
        /// free to pad a bitmap's rows, so this goes through the pixmap rather than reading
        /// <c>GetPixelSpan</c> and assuming the stride.
        /// </summary>
        internal static PreviewPixels ToPixels(SKBitmap bitmap, PreviewKind kind)
        {
            if (bitmap == null)
                return null;

            using var pixmap = bitmap.PeekPixels();
            return ToPixels(pixmap, bitmap.Width, bitmap.Height, kind);
        }
    }
}
