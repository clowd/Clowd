namespace Clowd
{
    /// <summary>
    /// Stroke dash pattern shared by every stroked graphic and by the per-tool saved settings.
    /// Solid is 0 so an absent value (old session files, old settings files) deserializes to it.
    /// </summary>
    public enum LineDashStyle
    {
        Solid,
        Dashed,
        Dotted,
    };
}
