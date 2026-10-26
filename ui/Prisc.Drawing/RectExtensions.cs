using Avalonia;

namespace Clowd.Drawing
{
    /// <summary>
    /// Replacements for WPF Rect.Empty/IsEmpty and Int32Rect.IsEmpty (decision table #3/#4).
    /// An "empty" rect is one whose width or height is &lt;= 0 (default(Rect) qualifies).
    /// </summary>
    public static class RectExtensions
    {
        public static bool IsEmptyRect(this Rect rect)
        {
            return rect.Width <= 0 || rect.Height <= 0;
        }

        public static bool IsEmptyRect(this PixelRect rect)
        {
            return rect.Width <= 0 || rect.Height <= 0;
        }

        public static Rect ToRect(this PixelRect rect)
        {
            return new Rect(rect.X, rect.Y, rect.Width, rect.Height);
        }
    }
}
