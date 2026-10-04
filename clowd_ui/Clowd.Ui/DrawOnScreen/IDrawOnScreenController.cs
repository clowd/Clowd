using System;

namespace Clowd.UI.DrawOnScreen
{
    /// <summary>
    /// What the draw-on-screen toolbar drives. The session owns the canvases and the history; the
    /// toolbar only reads <see cref="State"/>, <see cref="CanUndo"/> and <see cref="CanClear"/> and
    /// calls the commands. <see cref="Changed"/> fires after any of the readable members changes.
    /// </summary>
    public interface IDrawOnScreenController
    {
        DrawOnScreenState State { get; }
        bool CanUndo { get; }
        bool CanClear { get; }
        event EventHandler Changed;
        void PickTool(ToolType tool);
        void ToggleClickThrough();
        void ToggleHide();
        void SelectColor(int index);
        void SelectSize(int index);
        void Undo();
        void ClearAll();
        void Close();
    }
}
