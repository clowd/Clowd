using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// The numeric step badge is ONE graphic: the badge plus an optional arrow whose base is the
    /// badge center. Pins the parts of that contract that are easy to break silently: the badge is
    /// placed and re-measured around its center, the arrow handle is the tip (pulling it back inside
    /// the badge removes the arrow), the badge handle sits on the rim facing away from the tip and
    /// drags the badge with the tip anchored, the arrow follows a move for free, bounds and
    /// hit-testing cover the arrow, and badges never rotate.
    /// </summary>
    public class GraphicCountTests
    {
        private static readonly DpiScale Dpi = new DpiScale(1, 1);

        private static GraphicCount Make(string body = "1") =>
            new GraphicCount(Colors.Red, 3, new Point(100, 100), body) { FontSize = 24 };

        private static void AssertPointClose(Point expected, Point actual, double tol = 1e-6)
        {
            Assert.True(System.Math.Abs(expected.X - actual.X) < tol && System.Math.Abs(expected.Y - actual.Y) < tol,
                        $"expected {expected} actual {actual}");
        }

        [AvaloniaFact]
        public void Badge_IsCenteredOnItsPoint_AndRegrowsAroundTheCenter()
        {
            var g = Make();
            AssertPointClose(new Point(100, 100), g.Center);
            Assert.True(g.UnrotatedBounds.Width >= g.UnrotatedBounds.Height - 1e-9, "a single digit is at least a circle");

            var narrow = g.UnrotatedBounds.Width;
            g.Body = "1000";
            Assert.True(g.UnrotatedBounds.Width > narrow);
            AssertPointClose(new Point(100, 100), g.Center);
        }

        [AvaloniaFact]
        public void BadgeSize_ComesFromFontSizeAndStroke_AroundItsCenter()
        {
            var g = Make(); // 24pt, stroke 3 -> 4.5 ring
            Assert.Equal(24 * 1.9 + 4.5 * 2, g.BadgeRect.Height, 6);

            g.LineWidth = 8; // 12 ring
            Assert.Equal(24 * 1.9 + 12 * 2, g.BadgeRect.Height, 6);
            g.FontSize = 48;
            Assert.Equal(48 * 1.9 + 12 * 2, g.BadgeRect.Height, 6);
            AssertPointClose(new Point(100, 100), g.Center);
        }

        [AvaloniaFact]
        public void NewBadge_HasNoArrow_AndOffersItsHandlesOnOppositeSidesOfTheRim()
        {
            var g = Make();
            Assert.False(g.HasArrow);
            Assert.Equal(2, g.HandleCount);
            AssertPointClose(new Point(g.Right, 100), g.GetHandle(GraphicCount.ArrowHandle, Dpi), 1e-6);
            AssertPointClose(new Point(g.Left, 100), g.GetHandle(GraphicCount.BadgeHandle, Dpi), 1e-6);
        }

        [AvaloniaFact]
        public void BadgeHandle_SitsOnTheRimFacingAwayFromTheTip()
        {
            var g = Make();
            g.MoveHandleTo(new Point(100, 300), GraphicCount.ArrowHandle); // straight down
            var radius = (g.Bottom - g.Top) / 2;
            AssertPointClose(new Point(100, 100 - radius), g.GetHandle(GraphicCount.BadgeHandle, Dpi), 1e-6);
        }

        [AvaloniaFact]
        public void DraggingTheBadgeHandle_MovesTheBadge_AndKeepsTheTipAnchored()
        {
            var g = Make();
            g.MoveHandleTo(new Point(300, 100), GraphicCount.ArrowHandle); // tip to the right
            var radius = (g.Bottom - g.Top) / 2;

            // drag the far-side rim point down-left; the badge re-aims at the fixed tip
            g.MoveHandleTo(new Point(100, 250), GraphicCount.BadgeHandle);

            AssertPointClose(new Point(300, 100), g.ArrowTip, 1e-6);
            AssertPointClose(new Point(100, 250), g.GetHandle(GraphicCount.BadgeHandle, Dpi), 1e-6);
            Assert.Equal(radius, GraphicLine.Distance(new Point(100, 250), g.Center), 4);
        }

        [AvaloniaFact]
        public void DraggingTheHandle_PointsTheArrow_AndDroppingItInsideRemovesIt()
        {
            var g = Make();
            g.MoveHandleTo(new Point(200, 160), GraphicCount.ArrowHandle);
            Assert.True(g.HasArrow);
            Assert.Equal(new Point(100, 60), g.ArrowOffset);
            AssertPointClose(new Point(200, 160), g.GetHandle(GraphicCount.ArrowHandle, Dpi));

            g.MoveHandleTo(new Point(102, 101), GraphicCount.ArrowHandle);
            Assert.False(g.HasArrow);
        }

        [AvaloniaFact]
        public void Move_CarriesTheArrow()
        {
            var g = Make();
            g.MoveHandleTo(new Point(200, 160), GraphicCount.ArrowHandle);
            g.Move(10, -20);

            Assert.Equal(new Point(100, 60), g.ArrowOffset);
            AssertPointClose(new Point(210, 140), g.GetHandle(GraphicCount.ArrowHandle, Dpi));
        }

        [AvaloniaFact]
        public void BoundsAndHitTesting_CoverTheArrow()
        {
            var g = Make();
            Assert.False(g.Contains(new Point(150, 130)));

            g.MoveHandleTo(new Point(200, 160), GraphicCount.ArrowHandle);
            Assert.True(g.Bounds.Contains(new Point(199, 159)), g.Bounds.ToString());
            Assert.True(g.Contains(new Point(150, 130))); // on the shaft, halfway out
            Assert.False(g.Contains(new Point(150, 170)));
        }

        [AvaloniaFact]
        public void Badge_NeverRotates()
        {
            var g = Make();
            g.Angle = 30; // e.g. loaded from a document saved when badges could rotate
            g.Normalize();
            Assert.Equal(0, g.Angle);

            var skills = typeof(GraphicCount).GetCustomAttributes(typeof(GraphicDescAttribute), false);
            var desc = Assert.IsType<GraphicDescAttribute>(Assert.Single(skills));
            Assert.False(desc.Skills.HasFlag(Skill.Angle));
        }
    }
}
