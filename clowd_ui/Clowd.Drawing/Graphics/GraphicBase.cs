using System;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;

namespace Clowd.Drawing.Graphics
{
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [JsonDerivedType(typeof(GraphicRectangle), "rect")]
    [JsonDerivedType(typeof(GraphicFilledRectangle), "filledRect")]
    [JsonDerivedType(typeof(GraphicEllipse), "ellipse")]
    [JsonDerivedType(typeof(GraphicLine), "line")]
    [JsonDerivedType(typeof(GraphicArrow), "arrow")]
    [JsonDerivedType(typeof(GraphicPolyLine), "polyline")]
    [JsonDerivedType(typeof(GraphicText), "text")]
    [JsonDerivedType(typeof(GraphicCount), "count")]
    [JsonDerivedType(typeof(GraphicImage), "image")]
    public abstract class GraphicBase : SimpleNotifyObject, IJsonOnDeserialized
    {
        public string Id
        {
            get => _id;
            set => Set(ref _id, value);
        }

        public virtual Color ObjectColor
        {
            get => _objectColor;
            set => Set(ref _objectColor, value);
        }

        public virtual double LineWidth
        {
            get => _lineWidth;
            set => Set(ref _lineWidth, value);
        }

        public virtual bool DropShadowEffect
        {
            get => _dropShadowEffect;
            set => Set(ref _dropShadowEffect, value);
        }

        [JsonIgnore]
        public virtual bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        /// <summary>
        /// True for "scaffolding" graphics that should be excluded from artwork
        /// bounds and serialization (e.g. <c>GraphicSelectionRectangle</c>).
        /// Concrete subclasses override this to opt out of artwork.
        /// </summary>
        [JsonIgnore]
        public virtual bool IsScaffolding => false;

        private string _id = Guid.NewGuid().ToString();
        private Color _objectColor;
        private double _lineWidth;
        private bool _dropShadowEffect;
        private bool _isSelected;

        // Public so System.Text.Json can construct subclasses during undo deserialize.
        public GraphicBase()
        { }

        protected GraphicBase(Color objectColor, double lineWidth) : this(objectColor, lineWidth, true)
        { }

        protected GraphicBase(Color objectColor, double lineWidth, bool dropShadowEffect)
        {
            _objectColor = objectColor;
            _lineWidth = lineWidth;
            _dropShadowEffect = dropShadowEffect;
        }

        [JsonIgnore] public abstract Rect Bounds { get; }
        [JsonIgnore] internal abstract int HandleCount { get; }
        [JsonIgnore] internal static double UnscaledControlSize { get; set; } = 12.0;
        [JsonIgnore] internal static double UnscaledBorderSize { get; set; } = 2.0;
        [JsonIgnore] internal static IBrush HandleBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0, 0, 255));
        [JsonIgnore] internal static IBrush HandleBrush2 { get; set; } = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));

        internal abstract bool Contains(Point point);
        internal abstract void Move(double deltaX, double deltaY);
        internal abstract void MoveHandleTo(Point point, int handleNumber);
        internal abstract Cursor GetHandleCursor(int handleNumber);
        internal abstract Point GetHandle(int handleNumber, DpiScale uiscale);

        internal void DisconnectFromParent() => ClearPropertyChangedHandlers();

        internal virtual void Activate(object canvas) { }

        internal virtual void Normalize() { }

        /// <summary>
        /// Called by System.Text.Json after deserialization. Recomputes
        /// derived state (centre of rotation, etc.) via <see cref="Normalize"/>.
        /// Subclasses can override <see cref="OnDeserializedCore"/> to run
        /// extra initialisation.
        /// </summary>
        void IJsonOnDeserialized.OnDeserialized()
        {
            Normalize();
            OnDeserializedCore();
        }

        protected virtual void OnDeserializedCore() { }

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
            // Default no-op. Concrete shapes override; Phase 2 leaves these as
            // stubs in the subclasses so the project compiles before Phase 3
            // implements real rendering.
        }

        protected virtual void DrawDashedBorder(DrawingContext ctx, Rect rect, double lineWidth = 2)
        {
            // White underlay so the dashes are visible on dark and light backgrounds
            var underlayPen = new Pen(new SolidColorBrush(Color.FromArgb(127, 255, 255, 255)), lineWidth);
            ctx.DrawRectangle(null, underlayPen, rect);

            var dashedPen = new Pen(new SolidColorBrush(Color.FromArgb(127, 0, 0, 0)), lineWidth)
            {
                DashStyle = new DashStyle(new double[] { 4 }, 0)
            };
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
            var scaledline = 1 * uiscale.DpiScaleX;
            var ellRadius = rectangle.Width / 2;
            var ellCenter = new Point(rectangle.Left + ellRadius, rectangle.Top + ellRadius);

            ctx.DrawEllipse(HandleBrush, null, ellCenter, ellRadius, ellRadius);
            ellRadius -= scaledline;
            ctx.DrawEllipse(HandleBrush2, null, ellCenter, ellRadius, ellRadius);
            ellRadius -= (scaledline * 2);
            if (ellRadius > 0)
                ctx.DrawEllipse(HandleBrush, null, ellCenter, ellRadius, ellRadius);
        }

        protected virtual Rect GetHandleRectangle(int handleNumber, DpiScale uiscale)
        {
            // Handle rectangle should scale with window DPI
            Point point = GetHandle(handleNumber, uiscale);
            double size = UnscaledControlSize * uiscale.DpiScaleX;
            return new Rect(point.X - size / 2, point.Y - size / 2, size, size);
        }

        protected virtual bool SetAndNormalize<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null, params string[]? dependentProperties)
        {
            var changed = Set(ref storage, value, propertyName, dependentProperties);
            if (changed) Normalize();
            return changed;
        }
    }
}
