using System.Windows;
using System.Windows.Input;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    internal class ToolPolyLine : ToolBase
    {
        internal override ToolActionType ActionType => ToolActionType.Object;

        private GraphicPolyLine _newPolyLine;

        public ToolPolyLine() : base(Resource.CursorPencil)
        { }

        protected override void OnMouseDownImpl(DrawingCanvas canvas, Point pt)
        {
            _newPolyLine = new GraphicPolyLine(canvas.ObjectColor, canvas.LineWidth, pt);
            canvas.GraphicsList.Add(_newPolyLine);
        }

        protected override void OnMouseMoveImpl(DrawingCanvas canvas, Point pt)
        {
            _newPolyLine?.AddPoint(pt);
        }

        protected override void OnMouseUpImpl(DrawingCanvas canvas)
        {
            if (_newPolyLine != null)
            {
                _newPolyLine.EndDrawing();
                _newPolyLine.IsSelected = true;
                canvas.AddCommandToHistory();
                _newPolyLine = null;
            }
        }

        public override void AbortOperation(DrawingCanvas canvas)
        {
            if (_newPolyLine != null)
            {
                canvas.GraphicsList.Remove(_newPolyLine);
                _newPolyLine = null;
            }
        }
    }
}
