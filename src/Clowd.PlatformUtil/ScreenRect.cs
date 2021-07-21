using System;
using System.ComponentModel;

namespace Clowd.PlatformUtil
{
    public record ScreenRect
    {
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        public int Left => X;
        public int Top => Y;
        public int Right => Left + Width;
        public int Bottom => Top + Height;

        public ScreenPoint TopLeft => new ScreenPoint(Left, Top);
        public ScreenPoint TopRight => new ScreenPoint(Right, Top);
        public ScreenPoint BottomRight => new ScreenPoint(Right, Bottom);
        public ScreenPoint BottomLeft => new ScreenPoint(Left, Bottom);
        public ScreenPoint Center => new ScreenPoint(Left + Width / 2, Top + Height / 2);

        public ScreenSize Size => new ScreenSize(Width, Height);

        public static ScreenRect Empty => new ScreenRect(0, 0, 0, 0);

        public ScreenRect()
        {
        }

        public ScreenRect(ScreenPoint topLeft, ScreenSize size)
        {
            X = topLeft.X;
            Y = topLeft.Y;
            Width = size.Width;
            Height = size.Height;
        }

        public ScreenRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public static ScreenRect FromLTRB(int left, int top, int right, int bottom) => new ScreenRect(left, top, right - left, bottom - top);

        public bool Contains(ScreenPoint pt) => pt.X >= Left && pt.X < Right && pt.Y >= Top && pt.Y < Bottom;

        public bool IntersectsWith(ScreenRect rect)
        {
            // Touching ScreenRects do not intersect
            return !IsEmpty() && !rect.IsEmpty() && Left < rect.Right && rect.Left < Right && Top < rect.Bottom && rect.Top < Bottom;
        }

        public bool IsEmpty() => Width == 0 && Height == 0;

        public ScreenRect Grow(int amount) => new ScreenRect(Left - amount, Top - amount, Width + 2 * amount, Height + 2 * amount);

        public ScreenRect Intersect(ScreenRect rect)
        {
            var result = FromLTRB(
                Math.Max(Left, rect.Left),
                Math.Max(Top, rect.Top),
                Math.Min(Left + Width, rect.Left + rect.Width),
                Math.Min(Top + Height, rect.Top + rect.Height)
            );

            if (result.Width < 0 || result.Height < 0)
                return ScreenRect.Empty;

            return result;
        }

        public static explicit operator System.Drawing.Rectangle(ScreenRect rect)
            => new System.Drawing.Rectangle(rect.X, rect.Y, rect.Width, rect.Height);

        public static explicit operator ScreenRect(System.Drawing.Rectangle rect)
          => new ScreenRect(rect.X, rect.Y, rect.Width, rect.Height);

        [Obsolete]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public System.Drawing.Rectangle ToSystem() => (System.Drawing.Rectangle)this;

        [Obsolete]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static ScreenRect FromSystem(System.Drawing.Rectangle rect) => (ScreenRect)rect;
    }
}
