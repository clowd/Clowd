using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Clowd.Ui.Controls.ColorPicker;

/// <summary>
/// Custom Avalonia port of Clowd WPF ColorWheel — a circular HSL color picker
/// where hue is encoded as the angle around the centre and saturation as the
/// distance from the centre. Lightness is implicit (1 - saturation * 0.5),
/// matching the WPF original which used an HLSL pixel shader; here the wheel
/// is rendered into a WriteableBitmap once per size change.
/// </summary>
public class ColorWheel : Control
{
    public static readonly StyledProperty<HslRgbColor?> CurrentColorProperty =
        AvaloniaProperty.Register<ColorWheel, HslRgbColor?>(
            nameof(CurrentColor),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public HslRgbColor? CurrentColor
    {
        get => GetValue(CurrentColorProperty);
        set => SetValue(CurrentColorProperty, value);
    }

    private const double CursorSize = 10;
    private const double HalfCursor = CursorSize / 2;

    private WriteableBitmap? _bitmap;
    private Point _cursorPos;

    public ColorWheel()
    {
        ClipToBounds = false;
    }

    static ColorWheel()
    {
        AffectsRender<ColorWheel>(CurrentColorProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundsProperty)
        {
            RebuildBitmap();
            UpdateCursor();
            InvalidateVisual();
        }
        else if (change.Property == CurrentColorProperty)
        {
            if (change.OldValue is HslRgbColor oldC)
                oldC.PropertyChanged -= OnColorPropertyChanged;
            if (change.NewValue is HslRgbColor newC)
                newC.PropertyChanged += OnColorPropertyChanged;

            UpdateCursor();
            InvalidateVisual();
        }
    }

    private void OnColorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateCursor();
        InvalidateVisual();
    }

    private void RebuildBitmap()
    {
        var w = (int)Math.Round(Bounds.Width);
        var h = (int)Math.Round(Bounds.Height);
        if (w < 1 || h < 1)
        {
            _bitmap = null;
            return;
        }

        _bitmap = new WriteableBitmap(
            new PixelSize(w, h),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        var cx = w / 2.0;
        var cy = h / 2.0;
        var radius = Math.Min(cx, cy);
        var radiusSq = radius * radius;

        // Build the row buffer once and copy into the locked framebuffer.
        var rowBytes = w * 4;
        var pixels = new byte[w * h * 4];

        for (int y = 0; y < h; y++)
        {
            double dy = y - cy + 0.5;
            int rowOffset = y * rowBytes;
            for (int x = 0; x < w; x++)
            {
                double dx = x - cx + 0.5;
                double distSq = dx * dx + dy * dy;
                int o = rowOffset + x * 4;

                if (distSq > radiusSq)
                {
                    // Outside the wheel — fully transparent.
                    pixels[o + 0] = 0;
                    pixels[o + 1] = 0;
                    pixels[o + 2] = 0;
                    pixels[o + 3] = 0;
                    continue;
                }

                double distance = Math.Sqrt(distSq);
                double saturation = distance / radius;
                if (saturation > 1) saturation = 1;
                double angleR = Math.Atan2(-dy, dx);
                double hue = angleR * 180.0 / Math.PI;
                if (hue < 0) hue += 360.0;
                double lightness = 1 - (saturation * 0.5);

                HslToRgb(hue, 1.0, lightness, out var r, out var g, out var b);
                pixels[o + 0] = b;
                pixels[o + 1] = g;
                pixels[o + 2] = r;
                pixels[o + 3] = 255;
            }
        }

        using var fb = _bitmap.Lock();
        if (fb.RowBytes == rowBytes)
        {
            Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        }
        else
        {
            // Stride padding — copy row by row.
            for (int y = 0; y < h; y++)
                Marshal.Copy(pixels, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
        }
    }

    private void UpdateCursor()
    {
        if (CurrentColor == null || Bounds.Width < 1 || Bounds.Height < 1)
        {
            _cursorPos = new Point(Bounds.Width / 2, Bounds.Height / 2);
            return;
        }

        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2.0;
        var angle = CurrentColor.Hue * Math.PI / 180.0;

        double satRadius;
        if (CurrentColor.Saturation == 0)
        {
            satRadius = 0;
        }
        else
        {
            // Match WPF GetColorLocation(HslRgbColor) — saturation projected
            // through the lightness mapping used by the wheel.
            double mult = 1 - (Math.Max(0.5, CurrentColor.Lightness) / 0.5 - 1);
            satRadius = radius * mult;
        }

        double cx = Bounds.Width / 2.0;
        double cy = Bounds.Height / 2.0;
        double x = cx + Math.Cos(angle) * satRadius;
        double y = cy - Math.Sin(angle) * satRadius;
        _cursorPos = new Point(x, y);
    }

    public override void Render(DrawingContext ctx)
    {
        if (_bitmap == null)
            RebuildBitmap();

        if (_bitmap != null)
            ctx.DrawImage(_bitmap, new Rect(0, 0, Bounds.Width, Bounds.Height));

        if (CurrentColor == null)
            return;

        // Diamond cursor (rotated square) anchored at the current colour.
        var p = _cursorPos;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(p.X, p.Y - HalfCursor), true);
            g.LineTo(new Point(p.X + HalfCursor, p.Y));
            g.LineTo(new Point(p.X, p.Y + HalfCursor));
            g.LineTo(new Point(p.X - HalfCursor, p.Y));
            g.EndFigure(true);
        }

        var fill = new SolidColorBrush(CurrentColor.ToColor());
        ctx.DrawGeometry(fill, new Pen(Brushes.Black, 1), geo);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        if (!IsPointInWheel(p)) return;
        e.Pointer.Capture(this);
        SetColorFromPoint(p);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (ReferenceEquals(e.Pointer.Captured, this))
            SetColorFromPoint(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        SetColorFromPoint(e.GetPosition(this));
        e.Pointer.Capture(null);
    }

    private bool IsPointInWheel(Point p)
    {
        var cx = Bounds.Width / 2.0;
        var cy = Bounds.Height / 2.0;
        var radius = Math.Min(cx, cy);
        var dx = p.X - cx;
        var dy = p.Y - cy;
        return (dx * dx + dy * dy) <= (radius * radius);
    }

    private void SetColorFromPoint(Point p)
    {
        if (CurrentColor == null) return;
        var cx = Bounds.Width / 2.0;
        var cy = Bounds.Height / 2.0;
        var radius = Math.Min(cx, cy);
        var dx = p.X - cx;
        var dy = p.Y - cy;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        double saturation = Math.Min(1, distance / radius);
        double lightness = 1 - (saturation * 0.5);
        double angleR = Math.Atan2(-dy, dx);
        double hue = angleR * 180.0 / Math.PI;
        if (hue < 0) hue += 360.0;

        // Mutate the existing model in place — keeps PropertyChanged subscribers attached.
        CurrentColor.Hue = hue;
        CurrentColor.Saturation = 1;
        CurrentColor.Lightness = lightness;
    }

    private static void HslToRgb(double hue, double saturation, double lightness,
                                 out byte r, out byte g, out byte b)
    {
        double chroma = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        double h1 = hue / 60.0;
        double x = chroma * (1 - Math.Abs((h1 % 2) - 1));
        double m = lightness - 0.5 * chroma;
        double r1, g1, b1;

        if (h1 < 1)      { r1 = chroma; g1 = x;      b1 = 0; }
        else if (h1 < 2) { r1 = x;      g1 = chroma; b1 = 0; }
        else if (h1 < 3) { r1 = 0;      g1 = chroma; b1 = x; }
        else if (h1 < 4) { r1 = 0;      g1 = x;      b1 = chroma; }
        else if (h1 < 5) { r1 = x;      g1 = 0;      b1 = chroma; }
        else             { r1 = chroma; g1 = 0;      b1 = x; }

        r = (byte)Math.Round(255 * (r1 + m));
        g = (byte)Math.Round(255 * (g1 + m));
        b = (byte)Math.Round(255 * (b1 + m));
    }
}
