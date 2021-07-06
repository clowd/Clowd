using System;
using System.Collections.Generic;
using System.Text;

namespace Clowd.PlatformUtil
{
    public record ScreenSize
    {
        public int Width { get; init; }
        public int Height { get; init; }

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

        public static explicit operator System.Drawing.Size(ScreenSize pt) => new System.Drawing.Size(pt.Width, pt.Height);
        public static explicit operator ScreenSize(System.Drawing.Size pt) => new ScreenSize(pt.Width, pt.Height);
    }
}
