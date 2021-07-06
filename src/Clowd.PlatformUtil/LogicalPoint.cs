using System;
using System.Collections.Generic;
using System.Text;

namespace Clowd.PlatformUtil
{
    public record LogicalPoint
    {
        public double X { get; init; }
        public double Y { get; init; }

        public LogicalPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public static LogicalPoint operator -(LogicalPoint point) => new LogicalPoint(-point.X, -point.Y);
        public static LogicalPoint operator +(LogicalPoint point, double add) => new LogicalPoint(point.X + add, point.Y + add);
        public static LogicalPoint operator -(LogicalPoint point, double sub) => point + (-sub);
        public static LogicalPoint operator *(LogicalPoint point, double mul) => new LogicalPoint(point.X * mul, point.Y * mul);
        public static LogicalPoint operator /(LogicalPoint point, double div) => new LogicalPoint(point.X / div, point.Y / div);
        public static LogicalPoint operator +(LogicalPoint point, LogicalPoint add) => new LogicalPoint(point.X + add.X, point.Y + add.Y);
        public static LogicalPoint operator -(LogicalPoint point, LogicalPoint sub) => point + (-sub);

        public static explicit operator System.Drawing.PointF(LogicalPoint pt) => new System.Drawing.PointF((float)pt.X, (float)pt.Y);
        public static explicit operator LogicalPoint(System.Drawing.PointF pt) => new LogicalPoint(pt.X, pt.Y);
    }
}
