using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DrawToolsLib.Graphics
{
    [Serializable]
    public class GraphicsRectangle : GraphicsBase
    {
        public double Left
        {
            get { return _left; }
            set
            {
                if (value.Equals(_left)) return;
                _left = value;
                OnPropertyChanged(nameof(Left));
                OnPropertyChanged(nameof(Bounds));
            }
        }
        public double Top
        {
            get { return _top; }
            set
            {
                if (value.Equals(_top)) return;
                _top = value;
                OnPropertyChanged(nameof(Top));
                OnPropertyChanged(nameof(Bounds));
            }
        }
        public double Right
        {
            get { return _right; }
            set
            {
                if (value.Equals(_right)) return;
                _right = value;
                OnPropertyChanged(nameof(Right));
                OnPropertyChanged(nameof(Bounds));
            }
        }
        public double Bottom
        {
            get { return _bottom; }
            set
            {
                if (value.Equals(_bottom)) return;
                _bottom = value;
                OnPropertyChanged(nameof(Bottom));
                OnPropertyChanged(nameof(Bounds));
            }
        }

        public bool Filled
        {
            get { return _filled; }
            set
            {
                if (value == _filled) return;
                _filled = value;
                OnPropertyChanged();
            }
        }

        private double _left;
        private double _top;
        private double _right;
        private double _bottom;
        private bool _filled;

        protected GraphicsRectangle()
        {
        }
        public GraphicsRectangle(DrawingCanvas canvas, Rect rect)
            : this(canvas.ActualScale, canvas.ObjectColor, canvas.LineWidth, rect, false)
        {
        }
        public GraphicsRectangle(DrawingCanvas canvas, Rect rect, bool filled)
            : this(canvas.ActualScale, canvas.ObjectColor, canvas.LineWidth, rect, filled)
        {
        }
        public GraphicsRectangle(double scale, Color objectColor, double lineWidth, Rect rect)
            : this(scale, objectColor, lineWidth, rect, false)
        {
        }
        public GraphicsRectangle(double scale, Color objectColor, double lineWidth, Rect rect, bool filled)
            : base(scale, objectColor, lineWidth)
        {
            _left = rect.Left;
            _top = rect.Top;
            _right = rect.Right;
            _bottom = rect.Bottom;
            _filled = filled;
        }

        public override Rect Bounds
        {
            get
            {
                double l, t, w, h;

                if (Left <= Right)
                {
                    l = Left;
                    w = Right - Left;
                }
                else
                {
                    l = Right;
                    w = Left - Right;
                }

                if (Top <= Bottom)
                {
                    t = Top;
                    h = Bottom - Top;
                }
                else
                {
                    t = Bottom;
                    h = Top - Bottom;
                }

                return new Rect(l, t, w, h);
            }
        }
        internal override int HandleCount => 8;

        internal override bool Contains(Point point)
        {
            return Bounds.Contains(point);
        }
        internal override Point GetHandle(int handleNumber)
        {
            var xCenter = (Right + Left) / 2;
            var yCenter = (Bottom + Top) / 2;
            var x = Left;
            var y = Top;

            switch (handleNumber)
            {
                case 1:
                    x = Left;
                    y = Top;
                    break;
                case 2:
                    x = xCenter;
                    y = Top;
                    break;
                case 3:
                    x = Right;
                    y = Top;
                    break;
                case 4:
                    x = Right;
                    y = yCenter;
                    break;
                case 5:
                    x = Right;
                    y = Bottom;
                    break;
                case 6:
                    x = xCenter;
                    y = Bottom;
                    break;
                case 7:
                    x = Left;
                    y = Bottom;
                    break;
                case 8:
                    x = Left;
                    y = yCenter;
                    break;
            }
            return new Point(x, y);
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
            return Bounds.IntersectsWith(rectangle);
        }
        internal override void Move(double deltaX, double deltaY)
        {
            Left += deltaX;
            Right += deltaX;

            Top += deltaY;
            Bottom += deltaY;
            OnPropertyChanged();
        }
        internal override void MoveHandleTo(Point point, int handleNumber)
        {
            switch (handleNumber)
            {
                case 1:
                    Left = point.X;
                    Top = point.Y;
                    break;
                case 2:
                    Top = point.Y;
                    break;
                case 3:
                    Right = point.X;
                    Top = point.Y;
                    break;
                case 4:
                    Right = point.X;
                    break;
                case 5:
                    Right = point.X;
                    Bottom = point.Y;
                    break;
                case 6:
                    Bottom = point.Y;
                    break;
                case 7:
                    Left = point.X;
                    Bottom = point.Y;
                    break;
                case 8:
                    Left = point.X;
                    break;
            }
            OnPropertyChanged();
        }
        internal override Cursor GetHandleCursor(int handleNumber)
        {
            switch (handleNumber)
            {
                case 1:
                    return Cursors.SizeNWSE;
                case 2:
                    return Cursors.SizeNS;
                case 3:
                    return Cursors.SizeNESW;
                case 4:
                    return Cursors.SizeWE;
                case 5:
                    return Cursors.SizeNWSE;
                case 6:
                    return Cursors.SizeNS;
                case 7:
                    return Cursors.SizeNESW;
                case 8:
                    return Cursors.SizeWE;
                default:
                    return HelperFunctions.DefaultCursor;
            }
        }
        internal override void Normalize()
        {
            if (Left > Right)
            {
                double tmp = Left;
                Left = Right;
                Right = tmp;
            }

            if (Top > Bottom)
            {
                double tmp = Top;
                Top = Bottom;
                Bottom = tmp;
            }
        }
        internal override void Draw(DrawingContext drawingContext)
        {
            if (drawingContext == null)
                throw new ArgumentNullException(nameof(drawingContext));

            DrawRectangle(drawingContext);

            base.Draw(drawingContext);
        }
        internal virtual void DrawRectangle(DrawingContext drawingContext)
        {
            var brush = new SolidColorBrush(ObjectColor);
            var bounds = this.Bounds;
            drawingContext.DrawRoundedRectangle(
                _filled ? brush : null,
                new Pen(brush, ActualLineWidth),
                new Rect(bounds.Left + (ActualLineWidth / 2), bounds.Top + (ActualLineWidth / 2),
                    Math.Max(1, bounds.Width - ActualLineWidth), Math.Max(1, bounds.Height - ActualLineWidth)),
                LineWidth, LineWidth);
        }
        public override GraphicsBase Clone()
        {
            return new GraphicsRectangle(ActualScale, ObjectColor, LineWidth, Bounds) { ObjectId = ObjectId };
        }
    }
}
