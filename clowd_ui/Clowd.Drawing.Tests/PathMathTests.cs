using System;
using Avalonia;
using Clowd.Drawing.Curves;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// The pen path's vector helpers are pure (no Avalonia platform), so they are pinned here
    /// platform-free: 45° snapping, the smooth-anchor collinear rule, Inkscape-style auto handles,
    /// path reversal, the de Casteljau split and the "absent handle" threshold.
    /// </summary>
    public class PathMathTests
    {
        private static void AssertPointClose(Point expected, Point actual, double tol = 1e-9)
        {
            Assert.True(Math.Abs(expected.X - actual.X) < tol && Math.Abs(expected.Y - actual.Y) < tol,
                        $"expected {expected} actual {actual}");
        }

        [Fact]
        public void Snap45_RoundsToTheNearestMultiple_AndKeepsTheLength()
        {
            var flat = PathMath.Snap45(new Point(10, 1)); // ~5.7° → 0°
            AssertPointClose(new Point(Math.Sqrt(101), 0), flat);

            var diagonal = PathMath.Snap45(new Point(1, 1.2)); // ~50° → 45°
            var len = Math.Sqrt(1 + 1.44);
            AssertPointClose(new Point(len * Math.Cos(Math.PI / 4), len * Math.Sin(Math.PI / 4)), diagonal);

            var up = PathMath.Snap45(new Point(-0.3, -7)); // ~-92.5° → -90°
            AssertPointClose(new Point(0, -Math.Sqrt(0.09 + 49)), up);

            Assert.Equal(default, PathMath.Snap45(default));
        }

        [Fact]
        public void Collinear_FlipsToTheOppositeDirection_AndKeepsTheOppositesLength()
        {
            AssertPointClose(new Point(0, -5), PathMath.Collinear(new Point(5, 0), new Point(0, 3)));
            AssertPointClose(new Point(-6, -8), PathMath.Collinear(new Point(10, 0), new Point(3, 4)));

            // a zero drag leaves the opposite handle alone
            Assert.Equal(new Point(5, 0), PathMath.Collinear(new Point(5, 0), default));
        }

        [Fact]
        public void AutoHandles_InteriorAnchor_LieAlongTheChord_AtThirdsOfTheNeighbourDistances()
        {
            var (inHandle, outHandle) = PathMath.AutoHandles(new Point(0, 0), new Point(3, 0), new Point(3, 4));

            // chord (3,4) → tangent (0.6,0.8); |p-prev| = 3, |next-p| = 4
            AssertPointClose(new Point(-0.6, -0.8), inHandle);
            AssertPointClose(new Point(0.8, 4.0 / 3 * 0.8), outHandle);

            // collinear: in = -k * out
            var cross = inHandle.X * outHandle.Y - inHandle.Y * outHandle.X;
            Assert.True(Math.Abs(cross) < 1e-9);
            Assert.True(inHandle.X * outHandle.X + inHandle.Y * outHandle.Y < 0);
        }

        [Fact]
        public void AutoHandles_Endpoint_GivesZeroOnTheMissingSide()
        {
            var (inHandle, outHandle) = PathMath.AutoHandles(null, new Point(0, 0), new Point(9, 0));
            Assert.Equal(default, inHandle);
            AssertPointClose(new Point(3, 0), outHandle);

            (inHandle, outHandle) = PathMath.AutoHandles(new Point(0, 0), new Point(0, 6), null);
            AssertPointClose(new Point(0, -2), inHandle);
            Assert.Equal(default, outHandle);

            Assert.Equal((default(Point), default(Point)), PathMath.AutoHandles(null, new Point(1, 1), null));
        }

        [Fact]
        public void Reverse_SwapsInAndOut_AndTheOrder_AndIsAnInvolution()
        {
            var anchors = new[]
            {
                new PathAnchor(new Point(0, 0), new Point(1, 2), new Point(3, 4), true),
                new PathAnchor(new Point(10, 0), new Point(5, 6), new Point(7, 8), false),
                new PathAnchor(new Point(20, 0), new Point(9, 10), new Point(11, 12), true),
            };

            var reversed = PathMath.Reverse(anchors);
            Assert.Equal(new PathAnchor(new Point(20, 0), new Point(11, 12), new Point(9, 10), true), reversed[0]);
            Assert.Equal(new PathAnchor(new Point(10, 0), new Point(7, 8), new Point(5, 6), false), reversed[1]);
            Assert.Equal(new PathAnchor(new Point(0, 0), new Point(3, 4), new Point(1, 2), true), reversed[2]);

            Assert.Equal(anchors, PathMath.Reverse(reversed));
        }

        [Fact]
        public void Split_AtHalf_OfAStraightSegment_GivesTheMidpoint_WithQuarterLengthHandles()
        {
            var p0 = new Point(0, 0);
            var p3 = new Point(8, 0);
            var parts = PathMath.Split(p0, p0, p3, p3, 0.5);

            var s = parts[2];
            AssertPointClose(new Point(4, 0), s);
            AssertPointClose(new Point(-2, 0), parts[1] - s); // the new anchor's In
            AssertPointClose(new Point(2, 0), parts[3] - s);  // its Out

            // the split point is on the curve (the curve-fitting library's cubic agrees)
            var onCurve = new CubicBezier(p0, p0, p3, p3).Sample(0.5);
            AssertPointClose(new Point(onCurve.X, onCurve.Y), s);
        }

        [Fact]
        public void Segment_IsTheCubicBetweenTwoAnchors_InAbsoluteCoordinates()
        {
            var a = new PathAnchor(new Point(10, 10), new Point(-1, -1), new Point(5, 0), true);
            var b = new PathAnchor(new Point(50, 30), new Point(0, -8), new Point(2, 2), true);

            var (p0, p1, p2, p3) = PathMath.Segment(a, b);
            Assert.Equal(new Point(10, 10), p0);
            Assert.Equal(new Point(15, 10), p1);
            Assert.Equal(new Point(50, 22), p2);
            Assert.Equal(new Point(50, 30), p3);
        }

        [Fact]
        public void Handles_ShorterThanTheMinimum_CountAsAbsent()
        {
            Assert.False(new PathAnchor(default, new Point(0.3, 0), default, false).HasIn);
            Assert.True(new PathAnchor(default, new Point(0.5, 0), default, false).HasIn);
            Assert.False(new PathAnchor(default, default, new Point(0, -0.49), false).HasOut);
            Assert.True(new PathAnchor(default, default, new Point(3, 4), false).HasOut);

            var corner = PathAnchor.Corner(new Point(5, 5));
            Assert.False(corner.Smooth);
            Assert.True(PathMath.IsStraight(corner, corner));
            Assert.False(PathMath.IsStraight(corner, new PathAnchor(default, new Point(2, 0), default, false)));
        }
    }
}
