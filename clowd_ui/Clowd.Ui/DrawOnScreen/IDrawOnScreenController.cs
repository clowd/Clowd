using System;

namespace Clowd.UI.DrawOnScreen
{
    /// <summary>
    /// What the draw-on-screen toolbar drives. The session owns the canvases and the history; the
    /// toolbar only reads <see cref="State"/>, <see cref="IsFocused"/>, <see cref="CanUndo"/> and
    /// <see cref="CanClear"/> and calls the commands. <see cref="Changed"/> fires after any of the
    /// readable members changes.
    /// </summary>
    public interface IDrawOnScreenController
    {
        DrawOnScreenState State { get; }

        /// <summary>Whether the session has the keyboard: its toolbar or one of its canvas windows is
        /// focused. Settled, so the keyboard moving between the session's own windows never reads as
        /// leaving.</summary>
        bool IsFocused { get; }

        bool CanUndo { get; }
        bool CanClear { get; }
        event EventHandler Changed;
        void PickTool(ToolType tool);
        void ToggleClickThrough();

        /// <summary>Into click-through (<see cref="DrawOnScreenState.StopDrawing"/>), never out of it.</summary>
        void StopDrawing();

        void ToggleHide();
        void ToggleKeys();
        void SelectColor(int index);
        void SelectSize(int index);
        void Undo();
        void ClearAll();

        /// <summary>Deletes whatever the select tool has selected, on every canvas, as one undo step.</summary>
        void DeleteSelection();

        void Close();
    }
}
