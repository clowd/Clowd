using System.Windows;
using System.Windows.Input;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    internal abstract class ToolSelection : ToolBase
    {
        private GraphicSelectionRectangle _selection = new(new Rect(0, 0, 1, 1));

        public ToolSelection() : base(Cursors.Cross, SnapMode.Diagonal)
        { }

        protected override void OnMouseDownImpl(DrawingCanvas canvas, Point pt)
        {
            _selection.MoveHandleTo(pt, 1);
            _selection.MoveHandleTo(pt, 5);
            canvas.GraphicsList.Add(_selection);
        }

        protected override void OnMouseMoveImpl(DrawingCanvas canvas, Point pt)
        {
            _selection.MoveHandleTo(pt, 5);
        }

        protected override void OnMouseUpImpl(DrawingCanvas canvas)
        {
            canvas.GraphicsList.Remove(_selection);
            MakeSelection(canvas, _selection.UnrotatedBounds);
        }

        protected abstract void MakeSelection(DrawingCanvas canvas, Rect selectedArea);

        public override void AbortOperation(DrawingCanvas canvas)
        {
            canvas.GraphicsList.Remove(_selection);
        }
    }
}
