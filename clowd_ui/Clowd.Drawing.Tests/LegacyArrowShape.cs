using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace Clowd.Drawing.Tests
{
    /// <summary>The points one <see cref="LegacyArrowShape.Build"/> call emitted, in figure order.</summary>
    internal sealed class LegacyArrowPoints
    {
        public enum ShaftKind { None, Taper, Spine }

        /// <summary>The head figure: tip, left, notch, right.</summary>
        public readonly List<Point> Head = new List<Point>();

        public ShaftKind Shaft;

        /// <summary>The taper figure (left side, right side reversed, tail cap), when <see cref="Shaft"/> is Taper.</summary>
        public readonly List<Point> ShaftOutline = new List<Point>();

        // the dashed shaft, when Shaft is Spine
        public Point SpineStart, SpineEnd;
        public Point? SpineControl;

        public double CornerStrokeWidth;
        public double SpineStrokeWidth;
    }

    /// <summary>
    /// The permanent oracle for the arrow geometry: a verbatim copy of <c>ArrowShape.Build</c> and
    /// its builders as they were before the maths moved to <c>Clowd.Geometry</c>, changed only so
    /// that it also returns the points it emits. The quadratic helpers it used from
    /// <c>GraphicLine</c> are inlined, so it stays the old maths whatever later happens to them.
    /// Do not "fix" anything here: it is the reference the shared core is held to.
    /// </summary>
    internal static class LegacyArrowShape
    {
        private const double HeadLengthFactor = 5;
        private const double MinHeadLength = 14;
        private const double MaxHeadFraction = 0.45;
        private const double HeadAspect = 0.62;
        private const double NotchFraction = 0.22;
        private const double ShaftOverlapFraction = 0.4;
        private const double ShaftWidthFactor = 1.5;
        private const double TailWidthFactor = 0.6;
        private const double MinTailWidth = 2;
        private const int CurvedSpineSamples = 24;
        private const int StraightSpineSamples = 12;
        private const int CurveSampleCount = 32;

        public static LegacyArrowPoints Build(Point start, Point end, Point? control, double width, bool dashed,
                                              out Geometry shaft, out Geometry head, bool tapered = true)
        {
            var points = new LegacyArrowPoints { CornerStrokeWidth = CornerRounding(width), SpineStrokeWidth = width };

            // path length (arc length when curved) and the arrival direction at LineEnd
            double pathLength;
            Vector direction;
            Span<double> cumulative = stackalloc double[CurveSampleCount + 1];
            if (control is { } c)
            {
                cumulative[0] = 0;
                var previous = start;
                for (int i = 1; i <= CurveSampleCount; i++)
                {
                    var sample = EvalQuadratic(start, c, end, (double)i / CurveSampleCount);
                    cumulative[i] = cumulative[i - 1] + Distance(previous, sample);
                    previous = sample;
                }

                pathLength = cumulative[CurveSampleCount];
                direction = new Vector(end.X - c.X, end.Y - c.Y); // B'(1) ∥ end - control
            }
            else
            {
                pathLength = Distance(start, end);
                direction = new Vector(end.X - start.X, end.Y - start.Y);
            }

            direction = direction.Length > 0 ? direction.Normalize() : new Vector(1, 0);
            var normal = new Vector(-direction.Y, direction.X);

            var headLength = Math.Min(pathLength * MaxHeadFraction, Math.Max(width * HeadLengthFactor, MinHeadLength));
            var halfWidth = headLength * HeadAspect;

            var tipInset = (CornerRounding(width) / 2) / Math.Sin(Math.Atan2(halfWidth, headLength));
            var tip = end - direction * tipInset;
            var baseCenter = tip - direction * headLength;

            head = BuildHead(tip, baseCenter, direction, normal, halfWidth, headLength, points.Head);

            var shaftLength = dashed
                ? pathLength - tipInset - headLength
                : pathLength - tipInset - headLength * (1 - NotchFraction - ShaftOverlapFraction);
            shaft = null;

            if (shaftLength <= 0)
                return points;

            if (control is { } ctl)
            {
                var tEnd = ParameterAtLength(cumulative, shaftLength);
                var q1 = Lerp(start, ctl, tEnd);
                var q2 = Lerp(q1, Lerp(ctl, end, tEnd), tEnd);
                if (dashed)
                {
                    shaft = BuildQuadratic(start, q1, q2);
                    points.Shaft = LegacyArrowPoints.ShaftKind.Spine;
                    points.SpineStart = start;
                    points.SpineControl = q1;
                    points.SpineEnd = q2;
                }
                else
                {
                    shaft = BuildCurvedTaper(start, q1, q2, width, tapered, points.ShaftOutline);
                    points.Shaft = LegacyArrowPoints.ShaftKind.Taper;
                }
            }
            else
            {
                var shaftEnd = start + direction * shaftLength;
                if (dashed)
                {
                    shaft = new LineGeometry(start, shaftEnd);
                    points.Shaft = LegacyArrowPoints.ShaftKind.Spine;
                    points.SpineStart = start;
                    points.SpineEnd = shaftEnd;
                }
                else
                {
                    shaft = BuildStraightTaper(start, shaftEnd, direction, width, tapered, points.ShaftOutline);
                    points.Shaft = LegacyArrowPoints.ShaftKind.Taper;
                }
            }

            return points;
        }

        public static double StemWidth(double width) => Math.Max(MinTailWidth, width * ShaftWidthFactor);

        public static double CornerRounding(double width) => Math.Max(1, width * 0.7);

        private static Geometry BuildHead(Point tip, Point baseCenter, Vector direction, Vector normal, double halfWidth,
                                          double headLength, List<Point> emitted)
        {
            var left = baseCenter + normal * halfWidth;
            var right = baseCenter - normal * halfWidth;
            var notch = baseCenter + direction * (headLength * NotchFraction);

            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(tip, true);
                g.LineTo(left);
                g.LineTo(notch);
                g.LineTo(right);
                g.EndFigure(true);
            }

            emitted.AddRange(new[] { tip, left, notch, right });
            return geometry;
        }

        private static Geometry BuildStraightTaper(Point start, Point end, Vector direction, double width, bool tapered,
                                                   List<Point> emitted)
        {
            var spine = new Point[StraightSpineSamples + 1];
            var tangents = new Vector[StraightSpineSamples + 1];
            for (int i = 0; i <= StraightSpineSamples; i++)
            {
                spine[i] = Lerp(start, end, (double)i / StraightSpineSamples);
                tangents[i] = direction;
            }

            return BuildTaper(spine, tangents, width, tapered, emitted);
        }

        private static Geometry BuildCurvedTaper(Point start, Point q1, Point q2, double width, bool tapered,
                                                 List<Point> emitted)
        {
            var spine = new Point[CurvedSpineSamples + 1];
            var tangents = new Vector[CurvedSpineSamples + 1];
            for (int i = 0; i <= CurvedSpineSamples; i++)
            {
                var t = (double)i / CurvedSpineSamples;
                spine[i] = EvalQuadratic(start, q1, q2, t);

                var d = new Vector((1 - t) * (q1.X - start.X) + t * (q2.X - q1.X),
                                   (1 - t) * (q1.Y - start.Y) + t * (q2.Y - q1.Y));
                tangents[i] = d.Length > 0 ? d.Normalize() : (i > 0 ? tangents[i - 1] : new Vector(1, 0));
            }

            return BuildTaper(spine, tangents, width, tapered, emitted);
        }

        private static Geometry BuildTaper(Point[] spine, Vector[] tangents, double width, bool tapered, List<Point> emitted)
        {
            int n = spine.Length;
            var along = new double[n];
            for (int i = 1; i < n; i++)
                along[i] = along[i - 1] + Distance(spine[i - 1], spine[i]);
            var total = along[n - 1];

            var headHalf = StemWidth(width) / 2;
            var tailHalf = tapered ? Math.Min(headHalf, Math.Max(MinTailWidth, width * TailWidthFactor) / 2) : headHalf;

            double HalfAt(int i)
            {
                if (total <= 0)
                    return headHalf;
                var f = 1 - along[i] / total;
                return tailHalf + (headHalf - tailHalf) * (1 - f * f);
            }

            Vector NormalAt(int i) => new Vector(-tangents[i].Y, tangents[i].X);

            const int capSegments = 8;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                void Emit(Point p, bool first)
                {
                    if (first)
                        g.BeginFigure(p, true);
                    else
                        g.LineTo(p);
                    emitted.Add(p);
                }

                Emit(spine[0] + NormalAt(0) * HalfAt(0), true);
                for (int i = 1; i < n; i++)
                    Emit(spine[i] + NormalAt(i) * HalfAt(i), false);
                for (int i = n - 1; i >= 0; i--)
                    Emit(spine[i] - NormalAt(i) * HalfAt(i), false);

                var nrm = NormalAt(0);
                var back = -tangents[0];
                for (int k = 1; k < capSegments; k++)
                {
                    var a = Math.PI * k / capSegments;
                    Emit(spine[0] + (-nrm * Math.Cos(a) + back * Math.Sin(a)) * tailHalf, false);
                }

                g.EndFigure(true);
            }

            return geometry;
        }

        // GraphicLine's quadratic helpers as they were, inlined

        private static Geometry BuildQuadratic(Point start, Point control, Point end)
        {
            var geometry = new StreamGeometry();
            using (var gctx = geometry.Open())
            {
                gctx.BeginFigure(start, false);
                gctx.QuadraticBezierTo(control, end);
                gctx.EndFigure(false);
            }

            return geometry;
        }

        internal static double ParameterAtLength(ReadOnlySpan<double> cumulative, double length)
        {
            int segments = cumulative.Length - 1;
            for (int i = 1; i <= segments; i++)
            {
                if (cumulative[i] < length)
                    continue;

                var span = cumulative[i] - cumulative[i - 1];
                var fraction = span > 0 ? (length - cumulative[i - 1]) / span : 0;
                return (i - 1 + fraction) / segments;
            }

            return 1;
        }

        private static Point EvalQuadratic(Point start, Point control, Point end, double t)
        {
            var mt = 1 - t;
            var a = mt * mt;
            var b = 2 * mt * t;
            var c = t * t;
            return new Point(a * start.X + b * control.X + c * end.X,
                             a * start.Y + b * control.Y + c * end.Y);
        }

        private static Point Lerp(Point from, Point to, double t) =>
            new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);

        private static double Distance(Point a, Point b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
