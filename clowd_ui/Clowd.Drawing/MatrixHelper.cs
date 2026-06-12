using Avalonia;

namespace Clowd.Drawing
{
    /// <summary>
    /// Replacements for WPF RotateTransform(angle, cx, cy) and Matrix.ScaleAt (decision table #28).
    /// </summary>
    public static class MatrixHelper
    {
        /// <summary>
        /// Returns a matrix rotating by <paramref name="angleDegrees"/> around <paramref name="center"/>.
        /// </summary>
        public static Matrix Rotation(double angleDegrees, Point center)
        {
            return Matrix.CreateTranslation(-center.X, -center.Y)
                   * Matrix.CreateRotation(Matrix.ToRadians(angleDegrees))
                   * Matrix.CreateTranslation(center.X, center.Y);
        }

        /// <summary>
        /// Returns a matrix scaling by (<paramref name="scaleX"/>, <paramref name="scaleY"/>) around <paramref name="center"/>.
        /// </summary>
        public static Matrix ScaleAt(double scaleX, double scaleY, Point center)
        {
            return Matrix.CreateTranslation(-center.X, -center.Y)
                   * Matrix.CreateScale(scaleX, scaleY)
                   * Matrix.CreateTranslation(center.X, center.Y);
        }
    }
}
