using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// The arrow's third (mid) handle bows the shaft into a quadratic bezier. Pins the parts that
    /// are easy to break silently: the handle rides ON the ink at t=0.5 (not on the control point),
    /// the bow is stored relative to the chord so translation and endpoint drags preserve it,
    /// hit-testing and bounds follow the curve, the head is aimed along the curve's end tangent
    /// rather than the chord, and the drawn shaft still stops short of the head.
    /// </summary>
    public class GraphicArrowCurveTests
    {
        private static readonly DpiScale Dpi = new DpiScale(1, 1);
        private const int MidHandle = 3;

        private static GraphicArrow Horizontal(double lineWidth = 4) =>
            new GraphicArrow(Colors.Black, lineWidth, new Point(0, 0), new Point(100, 0));

        private static void AssertPointClose(Point expected, Point actual, double tol = 1e-6)
        {
            Assert.True(System.Math.Abs(expected.X - actual.X) < tol && System.Math.Abs(expected.Y - actual.Y) < tol,
                        $"expected {expected} actual {actual}");
        }

        [AvaloniaFact]
        public void NewArrow_IsStraight_AndExposesAMidHandleOnTheChord()
        {
            var g = Horizontal();
            Assert.Equal(3, g.HandleCount);
            Assert.Equal(0, g.CurveOffset);
            AssertPointClose(new Point(50, 0), g.GetHandle(MidHandle, Dpi));
        }

        [AvaloniaFact]
        public void MidHandleDrag_PutsTheCurveMidpointUnderThePointer()
        {
            // the control point is twice as far out as the drag, so the ON-CURVE t=0.5 point — which
            // is what GetHandle(3) reports — lands exactly where the pointer is
            var g = Horizontal();
            g.MoveHandleTo(new Point(50, 30), MidHandle);

            Assert.Equal(30, g.CurveOffset, 9);
            AssertPointClose(new Point(50, 30), g.GetHandle(MidHandle, Dpi));
        }

        [AvaloniaFact]
        public void MidHandleDrag_ProjectsOntoTheChordNormal()
        {
            // only the perpendicular component is curvature; sliding along the chord does nothing
            var g = Horizontal();
            g.MoveHandleTo(new Point(80, 25), MidHandle);

            Assert.Equal(25, g.CurveOffset, 9);
            AssertPointClose(new Point(50, 25), g.GetHandle(MidHandle, Dpi));
        }

        [AvaloniaFact]
        public void MidHandleDraggedBackToTheChord_SnapsToExactlyStraight()
        {
            var g = Horizontal();
            g.MoveHandleTo(new Point(50, 40), MidHandle);
            g.MoveHandleTo(new Point(50, 0.4), MidHandle);

            Assert.Equal(0, g.CurveOffset); // exact — the straight fast path must be reachable by hand
        }

        [AvaloniaFact]
        public void Bow_MovesTheHitCorridorAndTheBoundsOntoTheCurve()
        {
            var straight = Horizontal();
            Assert.False(straight.Contains(new Point(50, 30)));
            Assert.True(straight.Bounds.Height < 20);

            var curved = Horizontal();
            curved.CurveOffset = 30;

            Assert.True(curved.Contains(new Point(50, 30)));  // the corridor follows the bow
            Assert.False(curved.Contains(new Point(50, 0)));  // ...and no longer covers the chord
            Assert.True(curved.Bounds.Bottom > 29, $"bounds {curved.Bounds} should reach the bow");
        }

        [AvaloniaFact]
        public void Translation_PreservesTheBow()
        {
            var g = Horizontal();
            g.CurveOffset = 30;
            var before = g.GetHandle(MidHandle, Dpi);

            g.Move(10, 20);

            Assert.Equal(30, g.CurveOffset);
            AssertPointClose(new Point(before.X + 10, before.Y + 20), g.GetHandle(MidHandle, Dpi));
        }

        [AvaloniaFact]
        public void EndpointDrag_RebowsAroundTheNewChord()
        {
            var g = Horizontal();
            g.CurveOffset = 30;

            g.MoveHandleTo(new Point(0, 100), 2); // chord now points down; its normal points left

            Assert.Equal(30, g.CurveOffset);
            AssertPointClose(new Point(-30, 50), g.GetHandle(MidHandle, Dpi));
        }

        [AvaloniaFact]
        public void CurvedHead_IsAimedAlongTheEndTangent_AndTheShaftStopsShortOfIt()
        {
            var g = Horizontal(6);
            g.CurveOffset = 60;
            _ = g.Bounds; // fills the cached geometry slots

            // the head trails BACK along the tangent at t=1, which for this bow leaves the end
            // heading up and to the right — so the barbs hang well below a chord-aligned head
            // (which could only reach sin(15°) * tipLength ≈ 12 units off the chord)
            var tip = g.RenderCache.SecondaryGeometry;
            Assert.NotNull(tip);
            Assert.True(tip.Bounds.Bottom > 30, $"tip bounds {tip.Bounds} are not rotated onto the curve tangent");

            // the drawn shaft is the sub-curve that stops half a head-length short, so nothing pokes
            // through the head (the full-curve geometry used for hit-testing still reaches the end)
            var shaft = g.RenderCache.TertiaryGeometry;
            Assert.NotNull(shaft);
            var pen = new Pen(Brushes.Black, 6);
            Assert.False(shaft.StrokeContains(pen, g.LineEnd));
            Assert.True(g.RenderCache.Geometry.StrokeContains(pen, g.LineEnd));
        }
    }
}
