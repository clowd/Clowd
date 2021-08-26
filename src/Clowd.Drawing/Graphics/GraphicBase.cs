using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RT.Serialization;

namespace Clowd.Drawing.Graphics
{
    public abstract class GraphicBase : SimpleNotifyObject
    {
        public Color ObjectColor
        {
            get => _objectColor;
            set => Set(ref _objectColor, value);
        }

        public double LineWidth
        {
            get => _lineWidth;
            set => Set(ref _lineWidth, value);
        }

        public bool DropShadowEffect
        {
            get => _dropShadowEffect;
            set => Set(ref _dropShadowEffect, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        private Color _objectColor;
        private double _lineWidth;
        private bool _dropShadowEffect;
        [ClassifyIgnore] private bool _isSelected;

        protected GraphicBase()
        { }

        protected GraphicBase(Color objectColor, double lineWidth) : this(objectColor, lineWidth, true)
        { }

        protected GraphicBase(Color objectColor, double lineWidth, bool dropShadowEffect)
        {
            _objectColor = objectColor;
            _lineWidth = lineWidth;
            _dropShadowEffect = dropShadowEffect;
        }

        [ClassifyIgnore] public abstract Rect Bounds { get; }
        [ClassifyIgnore] internal abstract int HandleCount { get; }
        [ClassifyIgnore] internal static double UnscaledControlSize { get; set; } = 12.0;
        [ClassifyIgnore] internal static double UnscaledBorderSize { get; set; } = 2.0;
        [ClassifyIgnore] internal static SolidColorBrush HandleBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0, 0, 255));
        [ClassifyIgnore] internal static SolidColorBrush HandleBrush2 { get; set; } = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));

        internal abstract bool Contains(Point point);
        internal abstract void Move(double deltaX, double deltaY);
        internal abstract void MoveHandleTo(Point point, int handleNumber);
        internal abstract Cursor GetHandleCursor(int handleNumber);
        internal abstract Point GetHandle(int handleNumber);

        internal void DisconnectFromParent() => ClearPropertyChangedHandlers();

        internal virtual void Activate(DrawingCanvas canvas) { }

        internal virtual void Normalize() { }

        internal virtual int MakeHitTest(Point point, DpiScale uiscale)
        {
            if (IsSelected)
                for (int i = 1; i <= HandleCount; i++)
                    if (GetHandleRectangle(i, uiscale).Contains(point))
                        return i;

            if (Contains(point))
                return 0;

            return -1;
        }

        internal virtual void Draw(DrawingContext ctx, DpiScale uiscale)
        {
            DrawObject(ctx);
            if (IsSelected)
                DrawTrackers(ctx, uiscale);
        }

        internal virtual void DrawObject(DrawingContext ctx)
        {
        }

        protected virtual void DrawDashedBorder(DrawingContext ctx, Rect rect)
        {
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(127, 255, 255, 255)), LineWidth), rect);
            DashStyle dashStyle = new DashStyle();
            dashStyle.Dashes.Add(4);
            Pen dashedPen = new Pen(new SolidColorBrush(Color.FromArgb(127, 0, 0, 0)), LineWidth);
            dashedPen.DashStyle = dashStyle;
            ctx.DrawRectangle(null, dashedPen, rect);
        }

        protected virtual void DrawTrackers(DrawingContext ctx, DpiScale uiscale)
        {
            for (int i = 1; i <= HandleCount; i++)
                DrawSingleTracker(ctx, i, uiscale);
        }

        protected virtual void DrawSingleTracker(DrawingContext ctx, int handleNum, DpiScale uiscale)
        {
            var rectangle = GetHandleRectangle(handleNum, uiscale);
            ctx.DrawEllipse(HandleBrush, null, new Point(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Width / 2), rectangle.Width / 2 - 1, rectangle.Height / 2 - 1);
            ctx.DrawEllipse(HandleBrush2, null, new Point(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Width / 2), rectangle.Width / 2 - 2, rectangle.Height / 2 - 2);
            ctx.DrawEllipse(HandleBrush, null, new Point(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Width / 2), rectangle.Width / 2 - 3, rectangle.Height / 2 - 3);
        }

        protected virtual Rect GetHandleRectangle(int handleNumber, DpiScale uiscale)
        {
            // Handle rectangle should scale with window DPI
            Point point = GetHandle(handleNumber);
            double size = UnscaledControlSize * uiscale.DpiScaleX;
            return new Rect(point.X - size / 2, point.Y - size / 2, size, size);
        }
    }
}
