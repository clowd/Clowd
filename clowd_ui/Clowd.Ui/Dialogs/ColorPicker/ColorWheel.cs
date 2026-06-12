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
    /// Hue/saturation color wheel. The WPF HLSL pixel shader is replaced by a CPU-generated
    /// WriteableBitmap using the same sector math, with a 1.5 device-pixel feathered alpha rim
    /// (decision table #60). The bitmap is rebuilt when the control size or the window render
    /// scaling changes.
    /// </summary>
    public partial class ColorWheel : Control
    {
        public static readonly StyledProperty<HslRgbColor> CurrentColorProperty =
            AvaloniaProperty.Register<ColorWheel, HslRgbColor>(nameof(CurrentColor), defaultBindingMode: BindingMode.TwoWay);

        public HslRgbColor CurrentColor
        {
            get => GetValue(CurrentColorProperty);
            set => SetValue(CurrentColorProperty, value);
        }

        private const int CursorSize = 10;
        private const int HalfCursorSize = CursorSize / 2;
        private const double FeatherPx = 1.5;

        private static readonly IPen _cursorPen = new Pen(Brushes.Black, 1);

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
                // NOTE: the WPF source detached the handler from the NEW value here (bug);
                // fixed deliberately per MIGRATION.md §4 WP10 — detach from the OLD value.
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
                SetColor(pt);
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (Equals(e.Pointer.Captured, this))
            {
                e.Pointer.Capture(null);
                SetColor(e.GetPosition(this));
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (Equals(e.Pointer.Captured, this))
                SetColor(e.GetPosition(this));
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

            var color = CurrentColor;
            if (color != null)
            {
                var lc = GetColorLocation(color);
                var rect = new Rect(lc.X - HalfCursorSize, lc.Y - HalfCursorSize, CursorSize, CursorSize);
                var rotation = Matrix.CreateTranslation(-lc.X, -lc.Y)
                               * Matrix.CreateRotation(Math.PI / 4)
                               * Matrix.CreateTranslation(lc.X, lc.Y);
                using (context.PushTransform(rotation))
                    context.DrawRectangle(new SolidColorBrush(color.ToColor()), _cursorPen, rect);
            }
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

        protected virtual Point GetColorLocation(HslRgbColor color)
        {
            double angle = color.Hue * Math.PI / 180;
            double radius = Bounds.Width / 2;

            if (color.Saturation == 0)
            {
                radius = 0;
            }
            else
            {
                double mult = 1 - (Math.Max(0.5, color.Lightness) / 0.5 - 1);
                radius *= mult;
            }

            return this.GetColorLocation(angle, radius);
        }

        protected Point GetColorLocation(double angleR, double radius)
        {
            double x = (Bounds.Width / 2) + Math.Cos(angleR) * radius;
            double y = (Bounds.Height / 2) - Math.Sin(angleR) * radius;
            return new Point(x, y);
        }

        protected bool IsPointInWheel(Point point)
        {
            // http://my.safaribooksonline.com/book/programming/csharp/9780672331985/graphics-with-windows-forms-and-gdiplus/ch17lev1sec21
            Point normalized = new Point(point.X - (Bounds.Width / 2), point.Y - (Bounds.Height / 2));
            double radius = Bounds.Width / 2;
            return normalized.X * normalized.X + normalized.Y * normalized.Y <= radius * radius;
        }

        protected virtual void SetColor(Point point)
        {
            double radius = Bounds.Width / 2;
            double dx = Math.Abs(point.X - (Bounds.Width / 2));
            double dy = Math.Abs(point.Y - (Bounds.Height / 2));
            double angle = Math.Atan(dy / dx) / Math.PI * 180;
            if (double.IsNaN(angle)) angle = 0; // dx == dy == 0 (exact center click)
            double distance = Math.Pow(Math.Pow(dx, 2) + Math.Pow(dy, 2), 0.5);
            double saturation = Math.Min(1, distance / radius);
            double lightness = 1 - (saturation * 0.5);

            if (point.X < (Bounds.Width / 2))
            {
                angle = 180 - angle;
            }

            if (point.Y > (Bounds.Height / 2))
            {
                angle = 360 - angle;
            }

            CurrentColor = new HslRgbColor(angle, 1, lightness, CurrentColor?.Alpha ?? 1);
        }
    }
}
