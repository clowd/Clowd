namespace Clowd
{
    public enum ToolType
    {
        None,
        Pointer,
        Rectangle,
        FilledRectangle,
        Ellipse,
        Line,
        Arrow,
        PolyLine,
        Text,
        Count,
        Pixelate,
        // members are persisted BY NAME (SettingsEditor.ToolbarOrder/HiddenTools), so new tools
        // append here — reordering would silently remap saved toolbar configurations.
        Measure,
    };
}
