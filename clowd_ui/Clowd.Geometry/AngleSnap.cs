using System;

namespace Clowd.Geometry2D
{
    /// <summary>The Shift angle snap shared by every line-like handle (the image editor's
    /// <c>HelperFunctions.SnapPointToCommonAngle</c>).</summary>
    public static class AngleSnap
    {
        /// <summary>The snap step, in degrees. Fine enough to reach the common angles
        /// (15/30/45/60/75) while still landing exactly on the axes and diagonals.</summary>
        public const double StepDegrees = 15;

        /// <summary>Snaps <paramref name="point"/> about <paramref name="anchor"/> to the nearest
        /// <see cref="StepDegrees"/> multiple, or with <paramref name="diagOnly"/> to the nearest
        /// diagonal (the square / circle constraint). The point is projected onto the snapped
        /// direction, so its distance along it is kept.</summary>
        public static Vec2 Snap(Vec2 anchor, Vec2 point, bool diagOnly)
        {
            double x1 = anchor.X, y1 = anchor.Y, x2 = point.X, y2 = point.Y;
            double xDiff = x2 - x1;
            double yDiff = y2 - y1;

            double closest;

            if (diagOnly)
            {
                var angle = (Math.Atan2(yDiff, xDiff) * 180.0 / Math.PI + 360 + 45) % 360;
                closest = Math.Round(angle / 90d) * 90d - 45;
            }
            else
            {
                var angle = (Math.Atan2(yDiff, xDiff) * 180.0 / Math.PI + 360) % 360;
                closest = Math.Round(angle / StepDegrees) * StepDegrees;
            }

            // projection of the drag vector onto the unit vector at the snapped angle
            var theta = closest / 180 * Math.PI;
            var ux = Math.Cos(theta);
            var uy = Math.Sin(theta);
            var snapLen = xDiff * ux + yDiff * uy;

            var ox = anchor.X + snapLen * ux;
            var oy = anchor.Y + snapLen * uy;

            return new Vec2(ox, oy);
        }
    }
}
