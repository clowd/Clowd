using System;
using System.Globalization;

namespace Clowd.Geometry2D
{
    /// <summary>
    /// A 2D point or vector (the operations are component-wise, so one type serves both).
    /// </summary>
    /// <remarks>
    /// Every operation evaluates in exactly the order Avalonia's <c>Point</c> and <c>Vector</c> do
    /// (a divide in <see cref="Normalize"/>, <c>0 - x</c> for negation, the square root of the
    /// squared length), so maths ported from the image editor onto this type stays bit-identical.
    /// <c>ArrowGeometryParityTests</c> holds it to that; change the order here only if it still
    /// passes.
    /// </remarks>
    public readonly struct Vec2 : IEquatable<Vec2>
    {
        public Vec2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }

        public double Y { get; }

        public double Length => Math.Sqrt(X * X + Y * Y);

        /// <summary>This vector divided by its length (NaN components for a zero vector, as Avalonia's).</summary>
        public Vec2 Normalize() => this / Length;

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);

        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);

        // 0 - x rather than -x: the two differ on a zero component (+0 against -0), and Avalonia
        // negates this way
        public static Vec2 operator -(Vec2 a) => new Vec2(0.0 - a.X, 0.0 - a.Y);

        public static Vec2 operator *(Vec2 a, double k) => new Vec2(a.X * k, a.Y * k);

        public static Vec2 operator *(double k, Vec2 a) => new Vec2(a.X * k, a.Y * k);

        public static Vec2 operator /(Vec2 a, double k) => new Vec2(a.X / k, a.Y / k);

        public static bool operator ==(Vec2 left, Vec2 right) => left.Equals(right);

        public static bool operator !=(Vec2 left, Vec2 right) => !left.Equals(right);

        public bool Equals(Vec2 other) => X.Equals(other.X) && Y.Equals(other.Y);

        public override bool Equals(object obj) => obj is Vec2 other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y);

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "{0}, {1}", X, Y);
    }
}
