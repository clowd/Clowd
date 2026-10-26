using Avalonia;
using Prisc.Geometry2D;

namespace Prisc.Drawing.Graphics
{
    /// <summary>Conversions between Avalonia's points and the shared geometry core's
    /// <see cref="Vec2"/>. Both are plain coordinate pairs, so the round trip is exact.</summary>
    internal static class GeometryInterop
    {
        public static Vec2 ToVec(this Point p) => new Vec2(p.X, p.Y);

        public static Point ToPoint(this Vec2 v) => new Point(v.X, v.Y);
    }
}
