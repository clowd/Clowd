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

        // Shared cursors (cheap to reuse, and StandardCursorType doesn't change).
        private static readonly Cursor _cursorHand       = new Cursor(StandardCursorType.Hand);
        private static readonly Cursor _cursorHorizontal = new Cursor(StandardCursorType.RightSide);       // ↔
        private static readonly Cursor _cursorVertical   = new Cursor(StandardCursorType.TopSide);         // ↕
        private static readonly Cursor _cursorDiagNwSe   = new Cursor(StandardCursorType.BottomRightCorner); // ↘↖
        private static readonly Cursor _cursorDiagNeSw   = new Cursor(StandardCursorType.BottomLeftCorner);  // ↙↗

        internal override Cursor GetHandleCursor(int handleNumber)
        {
            if (handleNumber == 0 || handleNumber > 9)
                return HelperFunctions.DefaultCursor;

            if (handleNumber == 9)
                return _cursorHand; // rotation

            // Base direction of each handle in degrees, where 0° = east (screen
            // right) and positive angles rotate clockwise (screen Y is down).
            double baseAngle = handleNumber switch
            {
                1 => -135, // top-left   (NW)
                2 =>  -90, // top        (N)
                3 =>  -45, // top-right  (NE)
                4 =>    0, // right      (E)
                5 =>   45, // bot-right  (SE)
                6 =>   90, // bottom     (S)
                7 =>  135, // bot-left   (SW)
                8 =>  180, // left       (W)
                _ =>    0,
            };

            // Add the rectangle's own rotation to get the handle's on-screen
            // direction, then snap to the nearest 45° octant.
            double effective = (baseAngle + Angle) % 360;
            if (effective < 0) effective += 360;
            int octant = ((int)Math.Round(effective / 45.0)) % 8;

            // Avalonia renders horizontal/vertical/diagonal resize cursors the
            // same for the two opposing directions, so 8 octants reduce to 4
            // visual cursors.
            return octant switch
            {
                0 or 4 => _cursorHorizontal, // ↔
                2 or 6 => _cursorVertical,   // ↕
                1 or 5 => _cursorDiagNwSe,   // ↘↖
                3 or 7 => _cursorDiagNeSw,   // ↙↗
                _      => HelperFunctions.DefaultCursor,
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

        protected override void DrawTrackers(DrawingContext drawingContext, DpiScale uiscale)
        {
            // Handle positions are computed in unrotated (axis-aligned) space
            // by GetHandle, which is what MakeHitTest expects (it inverse-
            // transforms incoming pointer coords). But the visual trackers
            // must sit on top of the rotated shape, so wrap the whole thing
            // in the same rotation transform DrawObject uses.
            if (Angle == 0)
            {
                base.DrawTrackers(drawingContext, uiscale);
                return;
            }

            var rotateMatrix =
                Matrix.CreateTranslation(-CenterOfRotation.X, -CenterOfRotation.Y) *
                Matrix.CreateRotation(Angle * Math.PI / 180.0) *
                Matrix.CreateTranslation(CenterOfRotation.X, CenterOfRotation.Y);

            using (drawingContext.PushTransform(rotateMatrix))
            {
                base.DrawTrackers(drawingContext, uiscale);
            }
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
