using System;
using Clowd.Geometry2D;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The shared geometry core (<c>Clowd.Geometry</c>) as the video editor's shapes use it: the
    /// image editor's bend handle maths (the cases of its <c>GraphicArrowCurveTests</c>, ported onto
    /// <see cref="CurveBend"/>), the quadratic helpers the arrow builder and the box fit lean on,
    /// the arrow's head sizing, the double arrow's symmetry, and the Shift angle snap. Exact parity
    /// with the image editor's own code is <c>ArrowGeometryParityTests</c>' job.
    /// </summary>
    public class GeometryCoreTests
    {
        private static readonly Vec2 Origin = new Vec2(0, 0);
        private static readonly Vec2 Right100 = new Vec2(100, 0);

        private static void AssertClose(Vec2 expected, Vec2 actual, double tol = 1e-9) =>
            Assert.True(Math.Abs(expected.X - actual.X) < tol && Math.Abs(expected.Y - actual.Y) < tol,
                $"expected {expected} actual {actual}");

        // ------------------------------------------------------------------------- bend handle

        [Fact]
        public void A_bend_puts_the_curve_midpoint_under_the_pointer()
        {
            // the control point sits twice as far out as the drag, so the ON-CURVE t = 0.5 point is
            // what lands under the pointer
            Assert.True(CurveBend.TryBendFromPointer(Origin, Right100, new Vec2(50, 30), false, 1.5,
                out var offset, out var skew));

            Assert.Equal(30, offset, 9);
            Assert.Equal(0, skew);
            AssertClose(new Vec2(50, 30), CurveBend.HandlePoint(Origin, Right100, offset, skew));
            Assert.True(CurveBend.TryControlPoint(Origin, Right100, offset, skew, out var control));
            AssertClose(new Vec2(50, 60), control);
        }

        [Fact]
        public void A_bend_leans_along_the_chord_within_the_skew_limit()
        {
            CurveBend.TryBendFromPointer(Origin, Right100, new Vec2(65, 25), false, 1.5, out var offset, out var skew);
            Assert.Equal(25, offset, 9);
            Assert.Equal(0.15, skew, 9);
            AssertClose(new Vec2(65, 25), CurveBend.HandlePoint(Origin, Right100, offset, skew));

            // past the limit the handle stops a quarter chord from the midpoint
            CurveBend.TryBendFromPointer(Origin, Right100, new Vec2(95, 25), false, 1.5, out offset, out skew);
            Assert.Equal(CurveBend.MaxSkew, skew, 9);
            AssertClose(new Vec2(75, 25), CurveBend.HandlePoint(Origin, Right100, offset, skew));
        }

        [Fact]
        public void Snapping_steps_the_bow_by_a_tenth_of_the_chord_and_drops_the_skew()
        {
            CurveBend.TryBendFromPointer(Origin, Right100, new Vec2(70, 27), true, 1.5, out var offset, out var skew);
            Assert.Equal(30, offset, 9);
            Assert.Equal(0, skew);

            // within half a step of the chord, a snapped bend is exactly straight
            CurveBend.TryBendFromPointer(Origin, Right100, new Vec2(60, -4), true, 1.5, out offset, out skew);
            Assert.Equal(0, offset);
            Assert.Equal(0, skew);
        }

        [Fact]
        public void The_skew_scales_with_the_chord()
        {
            CurveBend.TryBendFromPointer(Origin, Right100, new Vec2(70, 20), false, 1.5, out var offset, out var skew);
            Assert.Equal(0.2, skew, 9);

            // the same bend on a chord twice as long: the offset is absolute, the skew relative
            AssertClose(new Vec2(140, 20), CurveBend.HandlePoint(Origin, new Vec2(200, 0), offset, skew));
        }

        [Fact]
        public void A_bend_dragged_back_onto_the_chord_is_exactly_straight()
        {
            CurveBend.TryBendFromPointer(Origin, Right100, new Vec2(50, 0.4), false, 1.5, out var offset, out var skew);

            Assert.Equal(0, offset);
            Assert.Equal(0, skew);
            Assert.False(CurveBend.TryControlPoint(Origin, Right100, offset, skew, out _));
            AssertClose(new Vec2(50, 0), CurveBend.HandlePoint(Origin, Right100, offset, skew));
        }

        [Fact]
        public void A_zero_length_chord_has_no_bend()
        {
            Assert.False(CurveBend.TryBendFromPointer(Origin, Origin, new Vec2(5, 5), false, 1.5, out _, out _));
            Assert.False(CurveBend.TryControlPoint(Origin, Origin, 10, 0, out _));
        }

        // ------------------------------------------------------------------------- quadratics

        [Theory]
        [InlineData(0.2, 0.7)]
        [InlineData(0, 0.4)]
        [InlineData(0.55, 1)]
        public void A_sub_curve_runs_along_the_curve(double t0, double t1)
        {
            var s = new Vec2(10, 90);
            var c = new Vec2(60, -40);
            var e = new Vec2(140, 70);

            QuadraticCurve.Sub(s, c, e, t0, t1, out var s1, out var c1, out var e1);

            AssertClose(QuadraticCurve.Eval(s, c, e, t0), s1);
            AssertClose(QuadraticCurve.Eval(s, c, e, t1), e1);
            // and in between, at the matching parameter
            for (int i = 1; i < 10; i++)
            {
                double u = i / 10.0;
                AssertClose(QuadraticCurve.Eval(s, c, e, t0 + u * (t1 - t0)), QuadraticCurve.Eval(s1, c1, e1, u));
            }
        }

        [Fact]
        public void The_whole_sub_curve_is_the_curve()
        {
            var s = new Vec2(-3.7, 12.1);
            var c = new Vec2(44.4, -80.25);
            var e = new Vec2(91.3, 6.6);

            QuadraticCurve.Sub(s, c, e, 0, 1, out var s1, out var c1, out var e1);

            // interpolated control points: equal to rounding, not bit for bit
            AssertClose(s, s1, 1e-12);
            AssertClose(c, c1, 1e-12);
            AssertClose(e, e1, 1e-12);
        }

        [Fact]
        public void Bounds_hold_every_point_of_the_curve_and_touch_it()
        {
            var random = new Random(17);
            for (int curve = 0; curve < 50; curve++)
            {
                Vec2 Next() => new Vec2(random.NextDouble() * 400 - 200, random.NextDouble() * 400 - 200);
                var s = Next();
                var c = Next();
                var e = Next();
                var (minX, minY, maxX, maxY) = QuadraticCurve.Bounds(s, c, e);

                double seenMinX = double.MaxValue, seenMinY = double.MaxValue;
                double seenMaxX = double.MinValue, seenMaxY = double.MinValue;
                for (int i = 0; i <= 1000; i++)
                {
                    var p = QuadraticCurve.Eval(s, c, e, i / 1000.0);
                    Assert.InRange(p.X, minX - 1e-9, maxX + 1e-9);
                    Assert.InRange(p.Y, minY - 1e-9, maxY + 1e-9);
                    seenMinX = Math.Min(seenMinX, p.X);
                    seenMinY = Math.Min(seenMinY, p.Y);
                    seenMaxX = Math.Max(seenMaxX, p.X);
                    seenMaxY = Math.Max(seenMaxY, p.Y);
                }

                // tight: the samples come within a hair of every side
                Assert.True(seenMinX - minX < 0.05 && maxX - seenMaxX < 0.05, $"x bounds {minX}..{maxX}, seen {seenMinX}..{seenMaxX}");
                Assert.True(seenMinY - minY < 0.05 && maxY - seenMaxY < 0.05, $"y bounds {minY}..{maxY}, seen {seenMinY}..{seenMaxY}");
            }
        }

        // ------------------------------------------------------------------------------ arrow

        /// <summary>How far the head's base sits behind its polygon tip: the head length.</summary>
        private static double HeadLength(ArrowParts parts)
        {
            var baseCenter = (parts.HeadLeft + parts.HeadRight) / 2;
            return (parts.Tip - baseCenter).Length;
        }

        [Fact]
        public void A_thin_arrow_still_gets_a_readable_head()
        {
            var parts = ArrowGeometry.Build(Origin, new Vec2(300, 0), null, 1, dashed: false);
            Assert.Equal(ArrowGeometry.MinHeadLength, HeadLength(parts), 9);
        }

        [Fact]
        public void A_heavy_arrow_grows_its_head_with_the_stroke()
        {
            var parts = ArrowGeometry.Build(Origin, new Vec2(300, 0), null, 8, dashed: false);
            Assert.Equal(8 * ArrowGeometry.HeadLengthFactor, HeadLength(parts), 9);
        }

        [Fact]
        public void A_short_arrow_is_not_all_head()
        {
            var parts = ArrowGeometry.Build(Origin, new Vec2(20, 0), null, 6, dashed: false);
            Assert.Equal(20 * ArrowGeometry.MaxHeadFraction, HeadLength(parts), 9);

            var both = ArrowGeometry.BuildDouble(Origin, new Vec2(20, 0), null, 6, dashed: false);
            Assert.Equal(20 * ArrowGeometry.DoubleMaxHeadFraction, HeadLength(both), 9);
        }

        [Fact]
        public void The_ink_radius_bounds_every_head()
        {
            foreach (var width in new[] { 1.0, 3, 6, 14, 40 })
            {
                var parts = ArrowGeometry.Build(Origin, new Vec2(1000, 0), null, width, dashed: false);
                double reach = Math.Max(Math.Abs(parts.HeadLeft.Y), Math.Abs(parts.HeadRight.Y)) + parts.CornerStrokeWidth / 2;
                Assert.True(reach <= ArrowGeometry.MaxInkRadius(width) + 1e-9, $"width {width}: reach {reach}");
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void A_double_arrow_is_its_own_mirror_image(bool curved, bool dashed)
        {
            // symmetric about the y axis: the start head is the end head reflected
            var start = new Vec2(-100, 0);
            var end = new Vec2(100, 0);
            Vec2? control = curved ? new Vec2(0, 80) : null;
            var parts = ArrowGeometry.BuildDouble(start, end, control, 5, dashed);
            static Vec2 Mirror(Vec2 v) => new Vec2(-v.X, v.Y);

            Assert.True(parts.HasStartHead);
            AssertClose(Mirror(parts.Tip), parts.StartTip, 1e-9);
            AssertClose(Mirror(parts.HeadNotch), parts.StartNotch, 1e-9);
            // each head's left/right is named from its own direction, so they swap in the mirror
            AssertClose(Mirror(parts.HeadLeft), parts.StartRight, 1e-9);
            AssertClose(Mirror(parts.HeadRight), parts.StartLeft, 1e-9);

            if (dashed)
            {
                Assert.Equal(ArrowShaftKind.Spine, parts.ShaftKind);
                AssertClose(Mirror(parts.SpineEnd), parts.SpineStart, 1e-6);
            }
            else
            {
                Assert.Equal(ArrowShaftKind.Taper, parts.ShaftKind);
                // untapered and cut back equally at both ends: the spine's ends (each the middle
                // of the outline's left and right sides there) mirror each other
                int n = (curved ? ArrowGeometry.CurvedSpineSamples : ArrowGeometry.StraightSpineSamples) + 1;
                var o = parts.ShaftOutline;
                var first = (o[0] + o[2 * n - 1]) / 2;
                var last = (o[n - 1] + o[n]) / 2;
                AssertClose(Mirror(last), first, 1e-6);
                Assert.Equal((o[n - 1] - o[n]).Length, (o[0] - o[2 * n - 1]).Length, 9);
            }
        }

        // a strongly bowed path cut at half its length, where it turns most: the piece an arrow
        // being drawn on is built from halfway through its entry
        private static readonly Vec2 BowStart = new Vec2(0, 0), BowControl = new Vec2(150, 220), BowEnd = new Vec2(300, 0);

        private static (Vec2 Start, Vec2 Control, Vec2 End, double Runout) FirstHalfOfBow()
        {
            Span<double> cumulative = stackalloc double[QuadraticCurve.SampleCount + 1];
            double length = QuadraticCurve.Cumulative(BowStart, BowControl, BowEnd, cumulative);
            double t = QuadraticCurve.ParameterAtLength(cumulative, length / 2);
            QuadraticCurve.Sub(BowStart, BowControl, BowEnd, 0, t, out var s, out var c, out var e);
            return (s, c, e, length / 2);
        }

        /// <summary>How far off the end head's centre line the curve passes at the head's base.</summary>
        private static double HeadOffCentre(ArrowParts parts, Vec2 start, Vec2 control, Vec2 end)
        {
            Span<double> cumulative = stackalloc double[QuadraticCurve.SampleCount + 1];
            double length = QuadraticCurve.Cumulative(start, control, end, cumulative);
            double head = HeadLength(parts);
            var baseCenter = (parts.HeadLeft + parts.HeadRight) / 2;
            var axis = (parts.Tip - baseCenter) / head;
            var onCurve = QuadraticCurve.Eval(start, control, end,
                QuadraticCurve.ParameterAtLength(cumulative, length - (end - parts.Tip).Length - head));
            var d = onCurve - baseCenter;
            return Math.Abs(axis.X * d.Y - axis.Y * d.X);
        }

        [Fact]
        public void A_head_cut_from_a_bowed_path_aims_along_the_arc_it_covers()
        {
            var (s, c, e, runout) = FirstHalfOfBow();
            var tangent = ArrowGeometry.Build(s, e, c, 6, dashed: false);
            var aimed = ArrowGeometry.Build(s, e, c, 6, dashed: false, runout: runout);

            // laid back along the cut's tangent the head leaves the shaft well off its centre line;
            // along the chord the shaft runs into the middle of its base
            double off = HeadOffCentre(tangent, s, c, e), onAxis = HeadOffCentre(aimed, s, c, e);
            Assert.True(off > 4, $"tangent head off centre by {off}");
            Assert.True(onAxis < off / 4, $"aimed head off centre by {onAxis} (tangent {off})");

            // only the direction changes: same size, same rounded point, same shaft
            Assert.Equal(HeadLength(tangent), HeadLength(aimed), 9);
            Assert.Equal((e - tangent.Tip).Length, (e - aimed.Tip).Length, 9);
            Assert.Equal(tangent.ShaftOutline, aimed.ShaftOutline);
        }

        [Fact]
        public void A_cut_head_eases_into_the_finished_arrows_as_the_path_runs_out()
        {
            var (s, c, e, _) = FirstHalfOfBow();
            var finished = ArrowGeometry.Build(s, e, c, 6, dashed: false);
            double head = HeadLength(finished);
            static double Angle(ArrowParts p) => Math.Atan2(p.Tip.Y - p.HeadNotch.Y, p.Tip.X - p.HeadNotch.X);

            // a head about to reach the real end is the finished arrow's, near enough to not jump
            var arriving = ArrowGeometry.Build(s, e, c, 6, dashed: false, runout: 1e-6);
            AssertClose(finished.HeadLeft, arriving.HeadLeft, 1e-4);
            AssertClose(finished.HeadRight, arriving.HeadRight, 1e-4);

            // it turns steadily over the last head length, and not at all before it
            double full = Angle(ArrowGeometry.Build(s, e, c, 6, dashed: false, runout: head));
            double half = Angle(ArrowGeometry.Build(s, e, c, 6, dashed: false, runout: head / 2));
            Assert.Equal(full, Angle(ArrowGeometry.Build(s, e, c, 6, dashed: false, runout: 3 * head)), 12);
            Assert.True(Math.Abs(half - Angle(finished)) > 0.2 * Math.Abs(full - Angle(finished)));
            Assert.True(Math.Abs(half - full) > 0.2 * Math.Abs(full - Angle(finished)));
        }

        [Fact]
        public void A_straight_arrow_ignores_the_runout()
        {
            var parts = ArrowGeometry.Build(Origin, new Vec2(300, 40), null, 6, dashed: false, runout: 500);
            var plain = ArrowGeometry.Build(Origin, new Vec2(300, 40), null, 6, dashed: false);
            Assert.Equal(plain.HeadLeft, parts.HeadLeft);
            Assert.Equal(plain.HeadRight, parts.HeadRight);
            Assert.Equal(plain.ShaftOutline, parts.ShaftOutline);
        }

        [Fact]
        public void A_double_arrow_aims_each_cut_head_on_its_own()
        {
            // the bow's middle half: both ends are cuts, mirror images of each other
            Span<double> cumulative = stackalloc double[QuadraticCurve.SampleCount + 1];
            double length = QuadraticCurve.Cumulative(BowStart, BowControl, BowEnd, cumulative);
            QuadraticCurve.Sub(BowStart, BowControl, BowEnd, QuadraticCurve.ParameterAtLength(cumulative, length / 4),
                QuadraticCurve.ParameterAtLength(cumulative, 3 * length / 4), out var s, out var c, out var e);
            static Vec2 Mirror(Vec2 v) => new Vec2(300 - v.X, v.Y);

            var both = ArrowGeometry.BuildDouble(s, e, c, 6, dashed: false, length / 4, length / 4);
            AssertClose(Mirror(both.Tip), both.StartTip, 1e-6);
            AssertClose(Mirror(both.HeadLeft), both.StartRight, 1e-6);
            AssertClose(Mirror(both.HeadRight), both.StartLeft, 1e-6);

            // a runout at one end leaves the other head laid along its tangent
            var plain = ArrowGeometry.BuildDouble(s, e, c, 6, dashed: false);
            var endOnly = ArrowGeometry.BuildDouble(s, e, c, 6, dashed: false, endRunout: length / 4);
            Assert.Equal(plain.StartLeft, endOnly.StartLeft);
            Assert.Equal(both.HeadLeft, endOnly.HeadLeft);
            Assert.NotEqual(plain.HeadLeft, endOnly.HeadLeft);
            Assert.True(HeadOffCentre(endOnly, s, c, e) < HeadOffCentre(plain, s, c, e) / 3);
        }

        // ------------------------------------------------------------------------- angle snap

        [Fact]
        public void The_angle_snap_lands_on_multiples_of_fifteen_degrees()
        {
            var anchor = new Vec2(40, -12);
            var random = new Random(5);
            for (int i = 0; i < 200; i++)
            {
                var point = new Vec2(random.NextDouble() * 600 - 300, random.NextDouble() * 600 - 300);
                var snapped = AngleSnap.Snap(anchor, point, diagOnly: false);
                var d = snapped - anchor;
                if (d.Length < 1e-6)
                    continue;

                double degrees = Math.Atan2(d.Y, d.X) * 180 / Math.PI;
                double steps = degrees / AngleSnap.StepDegrees;
                Assert.True(Math.Abs(steps - Math.Round(steps)) < 1e-9, $"{point} snapped to {degrees}°");
            }
        }
    }
}
