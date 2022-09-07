using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Clowd.Drawing.Filters;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    internal class ToolFilter<T> : ToolBase
        where T : FilterBase, new()
    {
        internal override ToolActionType ActionType => ToolActionType.Drawing;

        private FilterBase _filter;
        private DrawingBrush _brush;
        private Point _startPoint;
        private Point _lastPoint;
        private ShiftMode _shiftmode;

        public ToolFilter() : base(Cursors.None)
        {
            _brush = new DrawingBrush()
            {
                Color = Colors.White,
                Hardness = 0.5,
                Radius = 16,
                Type = DrawingBrushType.Circle,
            };
        }

        public override void OnMouseDown(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            if (_filter != null)
            {
                _filter.Dispose();
                _filter = null;
            }
            
            var point = e.GetPosition(canvas);
            _filter = new T();

            var started = _filter.Start(_brush, point, canvas);
            if (started)
            {
                base.OnMouseDown(canvas, e);
                _startPoint = _lastPoint = point;
            }
        }

        public override void OnMouseMove(DrawingCanvas canvas, MouseEventArgs e)
        {
            if (_filter != null && canvas.IsMouseCaptured)
            {
                base.OnMouseMove(canvas, e);
                var point = ConstrainPoint(e.GetPosition(canvas));
                var between = PlotPointsBetween(_lastPoint, point);
                _lastPoint = point;
                
                foreach (var p in between)
                {
                    _filter.Handle(_brush, p);
                }
            }
        }

        public override void OnMouseUp(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            if (_filter != null && canvas.IsMouseCaptured)
            {
                base.OnMouseUp(canvas, e);
                _filter.Dispose();
                _filter = null;
                canvas.AddCommandToHistory();
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

        /// <summary>
        /// Returns all the points between two points, including the last point but not the first. 
        /// Uses the Bresenham line algorithm
        /// </summary>
        private static IEnumerable<Point> PlotPointsBetween(Point p0, Point p1)
        {
            // https://en.wikipedia.org/wiki/Bresenham%27s_line_algorithm

            if (Math.Abs(p1.X - p0.X) < 1)
            {
                if (Math.Abs(p1.Y - p0.Y) < 1)
                {
                    yield return p0;
                }
                else
                {
                    var min = Math.Min(p1.Y, p0.Y);
                    var max = Math.Max(p1.Y, p0.Y);
                    for (var ye = min + 1; ye <= max; ye++)
                        yield return new Point(p1.X, ye);
                }

                yield break;
            }

            if (p1.X < p0.X)
            {
                // switch points as this algorithm expects to be drawing towards the right
                (p0, p1) = (p1, p0);
            }

            var deltax = p1.X - p0.X;
            var deltay = p1.Y - p0.Y;

            // Assume deltax != 0 (line is not vertical),
            // note that this division needs to be done in a way that preserves the fractional part
            var deltaerr = Math.Abs(deltay / deltax);

            // no error at start
            var error = 0d;

            var y = p0.Y;
            for (var x = p0.X; x <= p1.X; x++)
            {
                if (x > p0.X)
                    yield return new Point(x, y);

                error = error + deltaerr;
                while (error >= 0.5)
                {
                    y += deltay > 0 ? 1 : -1;
                    error = error - 1;
                }
            }
        }

        private enum ShiftMode
        {
            None,
            Vertical,
            Horizontal
        }
    }
}
