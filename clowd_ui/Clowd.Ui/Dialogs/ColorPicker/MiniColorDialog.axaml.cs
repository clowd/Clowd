using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Clowd.UI.Converters;
using Clowd.Util;

namespace Clowd.UI.Dialogs.ColorPicker
{
    public partial class MiniColorDialog : UserControl
    {
        public static readonly StyledProperty<HslRgbColor> CurrentColorProperty =
            AvaloniaProperty.Register<MiniColorDialog, HslRgbColor>(nameof(CurrentColor));

        public HslRgbColor CurrentColor
        {
            get => GetValue(CurrentColorProperty);
            set => SetValue(CurrentColorProperty, value);
        }

        public static readonly StyledProperty<bool> RealtimeProperty =
            AvaloniaProperty.Register<MiniColorDialog, bool>(nameof(Realtime));

        public bool Realtime
        {
            get => GetValue(RealtimeProperty);
            set => SetValue(RealtimeProperty, value);
        }

        public event EventHandler Cancelled;

        protected bool HandleTextEvents { get; private set; }

        public Action<Color> ColorSelectFn { get; set; }

        public Window ParentWindow { get; set; }

        private bool _initialized;

        public MiniColorDialog()
        {
            DataContext = this;
            InitializeComponent();
            _initialized = true;

            txtHex.TextChanged += (s, e) =>
            {
                try
                {
                    if (!HandleTextEvents) return;
                    CurrentColor = HslRgbColor.FromColor(ColorTextHelper.FromHex(txtHex.Text));
                }
                catch {; }
            };

            txtHex.LostFocus += (s, e) =>
            {
                if (CurrentColor != null)
                    txtHex.Text = ColorTextHelper.GetHex(CurrentColor);
            };

            CanvasBackground.PointerPressed += CanvasBackground_PointerPressed;
            CanvasBackground.PointerMoved += CanvasBackground_PointerMoved;
            CanvasBackground.PointerReleased += CanvasBackground_PointerReleased;

            // Re-run Update once layout has produced real sizes so the SV pointer lands at the
            // correct spot on first open (WPF relied on a later DP change to fix this up).
            CanvasBackground.SizeChanged += (s, e) =>
            {
                if (CurrentColor != null)
                    Update();
            };

            if (CurrentColor != null)
                Update();
        }

        private void CanvasBackground_PointerReleased(object sender, PointerReleasedEventArgs e)
        {
            if (Equals(e.Pointer.Captured, CanvasBackground))
            {
                CurrentColor = GetColorFromCanvasPoint(e.GetPosition(CanvasBackground));
                e.Pointer.Capture(null);
            }
        }

        private void CanvasBackground_PointerMoved(object sender, PointerEventArgs e)
        {
            if (Equals(e.Pointer.Captured, CanvasBackground))
                CurrentColor = GetColorFromCanvasPoint(e.GetPosition(CanvasBackground));
        }

        private void CanvasBackground_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            CurrentColor = GetColorFromCanvasPoint(e.GetPosition(CanvasBackground));
            e.Pointer.Capture(CanvasBackground);
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
                {
                    oldValue.PropertyChanged -= ColorPropertyChanged;
                }

                if (newValue != null)
                {
                    newValue.PropertyChanged += ColorPropertyChanged;
                    if (_initialized) Update();
                }
            }
        }

        private void ColorPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_initialized) Update();
        }

        private void Update()
        {
            var hsl = CurrentColor;
            if (hsl == null) return;

            var rgb = hsl.ToColor();

            if (Realtime && ColorSelectFn != null)
            {
                ColorSelectFn(rgb);
            }

            CurrentColorEllipse.Fill = new SolidColorBrush(rgb);

            var noAlphaColor = Color.FromArgb(255, rgb.R, rgb.G, rgb.B);
            var hueOnlyColor = new HslRgbColor(hsl.Hue, 1, 0.5, 1).ToColor();

            // WPF bound these colors into the gradient brushes; in Avalonia the brushes are
            // rebuilt here instead (bindings inside brushes are unreliable outside the tree).
            SvSquare.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Colors.White, 0),
                    new GradientStop(hueOnlyColor, 1),
                }
            };

            AlphaSlider.SliderBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(noAlphaColor, 1),
                }
            };

            HandleTextEvents = false;
            txtHex.Text = ColorTextHelper.GetHex(hsl);
            HandleTextEvents = true;

            foreach (var item in pnlPalette.Children.OfType<ColorPaletteItem>())
                item.IsSelected = item.Color == rgb;

            // convert HSL to HSB
            // https://stackoverflow.com/questions/3423214/convert-hsb-hsv-color-to-hsl
            var v = hsl.Saturation * Math.Min(hsl.Lightness, 1 - hsl.Lightness) + hsl.Lightness;
            var s = v != 0 ? 2 - 2 * hsl.Lightness / v : hsl.Saturation;
            var x = (CanvasBackground.Bounds.Width * s) - (CanvasPointer.Bounds.Width / 2);
            var y = (CanvasBackground.Bounds.Height * (1 - v)) - (CanvasPointer.Bounds.Height / 2);
            Canvas.SetLeft(CanvasPointer, x);
            Canvas.SetTop(CanvasPointer, y);
        }

        private HslRgbColor GetColorFromCanvasPoint(Point pt)
        {
            // convert HSB to HSL
            // https://stackoverflow.com/questions/3423214/convert-hsb-hsv-color-to-hsl
            var rx = Math.Min(1, Math.Max(0, pt.X / CanvasBackground.Bounds.Width));
            var ry = Math.Min(1, Math.Max(0, pt.Y / CanvasBackground.Bounds.Height));
            var v = 1 - ry;
            var l = v * (1 - (rx / 2));
            var s = l == 0 || l == 1 ? rx : (v - l) / Math.Min(l, 1 - l);
            return new HslRgbColor(CurrentColor?.Hue ?? 0, s, l, CurrentColor?.Alpha ?? 1);
        }

        private void PaletteItemClicked(object sender, ColorSelectedEventArgs e)
        {
            if (sender is ColorPaletteItem p)
            {
                CurrentColor = HslRgbColor.FromColor(p.Color);
                if (e.ClickCount >= 2)
                {
                    ButtonCheckClicked(sender, new RoutedEventArgs());
                }
            }
        }

        private void ButtonCheckClicked(object sender, RoutedEventArgs e)
        {
            Cancelled?.Invoke(this, new EventArgs());

            if (ColorSelectFn != default)
                ColorSelectFn(CurrentColor.ToColor());
        }

        private void ButtonCancelClicked(object sender, RoutedEventArgs e)
        {
            Cancelled?.Invoke(this, new EventArgs());
        }

        private async void ButtonPopoutClicked(object sender, RoutedEventArgs e)
        {
            Cancelled?.Invoke(this, new EventArgs());

            var clr = new ColorDialog(CurrentColor, true);
            var result = await clr.ShowAsync(ParentWindow);

            if (result == true && ColorSelectFn != default)
                ColorSelectFn(clr.CurrentColor.ToColor());
        }
    }
}
