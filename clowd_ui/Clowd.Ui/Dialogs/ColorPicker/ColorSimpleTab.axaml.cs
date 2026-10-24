using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Clowd.UI.Converters;
using Clowd.Util;

namespace Clowd.UI.Dialogs.ColorPicker
{
    /// <summary>
    /// The color dialog's Simple tab (brief §5): the hue/saturation wheel with the eyedropper beside
    /// it, the recent and standard swatches, and one slider per channel with a field for each. Its
    /// Selected color is simply <see cref="CurrentColor"/>.
    /// </summary>
    public partial class ColorSimpleTab : ColorDialogTab
    {
        public static readonly StyledProperty<HslRgbColor> CurrentColorProperty =
            AvaloniaProperty.Register<ColorSimpleTab, HslRgbColor>(nameof(CurrentColor));

        /// <summary>The color being edited. The sliders mutate it in place (it raises
        /// PropertyChanged), so assign a fresh instance rather than one shared with anything else.</summary>
        public HslRgbColor CurrentColor
        {
            get => GetValue(CurrentColorProperty);
            set => SetValue(CurrentColorProperty, value);
        }

        private readonly bool _initialized;

        // false while UpdateFields writes the boxes, so the echo is not read back as input
        private bool _handleTextEvents;

        // color to put back if an eyedropper drag ends without a pick
        private HslRgbColor _eyedropperRestore;

        public ColorSimpleTab()
        {
            // a fresh instance, never a registered default: the sliders mutate it in place, and a
            // default would be one object shared by every dialog
            CurrentColor = HslRgbColor.White;

            InitializeComponent();
            _initialized = true;

            wheel.CurrentColor = CurrentColor;
            wheel.PropertyChanged += (_, e) =>
            {
                // the wheel hands back a new instance for every pick
                if (e.Property == ColorWheel.CurrentColorProperty && !ReferenceEquals(wheel.CurrentColor, CurrentColor))
                    CurrentColor = wheel.CurrentColor;
            };

            foreach (var color in ColorPalettes.StandardSwatches)
                standardPanel.Children.Add(CreateChip(color));

            // value guard: replacing CurrentColor with an RGB round-trip loses hue/saturation for
            // desaturated colors, so ignore hex input that matches the current color
            HandleSet(txtHex, ColorTextHelper.FromHex, (c) =>
            {
                if (CurrentColor == null || c != CurrentColor.ToColor())
                    CurrentColor = HslRgbColor.FromColor(c);
            });
            HandleSet(txtR, int.Parse, (c) => CurrentColor.R = c);
            HandleSet(txtG, int.Parse, (c) => CurrentColor.G = c);
            HandleSet(txtB, int.Parse, (c) => CurrentColor.B = c);
            HandleSet(txtA, ParsePercent, (c) => CurrentColor.Alpha = c / 100d);
            HandleSet(txtH, ParseNumber, (c) => CurrentColor.Hue = c);
            HandleSet(txtS, ParsePercent, (c) => CurrentColor.Saturation = c / 100d);
            HandleSet(txtL, ParsePercent, (c) => CurrentColor.Lightness = c / 100d);

            // ValueChanged only fires on user drag, so syncing slider positions back in
            // UpdateChannels cannot loop.
            sliderR.ValueChanged += (s, v) => CurrentColor.R = (int)Math.Round(v);
            sliderG.ValueChanged += (s, v) => CurrentColor.G = (int)Math.Round(v);
            sliderB.ValueChanged += (s, v) => CurrentColor.B = (int)Math.Round(v);
            sliderA.ValueChanged += (s, v) => CurrentColor.Alpha = v;
            sliderH.ValueChanged += (s, v) => CurrentColor.Hue = v;
            sliderS.ValueChanged += (s, v) => CurrentColor.Saturation = v;
            sliderL.ValueChanged += (s, v) => CurrentColor.Lightness = v;

            // copies the current color as the fields show it (a gray keeps the hue in the H field,
            // which an RGB round trip would report as 0); never closes the window
            btnCopyHex.Click += async (_, _) => await Copy(ColorTextHelper.GetHex, copyHexIcon, btnCopyHex);
            btnCopyRgb.Click += async (_, _) => await Copy(ColorTextHelper.GetRgb, copyRgbIcon, btnCopyRgb);
            btnCopyHsl.Click += async (_, _) => await Copy(ColorTextHelper.GetHsl, copyHslIcon, btnCopyHsl);

            sliderH.SliderBrush = Horizontal(Colors.Red, Colors.Yellow, Colors.Lime, Colors.Cyan, Colors.Blue, Colors.Magenta, Colors.Red);

            // a color committed in another picker shows up here without reopening this one
            AttachedToVisualTree += (_, _) =>
            {
                RecentColorHistory.Changed += OnRecentColorsChanged;
                BuildRecentSwatches();
            };
            DetachedFromVisualTree += (_, _) => RecentColorHistory.Changed -= OnRecentColorsChanged;
            BuildRecentSwatches();

            UpdateChannels();
        }

        /// <summary>Loads <paramref name="color"/> as the current color.</summary>
        public void SetColor(Color color) => CurrentColor = HslRgbColor.FromColor(color);

        protected internal override bool AcceptsEyedropper => true;

        protected internal override void BeginEyedropper() => _eyedropperRestore = CurrentColor?.Clone();

        protected internal override void PreviewEyedropper(Color sampled) => SetColor(sampled);

        /// <summary>No auto-accept on a pick: unlike the mini picker this dialog has an explicit OK,
        /// and the user may well want to tweak the sampled color before committing it.</summary>
        protected internal override void EndEyedropper(Color? picked)
        {
            if (picked is { } color)
                SetColor(color);
            else if (_eyedropperRestore != null)
                CurrentColor = _eyedropperRestore;

            _eyedropperRestore = null;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == CurrentColorProperty)
            {
                if (change.OldValue is HslRgbColor oldValue)
                    oldValue.PropertyChanged -= ColorPropertyChanged;

                if (change.NewValue is HslRgbColor newValue)
                    newValue.PropertyChanged += ColorPropertyChanged;

                if (_initialized)
                {
                    wheel.CurrentColor = CurrentColor;
                    UpdateChannels();
                }
            }
        }

        private void ColorPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_initialized)
                UpdateChannels();
        }

        private async Task Copy(Func<HslRgbColor, string> format, LineIcon icon, Button button)
        {
            if (Host == null || CurrentColor == null)
                return;

            await Host.CopyTextAsync(format(CurrentColor));
            icon.ShowCopied();
            FlashCopied(button);
        }

        /// <summary>Flashes the button green and fades it back (the theme's <c>.copied</c>
        /// animation). The class comes off and goes back on a tick later, so a second copy while
        /// the first is still fading starts it over.</summary>
        private static void FlashCopied(Button button)
        {
            button.Classes.Remove("copied");
            Dispatcher.UIThread.Post(() => button.Classes.Add("copied"));
        }

        private void OnRecentColorsChanged(object sender, EventArgs e) => BuildRecentSwatches();

        private void BuildRecentSwatches()
        {
            recentPanel.Children.Clear();

            foreach (var color in RecentColorHistory.Colors)
                recentPanel.Children.Add(CreateChip(color));

            // an empty row would just be a gap with a rule under it
            var any = recentPanel.Children.Count > 0;
            recentPanel.IsVisible = any;
            recentDivider.IsVisible = any;
        }

        private ColorPaletteItem CreateChip(Color color)
        {
            var chip = new ColorPaletteItem(color);
            ToolTip.SetTip(chip, ColorTextHelper.GetHex(color));
            ColorDialogTips.Attach(chip);
            chip.Clicked += ChipClicked;
            return chip;
        }

        /// <summary>Click selects; a double-click selects and confirms, as it always has.</summary>
        private void ChipClicked(object sender, ColorSelectedEventArgs e)
        {
            CurrentColor = HslRgbColor.FromColor(e.SelectedColor);
            if (e.ClickCount >= 2)
                Host?.Accept();
        }

        /// <summary>Redraws every track for the current color, moves the thumbs and the wheel's
        /// marker, refreshes the fields and reports the new selection.</summary>
        private void UpdateChannels()
        {
            var clr = CurrentColor;
            var rgb = clr.ToColor();
            var opaque = Color.FromRgb(rgb.R, rgb.G, rgb.B);

            sliderR.SliderBrush = Horizontal(Color.FromRgb(0, rgb.G, rgb.B), Color.FromRgb(255, rgb.G, rgb.B));
            sliderG.SliderBrush = Horizontal(Color.FromRgb(rgb.R, 0, rgb.B), Color.FromRgb(rgb.R, 255, rgb.B));
            sliderB.SliderBrush = Horizontal(Color.FromRgb(rgb.R, rgb.G, 0), Color.FromRgb(rgb.R, rgb.G, 255));
            sliderA.SliderBrush = Horizontal(Color.FromArgb(0, rgb.R, rgb.G, rgb.B), opaque);
            sliderS.SliderBrush = Horizontal(Opaque(clr.Hue, 0, clr.Lightness), Opaque(clr.Hue, 1, clr.Lightness));
            sliderL.SliderBrush = Horizontal(Colors.Black, Opaque(clr.Hue, clr.Saturation, 0.5), Colors.White);

            sliderR.Value = clr.R;
            sliderG.Value = clr.G;
            sliderB.Value = clr.B;
            sliderA.Value = clr.Alpha;
            sliderH.Value = clr.Hue;
            sliderS.Value = clr.Saturation;
            sliderL.Value = clr.Lightness;

            UpdateFields();
            SetSelectedColor(rgb);
        }

        private static Color Opaque(double hue, double saturation, double lightness) =>
            new HslRgbColor(hue, saturation, lightness, 1).ToColor();

        private static IBrush Horizontal(params Color[] stops)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            };

            for (var i = 0; i < stops.Length; i++)
                brush.GradientStops.Add(new GradientStop(stops[i], i / (double)(stops.Length - 1)));

            return brush;
        }

        private void UpdateFields(bool skipFocused = true)
        {
            _handleTextEvents = false;
            var clr = CurrentColor;
            if (!skipFocused || !txtHex.IsFocused) txtHex.Text = ColorTextHelper.GetHex(clr);
            if (!skipFocused || !txtR.IsFocused) txtR.Text = clr.R.ToString(CultureInfo.InvariantCulture);
            if (!skipFocused || !txtG.IsFocused) txtG.Text = clr.G.ToString(CultureInfo.InvariantCulture);
            if (!skipFocused || !txtB.IsFocused) txtB.Text = clr.B.ToString(CultureInfo.InvariantCulture);
            if (!skipFocused || !txtA.IsFocused) txtA.Text = Math.Round(clr.Alpha * 100).ToString(CultureInfo.InvariantCulture) + "%";
            if (!skipFocused || !txtH.IsFocused) txtH.Text = Math.Round(clr.Hue).ToString(CultureInfo.InvariantCulture);
            if (!skipFocused || !txtS.IsFocused) txtS.Text = Math.Round(clr.Saturation * 100).ToString(CultureInfo.InvariantCulture);
            if (!skipFocused || !txtL.IsFocused) txtL.Text = Math.Round(clr.Lightness * 100).ToString(CultureInfo.InvariantCulture);
            txtRgbReadout.Text = ColorTextHelper.GetRgb(clr);
            txtHslReadout.Text = ColorTextHelper.GetHsl(clr);
            _handleTextEvents = true;
        }

        private static double ParseNumber(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.CurrentCulture);

        // the alpha field reads "100%", and a typed % is fine on any of the percentage fields
        private static double ParsePercent(string text) => ParseNumber((text ?? "").Trim().TrimEnd('%'));

        private void HandleSet<T>(TextBox txt, Func<string, T> parse, Action<T> set)
        {
            txt.TextChanged += (s, e) =>
            {
                try
                {
                    // TextChanged is dispatcher-posted in Avalonia (unlike WPF), so the flag cannot
                    // mask programmatic writes — the echo arrives after it is reset. UpdateFields
                    // only writes unfocused boxes, so a change while unfocused is always an echo,
                    // never user input.
                    if (!_handleTextEvents || !txt.IsFocused) return;
                    set(parse(txt.Text));
                }
                catch {; }
            };

            // the value is live while typing; leaving the field settles its text to the value
            // actually in effect (a half-typed or out-of-range entry snaps back)
            txt.LostFocus += (s, e) => UpdateFields(false);
        }
    }
}
