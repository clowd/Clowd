using System;
using System.IO;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Prisc.Drawing.Graphics;
using Prisc.Drawing.Tools;
using Xunit;

namespace Prisc.Drawing.Tests
{
    /// <summary>
    /// Pins the pointer tool's Shift (aspect-locked) corner resize. The Avalonia port built the
    /// resized rect "backwards" from the anchored corner with Rect(Point, Point), which — unlike
    /// WPF's — does not normalize, so handles 3/5/7 stored the shape's edges inverted mid-drag:
    /// handles drawn at the wrong corners, a jump when Shift was released mid-drag, an image left
    /// mirrored by Normalize on release, and a crop that escaped its clamp into a negative-size
    /// PixelRect. The anchor is the corner opposite the grabbed handle for the whole gesture, so
    /// dragging across it flips the box through it, as a free resize does.
    /// </summary>
    public class ShiftResizeTests
    {
        static ShiftResizeTests()
        {
            Prisc.Config.SettingsRoot.Current ??= new Prisc.Config.SettingsRoot();
        }

        // the pointer tool only acts while the canvas holds a capture, so its gestures need a
        // pointer that can actually be captured
        private static readonly IPointer Mouse = new Pointer(1, PointerType.Mouse, true);

        private static PointerState Press(Point p, KeyModifiers mods = KeyModifiers.None, bool left = true) =>
            new PointerState(p, mods, left, false, false, Mouse, 0, null);

        private static DrawingCanvas PointerCanvas(GraphicBase graphic)
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            canvas.GraphicsList.Add(graphic);
            canvas.AddCommandToHistory(false); // baseline
            canvas.Tool = ToolType.Pointer;
            graphic.IsSelected = true; // handles only hit-test on a selected graphic
            return canvas;
        }

        private static void WritePng(string path, int width, int height)
        {
            using var wb = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                                               PixelFormats.Bgra8888, AlphaFormat.Premul);
            wb.Save(path, PngBitmapEncoderOptions.Default);
        }

        // a 200x100 (2:1) rectangle at (100,100); each corner dragged outward to a 300x160 box,
        // which the aspect lock grows to 320x160 (it covers the pointer) around the opposite corner
        [AvaloniaTheory]
        [InlineData(1, 100, 100, -1, -1)]
        [InlineData(3, 300, 100, 1, -1)]
        [InlineData(5, 300, 200, 1, 1)]
        [InlineData(7, 100, 200, -1, 1)]
        public void ShiftDrag_EveryCorner_KeepsEdgesOrderedAndAspectAroundTheAnchor(
            int handle, double hx, double hy, int sx, int sy)
        {
            var rect = new GraphicRectangle(Colors.Red, 2, new Rect(100, 100, 200, 100));
            var canvas = PointerCanvas(rect);
            var anchor = new Point(sx < 0 ? 300 : 100, sy < 0 ? 200 : 100);

            var grab = new Point(hx, hy);
            Assert.Same(rect, canvas.ToolPointer.MakeHitTest(canvas, grab, out var hit));
            Assert.Equal(handle, hit);

            var to = new Point(hx + sx * 100, hy + sy * 60);
            canvas.ToolPointer.OnMouseDown(canvas, Press(grab, KeyModifiers.Shift), 1);
            canvas.ToolPointer.OnMouseMove(canvas, Press(to, KeyModifiers.Shift));

            // mid-drag, before Normalize gets a chance to hide an inversion
            Assert.True(rect.Left < rect.Right, $"Left {rect.Left} >= Right {rect.Right}");
            Assert.True(rect.Top < rect.Bottom, $"Top {rect.Top} >= Bottom {rect.Bottom}");
            Assert.Equal(320, rect.Right - rect.Left, 6);
            Assert.Equal(160, rect.Bottom - rect.Top, 6);
            Assert.Equal(anchor, new Point(sx < 0 ? rect.Right : rect.Left, sy < 0 ? rect.Bottom : rect.Top));

            // the grabbed handle is drawn (and hit-tested) at the dragged corner, not at the anchor
            var handleAt = rect.GetHandle(handle, new DpiScale(1, 1));
            Assert.Equal(new Point(anchor.X + sx * 320, anchor.Y + sy * 160), handleAt);

            canvas.ToolPointer.OnMouseUp(canvas, Press(to, KeyModifiers.Shift, left: false));
            var expected = new Rect(Math.Min(anchor.X, anchor.X + sx * 320), Math.Min(anchor.Y, anchor.Y + sy * 160), 320, 160);
            Assert.Equal(expected, rect.UnrotatedBounds);
        }

        [AvaloniaFact]
        public void ReleasingShiftMidDrag_ContinuesFromTheAnchor_WithoutAJump()
        {
            var rect = new GraphicRectangle(Colors.Red, 2, new Rect(100, 100, 200, 100));
            var canvas = PointerCanvas(rect);

            canvas.ToolPointer.OnMouseDown(canvas, Press(new Point(300, 200), KeyModifiers.Shift), 1);
            canvas.ToolPointer.OnMouseMove(canvas, Press(new Point(400, 260), KeyModifiers.Shift));
            canvas.ToolPointer.OnMouseMove(canvas, Press(new Point(350, 300)));

            // the free resize moves only the grabbed corner; the top-left anchor stays put
            Assert.Equal(100, rect.Left);
            Assert.Equal(100, rect.Top);
            Assert.Equal(350, rect.Right);
            Assert.Equal(300, rect.Bottom);
        }

        [AvaloniaFact]
        public void ShiftDrag_AcrossTheAnchor_FlipsThroughIt_AndTheAnchorStaysPut()
        {
            var rect = new GraphicRectangle(Colors.Red, 2, new Rect(100, 100, 200, 100));
            var canvas = PointerCanvas(rect);

            // bottom-right handle dragged up-left past the top-left corner: the box grows from
            // (100,100) toward the pointer (60x40 → 80x40 at 2:1), the grabbed handle under it
            var to = new Point(40, 60);
            canvas.ToolPointer.OnMouseDown(canvas, Press(new Point(300, 200), KeyModifiers.Shift), 1);
            canvas.ToolPointer.OnMouseMove(canvas, Press(new Point(200, 150), KeyModifiers.Shift));
            canvas.ToolPointer.OnMouseMove(canvas, Press(to, KeyModifiers.Shift));

            Assert.Equal(new Point(100, 100), new Point(rect.Left, rect.Top)); // the anchor
            Assert.Equal(new Point(20, 60), rect.GetHandle(5, new DpiScale(1, 1)));
            Assert.Equal(new Rect(20, 60, 80, 40), rect.UnrotatedBounds);

            canvas.ToolPointer.OnMouseUp(canvas, Press(to, KeyModifiers.Shift, left: false));

            // the old top-left is now the bottom-right; the grabbed corner became the top-left
            Assert.Equal(new Rect(20, 60, 80, 40), rect.UnrotatedBounds);
            Assert.Equal(new Point(100, 100), new Point(rect.Right, rect.Bottom));
        }

        [AvaloniaFact]
        public void ShiftDrag_AcrossTheAnchorAndBack_KeepsTheSameAnchor()
        {
            var rect = new GraphicRectangle(Colors.Red, 2, new Rect(100, 100, 200, 100));
            var canvas = PointerCanvas(rect);

            canvas.ToolPointer.OnMouseDown(canvas, Press(new Point(300, 200), KeyModifiers.Shift), 1);
            canvas.ToolPointer.OnMouseMove(canvas, Press(new Point(40, 60), KeyModifiers.Shift));
            canvas.ToolPointer.OnMouseMove(canvas, Press(new Point(150, 250), KeyModifiers.Shift)); // one axis back
            Assert.Equal(new Rect(100, 100, 300, 150), rect.UnrotatedBounds);

            canvas.ToolPointer.OnMouseMove(canvas, Press(new Point(400, 300), KeyModifiers.Shift));
            canvas.ToolPointer.OnMouseUp(canvas, Press(new Point(400, 300), KeyModifiers.Shift, left: false));
            Assert.Equal(new Rect(100, 100, 400, 200), rect.UnrotatedBounds);
        }

        [AvaloniaTheory]
        [InlineData(1, 100, 100, 360, 240, 300, 200, 80, 40)] // top-left past bottom-right
        [InlineData(3, 300, 100, 40, 240, 20, 200, 80, 40)]   // top-right past bottom-left
        [InlineData(7, 100, 200, 360, 60, 300, 60, 80, 40)]   // bottom-left past top-right
        public void ShiftDrag_EveryCornerAcrossItsAnchor_FlipsThroughIt(
            int handle, double hx, double hy, double toX, double toY, double x, double y, double w, double h)
        {
            var rect = new GraphicRectangle(Colors.Red, 2, new Rect(100, 100, 200, 100));
            var canvas = PointerCanvas(rect);

            canvas.ToolPointer.OnMouseDown(canvas, Press(new Point(hx, hy), KeyModifiers.Shift), 1);
            canvas.ToolPointer.OnMouseMove(canvas, Press(new Point(toX, toY), KeyModifiers.Shift));
            canvas.ToolPointer.OnMouseUp(canvas, Press(new Point(toX, toY), KeyModifiers.Shift, left: false));

            Assert.Equal(new Rect(x, y, w, h), rect.UnrotatedBounds);
        }

        [AvaloniaFact]
        public void ShiftDrag_RotatedRectangle_KeepsTheAnchorCornerInPlace()
        {
            // rotated after construction: the angle-taking constructor re-centres the rect onto
            // fractional edges, which UnrotatedBounds then rounds
            var rect = new GraphicRectangle(Colors.Red, 2, new Rect(100, 100, 200, 100)) { Angle = 30 };
            var canvas = PointerCanvas(rect);
            var (l, t) = (rect.Left, rect.Top);
            var anchorBefore = rect.ApplyRotation(new Point(l, t));

            var grab = rect.ApplyRotation(new Point(l + 200, t + 100)); // handle 5, on screen
            Assert.Same(rect, canvas.ToolPointer.MakeHitTest(canvas, grab, out var hit));
            Assert.Equal(5, hit);

            var to = rect.ApplyRotation(new Point(l + 300, t + 160));
            canvas.ToolPointer.OnMouseDown(canvas, Press(grab, KeyModifiers.Shift), 1);
            canvas.ToolPointer.OnMouseMove(canvas, Press(to, KeyModifiers.Shift));
            canvas.ToolPointer.OnMouseUp(canvas, Press(to, KeyModifiers.Shift, left: false));

            Assert.Equal(320, rect.Right - rect.Left, 6);
            Assert.Equal(160, rect.Bottom - rect.Top, 6);
            var anchorAfter = rect.ApplyRotation(new Point(rect.Left, rect.Top));
            Assert.Equal(anchorBefore.X, anchorAfter.X, 6);
            Assert.Equal(anchorBefore.Y, anchorAfter.Y, 6);
        }

        [AvaloniaFact]
        public void ShiftDrag_Image_DoesNotMirrorIt()
        {
            var dir = Directory.CreateTempSubdirectory();
            try
            {
                var path = Path.Combine(dir.FullName, "img.png");
                WritePng(path, 80, 60);
                var img = new GraphicImage(path, new Rect(0, 0, 80, 60), default);
                var canvas = PointerCanvas(img);

                canvas.ToolPointer.OnMouseDown(canvas, Press(new Point(80, 60), KeyModifiers.Shift), 1);
                canvas.ToolPointer.OnMouseMove(canvas, Press(new Point(160, 120), KeyModifiers.Shift));
                canvas.ToolPointer.OnMouseUp(canvas, Press(new Point(160, 120), KeyModifiers.Shift, left: false));

                Assert.Equal(1, img.FlipX);
                Assert.Equal(1, img.FlipY);
                Assert.Equal(new Rect(0, 0, 160, 120), img.UnrotatedBounds);
            }
            finally
            {
                dir.Delete(true);
            }
        }

        [AvaloniaTheory]
        [InlineData(40, 30, 0, 0, 40, 30)]   // inward: a real crop
        [InlineData(120, 90, 0, 0, 80, 60)]  // outward: clamped to the image
        public void ShiftDrag_InCropMode_StaysClampedAndCommitsAPositiveCrop(
            double toX, double toY, int cx, int cy, int cw, int ch)
        {
            var dir = Directory.CreateTempSubdirectory();
            try
            {
                var path = Path.Combine(dir.FullName, "img.png");
                WritePng(path, 80, 60);
                var img = new GraphicImage(path, new Rect(0, 0, 80, 60), default);
                var canvas = PointerCanvas(img);
                img.Activate(canvas);
                Assert.True(img.Editing);

                var grab = new Point(78, 58); // inside the bottom-right crop bracket
                Assert.Same(img, canvas.ToolPointer.MakeHitTest(canvas, grab, out var hit));
                Assert.Equal(5, hit);

                var to = new Point(toX, toY);
                canvas.ToolPointer.OnMouseDown(canvas, Press(grab, KeyModifiers.Shift), 1);
                canvas.ToolPointer.OnMouseMove(canvas, Press(to, KeyModifiers.Shift));
                canvas.ToolPointer.OnMouseUp(canvas, Press(to, KeyModifiers.Shift, left: false));

                Assert.True(img.Left < img.Right);
                Assert.True(img.Top < img.Bottom);

                img.EndCrop();
                Assert.Equal(new PixelRect(cx, cy, cw, ch), img.Crop);
            }
            finally
            {
                dir.Delete(true);
            }
        }
    }
}
