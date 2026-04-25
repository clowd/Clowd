using System;
using Avalonia;
using Avalonia.Input;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    /// <summary>
    /// Base class for tools that drag out a rectangular selection and then
    /// hand the selected area to <see cref="MakeSelection"/>. Used by
    /// <see cref="ToolPixelate"/> and (in the WPF original) the crop tool.
    /// </summary>
    internal abstract class ToolSelection : ToolBase
    {
        private GraphicSelectionRectangle? _selection;

        protected ToolSelection(Func<Cursor> cursor) : base(cursor, SnapMode.Diagonal)
        { }

        protected override void OnMouseDownImpl(DrawingCanvas canvas, Point pt)
        {
            _selection = new GraphicSelectionRectangle(new Rect(pt, new Size(1, 1)));
            canvas.GraphicsList.Add(_selection);
        }

        protected override void OnMouseMoveImpl(DrawingCanvas canvas, Point pt)
        {
            if (_selection != null)
                _selection.MoveHandleTo(pt, 5);
        }

        protected override void OnMouseUpImpl(DrawingCanvas canvas)
        {
            if (_selection == null) return;
            var rect = _selection.UnrotatedBounds;
            canvas.GraphicsList.Remove(_selection);
            _selection = null;
            MakeSelection(canvas, rect);
        }

        protected abstract void MakeSelection(DrawingCanvas canvas, Rect selectedArea);

        public override void AbortOperation(DrawingCanvas canvas)
        {
            if (_selection != null)
            {
                canvas.GraphicsList.Remove(_selection);
                _selection = null;
            }
        }
    }
}
