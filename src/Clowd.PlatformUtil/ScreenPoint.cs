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

        public static explicit operator System.Drawing.Point(ScreenPoint pt) => new System.Drawing.Point(pt.X, pt.Y);
        public static explicit operator ScreenPoint(System.Drawing.Point pt) => new ScreenPoint(pt.X, pt.Y);
    }
}
