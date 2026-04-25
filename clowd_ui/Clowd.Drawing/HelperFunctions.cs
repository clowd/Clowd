using System;
using Avalonia;
using Avalonia.Input;

namespace Clowd.Drawing
{
    internal static class HelperFunctions
    {
        // TODO Phase 12: replace with CursorResources.Default once cursor resources are ported.
        public static Cursor DefaultCursor => new Cursor(StandardCursorType.Arrow);

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

        /// <summary>
        /// Snaps the line from <paramref name="anchor"/> to <paramref name="point"/> onto the nearest 45° (or
        /// 90° offset by 45° if <paramref name="diagOnly"/>) angle. The returned point is the projection of
        /// <paramref name="point"/> onto that snap direction.
        /// </summary>
        public static Point SnapPointToCommonAngle(Point anchor, Point point, bool diagOnly)
        {
            double xDiff = point.X - anchor.X;
            double yDiff = point.Y - anchor.Y;

            double closest;

            if (diagOnly)
            {
                var angle = (Math.Atan2(yDiff, xDiff) * 180.0 / Math.PI + 360 + 45) % 360;
                closest = Math.Round(angle / 90d) * 90d - 45;
            }
            else
            {
                var angle = (Math.Atan2(yDiff, xDiff) * 180.0 / Math.PI + 360) % 360;
                closest = Math.Round(angle / 45d) * 45d;
            }

            // Unit vector along the snap angle
            var snapRad = closest / 180 * Math.PI;
            var snapX = Math.Cos(snapRad);
            var snapY = Math.Sin(snapRad);

            // Project (xDiff, yDiff) onto the snap direction (which is a unit vector)
            var snapLen = xDiff * snapX + yDiff * snapY;

            return new Point(anchor.X + snapLen * snapX, anchor.Y + snapLen * snapY);
        }
    }
}
