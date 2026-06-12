namespace Clowd.Drawing
{
    /// <summary>
    /// Replacement for the WPF DpiScale struct (decision table #13).
    /// </summary>
    public readonly record struct DpiScale(double DpiScaleX, double DpiScaleY);
}
