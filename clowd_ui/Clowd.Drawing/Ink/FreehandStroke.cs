using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Ink
{
    /// <summary>
    /// Turns the brush's raw pointer samples into a filled outline polygon. A port of
    /// perfect-freehand's getStrokePoints + getStrokeOutlinePoints
    /// (https://github.com/steveruizok/perfect-freehand — MIT, Steve Ruiz), with two deliberate
    /// changes:
    /// <list type="bullet">
    /// <item>the streamline (an EMA on the position) and the simulated pressure are normalised
    /// against each sample's elapsed time, so the 60 Hz tuning holds whether the mouse reports at
    /// 125 Hz or at 1000 Hz with coalesced points (stock pf gets stiffer and fatter as the rate
    /// goes up);</item>
    /// <item><c>last</c> is always true: the tip of the stroke is the raw cursor both while
    /// drawing and after, so a stroke never changes shape on mouse-up.</item>
    /// </list>
    /// Nothing here fits curves. The whole thing is a pure function of (samples, size): the live
    /// stroke and the saved stroke are the same function of the same input.
    ///
    /// Both stages are written as one step per point over an explicit state struct
    /// (<see cref="StrokeState"/>, <see cref="OutlineState"/>): the batch functions here loop the
    /// steps from a fresh state, and <see cref="FreehandStrokeBuilder"/> loops the same steps
    /// from a state it keeps between pointer events, so the incremental stroke is the batch
    /// stroke by construction.
    /// </summary>
    internal static class FreehandStroke
    {
        // pf's defaults (streamline 0.5 → t = 0.15 + 0.5 * 0.85)
        internal const double Thinning = 0.5;
        internal const double Smoothing = 0.5;
        internal const double StreamlineT = 0.575;
        internal const double RateOfPressureChange = 0.275;

        // time normalisation: pf's per-sample constants assume one 60 Hz frame per sample
        internal const double RefFrameMs = 1000.0 / 60;
        internal const double MinDtMs = 1;
        internal const double MaxDtMs = 50;

        internal const double FirstPressure = 0.25;
        internal const double DotPressure = 0.5;
        internal const double MinRadius = 0.01;
        internal const double EndNoiseThreshold = 3;
        internal const double FixedPi = Math.PI + 0.0001;
        internal const int StartCapSegments = 13;
        internal const int EndCapSegments = 29;
        internal const int CornerCapSegments = 13;
        internal const int DotSegments = 13;

        /// <summary>A smoothed centreline point with the state the outline pass needs.</summary>
        internal struct StrokePoint
        {
            public Point P;
            public double Pressure;
            /// <summary>Unit vector from this point BACK to the previous one (pf's convention).</summary>
            public Vector V;
            public double Distance;
            public double RunningLength;
        }

        /// <summary>Stage one's carry between samples: the last emitted point, the length so far,
        /// the simulated pressure and whether the start rule has been satisfied.</summary>
        internal struct StrokeState
        {
            public Point PrevPoint;
            public double Running;
            public double Pressure;
            public bool ReachedMin;

            public static StrokeState Start(Point first) =>
                new StrokeState { PrevPoint = first, Pressure = FirstPressure };
        }

        /// <summary>Stage two's carry between centreline points: the direction and the last
        /// vertex of each edge, and whether the previous point was a corner.</summary>
        internal struct OutlineState
        {
            public Vector PrevV;
            public Point Pl;
            public Point Pr;
            public bool PrevWasCorner;
            /// <summary>The radius at the last processed point; the end cap is swung at it.</summary>
            public double Radius;

            public static OutlineState Start(StrokePoint first, double size, double lastPressure) =>
                new OutlineState { PrevV = first.V, Pl = first.P, Pr = first.P, Radius = Radius(size, lastPressure) };
        }

        // the stages' scratch lists; BuildGeometry may run on the render thread and the UI thread
        [ThreadStatic] private static List<StrokePoint> _strokePoints;
        [ThreadStatic] private static List<Point> _left;
        [ThreadStatic] private static List<Point> _right;
        [ThreadStatic] private static List<Point> _outline;

        /// <summary>
        /// The streamline blend for a sample <paramref name="k"/> frames after the previous one.
        /// pf blends 0.575 of the way to each 60 Hz sample; what that smoothing looks like on a
        /// moving pointer is its lag, (1 − t)/t frames of motion behind a steady hand, so this is
        /// the blend that keeps the same lag at any rate (t at one frame, k·t/(k·t + 1 − t) in
        /// general). The exponential form 1 − (1 − t)^k matches step responses instead and
        /// over-smooths fine input by half.
        /// </summary>
        internal static double StreamlineBlend(double k) =>
            k * StreamlineT / (k * StreamlineT + 1 - StreamlineT);

        /// <summary>Half the drawn width at a pressure: linear easing, so size·(0.25 + 0.5·p) — from
        /// 0.5·size at full speed to 1.5·size at rest.</summary>
        internal static double Radius(double size, double pressure) =>
            Math.Max(MinRadius, size * (0.5 - Thinning * (0.5 - pressure)));

        /// <summary>
        /// Stage one, one sample: smooths <paramref name="cur"/> against the state and emits a
        /// centreline point, or returns false when the sample is swallowed (it landed on the
        /// previous point, or the start rule is still eating). The state is advanced either way —
        /// a swallowed sample still counts toward the running length, as in pf. With
        /// <paramref name="isLast"/> the point is the raw sample (the tip is the cursor) and the
        /// start rule does not apply.
        /// </summary>
        internal static bool StrokeStep(ref StrokeState st, in GraphicBrush.Sample prev, in GraphicBrush.Sample cur,
                                        bool isLast, double size, out StrokePoint result)
        {
            result = default;
            double dt = Math.Clamp(cur.T - prev.T, MinDtMs, MaxDtMs);
            double k = dt / RefFrameMs;

            // the tip is the raw cursor (last = true): no lag while drawing, no jump on release
            var point = isLast ? cur.P : GraphicLine.Lerp(st.PrevPoint, cur.P, StreamlineBlend(k));
            if (point == st.PrevPoint)
                return false;

            // PORT NOTE: pf accumulates the running length (and lerps) from the last EMITTED
            // point, so a skipped run below counts distances from the first sample — kept as is
            double distance = GraphicLine.Distance(st.PrevPoint, point);
            st.Running += distance;

            // pf's start rule: swallow points until the stroke is `size` long, which removes
            // the hook a hesitant start would leave
            if (!isLast && !st.ReachedMin)
            {
                if (st.Running < size)
                    return false;
                st.ReachedMin = true;
            }

            // simulated pressure from speed in units per frame: fast thins, slow thickens
            double sp = Math.Min(1, distance / k / size);
            double rp = Math.Min(1, 1 - sp);
            double rate = 1 - Math.Pow(1 - RateOfPressureChange, k);
            st.Pressure = Math.Min(1, st.Pressure + (rp - st.Pressure) * sp * rate);

            result = new StrokePoint
            {
                P = point,
                Pressure = st.Pressure,
                V = Unit(st.PrevPoint - point),
                Distance = distance,
                RunningLength = st.Running,
            };
            st.PrevPoint = point;
            return true;
        }

        /// <summary>
        /// Stage one: smooths the raw samples into centreline points with a simulated pressure.
        /// Each sample's elapsed time (clamped to [MinDtMs, MaxDtMs]) is measured in 60 Hz frames,
        /// k, and the per-sample constants become their k-frame equivalents (see
        /// <see cref="StreamlineBlend"/>): the pressure rate is 1 − (1 − r)^k and the speed that
        /// drives the pressure is the distance per frame. A click (one sample) is a single point at
        /// the resting pressure; a flick (two samples) is spread into five, pf's dash rule.
        /// </summary>
        internal static void GetStrokePoints(ReadOnlySpan<GraphicBrush.Sample> raw, double size, List<StrokePoint> output)
        {
            output.Clear();
            if (raw.Length == 0)
                return;

            if (raw.Length == 1)
            {
                output.Add(new StrokePoint { P = raw[0].P, Pressure = DotPressure, V = new Vector(1, 1) });
                return;
            }

            var pts = raw;
            if (raw.Length == 2)
                pts = Dash(raw);

            int n = pts.Length;
            var st = StrokeState.Start(pts[0].P);
            output.Add(FirstPoint(pts[0].P));

            for (int i = 1; i < n; i++)
            {
                if (StrokeStep(ref st, pts[i - 1], pts[i], i == n - 1, size, out var point))
                    output.Add(point);
            }

            if (output.Count > 1)
                output[0] = WithV(output[0], output[1].V);
        }

        /// <summary>The first centreline point (the first sample as is); its direction is patched
        /// to the second point's once that exists.</summary>
        internal static StrokePoint FirstPoint(Point p) =>
            new StrokePoint { P = p, Pressure = FirstPressure, V = new Vector(1, 1) };

        internal static StrokePoint WithV(StrokePoint point, Vector v)
        {
            point.V = v;
            return point;
        }

        /// <summary>pf's dash rule: a two-sample flick becomes five samples along the pair.</summary>
        internal static GraphicBrush.Sample[] Dash(ReadOnlySpan<GraphicBrush.Sample> raw)
        {
            var five = new GraphicBrush.Sample[5];
            for (int i = 0; i < 5; i++)
                five[i] = new GraphicBrush.Sample(GraphicLine.Lerp(raw[0].P, raw[1].P, i / 4.0), raw[0].T + (raw[1].T - raw[0].T) * i / 4.0);
            return five;
        }

        /// <summary>
        /// Stage two, one centreline point: offsets a left and a right edge vertex by the pressure
        /// radius, dropping a vertex closer than size·smoothing to the previous one on that edge,
        /// or swings a corner cap wherever the stroke turns back on itself (so a reversal stays
        /// round rather than pinching). The tip (<paramref name="i"/> == <paramref name="last"/>)
        /// is offset straight across. The caller applies the end-noise trim, which depends on the
        /// tip's running length, before calling.
        /// </summary>
        internal static void OutlineStep(ref OutlineState st, List<StrokePoint> points, int i, int last, double size,
                                         List<Point> left, List<Point> right)
        {
            var pt = points[i];
            double radius = st.Radius = Radius(size, pt.Pressure);

            var nextV = i < last ? points[i + 1].V : pt.V;
            double nextDpr = i < last ? Vector.Dot(pt.V, nextV) : 1.0;
            double prevDpr = Vector.Dot(pt.V, st.PrevV);
            bool isCorner = prevDpr < 0 && !st.PrevWasCorner;
            bool nextIsCorner = nextDpr < 0;

            if (isCorner || nextIsCorner)
            {
                // a turn sharper than 90°: a half-circle cap on each edge around the point
                var off = Perp(st.PrevV) * radius;
                Point tl = default, tr = default;
                for (int k = 0; k <= CornerCapSegments; k++)
                {
                    double t = (double)k / CornerCapSegments;
                    tl = RotateAround(pt.P - off, pt.P, FixedPi * t);
                    left.Add(tl);
                    tr = RotateAround(pt.P + off, pt.P, -FixedPi * t);
                    right.Add(tr);
                }

                st.Pl = tl;
                st.Pr = tr;
                st.PrevWasCorner = nextIsCorner;
                return; // pf leaves prevV at the vector before the corner
            }

            st.PrevWasCorner = false;

            if (i == last)
            {
                var off = Perp(pt.V) * radius;
                left.Add(pt.P - off);
                right.Add(pt.P + off);
                return;
            }

            // the edge normal is the mean of this and the next direction, weighted by how
            // much they agree, which rounds off gentle bends
            var offset = Perp(Lerp(nextV, pt.V, nextDpr)) * radius;
            double minDist2 = size * Smoothing * (size * Smoothing);

            var l = pt.P - offset;
            if (i <= 1 || Dist2(st.Pl, l) > minDist2)
            {
                left.Add(l);
                st.Pl = l;
            }

            var r = pt.P + offset;
            if (i <= 1 || Dist2(st.Pr, r) > minDist2)
            {
                right.Add(r);
                st.Pr = r;
            }

            st.PrevV = pt.V;
        }

        /// <summary>True when point <paramref name="i"/> is pointer noise just behind the tip:
        /// the last few units before the tip are dropped; the tip itself always draws.</summary>
        internal static bool IsEndNoise(List<StrokePoint> points, int i, int last) =>
            i < last && points[last].RunningLength - points[i].RunningLength < EndNoiseThreshold;

        /// <summary>
        /// Stage two: walks the centreline and offsets a left and a right edge by the pressure
        /// radius (see <see cref="OutlineStep"/>), trimming the last few units before the tip,
        /// which are pointer noise. The outline is left edge → end cap → right edge reversed →
        /// start cap, one closed loop. <paramref name="left"/> and <paramref name="right"/> are
        /// exposed for tests.
        /// </summary>
        internal static void GetStrokeOutlinePoints(List<StrokePoint> points, double size,
                                                    List<Point> left, List<Point> right, List<Point> outline)
        {
            left.Clear();
            right.Clear();
            outline.Clear();
            if (points.Count == 0 || size <= 0)
                return;

            int last = points.Count - 1;

            if (points.Count == 1)
            {
                AddDot(points[0], size, outline);
                return;
            }

            var st = OutlineState.Start(points[0], size, points[last].Pressure);
            for (int i = 0; i <= last; i++)
            {
                if (!IsEndNoise(points, i, last))
                    OutlineStep(ref st, points, i, last, size, left, right);
            }

            AddOutline(CollectionsMarshal.AsSpan(left), CollectionsMarshal.AsSpan(right), outline,
                       points[0].P, true, points[last], st.Radius);
        }

        /// <summary>A dot: a ring at the resting radius around the single point.</summary>
        internal static void AddDot(StrokePoint point, double size, List<Point> outline)
        {
            double r = Radius(size, point.Pressure);
            var c = point.P;
            for (int k = 0; k < DotSegments; k++)
            {
                double a = 2 * Math.PI * k / DotSegments;
                outline.Add(new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)));
            }
        }

        /// <summary>
        /// Assembles one closed loop from a run of edge vertices: left edge → end cap (when
        /// <paramref name="tip"/> is given) → right edge reversed → start cap (when
        /// <paramref name="startCap"/>, swung from the first right vertex around
        /// <paramref name="firstPoint"/>). Without a cap the loop simply closes across the stroke
        /// between the two edges' last (or first) vertices — the cut between two chunks of the same
        /// stroke, which the neighbouring chunk's interior covers.
        /// </summary>
        internal static void AddOutline(ReadOnlySpan<Point> left, ReadOnlySpan<Point> right, List<Point> outline,
                                        Point firstPoint, bool startCap, StrokePoint? tip, double radius)
        {
            foreach (var p in left)
                outline.Add(p);

            if (tip is { } t)
            {
                // end cap: a half turn from the left edge's tip around the last point to the right
                // edge's. (pf sweeps 3π here — an extra full turn, invisible under NonZero, that only
                // costs vertices — so this keeps the half turn.)
                var capStart = t.P + Perp(-t.V) * radius;
                for (int k = 1; k < EndCapSegments; k++)
                    outline.Add(RotateAround(capStart, t.P, FixedPi * k / EndCapSegments));
            }

            for (int i = right.Length - 1; i >= 0; i--)
                outline.Add(right[i]);

            if (startCap && right.Length > 0)
            {
                // start cap: swung from the first right-edge vertex around the first point
                for (int k = 1; k <= StartCapSegments; k++)
                    outline.Add(RotateAround(right[0], firstPoint, FixedPi * k / StartCapSegments));
            }
        }

        /// <summary>
        /// The outline as one closed, filled figure. Every outline vertex becomes a quadratic
        /// control point and the curve passes through the midpoints, which smooths the polygon
        /// without a fitting pass. NonZero is mandatory: EvenOdd would punch a hole wherever the
        /// stroke crosses itself. Depends on nothing but its arguments.
        /// </summary>
        internal static StreamGeometry BuildGeometry(ReadOnlySpan<GraphicBrush.Sample> samples, double size)
        {
            var points = _strokePoints ??= new List<StrokePoint>();
            var left = _left ??= new List<Point>();
            var right = _right ??= new List<Point>();
            var outline = _outline ??= new List<Point>();

            GetStrokePoints(samples, size, points);
            GetStrokeOutlinePoints(points, size, left, right, outline);
            return ToGeometry(outline);
        }

        /// <summary>The closed, midpoint-smoothed, NonZero figure of an outline loop.</summary>
        internal static StreamGeometry ToGeometry(List<Point> outline)
        {
            var geometry = new StreamGeometry();
            if (outline.Count == 0)
                return geometry;

            using (var ctx = geometry.Open())
            {
                int n = outline.Count;
                ctx.SetFillRule(FillRule.NonZero);
                ctx.BeginFigure(Mid(outline[n - 1], outline[0]), true);
                for (int i = 0; i < n; i++)
                    ctx.QuadraticBezierTo(outline[i], Mid(outline[i], outline[(i + 1) % n]));
                ctx.EndFigure(true);
            }

            return geometry;
        }

        // ---- vector helpers (pf's vec.ts; point lerp/distance are GraphicLine's) ------------

        internal static Vector Lerp(Vector a, Vector b, double t) =>
            new Vector(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

        internal static Point Mid(Point a, Point b) => new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);

        internal static double Dist2(Point a, Point b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        internal static Vector Unit(Vector v)
        {
            double len = v.Length;
            return len > 0 ? v / len : default;
        }

        /// <summary>pf's `per`: the vector rotated a quarter turn, (y, −x).</summary>
        internal static Vector Perp(Vector v) => new Vector(v.Y, -v.X);

        internal static Point RotateAround(Point a, Point center, double radians)
        {
            double s = Math.Sin(radians), c = Math.Cos(radians);
            double px = a.X - center.X, py = a.Y - center.Y;
            return new Point(px * c - py * s + center.X, px * s + py * c + center.Y);
        }
    }
}
