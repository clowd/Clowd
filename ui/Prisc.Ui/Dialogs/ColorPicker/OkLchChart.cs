using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

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

        // linear light (0..1, in 4096 steps) to 8-bit sRGB: a table, since a slice is ~100k pixels
        // and the exact curve is three Math.Pow calls each
        private static readonly byte[] EncodeTable = BuildEncodeTable();

        private WriteableBitmap _bitmap;
        private int[] _pixels;
        private bool[] _inside;
        private bool _sliceDirty = true;

        // an edge pixel is sampled this many times a side to find how much of it the gamut covers
        private const int EdgeSamples = 4;

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
                _inside = new bool[_pixels.Length];
                _sliceDirty = true;
            }

            if (_sliceDirty)
            {
                PaintSlice(pixelSize.Width, pixelSize.Height);
                using var buffer = _bitmap.Lock();
                for (var row = 0; row < pixelSize.Height; row++)
                    Marshal.Copy(_pixels, row * pixelSize.Width, buffer.Address + row * buffer.RowBytes, pixelSize.Width);
                _sliceDirty = false;
            }

            return _bitmap;
        }

        /// <summary>
        /// Renders the slice: every pixel is first sampled once, at its centre, then the pixels on
        /// the gamut's edge (any whose neighbours disagree with it) are sampled again on a grid and
        /// given the share of it that is in gamut as their alpha, so the cut-out edge is smooth.
        /// </summary>
        private void PaintSlice(int width, int height)
        {
            double xMax = XMax, yMax = YMax;
            for (var py = 0; py < height; py++)
            {
                var y = (1 - (py + 0.5) / height) * yMax;
                var row = py * width;
                for (var px = 0; px < width; px++)
                {
                    var (l, c, h) = At((px + 0.5) / width * xMax, y);
                    var inside = OkLchColor.ToLinearSrgb(l, c, h, out var r, out var g, out var b);
                    _inside[row + px] = inside;
                    _pixels[row + px] = inside ? Opaque(r, g, b) : 0;
                }
            }

            for (var py = 0; py < height; py++)
            {
                for (var px = 0; px < width; px++)
                {
                    if (IsOnEdge(px, py, width, height))
                        _pixels[py * width + px] = EdgePixel(px, py, width, height);
                }
            }
        }

        private bool IsOnEdge(int px, int py, int width, int height)
        {
            var inside = _inside[py * width + px];
            for (var ny = Math.Max(0, py - 1); ny <= Math.Min(height - 1, py + 1); ny++)
            {
                for (var nx = Math.Max(0, px - 1); nx <= Math.Min(width - 1, px + 1); nx++)
                {
                    if (_inside[ny * width + nx] != inside)
                        return true;
                }
            }
            return false;
        }

        /// <summary>A premultiplied pixel: the mean of its in-gamut samples, at the coverage they make up.</summary>
        private int EdgePixel(int px, int py, int width, int height)
        {
            double xMax = XMax, yMax = YMax, r = 0, g = 0, b = 0;
            var hits = 0;
            for (var sy = 0; sy < EdgeSamples; sy++)
            {
                var y = (1 - (py + (sy + 0.5) / EdgeSamples) / height) * yMax;
                for (var sx = 0; sx < EdgeSamples; sx++)
                {
                    var (l, c, h) = At((px + (sx + 0.5) / EdgeSamples) / width * xMax, y);
                    if (OkLchColor.ToLinearSrgb(l, c, h, out var sr, out var sg, out var sb))
                    {
                        (r, g, b) = (r + sr, g + sg, b + sb);
                        hits++;
                    }
                }
            }

            if (hits == 0)
                return 0;

            var alpha = (int)Math.Round(255d * hits / (EdgeSamples * EdgeSamples));
            int Premultiplied(double linear) => (Encoded(linear / hits) * alpha + 127) / 255;
            return alpha << 24 | Premultiplied(r) << 16 | Premultiplied(g) << 8 | Premultiplied(b);
        }

        /// <summary>An opaque BGRA pixel for a linear-light color (for the tab's slider tracks too).</summary>
        internal static int Opaque(double r, double g, double b) =>
            unchecked((int)0xFF000000) | Encoded(r) << 16 | Encoded(g) << 8 | Encoded(b);

        private static int Encoded(double linear) => EncodeTable[(int)(Math.Clamp(linear, 0, 1) * 4096 + 0.5)];

        private static byte[] BuildEncodeTable()
        {
            var table = new byte[4097];
            for (var i = 0; i < table.Length; i++)
                table[i] = OkLchColor.Encode(i / 4096d);
            return table;
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
