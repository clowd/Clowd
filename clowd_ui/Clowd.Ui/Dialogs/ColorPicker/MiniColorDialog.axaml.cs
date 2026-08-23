using System;
using System.ComponentModel;
using System.Globalization;
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

        public event EventHandler Canceled;

        public Action<Color> ColorSelectFn { get; set; }

        public Window ParentWindow { get; set; }

        private bool _initialized;

        private bool _suppressUpdate;

        // The color in effect before this popup was opened, so Escape can undo the live
        // (Realtime) writes that every slider/canvas move has already pushed to the caller.
        private Color? _originalColor;

        private TopLevel _keyRoot;

        // Notation of the text field. Always starts on hex and is cycled by the user for the
        // lifetime of this control only — deliberately not a persisted setting.
        private ColorTextFormat _textFormat = ColorTextFormat.Hex;

        // set once the current color has been handed to the caller, so dismissing the popup does
        // not record a color twice (or record one that was canceled)
        private bool _committed;

        // color to put back if an eyedropper drag ends without a pick
        private HslRgbColor _eyedropperRestore;

        // the four component slots, in display order. Slot 0 doubles as the full-width hex box.
        private TextBox[] _componentBoxes;

        private TextBlock[] _componentLabels;

        private StackPanel[] _componentPanels;

        public MiniColorDialog()
        {
            DataContext = this;
            InitializeComponent();
            _initialized = true;

            _componentBoxes = new[] { txtComp0, txtComp1, txtComp2, txtComp3 };
            _componentLabels = new[] { lblComp0, lblComp1, lblComp2, lblComp3 };
            _componentPanels = new[] { pnlComp0, pnlComp1, pnlComp2, pnlComp3 };

            for (int i = 0; i < _componentBoxes.Length; i++)
            {
                var index = i;
                var box = _componentBoxes[i];

                box.TextChanged += (s, e) =>
                {
                    // TextChanged is dispatcher-posted in Avalonia (unlike WPF), so a boolean
                    // flag around the programmatic write in Update() cannot mask the echo — it
                    // arrives after the flag is reset and would replace CurrentColor with an
                    // RGB round-trip, quantizing or resetting the stored hue. Update() only
                    // writes a box while unfocused, so unfocused changes are echoes.
                    if (!box.IsFocused) return;
                    ApplyComponentText(index, box.Text);
                };

                // rewrite whatever the user typed into canonical form once they leave the box
                box.LostFocus += (s, e) => UpdateComponentValues();
            }

            UpdateComponentLayout();

            btnEyedropper.Started += () => _eyedropperRestore = CurrentColor?.Clone();
            btnEyedropper.Preview += ApplyEyedropperSample;
            btnEyedropper.Picked += (c) =>
            {
                ApplyEyedropperSample(c);
                _eyedropperRestore = null;
                Accept();
            };
            // sampling failed or the drag was abandoned — put back what the live preview replaced
            btnEyedropper.Canceled += () =>
            {
                if (_eyedropperRestore != null)
                    CurrentColor = _eyedropperRestore;
                _eyedropperRestore = null;
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
        /// Prepares the picker for a fresh open: seeds the color without notifying the caller,
        /// then wires up the callback. Also records the starting color so <see cref="Cancel"/>
        /// can restore it.
        /// </summary>
        public void Reset(Color initial, Action<Color> selectFn)
        {
            ColorSelectFn = null; // don't echo the seed value back to the caller
            _originalColor = initial;
            _committed = false;
            CurrentColor = HslRgbColor.FromColor(initial);
            ColorSelectFn = selectFn;
        }

        /// <summary>Keeps the current color and closes.</summary>
        public void Accept()
        {
            var fn = ColorSelectFn;
            var color = CurrentColor?.ToColor();

            Commit(color);
            Canceled?.Invoke(this, EventArgs.Empty);

            if (fn != null && color.HasValue)
                fn(color.Value);
        }

        /// <summary>Closes, restoring the color that was in effect when the picker opened.</summary>
        public void Cancel()
        {
            var fn = ColorSelectFn;
            var original = _originalColor;

            _committed = true; // nothing was chosen, so nothing goes into the recent list
            Canceled?.Invoke(this, EventArgs.Empty);

            // In Realtime mode the caller has already been given every intermediate color, so
            // canceling means putting the original back.
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

            // subscribed per-open rather than for the control's lifetime: the history event is
            // static, and the editor's picker outlives any single popup
            RecentColorHistory.Changed += OnRecentColorsChanged;
            BuildRecentSwatches();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _keyRoot?.RemoveHandler(KeyDownEvent, RootKeyDown);
            _keyRoot = null;
            RecentColorHistory.Changed -= OnRecentColorsChanged;

            // Light dismiss (a click outside the popup) keeps the live color without going
            // through Accept, so this is the only place that outcome can be recorded.
            Commit(CurrentColor?.ToColor());

            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>Records a chosen color in the shared recent list, once per open.</summary>
        private void Commit(Color? color)
        {
            if (_committed)
                return;

            _committed = true;

            if (color.HasValue && color.Value != _originalColor)
                RecentColorHistory.Add(color.Value);
        }

        private void RootKeyDown(object sender, KeyEventArgs e)
        {
            // tunneled, so these win over the hex TextBox as well
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

            UpdateComponentValues();

            // programmatic sets do not raise ColorSlider.ValueChanged, so no feedback loop
            HueSlider.Value = hsl.Hue;
            AlphaSlider.Value = hsl.Alpha;

            foreach (var item in pnlPalette.Children.OfType<ColorPaletteItem>())
                item.IsSelected = item.Color == rgb;

            foreach (var item in pnlRecent.Children.OfType<ColorPaletteItem>())
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

        /// <summary>Applies a color sampled off the screen. The sample is always fully opaque:
        /// what you picked off the screen is what you saw, and a partially transparent brush would
        /// not reproduce it.</summary>
        private void ApplyEyedropperSample(Color sampled)
        {
            CurrentColor = HslRgbColor.FromColor(Color.FromArgb(255, sampled.R, sampled.G, sampled.B));
        }

        private void ButtonTextFormatClicked(object sender, RoutedEventArgs e)
        {
            _textFormat = _textFormat switch
            {
                ColorTextFormat.Hex => ColorTextFormat.Rgb,
                ColorTextFormat.Rgb => ColorTextFormat.Hsl,
                _ => ColorTextFormat.Hex,
            };

            UpdateComponentLayout();
        }

        /// <summary>Reshapes the readout for the current notation: one full-width hex box, or four
        /// per-channel boxes with their letter beneath.</summary>
        private void UpdateComponentLayout()
        {
            btnTextFormat.Content = _textFormat.ToString().ToUpperInvariant();

            var labels = _textFormat switch
            {
                ColorTextFormat.Rgb => new[] { "R", "G", "B", "A" },
                ColorTextFormat.Hsl => new[] { "H", "S", "L", "A" },
                _ => new[] { "HEX" },
            };

            for (int i = 0; i < _componentPanels.Length; i++)
            {
                var used = i < labels.Length;
                _componentPanels[i].IsVisible = used;
                if (used)
                    _componentLabels[i].Text = labels[i];
            }

            // hex has a single value, so slot 0 takes the whole row
            Grid.SetColumnSpan(_componentPanels[0], labels.Length == 1 ? 4 : 1);

            UpdateComponentValues();
        }

        /// <summary>Writes the current color into the component boxes, leaving whichever box has
        /// focus alone so it does not fight the user's typing.</summary>
        private void UpdateComponentValues(bool skipFocused = true)
        {
            var c = CurrentColor;
            if (c == null)
                return;

            var values = _textFormat switch
            {
                ColorTextFormat.Rgb => new[]
                {
                    c.R.ToString(CultureInfo.InvariantCulture),
                    c.G.ToString(CultureInfo.InvariantCulture),
                    c.B.ToString(CultureInfo.InvariantCulture),
                    FormatAlpha(c.Alpha),
                },
                ColorTextFormat.Hsl => new[]
                {
                    Math.Round(c.Hue).ToString(CultureInfo.InvariantCulture),
                    Math.Round(c.Saturation * 100).ToString(CultureInfo.InvariantCulture) + "%",
                    Math.Round(c.Lightness * 100).ToString(CultureInfo.InvariantCulture) + "%",
                    FormatAlpha(c.Alpha),
                },
                _ => new[] { ColorTextHelper.GetHex(c) },
            };

            for (int i = 0; i < values.Length; i++)
            {
                if (skipFocused && _componentBoxes[i].IsFocused)
                    continue;

                _componentBoxes[i].Text = values[i];
            }
        }

        private static string FormatAlpha(double alpha) =>
            Math.Round(alpha, 2).ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Applies one edited component. Values are mutated in place rather than replacing
        /// CurrentColor so the channels the user is not editing — and the controls positioned from
        /// them — keep their exact values; an RGB round-trip would quantize the hue away.
        /// </summary>
        private void ApplyComponentText(int index, string text)
        {
            var s = (text ?? "").Trim().TrimEnd('%').Trim();

            if (_textFormat == ColorTextFormat.Hex)
            {
                if (!ColorTextHelper.TryParse(s, out var parsed, out var detected))
                    return;

                // an RGB round-trip loses hue/saturation for desaturated colors, so don't replace
                // the color when the text already resolves to it
                if (CurrentColor != null && (detected == ColorTextFormat.Hsl
                                                 ? parsed == CurrentColor
                                                 : parsed.ToColor() == CurrentColor.ToColor()))
                    return;

                CurrentColor = parsed;
                return;
            }

            if (!Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return;

            if (_textFormat == ColorTextFormat.Rgb)
            {
                switch (index)
                {
                    case 0: SetColorComponent(c => c.R = ClampByte(value)); break;
                    case 1: SetColorComponent(c => c.G = ClampByte(value)); break;
                    case 2: SetColorComponent(c => c.B = ClampByte(value)); break;
                    case 3: SetColorComponent(c => c.Alpha = Math.Clamp(value, 0, 1)); break;
                }

                return;
            }

            switch (index)
            {
                // hue wraps rather than clamping, so typing 370 lands on red-orange not red
                case 0: SetColorComponent(c => c.Hue = ((value % 360) + 360) % 360); break;
                case 1: SetColorComponent(c => c.Saturation = Math.Clamp(value / 100d, 0, 1)); break;
                case 2: SetColorComponent(c => c.Lightness = Math.Clamp(value / 100d, 0, 1)); break;
                case 3: SetColorComponent(c => c.Alpha = Math.Clamp(value, 0, 1)); break;
            }
        }

        private static int ClampByte(double value) => (int)Math.Clamp(Math.Round(value), 0, 255);

        private void OnRecentColorsChanged(object sender, EventArgs e) => BuildRecentSwatches();

        private void BuildRecentSwatches()
        {
            pnlRecent.Children.Clear();

            foreach (var color in RecentColorHistory.Colors)
            {
                var item = new ColorPaletteItem(color);
                item.Clicked += PaletteItemClicked;
                pnlRecent.Children.Add(item);
            }

            // an empty row would just be a strip of checkerboard with no meaning
            pnlRecent.IsVisible = pnlRecent.Children.Count > 0;

            // sync the new items' selection ring directly rather than via Update(), which in
            // Realtime mode would re-broadcast the current color just for opening the popup
            if (CurrentColor != null)
            {
                var rgb = CurrentColor.ToColor();
                foreach (var item in pnlRecent.Children.OfType<ColorPaletteItem>())
                    item.IsSelected = item.Color == rgb;
            }
        }

        private async void ButtonPopoutClicked(object sender, RoutedEventArgs e)
        {
            var fn = ColorSelectFn;
            var original = _originalColor;

            // the full dialog owns the outcome from here — including what lands in the recent
            // list — so closing this popup must not record the in-progress color
            _committed = true;
            Canceled?.Invoke(this, EventArgs.Empty);

            // Clone: ColorDialog keeps the instance it is handed as its live CurrentColor, so
            // sharing ours would let its edits mutate (and, in Realtime, re-broadcast) this one.
            var clr = new ColorDialog(CurrentColor?.Clone(), true);
            var result = await clr.ShowAsync(ParentWindow);

            if (fn == null)
                return;

            if (result == true)
                fn(clr.CurrentColor.ToColor());
            else if (Realtime && original.HasValue)
                fn(original.Value); // canceled — undo the live edits made before popping out
        }
    }
}
