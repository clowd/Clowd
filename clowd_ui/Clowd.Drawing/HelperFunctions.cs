using System;
using Avalonia;
using Avalonia.Input;

namespace Clowd.Drawing
{
    internal static class HelperFunctions
    {
        public static Cursor DefaultCursor => CursorResources.Default;

        public static Rect CreateRectSafe(double Left, double Top, double Right, double Bottom)
        {
            double l, t, w, h;

            if (Left <= Right)
            {
                l = Left;
                w = Right - Left;
            }
            else
            {
                l = Right;
                w = Left - Right;
            }

            if (Top <= Bottom)
            {
                t = Top;
                h = Bottom - Top;
            }
            else
            {
                t = Bottom;
                h = Top - Bottom;
            }

            return new Rect(l, t, w, h);
        }

        public static Rect CreateRectSafeRounded(double Left, double Top, double Right, double Bottom)
        {
            var r = CreateRectSafe(Left, Top, Right, Bottom);
            return new Rect(Math.Round(r.Left), Math.Round(r.Top), Math.Round(r.Width), Math.Round(r.Height));
        }

        /// <summary>The Shift angle snap step, in degrees, for lines, arrows, measures, step-badge
        /// arrows and pen segments/handles. Fine enough to reach common angles (15/30/45/60/75)
        /// while still landing exactly on the axes and diagonals.</summary>
        public const double SnapAngleStepDegrees = 15;

        /// <summary>Snaps <paramref name="point"/> about <paramref name="anchor"/> to the nearest
        /// <see cref="SnapAngleStepDegrees"/> multiple, or with <paramref name="diagOnly"/> to the
        /// nearest diagonal (the square / circle constraint).</summary>
        public static Point SnapPointToCommonAngle(Point anchor, Point point, bool diagOnly)
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
                closest = Math.Round(angle / SnapAngleStepDegrees) * SnapAngleStepDegrees;
            }

            // projection of the drag vector onto the unit vector at the snapped angle.
            var theta = closest / 180 * Math.PI;
            var ux = Math.Cos(theta);
            var uy = Math.Sin(theta);
            var snapLen = xDiff * ux + yDiff * uy;

            var ox = anchor.X + snapLen * ux;
            var oy = anchor.Y + snapLen * uy;

            return new Point(ox, oy);
        }
    }
}
