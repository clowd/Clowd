using System;
using System.IO;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// Exercises the shared decode cache behind GraphicImage: undo/redo restores snapshots into
    /// brand new GraphicImage instances, and re-decoding the screenshot from disk on every step
    /// is what made undo slow — instances over the same (unchanged) file must share one decoded
    /// bitmap, and a rewritten file must not be served stale.
    /// </summary>
    public class GraphicImageTests
    {
        private static void WritePng(string path, int width, int height)
        {
            using var wb = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                                               PixelFormats.Bgra8888, AlphaFormat.Premul);
            wb.Save(path);
        }

        [AvaloniaFact]
        public void CompositedImage_SupportsCreateScaledBitmap()
        {
            // the pixelate obscure cache calls CreateScaledBitmap on the decoded source; Skia only
            // accepts immutable bitmaps there ("Invalid source bitmap type."), so the composited /
            // pixel-copied source must be immutable.
            var dir = Directory.CreateTempSubdirectory();
            try
            {
                var imgPath = Path.Combine(dir.FullName, "img.png");
                var cursorPath = Path.Combine(dir.FullName, "cursor.png");
                WritePng(imgPath, 10, 8);
                WritePng(cursorPath, 4, 4);

                var graphic = new GraphicImage(imgPath, new Rect(0, 0, 10, 8), default,
                                               cursorFilePath: cursorPath,
                                               cursorPosition: new PixelRect(0, 0, 4, 4),
                                               cursorVisible: true);

                using var scaled = graphic.ImageSource.CreateScaledBitmap(new PixelSize(5, 4));
                Assert.Equal(5, scaled.PixelSize.Width);
                Assert.Equal(4, scaled.PixelSize.Height);
            }
            finally
            {
                dir.Delete(true);
            }
        }

        [AvaloniaFact]
        public void DecodedBitmap_IsSharedAcrossInstances_AndInvalidatedOnRewrite()
        {
            var dir = Directory.CreateTempSubdirectory();
            try
            {
                var path = Path.Combine(dir.FullName, "img.png");
                WritePng(path, 10, 8);

                var first = new GraphicImage(path, new Size(10, 8));
                Assert.Equal(10, first.BitmapPixelWidth);
                Assert.Equal(8, first.BitmapPixelHeight);

                // a second instance over the same unchanged file shares the decoded bitmap
                var second = new GraphicImage(path, new Size(10, 8));
                Assert.Same(first.ImageSource, second.ImageSource);

                // rewriting the file must miss the cache (key carries mtime/length)
                WritePng(path, 20, 16);
                var third = new GraphicImage(path, new Size(20, 16));
                Assert.NotSame(first.ImageSource, third.ImageSource);
                Assert.Equal(20, third.BitmapPixelWidth);
                Assert.Equal(16, third.BitmapPixelHeight);
            }
            finally
            {
                dir.Delete(true);
            }
        }
    }
}
