using System;

namespace Clowd.Geometry2D
{
    /// <summary>
    /// How a line's mid handle bows it into a quadratic, in the image editor's terms
    /// (<c>GraphicLine</c>): the bend is an <c>offset</c> along the chord's left normal (canvas
    /// space, y down) and a <c>skew</c> that leans it along the chord, as a fraction of the chord.
    /// The handle rides on the ink at t = 0.5, not on the control point.
    /// </summary>
    public static class CurveBend
    {
        /// <summary>How far the mid handle may slide either side of the chord midpoint, as a
        /// fraction of the chord: enough for a lopsided bend without letting the curve fold back
        /// on itself.</summary>
        public const double MaxSkew = 0.25;

        /// <summary>Shift-drag quantizes the bow to multiples of this fraction of the chord (and
        /// drops the skew), so a bend is easy to straighten exactly or repeat.</summary>
        public const double SnappedOffsetStep = 0.1;

        /// <summary>
        /// The bezier control point implied by a bend: chordMid + 2·(offset·normal +
        /// skew·length·tangent). The factor 2 is the quadratic's on-curve/control relation (B(0.5)
        /// lands halfway between the chord midpoint and the control point), so the curve's own
        /// midpoint lands exactly at the offset/skew position. False means straight: offset 0, or a
        /// zero-length chord that has no normal.
        /// </summary>
        public static bool TryControlPoint(Vec2 s, Vec2 e, double offset, double skew, out Vec2 control)
        {
            if (offset == 0 || !TryChordNormal(s, e, out var normal))
            {
                control = default;
                return false;
            }

            var mid = ChordMidpoint(s, e);
            var along = skew * QuadraticCurve.Distance(s, e);
            control = new Vec2(mid.X + 2 * (offset * normal.X + along * normal.Y),
                               mid.Y + 2 * (offset * normal.Y - along * normal.X));
            return true;
        }

        /// <summary>The mid handle's position: the curve's t = 0.5 point when bowed, else the
        /// chord midpoint.</summary>
        public static Vec2 HandlePoint(Vec2 s, Vec2 e, double offset, double skew) =>
            TryControlPoint(s, e, offset, skew, out var control)
                ? QuadraticCurve.Eval(s, control, e, 0.5)
                : ChordMidpoint(s, e);

        /// <summary>
        /// The bend that puts the mid handle under <paramref name="pointer"/>: the pointer's offset
        /// from the chord midpoint projected onto the normal and the chord. <paramref name="snap"/>
        /// (Shift) rounds the offset to <see cref="SnappedOffsetStep"/> chords and drops the skew;
        /// otherwise the skew is clamped to ±<see cref="MaxSkew"/>. An offset within
        /// <paramref name="straightSnapDistance"/> of the chord snaps to exactly straight, so a
        /// bowed line can be restored by hand, and a straight line always carries skew 0. False
        /// for a zero-length chord (nothing to bow around): the outputs are then 0 and the caller
        /// keeps the bend it had.
        /// </summary>
        public static bool TryBendFromPointer(Vec2 s, Vec2 e, Vec2 pointer, bool snap, double straightSnapDistance,
                                              out double offset, out double skew)
        {
            offset = 0;
            skew = 0;
            if (!TryChordNormal(s, e, out var normal))
                return false;

            var length = QuadraticCurve.Distance(s, e);
            var tangent = new Vec2(normal.Y, -normal.X);
            var mid = ChordMidpoint(s, e);
            var dx = pointer.X - mid.X;
            var dy = pointer.Y - mid.Y;
            var off = dx * normal.X + dy * normal.Y;
            var sk = (dx * tangent.X + dy * tangent.Y) / length;

            if (snap)
            {
                var step = SnappedOffsetStep * length;
                off = Math.Round(off / step) * step;
                sk = 0;
            }
            else
            {
                sk = Math.Clamp(sk, -MaxSkew, MaxSkew);
            }

            if (Math.Abs(off) < straightSnapDistance)
                off = 0;

            if (off == 0)
                sk = 0;

            offset = off;
            skew = sk;
            return true;
        }

        private static Vec2 ChordMidpoint(Vec2 s, Vec2 e) => new Vec2((s.X + e.X) / 2, (s.Y + e.Y) / 2);

        private static bool TryChordNormal(Vec2 s, Vec2 e, out Vec2 normal)
        {
            var chord = new Vec2(e.X - s.X, e.Y - s.Y);
            var length = chord.Length;
            if (length <= 0)
            {
                normal = default;
                return false;
            }

            normal = new Vec2(-chord.Y / length, chord.X / length);
            return true;
        }
    }
}
