using System;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// The measure line's whole output — end ticks and the length/angle label — is derived from the
    /// two endpoints at render time, so these pin the derivation rather than any persisted state:
    /// the label string, the bounds that must enclose the derived ink (they drive invalidation and
    /// the export size), and the Move() fast path's right to keep the label cache.
    /// </summary>
    public class GraphicMeasureTests
    {
        // Bounds fills the same RenderCache.Text/TextKey slots DrawObject reads, and the key IS the
        // shaped string, so the cached key is the label that renders — no render pass needed.
        private static string LabelOf(GraphicMeasure g)
        {
            _ = g.Bounds;
            return g.RenderCache.TextKey is ValueTuple<string, double> key ? key.Item1 : null;
        }

        private static GraphicMeasure Make(double x0, double y0, double x1, double y1, double lineWidth = 2) =>
            new GraphicMeasure(Colors.Red, lineWidth, new Point(x0, y0), new Point(x1, y1));

        [AvaloniaTheory]
        [InlineData(0, 0, 100, 0, "100px 0°")]
        [InlineData(100, 0, 0, 0, "100px 180°")] // a right-to-left drag reads 180°, never -180°
        [InlineData(0, 100, 0, 0, "100px 90°")] // screen Y grows downward: up-the-screen is positive
        [InlineData(0, 0, 0, 100, "100px -90°")]
        [InlineData(0, 0, 3, -4, "5px 53°")]
        [InlineData(5, 5, 5, 5, "0px 0°")] // degenerate line: never "-0°", never NaN
        public void Label_ReadsLengthInCanvasPixels_AndAngleFromHorizontal(
            double x0, double y0, double x1, double y1, string expected)
        {
            Assert.Equal(expected, LabelOf(Make(x0, y0, x1, y1)));
        }

        [AvaloniaFact]
        public void Label_IsInvalidatedByAnEndpointMove_ButSurvivesATranslation()
        {
            var g = Make(0, 0, 100, 0);
            Assert.Equal("100px 0°", LabelOf(g));

            g.MoveHandleTo(new Point(200, 0), 2);
            Assert.Equal("200px 0°", LabelOf(g));

            // a pure translation changes neither length nor angle, so the Move() fast path's
            // Geometry-only clear must leave the shaped label alone
            var text = g.RenderCache.Text;
            g.Move(37, -12);
            Assert.Same(text, g.RenderCache.Text);
            Assert.Equal("200px 0°", LabelOf(g));
        }

        [AvaloniaFact]
        public void Bounds_EncloseTheTicksAndTheLabelPill()
        {
            var g = Make(0, 0, 100, 0);
            var bounds = g.Bounds;

            // ticks: 8px total (4 x LineWidth clamped up to the 8px floor) centered on each endpoint,
            // plus the round cap's half stroke past the tick end
            Assert.Equal(5, bounds.Bottom, 6);
            // the shaft's own render bounds only reach ±1 (half the 2px stroke)
            Assert.Equal(-1, bounds.Left, 6);
            Assert.Equal(101, bounds.Right, 6);
            // the pill sits above the line and is materially taller than the ticks
            Assert.True(bounds.Top < -12, bounds.ToString());
        }

        [AvaloniaTheory]
        [InlineData(1, 4)] // 4 x LineWidth clamps up to the 8px floor -> ±4
        [InlineData(3, 6)] // in range -> 12px total, ±6
        [InlineData(8, 8)] // clamps down to the 16px ceiling -> ±8
        public void TickLength_TracksStrokeWidth_WithinItsClamp(double lineWidth, double expectedHalfTick)
        {
            // the bottom edge is the tick reach plus the round cap's half stroke past the tick end
            var bounds = Make(0, 0, 100, 0, lineWidth).Bounds;
            Assert.Equal(expectedHalfTick + lineWidth / 2, bounds.Bottom, 6);
        }

        [AvaloniaFact]
        public void Contains_UsesTheInheritedLineCorridor()
        {
            var g = Make(0, 0, 100, 0);
            Assert.True(g.Contains(new Point(50, 2)));
            Assert.False(g.Contains(new Point(50, 40)));
        }
        [AvaloniaFact]
        public void Scale_GrowsTheLabelPill_AndClamps()
        {
            var g = Make(0, 0, 100, 0);
            var top = g.Bounds.Top;

            g.Scale = 2;
            Assert.True(g.Bounds.Top < top * 1.8, $"{g.Bounds.Top} vs {top}");
            Assert.Equal("100px 0°", LabelOf(g));

            g.Scale = 100;
            Assert.Equal(GraphicMeasure.MaxScale, g.Scale);
        }

        [AvaloniaFact]
        public void Scale_RoundTrips()
        {
            var g = new GraphicMeasure(Colors.Red, 2, new Point(0, 0), new Point(100, 0), 1.5);
            var bytes = GraphicsSerializer.SerializeToUtf8Bytes(new GraphicBase[] { g });
            var r = Assert.IsType<GraphicMeasure>(Assert.Single(GraphicsSerializer.DeserializeFromUtf8Bytes(bytes)));
            Assert.Equal(1.5, r.Scale);
            Assert.Equal(g.Bounds, r.Bounds);
        }
        [AvaloniaTheory]
        [InlineData(200, 4, "ft", "4ft 0°")]
        [InlineData(100, 3, "cm", "3cm 0°")]
        [InlineData(100, 30, "cm", "30cm 0°")]
        public void Label_ReadsInTheCanvasUnits(double pixels, double length, string unit, string expected)
        {
            var canvas = new DrawingCanvas();
            var g = Make(0, 0, pixels, 0);
            canvas.GraphicsList.Add(g);
            canvas.SetMeasureUnits(g, length, unit);
            Assert.Equal(expected, LabelOf(g));
        }

        [AvaloniaFact]
        public void Label_KeepsTwoDecimals_InUnits()
        {
            var canvas = new DrawingCanvas();
            var legend = Make(0, 0, 300, 0);
            var other = Make(0, 0, 100, 0);
            canvas.GraphicsList.Add(legend);
            canvas.GraphicsList.Add(other);
            canvas.SetMeasureUnits(legend, 1, "m");
            Assert.Equal("0.33m 0°", LabelOf(other));
        }

        [AvaloniaFact]
        public void SetMeasureUnits_RelabelsEveryMeasure_AndNewOnes_AndUndoes()
        {
            var canvas = new DrawingCanvas();
            var legend = Make(0, 0, 200, 0);
            var other = Make(0, 50, 0, 150);
            canvas.GraphicsList.Add(legend);
            var tiny = Make(0, 0, 10, 0); // its label pill, not the line, sets its width
            canvas.GraphicsList.Add(other);
            canvas.GraphicsList.Add(tiny);
            canvas.AddCommandToHistory(false);
            var pixelBounds = tiny.Bounds;

            canvas.SetMeasureUnits(legend, 4, " ft ");
            Assert.Equal(new MeasureUnits("ft", 50), canvas.MeasureUnits.Current);
            Assert.Equal("4ft 0°", LabelOf(legend));
            Assert.Equal("2ft -90°", LabelOf(other));
            Assert.Equal("0.2ft 0°", LabelOf(tiny));
            Assert.NotEqual(pixelBounds.Width, tiny.Bounds.Width); // the new label re-laid-out the pill

            // a measure that joins the canvas later reads in the same units
            var later = Make(0, 0, 25, 0);
            canvas.GraphicsList.Add(later);
            Assert.Equal("0.5ft 0°", LabelOf(later));
            canvas.AddCommandToHistory(false);

            canvas.Undo(); // the add
            canvas.Undo(); // the calibration
            Assert.Null(canvas.MeasureUnits.Current);
            Assert.Equal("200px 0°", LabelOf(legend));
            Assert.Equal("100px -90°", LabelOf(other));

            canvas.Redo();
            Assert.Equal("2ft -90°", LabelOf(other));

            canvas.ResetMeasureUnits();
            Assert.Null(canvas.MeasureUnits.Current);
            Assert.Equal("100px -90°", LabelOf(other));
            canvas.Undo();
            Assert.Equal("2ft -90°", LabelOf(other));
        }

        [AvaloniaFact]
        public void MeasureUnits_AreSavedWithTheDocument_NotOnTheGraphic()
        {
            var canvas = new DrawingCanvas();
            var g = Make(0, 0, 200, 0);
            canvas.GraphicsList.Add(g);
            canvas.SetMeasureUnits(g, 4, "ft");

            var doc = UndoManager.SerializeDocument(canvas);
            Assert.Equal("ft", (string)doc["MeasureUnits"]["Unit"]);
            Assert.Equal(50, (double)doc["MeasureUnits"]["PixelsPerUnit"]);
            Assert.DoesNotContain("ft", doc["Graphics"].ToJsonString());

            var reopened = new DrawingCanvas();
            reopened.RestoreState(doc);
            Assert.Equal(new MeasureUnits("ft", 50), reopened.MeasureUnits.Current);
            Assert.Equal("4ft 0°", LabelOf(Assert.IsType<GraphicMeasure>(Assert.Single(reopened.GraphicsList))));
            Assert.False(reopened.CommandUndo.CanExecute(null)); // loaded state is the baseline

            // a document from before measure units existed reads in pixels
            doc.Remove("MeasureUnits");
            var old = new DrawingCanvas();
            old.RestoreState(doc);
            Assert.Null(old.MeasureUnits.Current);
        }

        [AvaloniaFact]
        public void MeasureUnits_RejectInvalidCalibration()
        {
            var canvas = new DrawingCanvas();
            var g = Make(0, 0, 200, 0);
            canvas.GraphicsList.Add(g);
            Assert.Throws<System.ArgumentException>(() => canvas.SetMeasureUnits(g, 0, "ft"));
            Assert.Throws<System.ArgumentException>(() => canvas.SetMeasureUnits(g, 4, "  "));
            Assert.Null(MeasureUnits.Normalize(new MeasureUnits("ft", double.NaN)));
        }

    }
}
