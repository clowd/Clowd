using System;

namespace Prisc.Geometry2D
{
    /// <summary>How an arrow's shaft is drawn.</summary>
    public enum ArrowShaftKind
    {
        /// <summary>No shaft: the arrow is too short for one, the head is all there is.</summary>
        None,

        /// <summary>A filled outline (<see cref="ArrowParts.ShaftOutline"/>).</summary>
        Taper,

        /// <summary>A stroked spine at <see cref="ArrowParts.SpineStrokeWidth"/>, round caps,
        /// carrying the dash (a tapered fill cannot).</summary>
        Spine,
    }

    /// <summary>
    /// The pieces of one arrow, as points. Each renderer turns them into its own paths: fill the
    /// head polygon(s) and stroke them at <see cref="CornerStrokeWidth"/> with round joins and caps
    /// in the same colour (that softens the corners), then fill the taper or stroke the spine.
    /// </summary>
    public sealed class ArrowParts
    {
        // the end head, in figure order
        public Vec2 Tip, HeadLeft, HeadNotch, HeadRight;

        // the start head, only from BuildDouble
        public bool HasStartHead;
        public Vec2 StartTip, StartLeft, StartNotch, StartRight;

        public ArrowShaftKind ShaftKind;

        /// <summary>Taper: the closed outline, in figure order: the left side from the tail, the
        /// right side back to the tail, then the tail cap.</summary>
        public Vec2[] ShaftOutline;

        /// <summary>Spine: a line from <see cref="SpineStart"/> to <see cref="SpineEnd"/>, or a
        /// quadratic through <see cref="SpineControl"/> when it is set.</summary>
        public Vec2 SpineStart, SpineEnd;
        public Vec2? SpineControl;

        /// <summary>Width of the round-joined outline stroked over the head(s).</summary>
        public double CornerStrokeWidth;

        /// <summary>Width of the dashed spine (the nominal stroke).</summary>
        public double SpineStrokeWidth;
    }

    /// <summary>
    /// The arrow look shared by the image editor's arrow, its step badge's pointer and the video
    /// editor's arrow shapes: a filled shaft that tapers from a thin round tail to a heavy end, and
    /// a swept-back head (concave rear notch) whose corners are softened by a round-joined outline
    /// in the same colour. A dashed arrow keeps a constant-width dashed spine, because a tapered
    /// fill cannot carry a dash pattern.
    /// </summary>
    /// <remarks>
    /// <see cref="Build"/> is the image editor's builder moved here unchanged, expression for
    /// expression, and <c>ArrowGeometryParityTests</c> holds it to the old points exactly. Keep the
    /// operand order of every expression when editing it.
    /// </remarks>
    public static class ArrowGeometry
    {
        // head length as a multiple of the stroke, with a floor so thin arrows still get a
        // readable head, and a cap relative to the path so short arrows are not all head
        public const double HeadLengthFactor = 5;
        public const double MinHeadLength = 14;
        public const double MaxHeadFraction = 0.45;

        /// <summary>The cap for each head of a double arrow: the two heads share the path.</summary>
        public const double DoubleMaxHeadFraction = 0.3;

        // half-width of the head relative to its length (~32° half angle)
        public const double HeadAspect = 0.62;

        // how far the rear notch is pushed toward the tip, as a fraction of head length
        public const double NotchFraction = 0.22;

        // the shaft runs this far (fraction of head length) past the notch into the head, so the
        // antialiased seam between the two fills is buried inside the head
        public const double ShaftOverlapFraction = 0.4;

        // the shaft is drawn heavier than the nominal stroke (the taper removes a lot of ink, so a
        // literal width reads much thinner than a plain line of the same setting), widening from a
        // tail of TailWidthFactor·stroke to ShaftWidthFactor·stroke at the head
        public const double ShaftWidthFactor = 1.5;
        public const double TailWidthFactor = 0.6;

        // fills narrower than ~2px "rope" under coverage antialiasing (on shallow angles each pixel
        // column gets a different partial coverage, so the line reads as a dotted, uneven thread),
        // so the tail never gets thinner than this, in canvas units
        public const double MinTailWidth = 2;

        // samples along the shaft; the taper outline is a polygon through them. A straight shaft
        // needs them too, because the ease-out width profile is not linear along its length.
        public const int CurvedSpineSamples = 24;
        public const int StraightSpineSamples = 12;

        // segments of the semicircular tail cap
        public const int TailCapSegments = 8;

        /// <summary>
        /// Builds an arrow from <paramref name="start"/> to <paramref name="end"/>, optionally bowed
        /// through the quadratic <paramref name="control"/> point. <paramref name="tapered"/> false
        /// keeps the solid shaft at full weight all the way to its start, for an arrow growing out
        /// of another shape, where a thin tail would meet that shape at its thinnest point.
        /// <paramref name="runout"/> is for an arrow cut from a longer bowed path (one being drawn
        /// on): how far that path runs on past <paramref name="end"/>, see <see cref="AimHead"/>.
        /// </summary>
        public static ArrowParts Build(Vec2 start, Vec2 end, Vec2? control, double width, bool dashed, bool tapered = true,
                                       double runout = 0)
        {
            var parts = new ArrowParts { CornerStrokeWidth = CornerRounding(width), SpineStrokeWidth = width };

            // path length (arc length when curved) and the arrival direction at the end
            double pathLength;
            Vec2 direction;
            Span<double> cumulative = stackalloc double[QuadraticCurve.SampleCount + 1];
            if (control is { } c)
            {
                pathLength = QuadraticCurve.Cumulative(start, c, end, cumulative);
                direction = new Vec2(end.X - c.X, end.Y - c.Y); // B'(1) ∥ end - control
            }
            else
            {
                pathLength = QuadraticCurve.Distance(start, end);
                direction = new Vec2(end.X - start.X, end.Y - start.Y);
            }

            direction = direction.Length > 0 ? direction.Normalize() : new Vec2(1, 0);

            var headLength = Math.Min(pathLength * MaxHeadFraction, Math.Max(width * HeadLengthFactor, MinHeadLength));
            if (runout > 0 && control is { } bow)
                direction = AimHead(direction, end, QuadraticCurve.Eval(start, bow, end,
                    QuadraticCurve.ParameterAtLength(cumulative, pathLength - headLength)), headLength, runout);

            var tipInset = PlaceHead(end, direction, headLength, width, out parts.Tip, out parts.HeadLeft,
                                     out parts.HeadNotch, out parts.HeadRight);

            // a filled shaft runs into the head; a dashed stroke must stop at the head's rear
            var shaftLength = dashed
                ? pathLength - tipInset - headLength
                : pathLength - tipInset - headLength * (1 - NotchFraction - ShaftOverlapFraction);

            if (shaftLength <= 0)
                return parts;

            if (control is { } ctl)
            {
                // the t∈[0,tEnd] piece of the curve is itself an exact quadratic
                var tEnd = QuadraticCurve.ParameterAtLength(cumulative, shaftLength);
                QuadraticCurve.Sub(start, ctl, end, 0, tEnd, out _, out var q1, out var q2);
                if (dashed)
                    SetSpine(parts, start, q1, q2);
                else
                    SetTaper(parts, BuildCurvedTaper(start, q1, q2, width, tapered));
            }
            else
            {
                var shaftEnd = start + direction * shaftLength;
                if (dashed)
                    SetSpine(parts, start, null, shaftEnd);
                else
                    SetTaper(parts, BuildStraightTaper(start, shaftEnd, direction, width, tapered));
            }

            return parts;
        }

        /// <summary>
        /// Builds an arrow with a head at both ends. Each head is capped at
        /// <see cref="DoubleMaxHeadFraction"/> of the path, the start head points back along the
        /// curve's start tangent, and the solid shaft runs untapered between the heads (overlapping
        /// into each as <see cref="Build"/>'s does), while a dashed spine stops at each head's rear.
        /// <paramref name="startRunout"/> and <paramref name="endRunout"/> are how far a longer bowed
        /// path this one is cut from runs on past each end, see <see cref="AimHead"/>.
        /// </summary>
        public static ArrowParts BuildDouble(Vec2 start, Vec2 end, Vec2? control, double width, bool dashed,
                                             double startRunout = 0, double endRunout = 0)
        {
            var parts = new ArrowParts { CornerStrokeWidth = CornerRounding(width), SpineStrokeWidth = width, HasStartHead = true };

            double pathLength;
            Vec2 endDirection, startDirection;
            Span<double> cumulative = stackalloc double[QuadraticCurve.SampleCount + 1];
            if (control is { } c)
            {
                pathLength = QuadraticCurve.Cumulative(start, c, end, cumulative);
                endDirection = new Vec2(end.X - c.X, end.Y - c.Y);     // B'(1) ∥ end - control
                startDirection = new Vec2(start.X - c.X, start.Y - c.Y); // -B'(0) ∥ start - control
            }
            else
            {
                pathLength = QuadraticCurve.Distance(start, end);
                endDirection = new Vec2(end.X - start.X, end.Y - start.Y);
                startDirection = new Vec2(start.X - end.X, start.Y - end.Y);
            }

            endDirection = endDirection.Length > 0 ? endDirection.Normalize() : new Vec2(1, 0);
            startDirection = startDirection.Length > 0 ? startDirection.Normalize() : -endDirection;

            var headLength = Math.Min(pathLength * DoubleMaxHeadFraction, Math.Max(width * HeadLengthFactor, MinHeadLength));
            if (control is { } bow)
            {
                if (endRunout > 0)
                    endDirection = AimHead(endDirection, end, QuadraticCurve.Eval(start, bow, end,
                        QuadraticCurve.ParameterAtLength(cumulative, pathLength - headLength)), headLength, endRunout);
                if (startRunout > 0)
                    startDirection = AimHead(startDirection, start, QuadraticCurve.Eval(start, bow, end,
                        QuadraticCurve.ParameterAtLength(cumulative, headLength)), headLength, startRunout);
            }

            var tipInset = PlaceHead(end, endDirection, headLength, width, out parts.Tip, out parts.HeadLeft,
                                     out parts.HeadNotch, out parts.HeadRight);
            PlaceHead(start, startDirection, headLength, width, out parts.StartTip, out parts.StartLeft,
                      out parts.StartNotch, out parts.StartRight);

            // both heads are the same size, so the shaft is cut back by the same length at each end
            var inset = dashed
                ? tipInset + headLength
                : tipInset + headLength * (1 - NotchFraction - ShaftOverlapFraction);
            var from = inset;
            var to = pathLength - inset;

            if (to - from <= 0)
                return parts;

            if (control is { } ctl)
            {
                var t0 = QuadraticCurve.ParameterAtLength(cumulative, from);
                var t1 = QuadraticCurve.ParameterAtLength(cumulative, to);
                QuadraticCurve.Sub(start, ctl, end, t0, t1, out var s1, out var c1, out var e1);
                if (dashed)
                    SetSpine(parts, s1, c1, e1);
                else
                    SetTaper(parts, BuildCurvedTaper(s1, c1, e1, width, tapered: false));
            }
            else
            {
                var shaftStart = start + endDirection * from;
                var shaftEnd = start + endDirection * to;
                if (dashed)
                    SetSpine(parts, shaftStart, null, shaftEnd);
                else
                    SetTaper(parts, BuildStraightTaper(shaftStart, shaftEnd, endDirection, width, tapered: false));
            }

            return parts;
        }

        /// <summary>Width of a solid shaft at full weight (an untapered shaft is this wide throughout),
        /// for shapes that should read as the same weight as an arrow of <paramref name="width"/>.</summary>
        public static double StemWidth(double width) => Math.Max(MinTailWidth, width * ShaftWidthFactor);

        /// <summary>Thickness of the round-joined outline that softens the head's corners.</summary>
        public static double CornerRounding(double width) => Math.Max(1, width * 0.7);

        /// <summary>An upper bound on how far an arrow's ink reaches from its spine: the widest
        /// head's half-width plus half its corner outline. It ignores the cap relative to the path,
        /// so it is never below the real reach; callers pad boxes with it.</summary>
        public static double MaxInkRadius(double width) =>
            Math.Max(width * HeadLengthFactor, MinHeadLength) * HeadAspect + CornerRounding(width) / 2;

        /// <summary>
        /// The direction for a head on an arrow cut out of a longer bowed path, which runs on
        /// <paramref name="runout"/> past <paramref name="end"/>. A head lies straight back along
        /// its arrival <paramref name="tangent"/>, which suits a finished arrow, whose ends are
        /// where a quadratic turns least; a cut can fall where it turns most, and there the shaft
        /// meets the head well off its centre line. Such a head aims along the chord of the arc it
        /// covers (from <paramref name="back"/>, one head length back along the curve, to the
        /// end), easing into the tangent over the last head length of runout so a head reaching
        /// the path's real end arrives exactly as the finished arrow's.
        /// </summary>
        private static Vec2 AimHead(Vec2 tangent, Vec2 end, Vec2 back, double headLength, double runout)
        {
            var chord = end - back;
            if (chord.Length <= 0)
                return tangent;

            var weight = Math.Min(1, runout / headLength);
            var aim = tangent * (1 - weight) + chord.Normalize() * weight;
            return aim.Length > 0 ? aim.Normalize() : tangent;
        }

        // places a head whose rounded point lands on `end`, arriving along `direction`, and returns
        // how far the polygon's tip is pulled back from `end`
        private static double PlaceHead(Vec2 end, Vec2 direction, double headLength, double width,
                                        out Vec2 tip, out Vec2 left, out Vec2 notch, out Vec2 right)
        {
            var normal = new Vec2(-direction.Y, direction.X);
            var halfWidth = headLength * HeadAspect;

            // the corner outline grows the head by half its thickness; pull the tip back so the
            // rounded point still lands exactly on the end point
            var tipInset = (CornerRounding(width) / 2) / Math.Sin(Math.Atan2(halfWidth, headLength));
            tip = end - direction * tipInset;
            var baseCenter = tip - direction * headLength;

            left = baseCenter + normal * halfWidth;
            right = baseCenter - normal * halfWidth;
            notch = baseCenter + direction * (headLength * NotchFraction);
            return tipInset;
        }

        private static void SetSpine(ArrowParts parts, Vec2 start, Vec2? control, Vec2 end)
        {
            parts.ShaftKind = ArrowShaftKind.Spine;
            parts.SpineStart = start;
            parts.SpineControl = control;
            parts.SpineEnd = end;
        }

        private static void SetTaper(ArrowParts parts, Vec2[] outline)
        {
            parts.ShaftKind = ArrowShaftKind.Taper;
            parts.ShaftOutline = outline;
        }

        private static Vec2[] BuildStraightTaper(Vec2 start, Vec2 end, Vec2 direction, double width, bool tapered)
        {
            var spine = new Vec2[StraightSpineSamples + 1];
            var tangents = new Vec2[StraightSpineSamples + 1];
            for (int i = 0; i <= StraightSpineSamples; i++)
            {
                spine[i] = QuadraticCurve.Lerp(start, end, (double)i / StraightSpineSamples);
                tangents[i] = direction;
            }

            return BuildTaper(spine, tangents, width, tapered);
        }

        // the curved shaft is the quadratic start→q1→q2 (already cut to the shaft's length); sample
        // it with its tangents and outline the taper through the samples
        private static Vec2[] BuildCurvedTaper(Vec2 start, Vec2 q1, Vec2 q2, double width, bool tapered)
        {
            var spine = new Vec2[CurvedSpineSamples + 1];
            var tangents = new Vec2[CurvedSpineSamples + 1];
            for (int i = 0; i <= CurvedSpineSamples; i++)
            {
                var t = (double)i / CurvedSpineSamples;
                spine[i] = QuadraticCurve.Eval(start, q1, q2, t);

                // B'(t) = 2[(1-t)(q1-start) + t(q2-q1)]
                var d = new Vec2((1 - t) * (q1.X - start.X) + t * (q2.X - q1.X),
                                 (1 - t) * (q1.Y - start.Y) + t * (q2.Y - q1.Y));
                tangents[i] = d.Length > 0 ? d.Normalize() : (i > 0 ? tangents[i - 1] : new Vec2(1, 0));
            }

            return BuildTaper(spine, tangents, width, tapered);
        }

        /// <summary>
        /// Outline of a stroke whose width grows (by arc length, ease-out) from
        /// TailWidthFactor·width at spine[0] to ShaftWidthFactor·width at the last point, with a
        /// semicircular tail cap. The head covers the wide end, so it is left square. Ease-out
        /// keeps the thin stretch short: most of the shaft is near full weight and the taper reads
        /// as a brush flick at the tail rather than a long hairline.
        /// </summary>
        private static Vec2[] BuildTaper(Vec2[] spine, Vec2[] tangents, double width, bool tapered)
        {
            int n = spine.Length;
            var along = new double[n];
            for (int i = 1; i < n; i++)
                along[i] = along[i - 1] + QuadraticCurve.Distance(spine[i - 1], spine[i]);
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

            Vec2 NormalAt(int i) => new Vec2(-tangents[i].Y, tangents[i].X);

            var outline = new Vec2[2 * n + TailCapSegments - 1];
            int o = 0;
            for (int i = 0; i < n; i++)
                outline[o++] = spine[i] + NormalAt(i) * HalfAt(i);
            for (int i = n - 1; i >= 0; i--)
                outline[o++] = spine[i] - NormalAt(i) * HalfAt(i);

            // tail cap: sweep from the right side, behind the start, back to the left side
            var nrm = NormalAt(0);
            var back = -tangents[0];
            for (int k = 1; k < TailCapSegments; k++)
            {
                var a = Math.PI * k / TailCapSegments;
                outline[o++] = spine[0] + (-nrm * Math.Cos(a) + back * Math.Sin(a)) * tailHalf;
            }

            return outline;
        }
    }
}
