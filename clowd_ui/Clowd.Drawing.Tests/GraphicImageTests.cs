using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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
        private static readonly MethodInfo ImgUpdateObscure =
            typeof(GraphicImage).GetMethod("UpdateObscureCache", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo ImgObscured =
            typeof(GraphicImage).GetField("_imageObscured", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void WritePng(string path, int width, int height)
        {
            using var wb = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                                               PixelFormats.Bgra8888, AlphaFormat.Premul);
            wb.Save(path, PngBitmapEncoderOptions.Default);
        }

        /// <summary>Opaque white left of <paramref name="seamX"/>, opaque black right of it.</summary>
        private static void WriteSeamPng(string path, int width, int height, int seamX)
        {
            using var wb = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                                               PixelFormats.Bgra8888, AlphaFormat.Premul);
            using (var fb = wb.Lock())
            {
                var row = new byte[width * 4];
                for (int x = 0; x < width; x++)
                {
                    byte v = x < seamX ? (byte)255 : (byte)0;
                    row[x * 4 + 0] = v;
                    row[x * 4 + 1] = v;
                    row[x * 4 + 2] = v;
                    row[x * 4 + 3] = 255;
                }

                for (int y = 0; y < height; y++)
                    Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
            }

            wb.Save(path, PngBitmapEncoderOptions.Default);
        }

        // color channel + alpha of one overlay pixel. Only the first color byte is read, so this
        // does not care whether the backend hands back BGRA or RGBA (the fixture is grayscale).
        private static (byte Value, byte Alpha) ObscuredPixel(GraphicImage img, int x, int y)
        {
            var overlay = (Bitmap)ImgObscured.GetValue(img);
            var buffer = new byte[4];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                overlay.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), buffer.Length, 4);
            }
            finally
            {
                handle.Free();
            }

            return (buffer[0], buffer[3]);
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

        // ====================================================================
        // Corner radius: a picked window's rounded corners (seeded from session.json) and any
        // radius the user sets afterwards are a rounded clip around the drawn crop — the corners
        // must come out transparent in the export bitmap, the straight edges and interior must not.
        // ====================================================================

        private static (byte B, byte G, byte R, byte A) ExportPixel(Bitmap bmp, int x, int y)
        {
            var buffer = new byte[4];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                bmp.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), buffer.Length, 4);
            }
            finally
            {
                handle.Free();
            }

            return (buffer[0], buffer[1], buffer[2], buffer[3]);
        }

        [AvaloniaFact]
        public void CornerRadius_ExportsTransparentCorners()
        {
            var dir = Directory.CreateTempSubdirectory();
            try
            {
                var imgPath = Path.Combine(dir.FullName, "img.png");
                // opaque everywhere (white/black seam at x=30 — any opaque content does)
                WriteSeamPng(imgPath, 60, 40, 30);

                var canvas = new DrawingCanvas { Tool = ToolType.None };
                var img = new GraphicImage(imgPath, new Rect(0, 0, 60, 40), default) { CornerRadius = 12 };
                canvas.GraphicsList.Add(img);

                using var bmp = canvas.GraphicsList.DrawGraphicsToBitmap(Brushes.Transparent);
                Assert.NotNull(bmp);
                Assert.Equal(60, bmp.PixelSize.Width);
                Assert.Equal(40, bmp.PixelSize.Height);

                // the four corner pixels sit well outside a 12px arc → fully transparent
                foreach (var (x, y) in new[] { (0, 0), (59, 0), (0, 39), (59, 39) })
                    Assert.Equal(0, ExportPixel(bmp, x, y).A);

                // the straight edges between the arcs, and the interior, stay opaque
                Assert.Equal(255, ExportPixel(bmp, 30, 0).A);
                Assert.Equal(255, ExportPixel(bmp, 0, 20).A);
                Assert.Equal(255, ExportPixel(bmp, 59, 20).A);
                Assert.Equal(255, ExportPixel(bmp, 30, 20).A);
                // and the inner corner of the 12px square (inside the arc) is opaque too
                Assert.Equal(255, ExportPixel(bmp, 11, 11).A);
            }
            finally
            {
                dir.Delete(true);
            }
        }

        [AvaloniaFact]
        public void CornerRadius_ZeroIsSquare()
        {
            var dir = Directory.CreateTempSubdirectory();
            try
            {
                var imgPath = Path.Combine(dir.FullName, "img.png");
                WriteSeamPng(imgPath, 20, 16, 10);

                var canvas = new DrawingCanvas { Tool = ToolType.None };
                canvas.GraphicsList.Add(new GraphicImage(imgPath, new Rect(0, 0, 20, 16), default));

                using var bmp = canvas.GraphicsList.DrawGraphicsToBitmap(Brushes.Transparent);
                foreach (var (x, y) in new[] { (0, 0), (19, 0), (0, 15), (19, 15) })
                    Assert.Equal(255, ExportPixel(bmp, x, y).A);
            }
            finally
            {
                dir.Delete(true);
            }
        }

        [AvaloniaFact]
        public void CornerRadius_ScalesWithTheDrawnSize()
        {
            // the radius is in bitmap pixels: drawn at 2× the crop, a 4px radius rounds an 8px
            // corner, so the pixel at (5,5) — inside a 4px arc, outside an 8px one — is cut
            var dir = Directory.CreateTempSubdirectory();
            try
            {
                var imgPath = Path.Combine(dir.FullName, "img.png");
                WriteSeamPng(imgPath, 20, 20, 10);

                var canvas = new DrawingCanvas { Tool = ToolType.None };
                canvas.GraphicsList.Add(new GraphicImage(imgPath, new Rect(0, 0, 40, 40), default) { CornerRadius = 4 });

                using var bmp = canvas.GraphicsList.DrawGraphicsToBitmap(Brushes.Transparent);
                Assert.Equal(40, bmp.PixelSize.Width);
                Assert.Equal(0, ExportPixel(bmp, 0, 0).A);
                Assert.True(ExportPixel(bmp, 1, 1).A < 255, "an 8px arc leaves (1,1) outside");
                Assert.Equal(255, ExportPixel(bmp, 8, 8).A);
            }
            finally
            {
                dir.Delete(true);
            }
        }

        // ====================================================================
        // Obscure modes: each mode bakes a different overlay, and none of them paints outside the
        // shape (the composite is what export ships, so an unobscured leak here is a privacy bug)
        // ====================================================================

        private static GraphicImage ObscuredFixture(string path, ObscureMode mode)
        {
            var img = new GraphicImage(path, new Rect(0, 0, 80, 60), default);
            img.ObscuredShapes = new[]
            {
                new GraphicImage.ObscuredShape(new Point(20, 10), new Point(60, 10), new Point(60, 50), new Point(20, 50), 3,
                                               mode),
            };
            Assert.True((bool)ImgUpdateObscure.Invoke(img, null));
            return img;
        }

        [AvaloniaFact]
        public void ObscureModes_BakeDistinctOverlays_ConfinedToTheShape()
        {
            var dir = Directory.CreateTempSubdirectory();
            try
            {
                var path = Path.Combine(dir.FullName, "seam.png");
                WriteSeamPng(path, 80, 60, 40);

                var mosaic = ObscuredFixture(path, ObscureMode.Mosaic);
                var blur = ObscuredFixture(path, ObscureMode.Blur);
                var solid = ObscuredFixture(path, ObscureMode.Solid);

                // nothing is painted outside the shape, whatever the mode
                Assert.Equal(0, ObscuredPixel(mosaic, 5, 5).Alpha);
                Assert.Equal(0, ObscuredPixel(blur, 5, 5).Alpha);
                Assert.Equal(0, ObscuredPixel(solid, 5, 5).Alpha);

                // solid redacts to opaque black
                Assert.Equal((byte)0, ObscuredPixel(solid, 40, 30).Value);
                Assert.Equal((byte)255, ObscuredPixel(solid, 40, 30).Alpha);

                // deep inside the white half both samplers still read white; only at the seam do
                // they diverge — the blur kernel mixes the two halves, the mosaic cell does not
                Assert.Equal((byte)255, ObscuredPixel(mosaic, 25, 30).Value);
                Assert.Equal((byte)255, ObscuredPixel(blur, 25, 30).Value);

                var blurSeam = ObscuredPixel(blur, 40, 30);
                Assert.Equal((byte)255, blurSeam.Alpha);
                Assert.InRange(blurSeam.Value, 60, 195);
                Assert.False(ObscuredPixel(mosaic, 40, 30).Value == blurSeam.Value);
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
