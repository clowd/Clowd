namespace Clowd.PlatformUtil
{
    public record LogicalSize
    {
        public double Width { get; init; }
        public double Height { get; init; }

        public LogicalSize(double width, double height)
        {
            Width = width;
            Height = height;
        }

        public static LogicalSize operator -(LogicalSize size) => new LogicalSize(-size.Width, -size.Height);
        public static LogicalSize operator +(LogicalSize size, int add) => new LogicalSize(size.Width + add, size.Height + add);
        public static LogicalSize operator -(LogicalSize size, int sub) => size + (-sub);
        public static LogicalSize operator *(LogicalSize size, int mul) => new LogicalSize(size.Width * mul, size.Height * mul);
        public static LogicalSize operator /(LogicalSize size, int div) => new LogicalSize(size.Width / div, size.Height / div);
        public static LogicalSize operator +(LogicalSize size, LogicalSize add) => new LogicalSize(size.Width + add.Width, size.Height + add.Height);
        public static LogicalSize operator -(LogicalSize size, LogicalSize sub) => size + (-sub);

        public static explicit operator System.Drawing.SizeF(LogicalSize pt) => new System.Drawing.SizeF((float)pt.Width, (float)pt.Height);
        public static explicit operator LogicalSize(System.Drawing.SizeF pt) => new LogicalSize(pt.Width, pt.Height);
    }
}
