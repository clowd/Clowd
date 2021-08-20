using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Clowd.Drawing.Properties;

namespace Clowd.Drawing.Graphics
{
    [Serializable]
    public class GraphicRectangle : GraphicBase
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

        // This is always the center of the rectangle except while the user is dragging a resizing handle.
        private Point _centerOfRotation;

        protected GraphicRectangle()
        {
        }
        public GraphicRectangle(DrawingCanvas canvas, Rect rect)
            : this(canvas.ObjectColor, canvas.LineWidth, rect, false)
        {
        }
        public GraphicRectangle(DrawingCanvas canvas, Rect rect, bool filled)
            : this(canvas.ObjectColor, canvas.LineWidth, rect, filled)
        {
        }
        public GraphicRectangle(Color objectColor, double lineWidth, Rect rect)
            : this(objectColor, lineWidth, rect, false)
        {
        }
        public GraphicRectangle(Color objectColor, double lineWidth, Rect rect, bool filled, double angle = 0)
            : base(objectColor, lineWidth)
        {
            _left = rect.Left;
            _top = rect.Top;
            _right = rect.Right;
            _bottom = rect.Bottom;
            _centerOfRotation = new Point((_left + _right) / 2, (_top + _bottom) / 2);
            _filled = filled;
            _angle = angle;
        }

        public override Rect Bounds
        {
            get
            {
                if (Angle == 0)
                    return UnrotatedBounds;

                var points = new[] { new Point(Left, Top), new Point(Right, Top), new Point(Left, Bottom), new Point(Right, Bottom) };
                var rotated = points.Select(ApplyRotation).ToArray();
                var l = rotated.Min(p => p.X);
                var t = rotated.Min(p => p.Y);
                var r = rotated.Max(p => p.X);
                var b = rotated.Max(p => p.Y);
                return new Rect(l, t, r - l, b - t);
            }
        }

        public virtual Rect UnrotatedBounds => HelperFunctions.CreateRectSafeRounded(Left, Top, Right, Bottom);

        internal override int HandleCount => 9;

        internal override bool Contains(Point point)
        {
            return UnrotatedBounds.Contains(UnapplyRotation(point));
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
                    x = Right + 32;
                    y = yCenter;
                    break;
            }
            return new Point(x, y);
        }

        internal override int MakeHitTest(Point point)
        {
            if (IsSelected)
            {
                var rotated = UnapplyRotation(point);
                for (int i = 1; i <= HandleCount; i++)
                {
                    if (GetHandleRectangle(i).Contains(rotated))
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
            _centerOfRotation = new Point(
                _centerOfRotation.X + deltaX,
                _centerOfRotation.Y + deltaY);
            OnPropertyChanged();
        }

        internal Point ApplyRotation(Point point)
        {
            var d = point - _centerOfRotation;
            var angleRad = Angle / 180 * Math.PI;
            var newPoint = _centerOfRotation + new Vector(
                d.X * Math.Cos(angleRad) - d.Y * Math.Sin(angleRad),
                d.Y * Math.Cos(angleRad) + d.X * Math.Sin(angleRad));
            return newPoint;
        }

        internal Point UnapplyRotation(Point point)
        {
            var d = point - _centerOfRotation;
            var negAngleRad = -Angle / 180 * Math.PI;
            var newPoint = _centerOfRotation + new Vector(
                d.X * Math.Cos(negAngleRad) - d.Y * Math.Sin(negAngleRad),
                d.Y * Math.Cos(negAngleRad) + d.X * Math.Sin(negAngleRad));
            return newPoint;
        }

        internal override void MoveHandleTo(Point point, int handleNumber)
        {
            var unrotatedMid = new Point((UnrotatedBounds.Left + UnrotatedBounds.Right) / 2, (UnrotatedBounds.Top + UnrotatedBounds.Bottom) / 2);
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
                    Angle = Math.Atan2(point.Y - unrotatedMid.Y, point.X - unrotatedMid.X) / Math.PI * 180;
                    break;
            }
            OnPropertyChanged();
        }

        private static Cursor[] _resizeCursors = new Cursor[36];

        internal override Cursor GetHandleCursor(int handleNumber)
        {
            if (handleNumber == 0 || handleNumber > 9)
                return HelperFunctions.DefaultCursor;

            if (handleNumber == 9)
                return new Cursor(new MemoryStream(Resources.Rotate));

            var cursorNum = (int)((45 * handleNumber + Angle + 272.5) / 5) % 36;
            if (_resizeCursors[cursorNum] == null)
                _resizeCursors[cursorNum] = new Cursor(new MemoryStream((byte[])Resources.ResourceManager.GetObject($"Resize{cursorNum}")));
            return _resizeCursors[cursorNum];
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

            // If the user resized a rotated rectangle, we need to move the rectangle in such a way that the center of rotation is in the center of the rectangle again.
            // Step 1: find the *rotated* positions of the top-left and bottom-right corners.
            var topLeft = ApplyRotation(new Point(Left, Top));
            var bottomRight = ApplyRotation(new Point(Right, Bottom));
            // The center of rotation is in the middle between the top-left and bottom-right, even when rotated.
            _centerOfRotation = new Point((topLeft.X + bottomRight.X) / 2, (topLeft.Y + bottomRight.Y) / 2);
            // Step 2: reverse the rotation, but about the *new* center of rotation.
            topLeft = UnapplyRotation(topLeft);
            bottomRight = UnapplyRotation(bottomRight);

            Left = topLeft.X;
            Top = topLeft.Y;
            Right = bottomRight.X;
            Bottom = bottomRight.Y;
        }

        internal override void Draw(DrawingContext drawingContext)
        {
            if (drawingContext == null)
                throw new ArgumentNullException(nameof(drawingContext));

            drawingContext.PushTransform(new RotateTransform(Angle, _centerOfRotation.X, _centerOfRotation.Y));
            DrawRectangle(drawingContext);

            base.Draw(drawingContext);
        }

        internal override void DrawSingleTracker(DrawingContext drawingContext, int handleNum)
        {
            // draw rotation handle differently
            if (handleNum == 9)
            {
                DrawRotationTracker(drawingContext, GetHandle(4), GetHandleRectangle(9));
                base.DrawSingleTracker(drawingContext, 4);
            }
            else
            {
                base.DrawSingleTracker(drawingContext, handleNum);
            }
        }

        internal virtual void DrawRotationTracker(DrawingContext drawingContext, Point anchor, Rect rectangle)
        {
            Point center = new Point(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Width / 2);
            DashStyle dashStyle = new DashStyle();
            dashStyle.Dashes.Add(4);
            var dashedPen = new Pen(Brushes.White, 1);
            dashedPen.DashStyle = dashStyle;
            var basePen = new Pen(Brushes.Black, 1);
            drawingContext.DrawLine(basePen, anchor, center);
            drawingContext.DrawLine(dashedPen, anchor, center);
            drawingContext.DrawEllipse(Brushes.Green, null, center, rectangle.Width / 2 - 1, rectangle.Height / 2 - 1);
        }

        internal virtual void DrawRectangle(DrawingContext drawingContext)
        {
            var brush = new SolidColorBrush(ObjectColor);
            drawingContext.DrawRoundedRectangle(
                _filled ? brush : null,
                new Pen(brush, LineWidth),
                new Rect(UnrotatedBounds.Left + (LineWidth / 2),
                    UnrotatedBounds.Top + (LineWidth / 2),
                    Math.Max(1, UnrotatedBounds.Right - UnrotatedBounds.Left - LineWidth),
                    Math.Max(1, UnrotatedBounds.Bottom - UnrotatedBounds.Top - LineWidth)),
                LineWidth, LineWidth);
        }

        public override GraphicBase Clone()
        {
            return new GraphicRectangle(ObjectColor, LineWidth, UnrotatedBounds, Filled, Angle) { ObjectId = ObjectId };
        }
    }
}
