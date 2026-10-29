using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Prisc.Interop;

namespace Prisc.UI.Dialogs.ColorPicker
{
    /// <summary>Which OKLCH channel an <see cref="OkLchChart"/> holds fixed (the other two are its axes).</summary>
    public enum OkLchChartPlane
    {
        Lightness,
        Chroma,
        Hue,
    }

    /// <summary>
    /// One of the OKLCH tab's gamut charts, as oklch.com draws them: a slice through OKLCH at the
    /// current value of one channel, painted wherever sRGB can show the color and left empty where it
    /// cannot, so the shape of the gamut is the picture.
    /// <list type="bullet">
    /// <item><see cref="OkLchChartPlane.Lightness"/>: hue across, chroma up, at the current lightness.</item>
    /// <item><see cref="OkLchChartPlane.Chroma"/>: hue across, lightness up, at the current chroma.</item>
    /// <item><see cref="OkLchChartPlane.Hue"/>: lightness across, chroma up, at the current hue.</item>
    /// </list>
    /// A marker sits on the current color, and a click or drag sets the two channels the chart spans
    /// (raising <see cref="Picked"/>). The tab places each under the slider of a channel it spans.
    /// </summary>
    /// <remarks>
    /// The slice is rendered per device pixel into one bitmap, and only again when the fixed channel
    /// or the size changes: moving the marker along the slice is just a redraw.
    /// </remarks>
    public class OkLchChart : Control
    {
        public const double MarkerDiameter = 16;

        public static readonly StyledProperty<OkLchChartPlane> PlaneProperty =
            AvaloniaProperty.Register<OkLchChart, OkLchChartPlane>(nameof(Plane));

        public OkLchChartPlane Plane
        {
            get => GetValue(PlaneProperty);
            set => SetValue(PlaneProperty, value);
        }

        /// <summary>The fill behind the slice, showing where the gamut ends.</summary>
        public static readonly StyledProperty<IBrush> BackgroundProperty =
            AvaloniaProperty.Register<OkLchChart, IBrush>(nameof(Background));

        public IBrush Background
        {
            get => GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        public static readonly StyledProperty<IBrush> BorderBrushProperty =
            AvaloniaProperty.Register<OkLchChart, IBrush>(nameof(BorderBrush));

        public IBrush BorderBrush
        {
            get => GetValue(BorderBrushProperty);
            set => SetValue(BorderBrushProperty, value);
        }

        public static readonly StyledProperty<double> CornerRadiusProperty =
            AvaloniaProperty.Register<OkLchChart, double>(nameof(CornerRadius), 6);

        public double CornerRadius
        {
            get => GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        /// <summary>The user clicked or dragged on the chart; two of the three channels changed.</summary>
        public event EventHandler Picked;

        public double Lightness { get; private set; }
        public double Chroma { get; private set; }
        public double Hue { get; private set; }

        private WriteableBitmap _bitmap;
        private int[] _pixels;
        private bool _sliceDirty = true;

        static OkLchChart()
        {
            AffectsRender<OkLchChart>(BackgroundProperty, BorderBrushProperty, CornerRadiusProperty);
            ClipToBoundsProperty.OverrideDefaultValue<OkLchChart>(false);
        }

        /// <summary>Moves the marker to the current color, re-rendering the slice if the fixed channel moved.</summary>
        public void SetValues(double lightness, double chroma, double hue)
        {
            var fixedBefore = FixedValue;
            (Lightness, Chroma, Hue) = (lightness, chroma, hue);
            if (FixedValue != fixedBefore)
                _sliceDirty = true;
            InvalidateVisual();
        }

        private double FixedValue => Plane switch
        {
            OkLchChartPlane.Lightness => Lightness,
            OkLchChartPlane.Chroma => Chroma,
            _ => Hue,
        };

        // the axes: what each runs over, left to right and bottom to top
        private double XMax => Plane == OkLchChartPlane.Hue ? 1 : 360;
        private double YMax => Plane == OkLchChartPlane.Chroma ? 1 : OkLchColor.ChromaMax;

        private (double X, double Y) Position => Plane switch
        {
            OkLchChartPlane.Lightness => (Hue, Chroma),
            OkLchChartPlane.Chroma => (Hue, Lightness),
            _ => (Lightness, Chroma),
        };

        private (double L, double C, double H) At(double x, double y) => Plane switch
        {
            OkLchChartPlane.Lightness => (Lightness, y, x),
            OkLchChartPlane.Chroma => (y, Chroma, x),
            _ => (x, y, Hue),
        };

        private OkLchChannel FixedChannel => Plane switch
        {
            OkLchChartPlane.Lightness => OkLchChannel.Lightness,
            OkLchChartPlane.Chroma => OkLchChannel.Chroma,
            _ => OkLchChannel.Hue,
        };

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == PlaneProperty || change.Property == BoundsProperty)
            {
                _sliceDirty = true;
                InvalidateVisual();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _bitmap?.Dispose();
            _bitmap = null;
            _sliceDirty = true;
        }

        public override void Render(DrawingContext context)
        {
            var size = Bounds.Size;
            if (size.Width <= 0 || size.Height <= 0)
                return;

            var rect = new Rect(size);
            var rounded = new RoundedRect(rect, CornerRadius);

            if (Background != null)
                context.DrawRectangle(Background, null, rounded);

            if (EnsureSlice(size) is { } slice)
            {
                using (context.PushClip(rounded))
                    context.DrawImage(slice, new Rect(slice.Size), rect);
            }

            if (BorderBrush != null)
                context.DrawRectangle(null, new Pen(BorderBrush, 1), rounded.Deflate(0.5, 0.5));

            var (x, y) = Position;
            var center = new Point(Math.Clamp(x / XMax, 0, 1) * size.Width, (1 - Math.Clamp(y / YMax, 0, 1)) * size.Height);
            var fill = new OkLchColor(Lightness, Chroma, Hue).ToColor();
            ColorWheel.DrawMarker(context, center, MarkerDiameter, fill);
        }

        private WriteableBitmap EnsureSlice(Size size)
        {
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var pixelSize = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale)));

            if (_bitmap == null || _bitmap.PixelSize != pixelSize)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(pixelSize, new Vector(96 * scale, 96 * scale), PixelFormat.Bgra8888, AlphaFormat.Premul);
                _pixels = new int[pixelSize.Width * pixelSize.Height];
                _sliceDirty = true;
            }

            if (_sliceDirty)
            {
                // one native call per slice: prisc_rust_core::color::chart samples every pixel
                // centre, then resamples the pixels on the gamut's edge on a grid and gives them
                // the share that is in gamut as their alpha, so the cut-out edge is smooth
                OkLch.PaintSlice(FixedChannel, Lightness, Chroma, Hue, pixelSize.Width, pixelSize.Height,
                    MemoryMarshal.Cast<int, uint>(_pixels.AsSpan()));
                using var buffer = _bitmap.Lock();
                for (var row = 0; row < pixelSize.Height; row++)
                    Marshal.Copy(_pixels, row * pixelSize.Width, buffer.Address + row * buffer.RowBytes, pixelSize.Width);
                _sliceDirty = false;
            }

            return _bitmap;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            e.Pointer.Capture(this);
            Pick(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (Equals(e.Pointer.Captured, this))
                Pick(e.GetPosition(this));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (Equals(e.Pointer.Captured, this))
                e.Pointer.Capture(null);
        }

        private void Pick(Point point)
        {
            var size = Bounds.Size;
            if (size.Width <= 0 || size.Height <= 0)
                return;

            var x = Math.Clamp(point.X / size.Width, 0, 1) * XMax;
            var y = (1 - Math.Clamp(point.Y / size.Height, 0, 1)) * YMax;
            var (l, c, h) = At(x, y);
            // the right edge is 360 rather than 0, so the marker stays under the pointer
            SetValues(l, c, h);
            Picked?.Invoke(this, EventArgs.Empty);
        }
    }
}
