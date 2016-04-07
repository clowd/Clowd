using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DrawToolsLib.Graphics
{
    [Serializable]
    public class GraphicsLine : GraphicsBase
    {
        public Point LineStart
        {
            get { return _lineStart; }
            set
            {
                if (value.Equals(_lineStart)) return;
                _lineStart = value;
                OnPropertyChanged(nameof(LineStart));
            }
        }

        public Point LineEnd
        {
            get { return _lineEnd; }
            set
            {
                if (value.Equals(_lineEnd)) return;
                _lineEnd = value;
                OnPropertyChanged(nameof(LineEnd));
            }
        }

        private Point _lineStart;
        private Point _lineEnd;

        protected GraphicsLine()
        {
        }
        public GraphicsLine(DrawingCanvas canvas, Point start, Point end) : base(canvas)
        {
            _lineStart = start;
            _lineEnd = end;
        }
        public GraphicsLine(Color objectColor, double lineWidth, Point start, Point end)
            : base(objectColor, lineWidth)
        {
            _lineStart = start;
            _lineEnd = end;
        }

        public override Rect Bounds
        {
            get
            {
                var start = _lineStart;
                var end = _lineEnd;
                return new Rect(Math.Min(start.X, end.X),
                                Math.Min(start.Y, end.Y),
                                Math.Abs(start.X - end.X),
                                Math.Abs(start.Y - end.Y));
            }
        }

        internal override int HandleCount => 2;

        internal override bool Contains(Point point)
        {
            LineGeometry g = new LineGeometry(LineStart, LineEnd);
            return g.StrokeContains(new Pen(Brushes.Black, LineHitTestWidth), point);
        }
        internal override Point GetHandle(int handleNumber)
        {
            return handleNumber == 1 ? LineStart : LineEnd;
        }
        internal override int MakeHitTest(Point point)
        {
            if (IsSelected)
            {
                for (int i = 1; i <= HandleCount; i++)
                {
                    if (GetHandleRectangle(i).Contains(point))
                        return i;
                }
            }
            if (Contains(point))
                return 0;
            return -1;
        }
        internal override bool IntersectsWith(Rect rectangle)
        {
            RectangleGeometry rg = new RectangleGeometry(rectangle);
            LineGeometry lg = new LineGeometry(LineStart, LineEnd);
            PathGeometry widen = lg.GetWidenedPathGeometry(new Pen(Brushes.Black, LineHitTestWidth));
            PathGeometry p = Geometry.Combine(rg, widen, GeometryCombineMode.Intersect, null);

            return !p.IsEmpty();
        }
        internal override void Move(double deltaX, double deltaY)
        {
            _lineStart = new Point(LineStart.X + deltaX, LineStart.Y + deltaY);
            _lineEnd = new Point(LineEnd.X + deltaX, LineEnd.Y + deltaY);
            OnPropertyChanged();
        }
        internal override void MoveHandleTo(Point point, int handleNumber)
        {
            if (handleNumber == 1)
                LineStart = point;
            else
                LineEnd = point;
        }
        internal override Cursor GetHandleCursor(int handleNumber)
        {
            return Cursors.SizeAll;
        }
        internal override void Draw(DrawingContext drawingContext)
        {
            if (drawingContext == null)
                throw new ArgumentNullException(nameof(drawingContext));

            drawingContext.DrawLine(new Pen(new SolidColorBrush(ObjectColor), LineWidth),
                LineStart,
                LineEnd);

            base.Draw(drawingContext);
        }

        public override GraphicsBase Clone()
        {
            return new GraphicsLine(ObjectColor, LineWidth, LineStart, LineEnd) { ObjectId = ObjectId };
        }
    }
}
