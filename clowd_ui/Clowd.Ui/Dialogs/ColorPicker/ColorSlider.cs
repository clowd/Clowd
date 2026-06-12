using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Clowd.UI.Dialogs.ColorPicker
{
    /// <summary>
    /// Horizontal gradient slider with a black/white triangle thumb that hangs below the track.
    /// Ported from the WPF Border subclass; in Avalonia it is a plain Control with its own
    /// Background / CornerRadius properties (§2.8). ClipToBounds stays false so the thumb
    /// overhang is not clipped.
    /// </summary>
    public partial class ColorSlider : Control
    {
        public static readonly StyledProperty<double> ValueProperty =
            AvaloniaProperty.Register<ColorSlider, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

        public double Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public static readonly StyledProperty<double> ValueMaxProperty =
            AvaloniaProperty.Register<ColorSlider, double>(nameof(ValueMax));

        public double ValueMax
        {
            get => GetValue(ValueMaxProperty);
            set => SetValue(ValueMaxProperty, value);
        }

        public static readonly StyledProperty<IBrush> SliderBrushProperty =
            AvaloniaProperty.Register<ColorSlider, IBrush>(nameof(SliderBrush));

        public IBrush SliderBrush
        {
            get => GetValue(SliderBrushProperty);
            set => SetValue(SliderBrushProperty, value);
        }

        public static readonly StyledProperty<IBrush> BackgroundProperty =
            AvaloniaProperty.Register<ColorSlider, IBrush>(nameof(Background));

        public IBrush Background
        {
            get => GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
            AvaloniaProperty.Register<ColorSlider, CornerRadius>(nameof(CornerRadius));

        public CornerRadius CornerRadius
        {
            get => GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        /// <summary>
        /// Raised when the user changes <see cref="Value"/> by clicking or dragging the slider.
        /// Not raised when Value is set programmatically — dialogs update the bound color from
        /// this event in code-behind (TwoWay path bindings like [Binding CurrentColor.Hue] do
        /// not reliably write drag values back to the INPC leaf).
        /// </summary>
        public event EventHandler<double> ValueChanged;

        static ColorSlider()
        {
            AffectsRender<ColorSlider>(ValueProperty, ValueMaxProperty, SliderBrushProperty,
                                       BackgroundProperty, CornerRadiusProperty);
        }

        protected void HandleMouse(PointerEventArgs e)
        {
            var pos = e.GetPosition(this);
            var max = ValueMax;
            if (Bounds.Width > 0 && max > 0)
            {
                var value = Math.Max(Math.Min(pos.X / Bounds.Width, 1), 0) * max;
                SetCurrentValue(ValueProperty, value);
                ValueChanged?.Invoke(this, value);
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            e.Pointer.Capture(this);
            HandleMouse(e);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (Equals(e.Pointer.Captured, this))
                HandleMouse(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (Equals(e.Pointer.Captured, this))
                e.Pointer.Capture(null);
        }

        public override void Render(DrawingContext drawingContext)
        {
            // draw background
            var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
            var radius = CornerRadius.TopLeft;
            if (Background != null)
                drawingContext.DrawRectangle(Background, null, bounds, radius, radius);
            if (SliderBrush != null)
                drawingContext.DrawRectangle(SliderBrush, null, bounds, radius, radius);

            // draw cursor triangle
            var max = ValueMax;
            var pos = max > 0 ? bounds.Width * (Value / max) : 0;
            const int triSize = 10;
            const int halfTriSize = triSize / 2;

            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                gc.BeginFigure(new Point(pos, bounds.Height - halfTriSize), true);
                gc.LineTo(new Point(pos - halfTriSize, bounds.Height + halfTriSize));
                gc.LineTo(new Point(pos + halfTriSize, bounds.Height + halfTriSize));
                gc.EndFigure(true);
            }

            drawingContext.DrawGeometry(Brushes.Black, new Pen(Brushes.White, 1), geo);
        }
    }
}
