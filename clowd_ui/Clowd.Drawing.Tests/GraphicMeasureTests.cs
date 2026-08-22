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
            return g.RenderCache.TextKey as string;
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
            // Geometry-only clear must leave the shaped label (and the shadow) alone
            var text = g.RenderCache.Text;
            var shadowRev = g.ShadowRev;
            g.Move(37, -12);
            Assert.Same(text, g.RenderCache.Text);
            Assert.Equal(shadowRev, g.ShadowRev);
            Assert.Equal("200px 0°", LabelOf(g));
        }

        [AvaloniaFact]
        public void Bounds_EncloseTheTicksAndTheLabelPill()
        {
            var g = Make(0, 0, 100, 0);
            var bounds = g.Bounds;

            // ticks: 8px total (4 x LineWidth clamped up to the 8px floor) centered on each endpoint
            Assert.Equal(4, bounds.Bottom, 6);
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
            // a horizontal line's flat caps add nothing vertically, so the bottom edge IS the tick reach
            var bounds = Make(0, 0, 100, 0, lineWidth).Bounds;
            Assert.Equal(expectedHalfTick, bounds.Bottom, 6);
        }

        [AvaloniaFact]
        public void Contains_UsesTheInheritedLineCorridor()
        {
            var g = Make(0, 0, 100, 0);
            Assert.True(g.Contains(new Point(50, 2)));
            Assert.False(g.Contains(new Point(50, 40)));
        }
    }
}
