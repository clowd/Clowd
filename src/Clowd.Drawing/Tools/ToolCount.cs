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

        public ToolCount() : base(CursorResources.Numerical, SnapMode.All)
        { }

        protected override void OnMouseDownImpl(DrawingCanvas canvas, Point pt)
        {
            var maxNum = canvas.GraphicsList
                .OfType<GraphicCount>()
                .Where(g => int.TryParse(g.Body, out _))
                .MaxOrDefault(g => int.Parse(g.Body));

            _currentArrow = new GraphicArrow(canvas.ObjectColor, canvas.LineWidth, pt, pt);

            var o = new GraphicCount(canvas, pt, (maxNum + 1).ToString());
            o.Normalize();
            // we want count to be centered on point, not aligned to the top left
            o.Move(o.Bounds.Width / -2d, o.Bounds.Height / -2d);
            _currentCount = o;

            canvas.GraphicsList.Add(_currentArrow);
            canvas.GraphicsList.Add(o);
        }

        protected override void OnMouseMoveImpl(DrawingCanvas canvas, Point pt)
        {
            if (_currentArrow != null)
            {
                _currentArrow.LineEnd = pt;
            }
        }

        protected override void OnMouseUpImpl(DrawingCanvas canvas)
        {
            if (_currentArrow != null && _currentCount != null)
            {
                if (_currentCount.Contains(_currentArrow.LineStart) && _currentCount.Contains(_currentArrow.LineEnd))
                {
                    canvas.GraphicsList.Remove(_currentArrow);
                }

                // CreateTextBox adds command history etc.
                CreateTextBox(_currentCount, canvas, true);
            }
            
            _currentArrow = null;
            _currentCount = null;
        }

        public override void AbortOperation(DrawingCanvas canvas)
        {
            if (_currentArrow != null)
            {
                canvas.GraphicsList.Remove(_currentArrow);
                _currentArrow = null;
            }

            if (_currentCount != null)
            {
                canvas.GraphicsList.Remove(_currentCount);
                _currentCount = null;
            }
            
            base.AbortOperation(canvas);
        }
    }
}
