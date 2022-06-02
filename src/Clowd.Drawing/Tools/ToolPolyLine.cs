using System.Windows;
using System.Windows.Input;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    internal class ToolPolyLine : ToolBase
    {
        internal override ToolActionType ActionType => ToolActionType.Object;

        private GraphicPolyLine newPolyLine;

        public ToolPolyLine() : base(Resource.CursorPencil)
        { }

        public override void OnMouseDown(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            base.OnMouseDown(drawingCanvas, e);

            Point p = e.GetPosition(drawingCanvas);
            newPolyLine = new GraphicPolyLine(drawingCanvas.ObjectColor, drawingCanvas.LineWidth, new Point(p.X + 1, p.Y + 1));
            drawingCanvas.GraphicsList.Add(newPolyLine);
        }

        public override void OnMouseMove(DrawingCanvas drawingCanvas, MouseEventArgs e)
        {
            if (newPolyLine == null || e.LeftButton != MouseButtonState.Pressed || !drawingCanvas.IsMouseCaptured)
                return;

            Point p = e.GetPosition(drawingCanvas);
            newPolyLine.AddPoint(p);
        }

        public override void OnMouseUp(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            base.OnMouseUp(drawingCanvas, e);

            Point p = e.GetPosition(drawingCanvas);

            newPolyLine.AddPoint(p);
            newPolyLine.IsSelected = true;
            newPolyLine.Normalize();

            drawingCanvas.AddCommandToHistory();
            newPolyLine = null;
        }
    }
}
