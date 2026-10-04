using System.Windows.Input;
using Clowd.Drawing;

namespace Clowd.UI.DrawOnScreen
{
    /// <summary>
    /// One screen's canvas as <see cref="DrawHistory"/> steps it back. The canvas keeps its own undo
    /// stack; this only answers whether that stack has anything left and pops it, closing an open
    /// text edit first so the undo takes back the text rather than leaving a half-typed box behind.
    /// </summary>
    internal sealed class CanvasUndoTarget : IDrawUndoTarget
    {
        public CanvasUndoTarget(DrawingCanvas canvas)
        {
            Canvas = canvas;
        }

        public DrawingCanvas Canvas { get; }

        public bool CanUndo => ((ICommand)Canvas.CommandUndo).CanExecute(null);

        public void Undo()
        {
            Canvas.CommitTextEdit();
            Canvas.Undo();
        }
    }
}
