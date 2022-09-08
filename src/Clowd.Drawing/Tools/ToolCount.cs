using System.Linq;
using System.Windows;
using System.Windows.Input;
using Clowd.Drawing.Graphics;
using RT.Util.ExtensionMethods;

namespace Clowd.Drawing.Tools
{
    internal class ToolCount : ToolText
    {
        private GraphicArrow _currentArrow;
        private GraphicCount _currentCount;
        
        public ToolCount(DrawingCanvas drawingCanvas) : base(drawingCanvas)
        {
        }

        public override void OnMouseDown(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            var maxNum = drawingCanvas.GraphicsList
                .OfType<GraphicCount>()
                .Where(g => int.TryParse(g.Body, out var _))
                .MaxOrDefault(g => int.Parse((string)g.Body));

            Point point = e.GetPosition(drawingCanvas);

            _currentArrow = new GraphicArrow(drawingCanvas.ObjectColor, drawingCanvas.LineWidth, point, point);
            
            var o = new GraphicCount(drawingCanvas, point, (maxNum + 1).ToString());
            o.Normalize();
            // we want count to be centered on point, not aligned to the top left
            o.Move(o.Bounds.Width / -2d, o.Bounds.Height / -2d);
            _currentCount = o;
            
            drawingCanvas.UnselectAll();
            drawingCanvas.GraphicsList.Add(_currentArrow);
            drawingCanvas.GraphicsList.Add(o);
            drawingCanvas.CaptureMouse();
        }

        public override void OnMouseMove(DrawingCanvas drawingCanvas, MouseEventArgs e)
        {
            if (drawingCanvas.IsMouseCaptured)
            {
                Point point = e.GetPosition(drawingCanvas);
                if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.LeftShift))
                {
                    point = HelperFunctions.SnapPointToCommonAngle(_currentArrow.LineStart, point, false);
                }

                _currentArrow.LineEnd = point;
            }
        }

        public override void OnMouseUp(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            if (drawingCanvas.IsMouseCaptured)
            {
                if (_currentCount.Contains(_currentArrow.LineStart) && _currentCount.Contains(_currentArrow.LineEnd))
                {
                    drawingCanvas.GraphicsList.Remove(_currentArrow);
                }

                base.OnMouseUp(drawingCanvas, e);
            }
        }
    }
}
