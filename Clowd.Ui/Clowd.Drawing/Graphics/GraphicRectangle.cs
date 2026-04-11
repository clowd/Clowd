using System;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Rectangle", Skills = Skill.Angle | Skill.Color | Skill.Stroke)]
    public class GraphicRectangle : GraphicBase
    {
        public double Left
        {
            get => _left;
            set => Set(ref _left, value);
        }

        public double Top
        {
            get => _top;
            set => Set(ref _top, value);
        }

        public double Right
        {
            get => _right;
            set => Set(ref _right, value);
        }

        public double Bottom
        {
            get => _bottom;
            set => Set(ref _bottom, value);
        }

        public double Angle
        {
            get => _angle;
            set => Set(ref _angle, value);
        }

        // Recomputed by Normalize() on every deserialize, so excluded from JSON snapshots.
        [System.Text.Json.Serialization.JsonIgnore]
        public Point CenterOfRotation
        {
            get => _centerOfRotation;
            protected set => Set(ref _centerOfRotation, value);
        }

        // This is always the center of the rectangle except while the user is dragging a resizing handle.
        private Point _centerOfRotation;
        private double _left;
        private double _top;
        private double _right;
        private double _bottom;
        private double _angle;

        public GraphicRectangle()
        { }

        public GraphicRectangle(Color objectColor, double lineWidth, Rect rect)
            : this(objectColor, lineWidth, rect, 0)
        { }

        public GraphicRectangle(Color objectColor, double lineWidth, Rect rect, double angle = 0, bool dropShadowEffect = true)
            : base(objectColor, lineWidth, dropShadowEffect)
        {
            _left = rect.Left;
            _top = rect.Top;
            _right = rect.Right;
            _bottom = rect.Bottom;
            _angle = angle;
            Normalize(); // set CenterOfRotation
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

        internal override Point GetHandle(int handleNumber, DpiScale uiscale)
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
                    x = Right + (32 * uiscale.DpiScaleX);
                    y = yCenter;
                    break;
            }

            return new Point(x, y);
        }

        internal override int MakeHitTest(Point point, DpiScale uiscale)
        {
            if (IsSelected)
            {
                var rotated = UnapplyRotation(point);
                for (int i = 1; i <= HandleCount; i++)
                {
                    if (GetHandleRectangle(i, uiscale).Contains(rotated))
                        return i;
                }
            }

            if (Contains(point))
                return 0;

            return -1;
        }

        internal override void Move(double deltaX, double deltaY)
        {
            _left += deltaX;
            _right += deltaX;
            _top += deltaY;
            _bottom += deltaY;
            CenterOfRotation = new Point(
                CenterOfRotation.X + deltaX,
                CenterOfRotation.Y + deltaY);
            OnPropertyChanged(nameof(Left));
            OnPropertyChanged(nameof(Right));
            OnPropertyChanged(nameof(Top));
            OnPropertyChanged(nameof(Bottom));
        }

        internal Point ApplyRotation(Point point)
        {
            var dx = point.X - CenterOfRotation.X;
            var dy = point.Y - CenterOfRotation.Y;
            var angleRad = Angle / 180 * Math.PI;
            return new Point(
                CenterOfRotation.X + dx * Math.Cos(angleRad) - dy * Math.Sin(angleRad),
                CenterOfRotation.Y + dy * Math.Cos(angleRad) + dx * Math.Sin(angleRad));
        }

        internal Point UnapplyRotation(Point point)
        {
            var dx = point.X - CenterOfRotation.X;
            var dy = point.Y - CenterOfRotation.Y;
            var negAngleRad = -Angle / 180 * Math.PI;
            return new Point(
                CenterOfRotation.X + dx * Math.Cos(negAngleRad) - dy * Math.Sin(negAngleRad),
                CenterOfRotation.Y + dy * Math.Cos(negAngleRad) + dx * Math.Sin(negAngleRad));
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

            OnPropertyChanged(nameof(Left));
        }

        internal override Cursor GetHandleCursor(int handleNumber)
        {
            if (handleNumber == 0 || handleNumber > 9)
                return HelperFunctions.DefaultCursor;

            if (handleNumber == 9)
                return new Cursor(StandardCursorType.Hand); // rotation

            // Per-corner / per-edge resize cursors. We don't account for the
            // rectangle's own rotation here — Phase 12 keeps it simple by
            // mapping handle index → standard cursor type.
            return handleNumber switch
            {
                1 => new Cursor(StandardCursorType.TopLeftCorner),
                2 => new Cursor(StandardCursorType.TopSide),
                3 => new Cursor(StandardCursorType.TopRightCorner),
                4 => new Cursor(StandardCursorType.RightSide),
                5 => new Cursor(StandardCursorType.BottomRightCorner),
                6 => new Cursor(StandardCursorType.BottomSide),
                7 => new Cursor(StandardCursorType.BottomLeftCorner),
                8 => new Cursor(StandardCursorType.LeftSide),
                _ => HelperFunctions.DefaultCursor,
            };
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
            var x = (bottomRight.X - topLeft.X) / 2 + topLeft.X;
            var y = (bottomRight.Y - topLeft.Y) / 2 + topLeft.Y;
            CenterOfRotation = new Point(x, y);

            // Step 2: reverse the rotation, but about the *new* center of rotation.
            topLeft = UnapplyRotation(topLeft);
            bottomRight = UnapplyRotation(bottomRight);

            Left = topLeft.X;
            Top = topLeft.Y;
            Right = bottomRight.X;
            Bottom = bottomRight.Y;
        }

        internal override void DrawObject(DrawingContext drawingContext)
        {
            // Rotate around CenterOfRotation. Avalonia's DrawingContext uses a
            // disposable PushedState (the WPF PushTransform/Pop pair).
            var rotateMatrix =
                Matrix.CreateTranslation(-CenterOfRotation.X, -CenterOfRotation.Y) *
                Matrix.CreateRotation(Angle * Math.PI / 180.0) *
                Matrix.CreateTranslation(CenterOfRotation.X, CenterOfRotation.Y);

            using (drawingContext.PushTransform(rotateMatrix))
            {
                DrawRectangle(drawingContext);
            }
        }

        internal virtual void DrawRectangle(DrawingContext drawingContext)
        {
            var pen = new Pen(new SolidColorBrush(ObjectColor), LineWidth);
            var inset = LineWidth / 2;
            var rect = new Rect(
                UnrotatedBounds.Left + inset,
                UnrotatedBounds.Top + inset,
                Math.Max(1, UnrotatedBounds.Right - UnrotatedBounds.Left - LineWidth),
                Math.Max(1, UnrotatedBounds.Bottom - UnrotatedBounds.Top - LineWidth));
            drawingContext.DrawRectangle(null, pen, rect, LineWidth, LineWidth);
        }

        protected override void DrawSingleTracker(DrawingContext drawingContext, int handleNum, DpiScale uiscale)
        {
            if (handleNum == 9) // draw rotation handle differently
            {
                DrawRotationTracker(drawingContext, GetHandle(4, uiscale), GetHandleRectangle(9, uiscale), uiscale);
                base.DrawSingleTracker(drawingContext, 4, uiscale);
            }
            else
            {
                base.DrawSingleTracker(drawingContext, handleNum, uiscale);
            }
        }

        internal virtual void DrawRotationTracker(DrawingContext drawingContext, Point anchor, Rect rectangle, DpiScale uiscale)
        {
            var radius = rectangle.Width / 2;
            Point center = new Point(rectangle.Left + radius, rectangle.Top + radius);
            var scaledline = 1 * uiscale.DpiScaleX;
            radius -= scaledline;

            var basePen = new Pen(Brushes.Green, scaledline);
            drawingContext.DrawLine(basePen, anchor, center);
            drawingContext.DrawEllipse(Brushes.Green, null, center, radius, radius);
        }
    }
}
