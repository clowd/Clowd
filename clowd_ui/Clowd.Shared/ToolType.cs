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
        PolyLine, // legacy pencil: the graphic still loads, the tool is gone; kept so saved toolbar orders parse
        Text,
        Count,
        Pixelate,
        // members are persisted BY NAME (SettingsEditor.ToolbarOrder/HiddenTools), so new tools
        // append here — reordering would silently remap saved toolbar configurations.
        Measure,
        StickyNote,
        Pen,   // bezier path tool (replaces the pencil, which lives on as PolyLine above)
        Brush, // freehand ink
        Highlighter, // freehand translucent chisel ink
        Eraser, // overlay-only (draw on screen): deletes what it hovers/marquees; never on the editor toolbar
    };
}
