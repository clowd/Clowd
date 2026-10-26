using System;

namespace Clowd.Geometry2D
{
    /// <summary>
    /// The quadratic bezier maths behind a bowed line or arrow: evaluation, arc length by sampling,
    /// cutting a piece out, and bounds. The formulas are the image editor's (<c>GraphicLine</c>),
    /// term for term, so both editors land on the same points.
    /// </summary>
    public static class QuadraticCurve
    {
        /// <summary>Segments used to walk the curve when converting between arc length and the
        /// bezier parameter.</summary>
        public const int SampleCount = 32;

        public static Vec2 Eval(Vec2 s, Vec2 c, Vec2 e, double t)
        {
            var mt = 1 - t;
            var a = mt * mt;
            var b = 2 * mt * t;
            var cc = t * t;
            return new Vec2(a * s.X + b * c.X + cc * e.X,
                            a * s.Y + b * c.Y + cc * e.Y);
        }

        public static Vec2 Lerp(Vec2 a, Vec2 b, double t) =>
            new Vec2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

        public static double Distance(Vec2 a, Vec2 b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Bezier parameter at <paramref name="length"/> along the sampled polyline whose
        /// running lengths <see cref="Cumulative"/> filled in.</summary>
        public static double ParameterAtLength(ReadOnlySpan<double> cumulative, double length)
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

        /// <summary>Fills <c>cumulative[0..SampleCount]</c> with the running length of the curve
        /// sampled at <see cref="SampleCount"/> even parameter steps and returns the total arc
        /// length. <paramref name="cumulative"/> must hold at least <c>SampleCount + 1</c> values.</summary>
        public static double Cumulative(Vec2 s, Vec2 c, Vec2 e, Span<double> cumulative)
        {
            cumulative[0] = 0;
            var previous = s;
            for (int i = 1; i <= SampleCount; i++)
            {
                var sample = Eval(s, c, e, (double)i / SampleCount);
                cumulative[i] = cumulative[i - 1] + Distance(previous, sample);
                previous = sample;
            }

            return cumulative[SampleCount];
        }

        /// <summary>
        /// The piece of the curve between parameters <paramref name="t0"/> and
        /// <paramref name="t1"/> (<c>t0 &lt; t1</c>), which is itself an exact quadratic: a de
        /// Casteljau split at <paramref name="t1"/>, then a second split of the near piece. With
        /// <c>t0 == 0</c> only the first split runs. The control points are interpolated, so the
        /// result matches the curve to rounding, not bit for bit (<c>Sub(0, 1)</c> included).
        /// </summary>
        public static void Sub(Vec2 s, Vec2 c, Vec2 e, double t0, double t1, out Vec2 s1, out Vec2 c1, out Vec2 e1)
        {
            // [0, t1]: s, q1, q2
            var q1 = Lerp(s, c, t1);
            var q2 = Lerp(q1, Lerp(c, e, t1), t1);
            if (t0 <= 0)
            {
                s1 = s;
                c1 = q1;
                e1 = q2;
                return;
            }

            // the [t0, t1] tail of that piece, at its own parameter t0 / t1
            var u = t0 / t1;
            var m1 = Lerp(s, q1, u);
            var m2 = Lerp(q1, q2, u);
            s1 = Lerp(m1, m2, u);
            c1 = m2;
            e1 = q2;
        }

        /// <summary>Tight axis-aligned bounds: the endpoints plus each axis's extremum
        /// <c>t = (s - c) / (s - 2c + e)</c> when it falls inside (0, 1).</summary>
        public static (double MinX, double MinY, double MaxX, double MaxY) Bounds(Vec2 s, Vec2 c, Vec2 e)
        {
            double minX = Math.Min(s.X, e.X), maxX = Math.Max(s.X, e.X);
            double minY = Math.Min(s.Y, e.Y), maxY = Math.Max(s.Y, e.Y);

            if (TryExtremum(s.X, c.X, e.X, out var tx))
            {
                var x = Eval(s, c, e, tx).X;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
            }

            if (TryExtremum(s.Y, c.Y, e.Y, out var ty))
            {
                var y = Eval(s, c, e, ty).Y;
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }

            return (minX, minY, maxX, maxY);
        }

        // where one coordinate of the curve turns around: B'(t) = 0 along that axis
        private static bool TryExtremum(double s, double c, double e, out double t)
        {
            var denominator = s - 2 * c + e;
            t = denominator != 0 ? (s - c) / denominator : 0;
            return t > 0 && t < 1;
        }
    }
}
