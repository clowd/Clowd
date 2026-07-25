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

        public Action<Color> ColorSelectFn { get; set; }

        public Window ParentWindow { get; set; }

        private bool _initialized;

        private bool _suppressUpdate;

        // The colour in effect before this popup was opened, so Escape can undo the live
        // (Realtime) writes that every slider/canvas move has already pushed to the caller.
        private Color? _originalColor;

        private TopLevel _keyRoot;

        public MiniColorDialog()
        {
            DataContext = this;
            InitializeComponent();
            _initialized = true;

            txtHex.TextChanged += (s, e) =>
            {
                try
                {
                    // TextChanged is dispatcher-posted in Avalonia (unlike WPF), so a boolean
                    // flag around the programmatic write in Update() cannot mask the echo — it
                    // arrives after the flag is reset and would replace CurrentColor with an
                    // RGB round-trip, quantizing or resetting the stored hue. Update() only
                    // writes the box while unfocused, so unfocused changes are echoes.
                    if (!txtHex.IsFocused) return;
                    var parsed = ColorTextHelper.FromHex(txtHex.Text);
                    // value guard: an RGB round-trip loses hue/saturation for desaturated
                    // colors, so don't replace the color when the hex already matches it
                    if (CurrentColor != null && parsed == CurrentColor.ToColor()) return;
                    CurrentColor = HslRgbColor.FromColor(parsed);
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

            HueSlider.ValueChanged += (s, v) => SetColorComponent(c => c.Hue = v);
            AlphaSlider.ValueChanged += (s, v) => SetColorComponent(c => c.Alpha = v);

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

        /// <summary>
        /// Prepares the picker for a fresh open: seeds the colour without notifying the caller,
        /// then wires up the callback. Also records the starting colour so <see cref="Cancel"/>
        /// can restore it.
        /// </summary>
        public void Reset(Color initial, Action<Color> selectFn)
        {
            ColorSelectFn = null; // don't echo the seed value back to the caller
            _originalColor = initial;
            CurrentColor = HslRgbColor.FromColor(initial);
            ColorSelectFn = selectFn;
        }

        /// <summary>Keeps the current colour and closes.</summary>
        public void Accept()
        {
            var fn = ColorSelectFn;
            var color = CurrentColor?.ToColor();

            Cancelled?.Invoke(this, EventArgs.Empty);

            if (fn != null && color.HasValue)
                fn(color.Value);
        }

        /// <summary>Closes, restoring the colour that was in effect when the picker opened.</summary>
        public void Cancel()
        {
            var fn = ColorSelectFn;
            var original = _originalColor;

            Cancelled?.Invoke(this, EventArgs.Empty);

            // In Realtime mode the caller has already been given every intermediate colour, so
            // cancelling means putting the original back.
            if (Realtime && fn != null && original.HasValue)
                fn(original.Value);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            // This control is hosted in a Popup, which lives in its own PopupRoot with its own
            // focus scope — key events raised in there never reach the owning window's handlers.
            // Hook the popup root so Escape/Enter work no matter which child has focus.
            _keyRoot = TopLevel.GetTopLevel(this);
            _keyRoot?.AddHandler(KeyDownEvent, RootKeyDown, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _keyRoot?.RemoveHandler(KeyDownEvent, RootKeyDown);
            _keyRoot = null;
            base.OnDetachedFromVisualTree(e);
        }

        private void RootKeyDown(object sender, KeyEventArgs e)
        {
            // tunnelled, so these win over the hex TextBox as well
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Accept();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Cancel();
            }
        }

        private void CanvasBackground_PointerReleased(object sender, PointerReleasedEventArgs e)
        {
            if (Equals(e.Pointer.Captured, CanvasBackground))
            {
                SetColorFromCanvasPoint(e.GetPosition(CanvasBackground));
                e.Pointer.Capture(null);
            }
        }

        private void CanvasBackground_PointerMoved(object sender, PointerEventArgs e)
        {
            if (Equals(e.Pointer.Captured, CanvasBackground))
                SetColorFromCanvasPoint(e.GetPosition(CanvasBackground));
        }

        private void CanvasBackground_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            SetColorFromCanvasPoint(e.GetPosition(CanvasBackground));
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
            if (_initialized && !_suppressUpdate) Update();
        }

        /// <summary>
        /// Mutates the current color in place (rather than replacing CurrentColor) so the
        /// untouched components — and the controls positioned from them — stay put. Each HSL
        /// setter also fires R/G/B change notifications, so updates are suppressed while
        /// mutating and a single Update runs at the end.
        /// </summary>
        private void SetColorComponent(Action<HslRgbColor> mutate)
        {
            var c = CurrentColor;
            if (c == null) return;

            _suppressUpdate = true;
            try
            {
                mutate(c);
            }
            finally
            {
                _suppressUpdate = false;
            }

            Update();
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

            if (!txtHex.IsFocused)
                txtHex.Text = ColorTextHelper.GetHex(hsl);

            // programmatic sets do not raise ColorSlider.ValueChanged, so no feedback loop
            HueSlider.Value = hsl.Hue;
            AlphaSlider.Value = hsl.Alpha;

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

        private void SetColorFromCanvasPoint(Point pt)
        {
            // convert HSB to HSL
            // https://stackoverflow.com/questions/3423214/convert-hsb-hsv-color-to-hsl
            var rx = Math.Min(1, Math.Max(0, pt.X / CanvasBackground.Bounds.Width));
            var ry = Math.Min(1, Math.Max(0, pt.Y / CanvasBackground.Bounds.Height));
            var v = 1 - ry;
            var l = v * (1 - (rx / 2));
            var s = l == 0 || l == 1 ? rx : (v - l) / Math.Min(l, 1 - l);

            if (CurrentColor == null)
            {
                CurrentColor = new HslRgbColor(0, s, l, 1);
                return;
            }

            // only saturation/lightness change — hue and alpha (and their sliders) stay put
            SetColorComponent(c =>
            {
                c.Saturation = s;
                c.Lightness = l;
            });
        }

        private void PaletteItemClicked(object sender, ColorSelectedEventArgs e)
        {
            if (sender is ColorPaletteItem p)
            {
                CurrentColor = HslRgbColor.FromColor(p.Color);
                if (e.ClickCount >= 2)
                {
                    Accept();
                }
            }
        }

        private async void ButtonPopoutClicked(object sender, RoutedEventArgs e)
        {
            var fn = ColorSelectFn;
            var original = _originalColor;

            Cancelled?.Invoke(this, EventArgs.Empty);

            // Clone: ColorDialog keeps the instance it is handed as its live CurrentColor, so
            // sharing ours would let its edits mutate (and, in Realtime, re-broadcast) this one.
            var clr = new ColorDialog(CurrentColor?.Clone(), true);
            var result = await clr.ShowAsync(ParentWindow);

            if (fn == null)
                return;

            if (result == true)
                fn(clr.CurrentColor.ToColor());
            else if (Realtime && original.HasValue)
                fn(original.Value); // cancelled — undo the live edits made before popping out
        }
    }
}
