using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// Text exported onto a transparent background (Copy, Save) must use grayscale antialiasing:
    /// LCD subpixel coverage composited over nothing renders every glyph edge solid black.
    /// </summary>
    public class ExportTextTests
    {
        static ExportTextTests()
        {
            // DrawingCanvas reads tool settings from SettingsRoot.Current, which the app assigns
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
        }

        [AvaloniaFact]
        public void UnfilledText_OnTransparent_HasNoBlackEdges()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            canvas.GraphicsList.Add(new GraphicText(Colors.Transparent, 2, new Point(0, 0)) { Foreground = Colors.Red });

            using var bmp = canvas.GraphicsList.DrawGraphicsToBitmap(null);
            var size = bmp.PixelSize;
            var buf = new byte[size.Width * size.Height * 4];
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                bmp.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), buf.Length, size.Width * 4);
            }
            finally
            {
                handle.Free();
            }

            // premultiplied red ink: every covered pixel's red channel equals its alpha, and green
            // and blue stay 0. A black fringe shows up as alpha without matching red. Red sits at
            // byte 0 or 2 depending on BGRA/RGBA, so take the larger of the two.
            int inked = 0;
            for (int i = 0; i < buf.Length; i += 4)
            {
                var a = buf[i + 3];
                if (a == 0)
                    continue;
                inked++;
                var red = System.Math.Max(buf[i], buf[i + 2]);
                Assert.True(red >= a - 2, $"pixel {i / 4}: alpha {a} but red {red} — a dark fringe");
            }

            Assert.True(inked > 100, "nothing was drawn");
        }
    }
}
