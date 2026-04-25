using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Clowd.Ui.Controls.ColorPicker;

public class ColorSelectedEventArgs : EventArgs
{
    public Color SelectedColor { get; }
    public int ClickCount { get; }

    public ColorSelectedEventArgs(Color selectedColor, int clickCount)
    {
        SelectedColor = selectedColor;
        ClickCount = clickCount;
    }
}

/// <summary>
/// Custom Avalonia port of Clowd WPF ColorPaletteItem — a clickable color
/// swatch with hover and selection borders, and a Clicked event carrying the
/// pointer click count so callers can distinguish single vs. double clicks.
/// </summary>
public class ColorPaletteItem : Control
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<ColorPaletteItem, Color>(nameof(Color));

    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<ColorPaletteItem, bool>(nameof(IsSelected));

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public event EventHandler<ColorSelectedEventArgs>? Clicked;

    private static readonly Pen BlackPen = new Pen(Brushes.Black, 1);
    private static readonly Pen WhitePen = new Pen(Brushes.White, 1);

    static ColorPaletteItem()
    {
        AffectsRender<ColorPaletteItem>(ColorProperty, IsSelectedProperty);
    }

    public ColorPaletteItem()
    {
    }

    public ColorPaletteItem(Color color)
    {
        Color = color;
        Focusable = false;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;
        Clicked?.Invoke(this, new ColorSelectedEventArgs(Color, e.ClickCount));
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;

        ctx.FillRectangle(new SolidColorBrush(Color), new Rect(0, 0, w, h));

        if (IsPointerOver)
        {
            ctx.DrawRectangle(null, BlackPen, new Rect(0.5, 0.5, w - 1, h - 1));
            ctx.DrawRectangle(null, WhitePen, new Rect(1.5, 1.5, w - 3, h - 3));
        }
        else if (IsSelected)
        {
            ctx.DrawRectangle(null, WhitePen, new Rect(0.5, 0.5, w - 1, h - 1));
            ctx.DrawRectangle(null, BlackPen, new Rect(1.5, 1.5, w - 3, h - 3));
        }
    }
}
