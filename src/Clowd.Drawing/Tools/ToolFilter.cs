using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Clowd.Drawing.Commands;
using Clowd.Drawing.Filters;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    internal class ToolFilter<T> : ToolBase
        where T : FilterBase
    {
        internal override ToolActionType ActionType => ToolActionType.Drawing;

        private FilterBase _filter;
        private DrawingBrush _brush;
        private CommandChangeState _state;
        private Point _startPoint;
        private ShiftMode _shiftmode;

        public ToolFilter() : base(Cursors.None)
        {
            _brush = new DrawingBrush()
            {
                Color = Colors.White,
                Hardness = 0.7,
                Radius = 16,
                Type = DrawingBrushType.Circle
            };
        }

        public override void OnMouseDown(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            base.OnMouseDown(canvas, e);
            var point = e.GetPosition(canvas);
            _startPoint = point;
            _state = new CommandChangeState(canvas);
            Start(canvas, point);
        }
        public override void OnMouseMove(DrawingCanvas canvas, MouseEventArgs e)
        {
            base.OnMouseMove(canvas, e);
            var point = e.GetPosition(canvas);
            Move(canvas, ConstrainPoint(point));
        }
        public override void OnMouseUp(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            base.OnMouseUp(canvas, e);
            End();
            if (_state != null)
            {
                _state.NewState(canvas);
                canvas.AddCommandToHistory(_state);
            }
        }

        public override void SetCursor(DrawingCanvas canvas)
        {
            canvas.Cursor = _brush.GetBrushCursor(canvas);
        }

        private Point ConstrainPoint(Point p)
        {
            if (!Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
                return p;

            var xDistance = Math.Abs(p.X - _startPoint.X);
            var yDistance = Math.Abs(p.Y - _startPoint.Y);

            // this is here so that when dragging in the same orientation with shift held that we don't 
            // switch orentation when passing the mouse origin
            int snapDistance = 20;
            var mode = _shiftmode;
            if (xDistance > snapDistance || yDistance > snapDistance)
                mode = ShiftMode.None;
            if (mode == ShiftMode.None)
                mode = xDistance < yDistance ? ShiftMode.Horizontal : ShiftMode.Vertical;
            _shiftmode = mode;

            if (mode == ShiftMode.Horizontal)
                return new Point(_startPoint.X, p.Y);
            if (mode == ShiftMode.Vertical)
                return new Point(p.X, _startPoint.Y);
            return p;
        }

        private void Start(DrawingCanvas canvas, Point point)
        {
            if (_filter != null)
                End();

            int handleNum;
            var clicked = canvas.ToolPointer.MakeHitTest(canvas, point, out handleNum);
            if (clicked is GraphicImage)
            {
                var gImg = clicked as GraphicImage;
                //gImg.Flatten();
                _filter = CreateFilter(canvas, gImg);
                _filter.Handle(_brush, point);
            }
        }

        private void Move(DrawingCanvas canvas, Point point)
        {
            if (!canvas.IsMouseCaptured)
                return;

            int handleNum;
            var clicked = canvas.ToolPointer.MakeHitTest(canvas, point, out handleNum);
            if (clicked is GraphicImage)
            {
                if (_filter == null)
                {
                    Start(canvas, point);
                }
                if (_filter.Source != clicked)
                {
                    End();
                    Start(canvas, point);
                }
                else
                {
                    _filter.Handle(_brush, point);
                }
            }
        }

        private void End()
        {
            _filter?.Close();
            _filter = null;
        }

        private FilterBase CreateFilter(DrawingCanvas canvas, GraphicImage image)
        {
            return (FilterBase)Activator.CreateInstance(typeof(T), new object[] { canvas, image });
        }

        private enum ShiftMode
        {
            None,
            Vertical,
            Horizontal
        }
    }
}
