using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Clowd.UI.Converters;
using Clowd.Util;

namespace Clowd.UI.Dialogs.ColorPicker
{
    public partial class ColorDialog : Window
    {
        public static readonly StyledProperty<HslRgbColor> CurrentColorProperty =
            AvaloniaProperty.Register<ColorDialog, HslRgbColor>(nameof(CurrentColor));

        public HslRgbColor CurrentColor
        {
            get => GetValue(CurrentColorProperty);
            set => SetValue(CurrentColorProperty, value);
        }

        public static readonly StyledProperty<HslRgbColor> PreviousColorProperty =
            AvaloniaProperty.Register<ColorDialog, HslRgbColor>(nameof(PreviousColor));

        public HslRgbColor PreviousColor
        {
            get => GetValue(PreviousColorProperty);
            set => SetValue(PreviousColorProperty, value);
        }

        public static readonly StyledProperty<IBrush> SliderRProperty =
            AvaloniaProperty.Register<ColorDialog, IBrush>(nameof(SliderR));

        public IBrush SliderR
        {
            get => GetValue(SliderRProperty);
            set => SetValue(SliderRProperty, value);
        }

        public static readonly StyledProperty<IBrush> SliderGProperty =
            AvaloniaProperty.Register<ColorDialog, IBrush>(nameof(SliderG));

        public IBrush SliderG
        {
            get => GetValue(SliderGProperty);
            set => SetValue(SliderGProperty, value);
        }

        public static readonly StyledProperty<IBrush> SliderBProperty =
            AvaloniaProperty.Register<ColorDialog, IBrush>(nameof(SliderB));

        public IBrush SliderB
        {
            get => GetValue(SliderBProperty);
            set => SetValue(SliderBProperty, value);
        }

        public static readonly StyledProperty<IBrush> SliderHProperty =
            AvaloniaProperty.Register<ColorDialog, IBrush>(nameof(SliderH));

        public IBrush SliderH
        {
            get => GetValue(SliderHProperty);
            set => SetValue(SliderHProperty, value);
        }

        public static readonly StyledProperty<IBrush> SliderSProperty =
            AvaloniaProperty.Register<ColorDialog, IBrush>(nameof(SliderS));

        public IBrush SliderS
        {
            get => GetValue(SliderSProperty);
            set => SetValue(SliderSProperty, value);
        }

        public static readonly StyledProperty<IBrush> SliderLProperty =
            AvaloniaProperty.Register<ColorDialog, IBrush>(nameof(SliderL));

        public IBrush SliderL
        {
            get => GetValue(SliderLProperty);
            set => SetValue(SliderLProperty, value);
        }

        public static readonly StyledProperty<IBrush> SliderAProperty =
            AvaloniaProperty.Register<ColorDialog, IBrush>(nameof(SliderA));

        public IBrush SliderA
        {
            get => GetValue(SliderAProperty);
            set => SetValue(SliderAProperty, value);
        }

        public static readonly StyledProperty<string> TextRgbProperty =
            AvaloniaProperty.Register<ColorDialog, string>(nameof(TextRgb));

        public string TextRgb
        {
            get => GetValue(TextRgbProperty);
            set => SetValue(TextRgbProperty, value);
        }

        public static readonly StyledProperty<string> TextHslProperty =
            AvaloniaProperty.Register<ColorDialog, string>(nameof(TextHsl));

        public string TextHsl
        {
            get => GetValue(TextHslProperty);
            set => SetValue(TextHslProperty, value);
        }

        public bool? MyDialogResult { get; private set; }

        protected bool HandleTextEvents { get; private set; }

        protected bool IsDialogMode { get; private set; }

        private bool _initialized;

        public ColorDialog(HslRgbColor previousColor = null, bool asDialog = true)
        {
            IsDialogMode = asDialog;
            DataContext = this;

            if (previousColor != null)
            {
                PreviousColor = previousColor;
                CurrentColor = previousColor;
            }
            else
            {
                PreviousColor = HslRgbColor.Transparent;
                CurrentColor = HslRgbColor.White;
            }

            InitializeComponent();
            _initialized = true;

            Icon = AppStyles.AppIcon;
            CreateColorPalette();

            if (!asDialog)
            {
                btnOK.IsVisible = false;
                btnCancel.Content = "Close";
                Title = "Clowd - Color Viewer";
            }
            else
            {
                Title = "Clowd - Color Picker";
            }

            // WPF used RoutedUICommands whose CanExecute was !IsDialogMode.
            btnCopyHex.IsEnabled = !IsDialogMode;
            btnCopyRgb.IsEnabled = !IsDialogMode;
            btnCopyHsl.IsEnabled = !IsDialogMode;

            const double stop = 1d / 6d;
            SliderH = new LinearGradientBrush()
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Colors.Red, 0),
                    new GradientStop(Colors.Yellow, stop),
                    new GradientStop(Colors.Lime, stop * 2),
                    new GradientStop(Colors.Cyan, stop * 3),
                    new GradientStop(Colors.Blue, stop * 4),
                    new GradientStop(Colors.Magenta, stop * 5),
                    new GradientStop(Colors.Red, stop * 6),
                }
            };

            pathPrevColor.Fill = new SolidColorBrush(PreviousColor.ToColor());

            // value guard: replacing CurrentColor with an RGB round-trip loses hue/saturation
            // for desaturated colors, so ignore hex input that matches the current color
            HandleSet(txtHex, ColorTextHelper.FromHex, (c) =>
            {
                if (CurrentColor == null || c != CurrentColor.ToColor())
                    CurrentColor = HslRgbColor.FromColor(c);
            });
            HandleSet(txtClrR, int.Parse, (c) => CurrentColor.R = c);
            HandleSet(txtClrG, int.Parse, (c) => CurrentColor.G = c);
            HandleSet(txtClrB, int.Parse, (c) => CurrentColor.B = c);
            HandleSet(txtClrA, double.Parse, (c) => CurrentColor.Alpha = c / 100d);
            HandleSet(txtClrH, double.Parse, (c) => CurrentColor.Hue = c);
            HandleSet(txtClrS, double.Parse, (c) => CurrentColor.Saturation = c / 100d);
            HandleSet(txtClrL, double.Parse, (c) => CurrentColor.Lightness = c / 100d);

            // ValueChanged only fires on user drag, so syncing slider positions back in
            // UpdateBrushes cannot loop.
            sliderR.ValueChanged += (s, v) => CurrentColor.R = (int)Math.Round(v);
            sliderG.ValueChanged += (s, v) => CurrentColor.G = (int)Math.Round(v);
            sliderB.ValueChanged += (s, v) => CurrentColor.B = (int)Math.Round(v);
            sliderA.ValueChanged += (s, v) => CurrentColor.Alpha = v;
            sliderH.ValueChanged += (s, v) => CurrentColor.Hue = v;
            sliderS.ValueChanged += (s, v) => CurrentColor.Saturation = v;
            sliderL.ValueChanged += (s, v) => CurrentColor.Lightness = v;

            // Reset focus when clicking on anything other than a textbox (the WPF
            // OnPreviewMouseLeftButtonDown + tabReset mechanism).
            AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);

            UpdateBrushes();
        }

        /// <summary>
        /// Shows the dialog: modal (ShowDialog) when an owner is supplied, otherwise non-modal
        /// with the result delivered when the window closes (§2.8).
        /// </summary>
        public Task<bool?> ShowAsync(Window owner)
        {
            if (owner != null)
            {
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                return ShowDialog<bool?>(owner);
            }

            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var tcs = new TaskCompletionSource<bool?>();
            Closed += (_, _) => tcs.TrySetResult(MyDialogResult);
            Show();
            Activate();
            return tcs.Task;
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

                if (_initialized) UpdateBrushes();
            }
        }

        private void ColorPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_initialized) UpdateBrushes();
        }

        private void UpdateBrushes()
        {
            var rgb = CurrentColor.ToColor();

            HslRgbColor hsl;

            // R
            SliderR = Horizontal(Color.FromArgb(rgb.A, 0, rgb.G, rgb.B),
                                 Color.FromArgb(rgb.A, 255, rgb.G, rgb.B));

            // G
            SliderG = Horizontal(Color.FromArgb(rgb.A, rgb.R, 0, rgb.B),
                                 Color.FromArgb(rgb.A, rgb.R, 255, rgb.B));

            // B
            SliderB = Horizontal(Color.FromArgb(rgb.A, rgb.R, rgb.G, 0),
                                 Color.FromArgb(rgb.A, rgb.R, rgb.G, 255));

            // A
            SliderA = Horizontal(Color.FromArgb(0, rgb.R, rgb.G, rgb.B),
                                 Color.FromArgb(255, rgb.R, rgb.G, rgb.B));

            // S
            hsl = CurrentColor.Clone();
            hsl.Saturation = 1;
            SliderS = Horizontal(Color.FromArgb(rgb.A, 128, 128, 128), hsl.ToColor());

            // L
            hsl = CurrentColor.Clone();
            hsl.Lightness = 0.5;
            SliderL = new LinearGradientBrush()
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(rgb.A, 0, 0, 0), 0),
                    new GradientStop(hsl.ToColor(), 0.5),
                    new GradientStop(Color.FromArgb(rgb.A, 255, 255, 255), 1),
                }
            };

            // WPF bound the swatch Border to CurrentColor via HslColorToBrushConverter; the
            // converter is not ported so the brush is assigned here instead.
            currentSwatch.Background = new SolidColorBrush(rgb);

            var clr = CurrentColor;
            sliderR.Value = clr.R;
            sliderG.Value = clr.G;
            sliderB.Value = clr.B;
            sliderA.Value = clr.Alpha;
            sliderH.Value = clr.Hue;
            sliderS.Value = clr.Saturation;
            sliderL.Value = clr.Lightness;

            TextRgb = ColorTextHelper.GetRgb(CurrentColor);
            TextHsl = ColorTextHelper.GetHsl(CurrentColor);
            UpdateTextComponents();
        }

        private static IBrush Horizontal(Color start, Color end)
        {
            return new LinearGradientBrush()
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(start, 0),
                    new GradientStop(end, 1),
                }
            };
        }

        private void UpdateTextComponents(bool skipFocused = true)
        {
            HandleTextEvents = false;
            var clr = CurrentColor;
            if (!skipFocused || !txtHex.IsFocused) txtHex.Text = ColorTextHelper.GetHex(clr);
            if (!skipFocused || !txtClrR.IsFocused) txtClrR.Text = clr.R.ToString();
            if (!skipFocused || !txtClrG.IsFocused) txtClrG.Text = clr.G.ToString();
            if (!skipFocused || !txtClrB.IsFocused) txtClrB.Text = clr.B.ToString();
            if (!skipFocused || !txtClrA.IsFocused) txtClrA.Text = Math.Round(clr.Alpha * 100).ToString();
            if (!skipFocused || !txtClrH.IsFocused) txtClrH.Text = Math.Round(clr.Hue).ToString();
            if (!skipFocused || !txtClrS.IsFocused) txtClrS.Text = Math.Round(clr.Saturation * 100).ToString();
            if (!skipFocused || !txtClrL.IsFocused) txtClrL.Text = Math.Round(clr.Lightness * 100).ToString();
            pathPrevColor.Cursor = (PreviousColor != HslRgbColor.Transparent && PreviousColor != clr)
                ? new Cursor(StandardCursorType.Hand)
                : Cursor.Default;
            HandleTextEvents = true;
        }

        private void OnPreviewPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if ((e.Source as Visual)?.FindAncestorOfType<TextBox>(true) == null)
            {
                // reset focus if clicking on anything other than textbox
                tabReset.Focus();
            }
        }

        private void CreateColorPalette()
        {
            ColorPalette.Children.Clear();
            foreach (var c in ColorPalettes.PaintPalette)
            {
                var item = new ColorPaletteItem(c);
                item.Clicked += ColorPaletteItemClicked;
                ColorPalette.Children.Add(item);
            }
        }

        private void ColorPaletteItemClicked(object sender, ColorSelectedEventArgs e)
        {
            CurrentColor = HslRgbColor.FromColor(e.SelectedColor);
            if (e.ClickCount >= 2)
            {
                MyDialogResult = true;
                Close(true);
            }
        }

        private void HandleSet<T>(TextBox txt, Func<string, T> parse, Action<T> set)
        {
            txt.TextChanged += (s, e) =>
            {
                try
                {
                    // TextChanged is dispatcher-posted in Avalonia (unlike WPF), so the
                    // HandleTextEvents flag cannot mask programmatic writes — the echo arrives
                    // after the flag is reset. UpdateTextComponents only writes unfocused boxes,
                    // so a change while unfocused is always an echo, never user input.
                    if (!HandleTextEvents || !txt.IsFocused) return;
                    set(parse(txt.Text));
                }
                catch {; }
            };

            txt.LostFocus += (s, e) =>
            {
                UpdateTextComponents(false);
            };
        }

        private async void CopyHexExecuted(object sender, RoutedEventArgs e)
        {
            if (Clipboard != null)
                await Clipboard.SetTextAsync(ColorTextHelper.GetHex(CurrentColor));
            Close();
        }

        private async void CopyRgbExecuted(object sender, RoutedEventArgs e)
        {
            if (Clipboard != null)
                await Clipboard.SetTextAsync(ColorTextHelper.GetRgb(CurrentColor));
            Close();
        }

        private async void CopyHslExecuted(object sender, RoutedEventArgs e)
        {
            if (Clipboard != null)
                await Clipboard.SetTextAsync(ColorTextHelper.GetHsl(CurrentColor));
            Close();
        }

        private void OKClicked(object sender, RoutedEventArgs e)
        {
            MyDialogResult = true;
            Close(true);
        }

        private void CloseClicked(object sender, RoutedEventArgs e)
        {
            MyDialogResult = false;
            Close(false);
        }

        private void PrevColorClicked(object sender, PointerPressedEventArgs e)
        {
            if (PreviousColor != HslRgbColor.Transparent)
                CurrentColor = PreviousColor;
        }
    }
}
