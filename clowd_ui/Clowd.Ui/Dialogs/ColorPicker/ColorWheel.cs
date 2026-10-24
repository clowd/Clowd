using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Clowd.Util;

namespace Clowd.UI.Dialogs.ColorPicker
{
    /// <summary>
    /// Hue/saturation color wheel: hue around the circle (red at three o'clock, turning
    /// counter-clockwise), HSV saturation from the white centre out to the pure hue at the rim. The
    /// wheel itself is a CPU-generated WriteableBitmap with a 1.5 device-pixel feathered alpha rim,
    /// rebuilt when the control size or the window render scaling changes.
    /// </summary>
    /// <remarks>
    /// The Simple tab uses it as it stands: one marker for <see cref="CurrentColor"/>, and a click
    /// or drag picks hue and saturation while keeping the color's brightness (that is the L slider's
    /// job). The Palette tab's multi-marker wheel derives from it, overriding
    /// <see cref="RenderMarkers"/> and <see cref="OnPick"/> and reusing the geometry helpers
    /// (<see cref="PointFor"/>, <see cref="HueSaturationAt"/>, <see cref="DrawMarker"/>).
    /// </remarks>
    public class ColorWheel : Control
    {
        public static readonly StyledProperty<HslRgbColor> CurrentColorProperty =
            AvaloniaProperty.Register<ColorWheel, HslRgbColor>(nameof(CurrentColor), defaultBindingMode: BindingMode.TwoWay);

        public HslRgbColor CurrentColor
        {
            get => GetValue(CurrentColorProperty);
            set => SetValue(CurrentColorProperty, value);
        }

        /// <summary>The Simple tab's marker: the mockup's 20px ring.</summary>
        public const double MarkerDiameter = 20;

        private const double FeatherPx = 1.5;

        private static readonly IPen MarkerRing = new Pen(Brushes.White, 2);

        // the marker's thin dark outline and its drop, so a white ring still reads on the wheel's
        // white centre
        private static readonly BoxShadows MarkerShadow = BoxShadows.Parse("0 0 0 1 #80000000, 0 2 5 0 #66000000");

        private WriteableBitmap _wheelBitmap;
        private int _bitmapPixelWidth;
        private int _bitmapPixelHeight;
        private TopLevel _topLevel;

        public ColorWheel()
        {
            SizeChanged += (_, _) => InvalidateVisual();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _topLevel = TopLevel.GetTopLevel(this);
            if (_topLevel != null)
                _topLevel.ScalingChanged += OnScalingChanged;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_topLevel != null)
                _topLevel.ScalingChanged -= OnScalingChanged;
            _topLevel = null;
        }

        private void OnScalingChanged(object sender, EventArgs e)
        {
            DisposeBitmap();
            InvalidateVisual();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == CurrentColorProperty)
            {
                var oldValue = (HslRgbColor)change.OldValue;
                var newValue = (HslRgbColor)change.NewValue;

                if (oldValue != null)
                    oldValue.PropertyChanged -= ColorPropertyChanged;

                if (newValue != null)
                    newValue.PropertyChanged += ColorPropertyChanged;

                InvalidateVisual();
            }
        }

        private void ColorPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var pt = e.GetPosition(this);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && IsPointInWheel(pt))
            {
                e.Pointer.Capture(this);
                e.Handled = true;
                OnPick(pt, true);
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (Equals(e.Pointer.Captured, this))
            {
                e.Pointer.Capture(null);
                OnPick(e.GetPosition(this), false);
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (Equals(e.Pointer.Captured, this))
                OnPick(e.GetPosition(this), false);
        }

        /// <summary>
        /// A press on the wheel (<paramref name="isStart"/>) or a drag that began on it. Picks hue
        /// and saturation at the point and keeps the current color's brightness and alpha — except
        /// from pure black, where every hue is black and the pick would show nothing, so it comes
        /// up to full brightness instead.
        /// </summary>
        protected virtual void OnPick(Point point, bool isStart)
        {
            var (hue, saturation) = HueSaturationAt(point);
            var current = CurrentColor;
            var value = current == null ? 1 : HsvValue(current);
            if (value <= 0)
                value = 1;

            // straight from HSV to HSL rather than through RGB, which would lose the hue at the
            // white centre (and so the marker's angle)
            var lightness = value * (1 - saturation / 2);
            var hslSaturation = lightness is <= 0 or >= 1 ? 0 : (value - lightness) / Math.Min(lightness, 1 - lightness);
            CurrentColor = new HslRgbColor(hue, hslSaturation, lightness, current?.Alpha ?? 1);
        }

        public override void Render(DrawingContext context)
        {
            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w <= 0 || h <= 0)
                return;

            double scaling = (_topLevel ?? TopLevel.GetTopLevel(this))?.RenderScaling ?? 1.0;
            EnsureWheelBitmap(w, h, scaling);

            if (_wheelBitmap != null)
            {
                context.DrawImage(_wheelBitmap,
                                  new Rect(0, 0, _wheelBitmap.Size.Width, _wheelBitmap.Size.Height),
                                  new Rect(0, 0, w, h));
            }

            RenderMarkers(context);
        }

        /// <summary>Draws the markers over the wheel: by default the one for <see cref="CurrentColor"/>.</summary>
        protected virtual void RenderMarkers(DrawingContext context)
        {
            var color = CurrentColor;
            if (color == null)
                return;

            var center = PointFor(color.Hue, HsvSaturation(color));
            DrawMarker(context, center, MarkerDiameter, color.ToColor());
        }

        /// <summary>
        /// A wheel marker: a disc of <paramref name="fill"/> inside a 2px white ring, with a thin
        /// dark outline and a soft drop so it reads on any color. A <paramref name="halo"/> (the
        /// Palette tab's highlighted marker) is drawn as a 3px band outside the outline.
        /// </summary>
        public static void DrawMarker(DrawingContext context, Point center, double diameter, Color fill, IBrush halo = null)
        {
            var radius = diameter / 2;
            var disc = new RoundedRect(new Rect(center.X - radius, center.Y - radius, diameter, diameter), radius);

            if (halo != null)
            {
                // 4px past the disc: the 1px outline, then the 3px band (the mockup's 4px spread)
                var haloRadius = radius + 4;
                context.DrawEllipse(halo, null, center, haloRadius, haloRadius);
            }

            context.DrawRectangle(new SolidColorBrush(fill), null, disc, MarkerShadow);
            context.DrawEllipse(null, MarkerRing, center, radius - 1, radius - 1);
        }

        /// <summary>
        /// Where a color at <paramref name="hue"/> / HSV <paramref name="saturation"/> sits on the
        /// wheel. A marker is centred on its color, as a slider's thumb is on its value, so a fully
        /// saturated color's marker sits on the rim and overhangs the wheel by half its width.
        /// </summary>
        public Point PointFor(double hue, double saturation)
        {
            var c = Bounds.Width / 2;
            var radius = c * Math.Clamp(saturation, 0, 1);
            var angle = hue * Math.PI / 180;
            return new Point(c + Math.Cos(angle) * radius, (Bounds.Height / 2) - Math.Sin(angle) * radius);
        }

        /// <summary>The hue (degrees) and HSV saturation under <paramref name="point"/>; points
        /// outside the wheel clamp to the rim.</summary>
        public (double Hue, double Saturation) HueSaturationAt(Point point)
        {
            var dx = point.X - Bounds.Width / 2;
            var dy = (Bounds.Height / 2) - point.Y;
            var radius = Bounds.Width / 2;
            var hue = Math.Atan2(dy, dx) * 180 / Math.PI;
            if (hue < 0)
                hue += 360;
            var saturation = radius > 0 ? Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / radius) : 0;
            return (hue, saturation);
        }

        /// <summary>HSV value (brightness) of an HSL color.</summary>
        public static double HsvValue(HslRgbColor color) =>
            color.Lightness + color.Saturation * Math.Min(color.Lightness, 1 - color.Lightness);

        /// <summary>HSV saturation of an HSL color: what places it out from the wheel's centre.</summary>
        public static double HsvSaturation(HslRgbColor color)
        {
            var value = HsvValue(color);
            return value <= 0 ? 0 : 2 * (1 - color.Lightness / value);
        }

        protected bool IsPointInWheel(Point point)
        {
            Point normalized = new Point(point.X - (Bounds.Width / 2), point.Y - (Bounds.Height / 2));
            double radius = Bounds.Width / 2;
            return normalized.X * normalized.X + normalized.Y * normalized.Y <= radius * radius;
        }

        private void DisposeBitmap()
        {
            _wheelBitmap?.Dispose();
            _wheelBitmap = null;
            _bitmapPixelWidth = 0;
            _bitmapPixelHeight = 0;
        }

        /// <summary>
        /// Generates the wheel bitmap at device-pixel resolution using the same math as the
        /// original ColorWheelShader.hlsl (value = 1; alpha = inside-circle), but with the hard
        /// "saturation &lt; 1" rim replaced by a 1.5px feathered alpha ramp.
        /// </summary>
        private void EnsureWheelBitmap(double width, double height, double scaling)
        {
            int pw = Math.Max(1, (int)Math.Round(width * scaling));
            int ph = Math.Max(1, (int)Math.Round(height * scaling));

            if (_wheelBitmap != null && pw == _bitmapPixelWidth && ph == _bitmapPixelHeight)
                return;

            DisposeBitmap();

            var bitmap = new WriteableBitmap(new PixelSize(pw, ph), new Vector(96, 96),
                                             PixelFormat.Bgra8888, AlphaFormat.Premul);

            double radiusPx = pw / 2.0;

            using (var fb = bitmap.Lock())
            {
                int stride = fb.RowBytes;
                var pixels = new byte[stride * ph];

                for (int y = 0; y < ph; y++)
                {
                    // uv = 2 * uv - 1; uv.y /= -1;
                    double uvy = -((((y + 0.5) / ph) * 2.0) - 1.0);
                    int row = y * stride;

                    for (int x = 0; x < pw; x++)
                    {
                        double uvx = (((x + 0.5) / pw) * 2.0) - 1.0;
                        double saturation = Math.Sqrt(uvx * uvx + uvy * uvy);

                        // feathered rim: fully opaque until 1.5px from the edge, then ramp to 0
                        double alpha = Math.Clamp((1.0 - saturation) * radiusPx / FeatherPx, 0.0, 1.0);
                        if (alpha <= 0)
                            continue;

                        double chroma = Math.Min(saturation, 1.0); // value = 1
                        double hue = 3.0 * (Math.PI - Math.Atan2(uvy, -uvx)) / Math.PI;
                        double second = chroma * (1.0 - Math.Abs((hue % 2.0) - 1.0));
                        double m = 1.0 - chroma;

                        double r, g, b;
                        if (hue < 1) { r = chroma; g = second; b = 0; }
                        else if (hue < 2) { r = second; g = chroma; b = 0; }
                        else if (hue < 3) { r = 0; g = chroma; b = second; }
                        else if (hue < 4) { r = 0; g = second; b = chroma; }
                        else if (hue < 5) { r = second; g = 0; b = chroma; }
                        else { r = chroma; g = 0; b = second; }

                        int i = row + (x * 4);
                        pixels[i + 0] = (byte)Math.Round((b + m) * alpha * 255.0);
                        pixels[i + 1] = (byte)Math.Round((g + m) * alpha * 255.0);
                        pixels[i + 2] = (byte)Math.Round((r + m) * alpha * 255.0);
                        pixels[i + 3] = (byte)Math.Round(alpha * 255.0);
                    }
                }

                Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
            }

            _wheelBitmap = bitmap;
            _bitmapPixelWidth = pw;
            _bitmapPixelHeight = ph;
        }
    }
}
