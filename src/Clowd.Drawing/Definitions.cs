namespace Clowd.Drawing
{
    // relocated to Clowd.Shared
    //public enum ToolType
    //{
    //    None,
    //    Pointer,
    //    Rectangle,
    //    FilledRectangle,
    //    Ellipse,
    //    Line,
    //    Arrow,
    //    PolyLine,
    //    Text,
    //    //Pixelate,
    //    //Erase,
    //    Max
    //};

    public enum ToolActionType
    {
        Cursor,
        Object,
        Drawing,
    }

    internal enum ContextMenuCommand
    {
        SelectAll,
        UnselectAll,
        Delete,
        DeleteAll,
        MoveToFront,
        MoveToBack,
        ResetRotation,
        Undo,
        Redo,
        SetProperties
    };
}
