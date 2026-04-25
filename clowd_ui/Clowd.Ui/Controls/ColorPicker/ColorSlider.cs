using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Clowd.Ui.Controls.ColorPicker;

/// <summary>
/// Custom Avalonia port of Clowd WPF ColorSlider — a horizontal slider that
/// renders a configurable gradient brush as the track and a downward-pointing
/// triangle cursor at the bottom edge.
/// </summary>
public class ColorSlider : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ColorSlider, double>(
            nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> ValueMaxProperty =
        AvaloniaProperty.Register<ColorSlider, double>(nameof(ValueMax), 1d);

    public static readonly StyledProperty<IBrush?> SliderBrushProperty =
        AvaloniaProperty.Register<ColorSlider, IBrush?>(nameof(SliderBrush));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<ColorSlider, IBrush?>(nameof(Background));

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<ColorSlider, CornerRadius>(nameof(CornerRadius));

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double ValueMax
    {
        get => GetValue(ValueMaxProperty);
        set => SetValue(ValueMaxProperty, value);
    }

    public IBrush? SliderBrush
    {
        get => GetValue(SliderBrushProperty);
        set => SetValue(SliderBrushProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    static ColorSlider()
    {
        AffectsRender<ColorSlider>(
            ValueProperty,
            ValueMaxProperty,
            SliderBrushProperty,
            BackgroundProperty,
            CornerRadiusProperty);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Pointer.Capture(this);
        HandlePointer(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (ReferenceEquals(e.Pointer.Captured, this))
            HandlePointer(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (ReferenceEquals(e.Pointer.Captured, this))
        {
            HandlePointer(e);
            e.Pointer.Capture(null);
        }
    }

    private void HandlePointer(PointerEventArgs e)
    {
        var w = Bounds.Width;
        if (w <= 0) return;
        var pos = e.GetPosition(this);
        Value = Math.Max(0, Math.Min(1, pos.X / w)) * ValueMax;
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var rect = new Rect(0, 0, w, h);
        var radius = CornerRadius.TopLeft;

        if (Background != null)
            ctx.DrawRectangle(Background, null, rect, radius, radius);
        if (SliderBrush != null)
            ctx.DrawRectangle(SliderBrush, null, rect, radius, radius);

        var valueMax = ValueMax <= 0 ? 1 : ValueMax;
        var pos = w * (Value / valueMax);
        const double triSize = 10;
        const double halfTri = triSize / 2;

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(pos, h - halfTri), true);
            g.LineTo(new Point(pos - halfTri, h + halfTri));
            g.LineTo(new Point(pos + halfTri, h + halfTri));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Brushes.Black, new Pen(Brushes.White, 1), geo);
    }
}
