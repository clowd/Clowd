using System;
using Avalonia.Animation.Easings;

namespace Prisc.UI.Controls.Tray
{
    /// <summary>
    /// CSS's <c>cubic-bezier(x1, y1, x2, y2)</c>: the curve from (0,0) to (1,1) through the two control
    /// points, read as progress-in → progress-out. The x coordinates must lie in [0, 1] so the curve is
    /// a function of time; the y coordinates may leave it, which is how a curve overshoots its target
    /// and settles back (a y above 1) — the one thing the built-in easings and Avalonia's key splines
    /// cannot be asked for by numbers taken straight off a mockup.
    /// </summary>
    public sealed class CubicBezierEasing : Easing
    {
        private readonly double _x1, _y1, _x2, _y2;

        public CubicBezierEasing(double x1, double y1, double x2, double y2)
        {
            if (x1 is < 0 or > 1 || x2 is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(x1), "A cubic-bezier easing's x coordinates must lie in [0, 1].");

            _x1 = x1;
            _y1 = y1;
            _x2 = x2;
            _y2 = y2;
        }

        public override double Ease(double progress)
        {
            if (progress <= 0)
                return 0;
            if (progress >= 1)
                return 1;

            return Bezier(SolveT(progress), _y1, _y2);
        }

        /// <summary>The curve parameter whose x is <paramref name="x"/>. x(t) is monotonic with both x
        /// control points in [0, 1], so Newton converges from t = x in a few steps almost everywhere;
        /// where the slope flattens out, bisection finishes the job.</summary>
        private double SolveT(double x)
        {
            var t = x;
            for (var i = 0; i < 8; i++)
            {
                var error = Bezier(t, _x1, _x2) - x;
                if (Math.Abs(error) < 1e-7)
                    return t;

                var slope = Slope(t, _x1, _x2);
                if (Math.Abs(slope) < 1e-6)
                    break;

                t -= error / slope;
            }

            double lo = 0, hi = 1;
            t = x;
            for (var i = 0; i < 40; i++)
            {
                var value = Bezier(t, _x1, _x2);
                if (Math.Abs(value - x) < 1e-7)
                    break;

                if (value < x)
                    lo = t;
                else
                    hi = t;
                t = (lo + hi) / 2;
            }

            return t;
        }

        // one axis of the curve with end points 0 and 1
        private static double Bezier(double t, double p1, double p2)
        {
            var u = 1 - t;
            return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
        }

        private static double Slope(double t, double p1, double p2)
        {
            var u = 1 - t;
            return 3 * u * u * p1 + 6 * u * t * (p2 - p1) + 3 * t * t * (1 - p2);
        }
    }
}
