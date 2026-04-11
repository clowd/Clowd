namespace Clowd.Drawing
{
    /// <summary>
    /// Lightweight stand-in for WPF's <c>System.Windows.DpiScale</c>. Carries the
    /// X/Y scale factors so resize handles and stroke widths can be sized in pixels
    /// regardless of the canvas zoom level.
    /// </summary>
    public readonly struct DpiScale
    {
        public double DpiScaleX { get; }

        public double DpiScaleY { get; }

        public double PixelsPerDip => DpiScaleX;

        public DpiScale(double scale)
        {
            DpiScaleX = scale;
            DpiScaleY = scale;
        }

        public DpiScale(double scaleX, double scaleY)
        {
            DpiScaleX = scaleX;
            DpiScaleY = scaleY;
        }

        public static DpiScale Default => new DpiScale(1.0);
    }
}
