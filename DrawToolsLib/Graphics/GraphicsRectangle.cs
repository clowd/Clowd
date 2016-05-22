using System;
using System.Linq;
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
                if (value == _left) return;
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
                if (value == _top) return;
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
                if (value == _right) return;
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
                if (value == _bottom) return;
                _bottom = value;
                OnPropertyChanged(nameof(Bottom));
                OnPropertyChanged(nameof(Bounds));
            }
        }
        public double Angle
        {
            get { return _angle; }
            set
            {
                if (value == _angle) return;
                _angle = value;
                OnPropertyChanged(nameof(Angle));
            }
        }

        public bool Filled
        {
            get { return _filled; }
            set
            {
                if (value == _filled) return;
                _filled = value;
                OnPropertyChanged(nameof(Filled));
            }
        }

        private double _left;
        private double _top;
        private double _right;
        private double _bottom;
        private double _angle = 0;
        private bool _filled;

        protected GraphicsRectangle()
        {
        }
        public GraphicsRectangle(DrawingCanvas canvas, Rect rect)
            : this(canvas.ObjectColor, canvas.LineWidth, rect, false)
        {
        }
        public GraphicsRectangle(DrawingCanvas canvas, Rect rect, bool filled)
            : this(canvas.ObjectColor, canvas.LineWidth, rect, filled)
        {
        }
        public GraphicsRectangle(Color objectColor, double lineWidth, Rect rect)
            : this(objectColor, lineWidth, rect, false)
        {
        }
        public GraphicsRectangle(Color objectColor, double lineWidth, Rect rect, bool filled, double angle = 0)
            : base(objectColor, lineWidth)
        {
            _left = rect.Left;
            _top = rect.Top;
            _right = rect.Right;
            _bottom = rect.Bottom;
            _filled = filled;
            _angle = angle;
        }

        public override Rect Bounds
        {
            get
            {
                var points = new[] { new Point(Left, Top), new Point(Right, Top), new Point(Left, Bottom), new Point(Right, Bottom) };
                var rotated = points.Select(p => ApplyRotation(p));
                var l = rotated.Min(p => p.X);
                var t = rotated.Min(p => p.Y);
                var r = rotated.Max(p => p.X);
                var b = rotated.Max(p => p.Y);
                return new Rect(l, t, r - l, b - t);
            }
        }
        public Rect UnrotatedBounds
        {
            get
            {
                return new Rect(Left, Top, Right - Left, Bottom - Top);
            }
        }

        internal override int HandleCount => 9;

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

                case 9: // handle for rotation
                    x = (xCenter + Right) / 2;
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
                    if (GetHandleRectangle(i).Contains(UnapplyRotation(point)))
                        return i;
                }
            }

            if (Contains(point))
                return 0;

            return -1;
        }
        internal override void Move(double deltaX, double deltaY)
        {
            Left += deltaX;
            Right += deltaX;

            Top += deltaY;
            Bottom += deltaY;
            OnPropertyChanged();
        }
        internal Point ApplyRotation(Point point)
        {
            var midPoint = new Point((Left + Right) / 2, (Top + Bottom) / 2);
            var d = point - midPoint;
            var angleRad = Angle / 180 * Math.PI;
            var newPoint = midPoint + new Vector(
                d.X * Math.Cos(angleRad) - d.Y * Math.Sin(angleRad),
                d.Y * Math.Cos(angleRad) + d.X * Math.Sin(angleRad));
            return newPoint;
        }
        internal Point UnapplyRotation(Point point)
        {
            var midPoint = new Point((Left + Right) / 2, (Top + Bottom) / 2);
            var d = point - midPoint;
            var negAngleRad = -Angle / 180 * Math.PI;
            var newPoint = midPoint + new Vector(
                d.X * Math.Cos(negAngleRad) - d.Y * Math.Sin(negAngleRad),
                d.Y * Math.Cos(negAngleRad) + d.X * Math.Sin(negAngleRad));
            return newPoint;
        }
        internal override void MoveHandleTo(Point point, int handleNumber)
        {
            var rPoint = UnapplyRotation(point);
            switch (handleNumber)
            {
                case 1:
                    Left = rPoint.X;
                    Top = rPoint.Y;
                    break;
                case 2:
                    Top = rPoint.Y;
                    break;
                case 3:
                    Right = rPoint.X;
                    Top = rPoint.Y;
                    break;
                case 4:
                    Right = rPoint.X;
                    break;
                case 5:
                    Right = rPoint.X;
                    Bottom = rPoint.Y;
                    break;
                case 6:
                    Bottom = rPoint.Y;
                    break;
                case 7:
                    Left = rPoint.X;
                    Bottom = rPoint.Y;
                    break;
                case 8:
                    Left = rPoint.X;
                    break;

                case 9: // rotation
                    var unrotatedMid = new Point((Left + Right) / 2, (Top + Bottom) / 2);
                    Angle = Math.Atan2(point.Y - unrotatedMid.Y, point.X - unrotatedMid.X) / Math.PI * 180;
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
                case 9:
                    return Cursors.Cross;
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

            drawingContext.PushTransform(new RotateTransform(Angle, (Left + Right) / 2, (Top + Bottom) / 2));
            DrawRectangle(drawingContext);
            base.Draw(drawingContext);
        }
        internal virtual void DrawRectangle(DrawingContext drawingContext)
        {
            var brush = new SolidColorBrush(ObjectColor);
            drawingContext.DrawRoundedRectangle(
                _filled ? brush : null,
                new Pen(brush, LineWidth),
                new Rect(Left + (LineWidth / 2), Top + (LineWidth / 2),
                    Math.Max(1, Right - Left - LineWidth), Math.Max(1, Bottom - Top - LineWidth)),
                LineWidth, LineWidth);
        }
        public override GraphicsBase Clone()
        {
            return new GraphicsRectangle(ObjectColor, LineWidth, UnrotatedBounds, Filled, Angle) { ObjectId = ObjectId };
        }
    }
}
