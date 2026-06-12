using System;
using Newtonsoft.Json;

namespace Clowd.PlatformUtil
{
    public record ScreenPoint
    {
        public int X { get; init; }
        public int Y { get; init; }

        public ScreenPoint()
        { }

        public ScreenPoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public static ScreenPoint operator -(ScreenPoint point) => new ScreenPoint(-point.X, -point.Y);
        public static ScreenPoint operator +(ScreenPoint point, int add) => new ScreenPoint(point.X + add, point.Y + add);
        public static ScreenPoint operator -(ScreenPoint point, int sub) => point + (-sub);
        public static ScreenPoint operator *(ScreenPoint point, int mul) => new ScreenPoint(point.X * mul, point.Y * mul);
        public static ScreenPoint operator /(ScreenPoint point, int div) => new ScreenPoint(point.X / div, point.Y / div);
        public static ScreenPoint operator +(ScreenPoint point, ScreenPoint add) => new ScreenPoint(point.X + add.X, point.Y + add.Y);
        public static ScreenPoint operator -(ScreenPoint point, ScreenPoint sub) => point + (-sub);
    }

    public record ScreenSize
    {
        public int Width { get; init; }
        public int Height { get; init; }

        public ScreenSize()
        { }

        public ScreenSize(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public static ScreenSize operator -(ScreenSize size) => new ScreenSize(-size.Width, -size.Height);
        public static ScreenSize operator +(ScreenSize size, int add) => new ScreenSize(size.Width + add, size.Height + add);
        public static ScreenSize operator -(ScreenSize size, int sub) => size + (-sub);
        public static ScreenSize operator *(ScreenSize size, int mul) => new ScreenSize(size.Width * mul, size.Height * mul);
        public static ScreenSize operator /(ScreenSize size, int div) => new ScreenSize(size.Width / div, size.Height / div);
        public static ScreenSize operator +(ScreenSize size, ScreenSize add) => new ScreenSize(size.Width + add.Width, size.Height + add.Height);
        public static ScreenSize operator -(ScreenSize size, ScreenSize sub) => size + (-sub);
    }

    public record ScreenRect
    {
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        // computed members are [JsonIgnore] so the serialized shape is exactly {X,Y,Width,Height},
        // matching the session.json schema shared with the Rust capture writer (§2.11).
        [JsonIgnore] public int Left => X;
        [JsonIgnore] public int Top => Y;
        [JsonIgnore] public int Right => Left + Width;
        [JsonIgnore] public int Bottom => Top + Height;

        [JsonIgnore] public ScreenPoint TopLeft => new ScreenPoint(Left, Top);
        [JsonIgnore] public ScreenPoint TopRight => new ScreenPoint(Right, Top);
        [JsonIgnore] public ScreenPoint BottomRight => new ScreenPoint(Right, Bottom);
        [JsonIgnore] public ScreenPoint BottomLeft => new ScreenPoint(Left, Bottom);
        [JsonIgnore] public ScreenPoint Center => new ScreenPoint(Left + Width / 2, Top + Height / 2);

        [JsonIgnore] public ScreenSize Size => new ScreenSize(Width, Height);

        public static ScreenRect Empty => new ScreenRect(0, 0, 0, 0);

        public ScreenRect()
        { }

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

        public ScreenRect Translate(int tx, int ty) => new ScreenRect(Left + tx, Top + ty, Width, Height);

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
    }
}
