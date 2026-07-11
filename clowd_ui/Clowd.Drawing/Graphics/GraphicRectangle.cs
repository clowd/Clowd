using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    /// <summary>
    /// PORT NOTE (exemplar): this type is the reference port for the render-cache
    /// infrastructure. A port touches exactly four pattern points — the aspect map entry
    /// (DeclarePropertyEffects), the cached bounds (ComputeBounds replacing the Bounds
    /// override), the _translating fast path (Move), and cached resources (RenderResources in
    /// every draw/hit-test path) — and changes NOTHING else: fields, JSON names, handle
    /// numbering, Normalize and the Move/MoveHandleTo raise pattern are contracts.
    /// </summary>
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

        protected GraphicRectangle()
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

        // PORT NOTE (ComputeBounds): the old Bounds override body moves here verbatim; the
        // cached base Bounds getter now serves reads, so this runs only after an invalidating
        // change (aspect map / bare raise), not on every read.
        protected override Rect ComputeBounds()
        {
            if (Angle == 0)
                return UnrotatedBounds;

            // no LINQ/array allocations — rotate the 4 corners and take the AABB
            var p0 = ApplyRotation(new Point(Left, Top));
            var p1 = ApplyRotation(new Point(Right, Top));
            var p2 = ApplyRotation(new Point(Left, Bottom));
            var p3 = ApplyRotation(new Point(Right, Bottom));
            var l = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
            var t = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
            var r = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
            var b = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));
            return new Rect(l, t, r - l, b - t);
        }

        // PORT NOTE (aspect map entry): call base first, then one entry per persisted property
        // this type declares. Geometry-defining properties invalidate Bounds|Geometry|Shadow;
        // IsSelected/ObjectColor exceptions are declared once in GraphicBase.
        internal override void DeclarePropertyEffects(Dictionary<string, InvalidationAspects> map)
        {
            base.DeclarePropertyEffects(map);
            const InvalidationAspects shape = InvalidationAspects.Bounds | InvalidationAspects.Geometry | InvalidationAspects.Shadow;
            map[nameof(Left)] = shape;
            map[nameof(Top)] = shape;
            map[nameof(Right)] = shape;
            map[nameof(Bottom)] = shape;
            map[nameof(Angle)] = shape;
            map[nameof(CenterOfRotation)] = shape;
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

        // PORT NOTE (_translating fast path): pure translation must not rebuild geometry or
        // shadows per pointer event. Set _translating around the existing mutations (do NOT
        // restructure the raise pattern — DrawingCanvas's BoundGraphicPropertyChanged whitelist
        // listens to these property names), offset the cached bounds once, then the usual bare
        // raise. Every raise made while _translating clears only the Geometry aspect.
        internal override void Move(double deltaX, double deltaY)
        {
            _translating = true;
            try
            {
                Left += deltaX;
                Right += deltaX;
                Top += deltaY;
                Bottom += deltaY;
                CenterOfRotation = new Point(
                    CenterOfRotation.X + deltaX,
                    CenterOfRotation.Y + deltaY);
                RenderCache.TranslateCachedBounds(deltaX, deltaY);
                OnPropertyChanged();
            }
            finally
            {
                _translating = false;
            }
        }

        internal Point ApplyRotation(Point point)
        {
            var dx = point.X - CenterOfRotation.X;
            var dy = point.Y - CenterOfRotation.Y;
            var angleRad = Angle / 180 * Math.PI;
            return new Point(
                CenterOfRotation.X + (dx * Math.Cos(angleRad) - dy * Math.Sin(angleRad)),
                CenterOfRotation.Y + (dy * Math.Cos(angleRad) + dx * Math.Sin(angleRad)));
        }

        internal Point UnapplyRotation(Point point)
        {
            var dx = point.X - CenterOfRotation.X;
            var dy = point.Y - CenterOfRotation.Y;
            var negAngleRad = -Angle / 180 * Math.PI;
            return new Point(
                CenterOfRotation.X + (dx * Math.Cos(negAngleRad) - dy * Math.Sin(negAngleRad)),
                CenterOfRotation.Y + (dy * Math.Cos(negAngleRad) + dx * Math.Sin(negAngleRad)));
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

        internal override Cursor GetHandleCursor(int handleNumber)
        {
            if (handleNumber == 0 || handleNumber > 9)
                return HelperFunctions.DefaultCursor;

            if (handleNumber == 9)
                return CursorResources.Rotate;

            var cursorNum = (int)((45 * handleNumber + Angle + 272.5) / 5) % 36;
            return CursorResources.GetResizeCursor(cursorNum);
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

            // If the user resized a rotated rectangle, we need to move the rectangle in such a way that the center of
            // rotation is in the center of the rectangle again.
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

        // Transform-scoping rule (§2.1 / decision #27): Draw owns the rotation scope and draws the
        // selection trackers inside it; DrawObject pushes the same rotation around the shape body only.
        internal override void Draw(DrawingContext ctx, DpiScale uiscale)
        {
            using (ctx.PushTransform(MatrixHelper.Rotation(Angle, CenterOfRotation)))
            {
                DrawRectangle(ctx);
                if (IsSelected)
                    DrawTrackers(ctx, uiscale);
            }
        }

        internal override void DrawObject(DrawingContext drawingContext)
        {
            if (drawingContext == null)
                throw new ArgumentNullException(nameof(drawingContext));

            using (drawingContext.PushTransform(MatrixHelper.Rotation(Angle, CenterOfRotation)))
                DrawRectangle(drawingContext);
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

            var basePen2 = RenderResources.GetPen(Colors.Green, scaledline);
            drawingContext.DrawLine(basePen2, anchor, center);
            drawingContext.DrawEllipse(Brushes.Green, null, center, radius, radius);
        }

        // PORT NOTE (RenderResources): draw and hit-test paths never allocate brushes/pens —
        // ask the process-wide cache instead (thickness/dash arguments identical to the old
        // `new Pen(new SolidColorBrush(...))` calls).
        internal virtual void DrawRectangle(DrawingContext drawingContext)
        {
            drawingContext.DrawRectangle(
                null,
                RenderResources.GetPen(ObjectColor, LineWidth),
                new Rect(UnrotatedBounds.Left + (LineWidth / 2),
                         UnrotatedBounds.Top + (LineWidth / 2),
                         Math.Max(1, UnrotatedBounds.Right - UnrotatedBounds.Left - LineWidth),
                         Math.Max(1, UnrotatedBounds.Bottom - UnrotatedBounds.Top - LineWidth)),
                LineWidth, LineWidth);
        }
    }
}
