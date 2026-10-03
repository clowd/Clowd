using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Ink;
using Xunit;
using Sample = Clowd.Drawing.Graphics.GraphicBrush.Sample;
using StrokePoint = Clowd.Drawing.Ink.FreehandStroke.StrokePoint;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// The freehand outline algorithm is a pure function of (samples, size), so it is pinned here
    /// platform-free: determinism, the raw-cursor tip, the time normalisation (a 1000 Hz
    /// resample of a 60 Hz stroke lands on the same centreline with the same pressure), the
    /// speed → pressure rule, the start skip, the dot and dash rules, the edge offsets, corner
    /// caps, and the no-retroactive-change property that makes a lift invisible.
    /// </summary>
    public class FreehandStrokeTests
    {
        private const double Size = 6;
        private const double Frame = FreehandStroke.RefFrameMs;

        private static Sample S(double x, double y, double t) => new Sample(new Point(x, y), t);

        /// <summary>A straight run along +x: <paramref name="n"/> samples, <paramref name="step"/>
        /// apart, one 60 Hz frame apart in time.</summary>
        private static Sample[] Line(int n, double step, double dt = Frame)
        {
            var raw = new Sample[n];
            for (int i = 0; i < n; i++)
                raw[i] = S(i * step, 0, i * dt);
            return raw;
        }

        /// <summary>A wiggle: steady +x drift with a sine across it, one 60 Hz frame per sample.</summary>
        private static Sample[] Wiggle(int n, double speed = 1.0, double amplitude = 10, double dt = Frame)
        {
            var raw = new Sample[n];
            for (int i = 0; i < n; i++)
                raw[i] = Curve(i * dt, speed, amplitude);
            return raw;
        }

        private static Sample Curve(double tMs, double speed, double amplitude)
        {
            double f = tMs / Frame;
            return S(speed * f, amplitude * Math.Sin(f / 8), tMs);
        }

        private static List<StrokePoint> Points(Sample[] raw, double size = Size)
        {
            var points = new List<StrokePoint>();
            FreehandStroke.GetStrokePoints(raw, size, points);
            return points;
        }

        private static (List<Point> Left, List<Point> Right, List<Point> Outline) Outline(Sample[] raw, double size = Size)
        {
            var left = new List<Point>();
            var right = new List<Point>();
            var outline = new List<Point>();
            FreehandStroke.GetStrokeOutlinePoints(Points(raw, size), size, left, right, outline);
            return (left, right, outline);
        }

        private static double Dist(Point a, Point b) => GraphicLine.Distance(a, b);

        [Fact]
        public void GetStrokePoints_IsDeterministic()
        {
            var raw = Wiggle(80);
            var a = Points(raw);
            var b = Points(raw);

            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].P, b[i].P);
                Assert.Equal(a[i].Pressure, b[i].Pressure);
                Assert.Equal(a[i].V, b[i].V);
                Assert.Equal(a[i].Distance, b[i].Distance);
                Assert.Equal(a[i].RunningLength, b[i].RunningLength);
            }

            var (_, _, o1) = Outline(raw);
            var (_, _, o2) = Outline(raw);
            Assert.Equal(o1, o2);
        }

        [Fact]
        public void TheTip_IsTheRawCursor_NotASmoothedPoint()
        {
            var raw = Wiggle(40);
            var points = Points(raw);

            // last = true: the final stroke point sits exactly on the final sample...
            Assert.Equal(raw[^1].P, points[^1].P);

            // ...while every interior point is pulled back toward its predecessor (the EMA)
            var interior = points[^2];
            var rawInterior = raw[^2].P;
            Assert.NotEqual(rawInterior, interior.P);
            Assert.True(interior.P.X < rawInterior.X, $"{interior.P} should trail {rawInterior}");
        }

        [Fact]
        public void A1000HzResample_LandsOnThe60HzCentreline_WithTheSamePressure()
        {
            // the same path, sampled at 60 Hz and at 1000 Hz (linear interpolation in time)
            var slow = Wiggle(120);
            var fast = new List<Sample>();
            for (double t = 0; t <= slow[^1].T; t += 1)
            {
                int i = Math.Min((int)(t / Frame), slow.Length - 2);
                double u = (t - slow[i].T) / Frame;
                fast.Add(new Sample(GraphicLine.Lerp(slow[i].P, slow[i + 1].P, u), t));
            }

            if (fast[^1].T < slow[^1].T)
                fast.Add(slow[^1]);

            var slowPoints = Points(slow);
            var fastPoints = Points(fast.ToArray());

            // every 60 Hz centreline point is within half a unit of the 1000 Hz centreline
            foreach (var sp in slowPoints.Skip(1))
            {
                double nearest = fastPoints.Min(fp => Dist(fp.P, sp.P));
                Assert.True(nearest < 0.5, $"60 Hz point {sp.P} is {nearest:F3} from the 1000 Hz centreline");
            }

            // and the simulated pressure settles to the same value
            Assert.Equal(slowPoints[^1].Pressure, fastPoints[^1].Pressure, 0.05);
            Assert.Equal(slowPoints[slowPoints.Count / 2].Pressure,
                         fastPoints.MinBy(fp => Dist(fp.P, slowPoints[slowPoints.Count / 2].P)).Pressure, 0.05);
        }

        [Fact]
        public void Pressure_FallsWhenFast_AndRisesWhenSlow()
        {
            // two units of size per frame: sp = 1, so the pressure decays toward 0
            var fast = Points(Line(30, 2 * Size));
            for (int i = 2; i < fast.Count; i++)
                Assert.True(fast[i].Pressure < fast[i - 1].Pressure, $"fast stroke pressure rose at {i}");

            // a tenth of size per frame: the pressure climbs from the first-point 0.25 toward 0.9
            var slow = Points(Line(60, 0.1 * Size));
            for (int i = 2; i < slow.Count; i++)
                Assert.True(slow[i].Pressure > slow[i - 1].Pressure, $"slow stroke pressure fell at {i}");
            Assert.True(slow[^1].Pressure > 0.5, $"slow stroke stayed at {slow[^1].Pressure}");
            Assert.Equal(FreehandStroke.FirstPressure, slow[0].Pressure);
        }

        [Fact]
        public void StartSkip_SwallowsPointsUntilTheStrokeIsSizeLong()
        {
            var raw = Line(40, 1);
            var points = Points(raw);

            Assert.Equal(0, points[0].RunningLength);
            Assert.True(points[1].RunningLength >= Size, $"first emitted point at {points[1].RunningLength}");
            Assert.True(points.Count < raw.Length, "no sample was skipped");

            // the skip is a start rule only: a handful of samples, then every one emits a point
            Assert.InRange(points.Count, raw.Length - 6, raw.Length - 1);
            Assert.Equal(raw[^1].P, points[^1].P);
        }

        [Fact]
        public void StreamlineBlend_IsTheTunedValueAtOneFrame_AndKeepsTheLagAtAnyRate()
        {
            Assert.Equal(FreehandStroke.StreamlineT, FreehandStroke.StreamlineBlend(1), 12);

            // a steady hand at v per frame: the EMA settles v·k·(1 − α)/α behind, which must be the
            // 60 Hz lag v·(1 − t)/t whatever the sample spacing
            double lag60 = (1 - FreehandStroke.StreamlineT) / FreehandStroke.StreamlineT;
            foreach (var k in new[] { 1 / 16.67, 0.25, 1, 2, 3 })
            {
                double a = FreehandStroke.StreamlineBlend(k);
                Assert.Equal(lag60, k * (1 - a) / a, 9);
            }
        }

        [Fact]
        public void OneSample_IsADot_AtTheRestingRadius()
        {
            var (_, _, outline) = Outline(new[] { S(10, 20, 0) });

            Assert.Equal(FreehandStroke.DotSegments, outline.Count);
            double r = FreehandStroke.Radius(Size, FreehandStroke.DotPressure);
            Assert.Equal(Size / 2, r); // LineWidth: a click dots at 2·LineWidth across
            foreach (var p in outline)
                Assert.Equal(r, Dist(p, new Point(10, 20)), 9);
        }

        [Fact]
        public void TwoSamples_BecomeFiveAlongThePair()
        {
            var points = Points(new[] { S(0, 0, 0), S(100, 0, 100) });

            Assert.Equal(5, points.Count);
            Assert.Equal(new Point(0, 0), points[0].P);
            Assert.Equal(new Point(100, 0), points[^1].P);
            for (int i = 1; i < points.Count; i++)
            {
                Assert.True(points[i].P.X > points[i - 1].P.X);
                Assert.Equal(0, points[i].P.Y);
            }
        }

        [Fact]
        public void StraightStroke_EdgesSitAtThePressureRadius_EitherSide()
        {
            var raw = Line(60, 2);
            var points = Points(raw);
            var (left, right, _) = Outline(raw);

            Assert.Equal(left.Count, right.Count);
            Assert.True(left.Count > 5);
            for (int i = 0; i < left.Count; i++)
            {
                // moving +x, pf's V points back along −x, so perp(V) is +y: left above, right below
                Assert.Equal(left[i].X, right[i].X, 9);
                Assert.Equal(-left[i].Y, right[i].Y, 9);
                Assert.True(left[i].Y < 0);

                var sp = points.Single(p => Math.Abs(p.P.X - left[i].X) < 1e-9);
                Assert.Equal(FreehandStroke.Radius(Size, sp.Pressure), -left[i].Y, 9);
            }
        }

        [Fact]
        public void AReversal_EmitsACornerCap_OnEachEdge()
        {
            // out along +x for 20 frames, then straight back
            var raw = new List<Sample>();
            for (int i = 0; i <= 20; i++) raw.Add(S(i * 3, 0, i * Frame));
            for (int i = 1; i <= 20; i++) raw.Add(S(60 - i * 3, 0, (20 + i) * Frame));

            var points = Points(raw.ToArray());
            var apex = points.MaxBy(p => p.P.X).P;
            var (left, right, _) = Outline(raw.ToArray());

            // the cap is a ring of CornerCapSegments + 1 vertices around the apex on each edge
            var apexPressure = points.Single(p => p.P == apex).Pressure;
            double r = FreehandStroke.Radius(Size, apexPressure);
            int leftRing = left.Count(p => Math.Abs(Dist(p, apex) - r) < 1e-6);
            int rightRing = right.Count(p => Math.Abs(Dist(p, apex) - r) < 1e-6);
            Assert.Equal(FreehandStroke.CornerCapSegments + 1, leftRing);
            Assert.Equal(FreehandStroke.CornerCapSegments + 1, rightRing);

            // the straight run before it has no such ring
            var straight = Outline(Line(21, 3));
            var end = new Point(60, 0);
            Assert.True(straight.Left.Count(p => Math.Abs(Dist(p, end) - r) < 1e-6) <= 1);
        }

        [Fact]
        public void AppendingASample_LeavesEverythingBehindTheTailUntouched()
        {
            // the property that makes a lift invisible (and an incremental rebuild possible): a
            // new sample can only change the outline within size + EndNoiseThreshold of the old end
            var before = Wiggle(90, speed: 1.5);
            var after = before.Concat(new[] { Curve(90 * Frame, 1.5, 10) }).ToArray();

            var oldPoints = Points(before);
            double oldTotal = oldPoints[^1].RunningLength;
            double tail = Size + FreehandStroke.EndNoiseThreshold + 2 * 1.5; // plus a step of slack for the vertex → point mapping

            var (oldLeft, oldRight, _) = Outline(before);
            var (newLeft, newRight, _) = Outline(after);
            AssertFrozenPrefix(oldLeft, newLeft, oldPoints, oldTotal - tail);
            AssertFrozenPrefix(oldRight, newRight, oldPoints, oldTotal - tail);
        }

        private static void AssertFrozenPrefix(List<Point> before, List<Point> after, List<StrokePoint> points, double frozenUpTo)
        {
            int common = 0;
            while (common < before.Count && common < after.Count && before[common] == after[common])
                common++;

            // something near the tip did change (the old tip was the raw cursor, now it is smoothed)...
            Assert.True(common < before.Count, "the outline did not change at all");

            // ...but the first vertex that differs belongs to a point inside the tail zone
            var firstChanged = before[common];
            var nearest = points.MinBy(p => FreehandStroke.Dist2(p.P, firstChanged));
            Assert.True(nearest.RunningLength >= frozenUpTo,
                        $"vertex {common} of {before.Count} changed at running length {nearest.RunningLength:F1}, frozen up to {frozenUpTo:F1}");

            // and the frozen prefix is most of the stroke, so the check is not vacuous
            Assert.True(common > before.Count / 2, $"only {common} of {before.Count} vertices frozen");
        }
    }
}
