using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    /// <summary>
    /// The arrow look shared by <see cref="GraphicArrow"/> and the numeric step badge's pointer: a
    /// filled shaft that tapers from a thin round tail to a heavy end, and a swept-back head
    /// (concave rear notch) whose corners are softened by a round-joined outline in the same
    /// color. A dashed arrow keeps a constant-width dashed shaft — a tapered fill cannot carry a
    /// dash pattern. Callers cache the two geometries <see cref="Build"/> returns; nothing here
    /// holds state.
    /// </summary>
    internal static class ArrowShape
    {
        // head length as a multiple of the stroke, with a floor so thin arrows still get a
        // readable head, and a cap relative to the path so short arrows are not all head
        private const double HeadLengthFactor = 5;
        private const double MinHeadLength = 14;
        private const double MaxHeadFraction = 0.45;

        // half-width of the head relative to its length (~32° half angle)
        private const double HeadAspect = 0.62;

        // how far the rear notch is pushed toward the tip, as a fraction of head length
        private const double NotchFraction = 0.22;

        // the shaft runs this far (fraction of head length) past the notch into the head, so the
        // antialiased seam between the two fills is buried inside the head
        private const double ShaftOverlapFraction = 0.4;

        // the shaft is drawn heavier than the nominal stroke (the taper removes a lot of ink, so a
        // literal width reads much thinner than a plain line of the same setting), widening from a
        // tail of TailWidthFactor·stroke to ShaftWidthFactor·stroke at the head
        private const double ShaftWidthFactor = 1.5;
        private const double TailWidthFactor = 0.6;

        // fills narrower than ~2px "rope" under coverage antialiasing (on shallow angles each pixel
        // column gets a different partial coverage, so the line reads as a dotted, uneven thread),
        // so the tail never gets thinner than this, in canvas units
        private const double MinTailWidth = 2;

        // samples along the shaft; the taper outline is a polygon through them. A straight shaft
        // needs them too, because the ease-out width profile is not linear along its length.
        private const int CurvedSpineSamples = 24;
        private const int StraightSpineSamples = 12;

        /// <summary>
        /// Builds the head and the shaft (null when the arrow is too short to have one) of an arrow
        /// from <paramref name="start"/> to <paramref name="end"/>, optionally bowed through the
        /// quadratic <paramref name="control"/> point. <paramref name="tapered"/> false keeps the
        /// solid shaft at full weight all the way to its start — for an arrow growing out of
        /// another shape, where a thin tail would meet that shape at its thinnest point.
        /// </summary>
        public static void Build(Point start, Point end, Point? control, double width, bool dashed,
                                 out Geometry shaft, out Geometry head, bool tapered = true)
        {
            // path length (arc length when curved) and the arrival direction at LineEnd
            double pathLength;
            Vector direction;
            Span<double> cumulative = stackalloc double[GraphicLine.CurveSampleCount + 1];
            if (control is { } c)
            {
                cumulative[0] = 0;
                var previous = start;
                for (int i = 1; i <= GraphicLine.CurveSampleCount; i++)
                {
                    var sample = GraphicLine.EvalQuadratic(start, c, end, (double)i / GraphicLine.CurveSampleCount);
                    cumulative[i] = cumulative[i - 1] + GraphicLine.Distance(previous, sample);
                    previous = sample;
                }

                pathLength = cumulative[GraphicLine.CurveSampleCount];
                direction = new Vector(end.X - c.X, end.Y - c.Y); // B'(1) ∥ end - control
            }
            else
            {
                pathLength = GraphicLine.Distance(start, end);
                direction = new Vector(end.X - start.X, end.Y - start.Y);
            }

            direction = direction.Length > 0 ? direction.Normalize() : new Vector(1, 0);
            var normal = new Vector(-direction.Y, direction.X);

            var headLength = Math.Min(pathLength * MaxHeadFraction, Math.Max(width * HeadLengthFactor, MinHeadLength));
            var halfWidth = headLength * HeadAspect;

            // the corner outline grows the head by half its thickness; pull the tip back so the
            // rounded point still lands exactly on the end point
            var tipInset = (CornerRounding(width) / 2) / Math.Sin(Math.Atan2(halfWidth, headLength));
            var tip = end - direction * tipInset;
            var baseCenter = tip - direction * headLength;

            head = BuildHead(tip, baseCenter, direction, normal, halfWidth, headLength);

            // a filled shaft runs into the head; a dashed stroke must stop at the head's rear
            var shaftLength = dashed
                ? pathLength - tipInset - headLength
                : pathLength - tipInset - headLength * (1 - NotchFraction - ShaftOverlapFraction);
            shaft = null;

            if (shaftLength <= 0)
                return;

            if (control is { } ctl)
            {
                // de Casteljau split at tEnd — the t∈[0,tEnd] piece is itself an exact quadratic
                var tEnd = GraphicLine.ParameterAtLength(cumulative, shaftLength);
                var q1 = GraphicLine.Lerp(start, ctl, tEnd);
                var q2 = GraphicLine.Lerp(q1, GraphicLine.Lerp(ctl, end, tEnd), tEnd);
                shaft = dashed ? GraphicLine.BuildQuadratic(start, q1, q2) : BuildCurvedTaper(start, q1, q2, width, tapered);
            }
            else
            {
                var shaftEnd = start + direction * shaftLength;
                shaft = dashed ? new LineGeometry(start, shaftEnd) : BuildStraightTaper(start, shaftEnd, direction, width, tapered);
            }
        }

        /// <summary>Width of a solid shaft at full weight (an untapered shaft is this wide throughout),
        /// for shapes that should read as the same weight as an arrow of <paramref name="width"/>.</summary>
        public static double StemWidth(double width) => Math.Max(MinTailWidth, width * ShaftWidthFactor);

        /// <summary>Render bounds of the parts <see cref="Build"/> produced.</summary>
        public static Rect GetBounds(Geometry shaft, Geometry head, double width, ImmutableDashStyle dash)
        {
            var bounds = head.GetRenderBounds(CornerPen(default, width));
            if (shaft != null)
                bounds = bounds.Union(dash != null ? shaft.GetRenderBounds(DashedShaftPen(default, width, dash)) : shaft.Bounds);
            return bounds;
        }

        public static void Draw(DrawingContext ctx, Geometry shaft, Geometry head, Color color, double width, ImmutableDashStyle dash)
        {
            var brush = RenderResources.GetBrush(color);

            if (shaft != null)
            {
                if (dash != null)
                    ctx.DrawGeometry(null, DashedShaftPen(color, width, dash), shaft);
                else
                    ctx.DrawGeometry(brush, null, shaft);
            }

            ctx.DrawGeometry(brush, CornerPen(color, width), head);
        }

        private static ImmutablePen CornerPen(Color color, double width) =>
            RenderResources.GetPen(color, CornerRounding(width), null, PenLineCap.Round, PenLineJoin.Round);

        private static ImmutablePen DashedShaftPen(Color color, double width, ImmutableDashStyle dash) =>
            RenderResources.GetPen(color, width, dash, PenLineCap.Round);

        // thickness of the round-joined outline that softens the head's corners
        private static double CornerRounding(double width) => Math.Max(1, width * 0.7);

        private static Geometry BuildHead(Point tip, Point baseCenter, Vector direction, Vector normal, double halfWidth, double headLength)
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

            return geometry;
        }

        private static Geometry BuildStraightTaper(Point start, Point end, Vector direction, double width, bool tapered)
        {
            var spine = new Point[StraightSpineSamples + 1];
            var tangents = new Vector[StraightSpineSamples + 1];
            for (int i = 0; i <= StraightSpineSamples; i++)
            {
                spine[i] = GraphicLine.Lerp(start, end, (double)i / StraightSpineSamples);
                tangents[i] = direction;
            }

            return BuildTaper(spine, tangents, width, tapered);
        }

        // the curved shaft is the quadratic start→q1→q2 (already split at the shaft's end); sample
        // it with its tangents and outline the taper through the samples
        private static Geometry BuildCurvedTaper(Point start, Point q1, Point q2, double width, bool tapered)
        {
            var spine = new Point[CurvedSpineSamples + 1];
            var tangents = new Vector[CurvedSpineSamples + 1];
            for (int i = 0; i <= CurvedSpineSamples; i++)
            {
                var t = (double)i / CurvedSpineSamples;
                spine[i] = GraphicLine.EvalQuadratic(start, q1, q2, t);

                // B'(t) = 2[(1-t)(q1-start) + t(q2-q1)]
                var d = new Vector((1 - t) * (q1.X - start.X) + t * (q2.X - q1.X),
                                   (1 - t) * (q1.Y - start.Y) + t * (q2.Y - q1.Y));
                tangents[i] = d.Length > 0 ? d.Normalize() : (i > 0 ? tangents[i - 1] : new Vector(1, 0));
            }

            return BuildTaper(spine, tangents, width, tapered);
        }

        /// <summary>
        /// Filled outline of a stroke whose width grows (by arc length, ease-out) from
        /// TailWidthFactor·width at spine[0] to ShaftWidthFactor·width at the last point, with a
        /// semicircular tail cap. The head covers the wide end, so it is left square. Ease-out
        /// keeps the thin stretch short: most of the shaft is near full weight and the taper reads
        /// as a brush flick at the tail rather than a long hairline.
        /// </summary>
        private static Geometry BuildTaper(Point[] spine, Vector[] tangents, double width, bool tapered)
        {
            int n = spine.Length;
            var along = new double[n];
            for (int i = 1; i < n; i++)
                along[i] = along[i - 1] + GraphicLine.Distance(spine[i - 1], spine[i]);
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
                g.BeginFigure(spine[0] + NormalAt(0) * HalfAt(0), true);
                for (int i = 1; i < n; i++)
                    g.LineTo(spine[i] + NormalAt(i) * HalfAt(i));
                for (int i = n - 1; i >= 0; i--)
                    g.LineTo(spine[i] - NormalAt(i) * HalfAt(i));

                // tail cap: sweep from the right side, behind the start, back to the left side
                var nrm = NormalAt(0);
                var back = -tangents[0];
                for (int k = 1; k < capSegments; k++)
                {
                    var a = Math.PI * k / capSegments;
                    g.LineTo(spine[0] + (-nrm * Math.Cos(a) + back * Math.Sin(a)) * tailHalf);
                }

                g.EndFigure(true);
            }

            return geometry;
        }
    }
}
