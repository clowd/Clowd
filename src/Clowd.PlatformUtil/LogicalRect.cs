using System;

namespace Clowd.PlatformUtil
{
    public record LogicalRect
    {
        public double X { get; init; }
        public double Y { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }

        public double Left => X;
        public double Top => Y;
        public double Right => Left + Width;
        public double Bottom => Top + Height;

        public LogicalPoint TopLeft => new LogicalPoint(Left, Top);
        public LogicalPoint TopRight => new LogicalPoint(Right, Top);
        public LogicalPoint BottomRight => new LogicalPoint(Right, Bottom);
        public LogicalPoint BottomLeft => new LogicalPoint(Left, Bottom);
        public LogicalPoint Center => new LogicalPoint(Left + Width / 2, Top + Height / 2);

        public LogicalSize Size => new LogicalSize(Width, Height);

        public static LogicalRect Empty => new LogicalRect(0, 0, 0, 0);

        public LogicalRect(double x, double y, double width, double height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public static LogicalRect FromLTRB(double left, double top, double right, double bottom) => new LogicalRect(left, top, right - left, bottom - top);

        public bool Contains(LogicalPoint pt) => pt.X >= Left && pt.X < Right && pt.Y >= Top && pt.Y < Bottom;

        public bool IntersectsWith(LogicalRect rect)
        {
            // Touching LogicalRects do not intersect
            return !IsEmpty() && !rect.IsEmpty() && Left < rect.Right && rect.Left < Right && Top < rect.Bottom && rect.Top < Bottom;
        }

        public bool IsEmpty() => Width == 0 && Height == 0;

        public LogicalRect Grow(double amount) => new LogicalRect(Left - amount, Top - amount, Width + 2 * amount, Height + 2 * amount);

        public LogicalRect Intersect(LogicalRect rect)
        {
            var result = FromLTRB(
                Math.Max(Left, rect.Left),
                Math.Max(Top, rect.Top),
                Math.Min(Left + Width, rect.Left + rect.Width),
                Math.Min(Top + Height, rect.Top + rect.Height)
            );

            if (result.Width < 0 || result.Height < 0)
                return Empty;

            return result;
        }

        public static explicit operator System.Drawing.RectangleF(LogicalRect rect)
            => new System.Drawing.RectangleF((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);

        public static explicit operator LogicalRect(System.Drawing.RectangleF rect)
          => new LogicalRect(rect.X, rect.Y, rect.Width, rect.Height);
    }
}
