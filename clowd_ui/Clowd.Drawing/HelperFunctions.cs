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

        public static Point SnapPointToCommonAngle(Point anchor, Point point, bool diagOnly)
        {
            double x1 = anchor.X, y1 = anchor.Y, x2 = point.X, y2 = point.Y;
            double xDiff = x2 - x1;
            double yDiff = y2 - y1;

            double closest45;

            if (diagOnly)
            {
                var angle = (Math.Atan2(yDiff, xDiff) * 180.0 / Math.PI + 360 + 45) % 360;
                closest45 = Math.Round(angle / 90d) * 90d - 45;
            }
            else
            {
                var angle = (Math.Atan2(yDiff, xDiff) * 180.0 / Math.PI + 360) % 360;
                closest45 = Math.Round(angle / 45d) * 45d;
            }

            // projection of the drag vector onto the unit vector at the snapped angle.
            var theta = closest45 / 180 * Math.PI;
            var ux = Math.Cos(theta);
            var uy = Math.Sin(theta);
            var snapLen = xDiff * ux + yDiff * uy;

            var ox = anchor.X + snapLen * ux;
            var oy = anchor.Y + snapLen * uy;

            return new Point(ox, oy);
        }
    }
}
