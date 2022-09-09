using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    internal class ToolPixelate : ToolBase
    {
        internal override ToolActionType ActionType => ToolActionType.Drawing;

        private GraphicSelectionRectangle _selection = new(new Rect(0, 0, 1, 1));
        
        public ToolPixelate() : base(Cursors.Cross)
        {
        }

        public override void OnMouseDown(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            base.OnMouseDown(canvas, e);
            Point p = e.GetPosition(canvas);
            _selection.MoveHandleTo(p, 1);
            _selection.MoveHandleTo(p, 5);
            canvas.GraphicsList.Add(_selection);
        }

        public override void OnMouseMove(DrawingCanvas canvas, MouseEventArgs e)
        {
            if (canvas.IsMouseCaptured)
            {
                base.OnMouseMove(canvas, e);
                Point p = e.GetPosition(canvas);
                _selection.MoveHandleTo(p, 5);
            }
        }

        public override void OnMouseUp(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            if (canvas.IsMouseCaptured)
            {
                base.OnMouseUp(canvas, e);
                Point p = e.GetPosition(canvas);
                _selection.MoveHandleTo(p, 5);
                canvas.GraphicsList.Remove(_selection);
                foreach (var g in canvas.GraphicsList.OfType<GraphicImage>())
                {
                    g.AddObscuredArea(_selection.UnrotatedBounds);             
                }
                canvas.AddCommandToHistory();
            }
        }
    }
}
