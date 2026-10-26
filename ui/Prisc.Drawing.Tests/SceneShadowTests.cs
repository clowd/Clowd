using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// Pins the live drop shadow drawn by SceneRenderer (a tinted, blurred Skia layer): it is the
    /// shadow color whatever the ink color, soft, offset down-right, and absent when the graphic
    /// has its shadow turned off.
    /// </summary>
    public class SceneShadowTests
    {
        static SceneShadowTests()
        {
            // DrawingCanvas reads tool settings from SettingsRoot.Current, which the app assigns
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
        }

        // the export spans the two specks: (0,0) to (200,200)
        private const int Size = 200;

        private static byte[] Export(bool shadow)
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            // a thick light gray frame casting the shadow, and shadowless specks at two corners so the export
            // bounds leave room for the shadow on every side
            canvas.GraphicsList.Add(new GraphicRectangle(Color.FromRgb(200, 200, 200), 10, new Rect(40, 40, 100, 100), dropShadowEffect: shadow));
            canvas.GraphicsList.Add(new GraphicRectangle(Colors.Blue, 1, new Rect(0, 0, 1, 1), dropShadowEffect: false));
            canvas.GraphicsList.Add(new GraphicRectangle(Colors.Blue, 1, new Rect(199, 199, 1, 1), dropShadowEffect: false));

            using var bmp = canvas.GraphicsList.DrawGraphicsToBitmap(null);
            Assert.Equal(new PixelSize(Size, Size), bmp.PixelSize);

            var buf = new byte[Size * Size * 4];
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                bmp.CopyPixels(new PixelRect(0, 0, Size, Size), handle.AddrOfPinnedObject(), buf.Length, Size * 4);
            }
            finally
            {
                handle.Free();
            }

            return buf;
        }

        // (first color byte, alpha) — the shadow is black, so BGRA vs RGBA order does not matter
        private static (byte Color, byte Alpha) Pixel(byte[] buf, int x, int y)
        {
            var i = (y * Size + x) * 4;
            return (buf[i], buf[i + 3]);
        }

        [AvaloniaFact]
        public void Shadow_IsSoftBlack_BelowAndRightOfTheInk()
        {
            var on = Export(shadow: true);
            var off = Export(shadow: false);

            // just outside the frame's right and bottom edges (the stroke is inset, so it ends at
            // 140): shadow there, nothing without it
            foreach (var (x, y) in new[] { (141, 90), (90, 141) })
            {
                var p = Pixel(on, x, y);
                Assert.InRange(p.Alpha, 10, 0x80); // never above the shadow's own opacity
                Assert.Equal(0, p.Color);           // black, not the gray ink
                Assert.Equal(0, Pixel(off, x, y).Alpha);
            }

            // soft: it fades with distance from the edge, and is gone well past the blur reach
            Assert.True(Pixel(on, 141, 90).Alpha > Pixel(on, 144, 90).Alpha);
            // and as wide as the original look's blur (sigma ≈ 3.4, about 22 here); a radius mapped
            // to sigma ≈ 1.9 falls to about 10 by this point
            Assert.InRange(Pixel(on, 144, 90).Alpha, 16, 30);
            Assert.Equal(0, Pixel(on, 160, 90).Alpha);

            // offset down-right: the top-left side gets less shadow than the bottom-right one
            Assert.True(Pixel(on, 38, 90).Alpha < Pixel(on, 141, 90).Alpha);
        }
    }
}
