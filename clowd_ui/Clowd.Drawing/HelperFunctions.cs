using System;
using Avalonia;
using Avalonia.Input;
using Clowd.Drawing.Graphics;
using Clowd.Geometry2D;

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
        public const double SnapAngleStepDegrees = AngleSnap.StepDegrees;

        /// <summary>Snaps <paramref name="point"/> about <paramref name="anchor"/> to the nearest
        /// <see cref="SnapAngleStepDegrees"/> multiple, or with <paramref name="diagOnly"/> to the
        /// nearest diagonal (the square / circle constraint).</summary>
        public static Point SnapPointToCommonAngle(Point anchor, Point point, bool diagOnly)
        {
            // the shared core's snap, so the video editor's arrow handles snap to the same angles
            return AngleSnap.Snap(anchor.ToVec(), point.ToVec(), diagOnly).ToPoint();
        }
    }
}
