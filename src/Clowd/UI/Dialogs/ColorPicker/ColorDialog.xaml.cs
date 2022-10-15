using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.TextFormatting;
using Clowd.Clipboard;
using Clowd.UI.Converters;
using Clowd.UI.Helpers;
using Clowd.Util;
using DependencyPropertyGenerator;

namespace Clowd.UI.Dialogs.ColorPicker
{
    [DependencyProperty<HslRgbColor>("CurrentColor")]
    [DependencyProperty<HslRgbColor>("PreviousColor")]
    [DependencyProperty<LinearGradientBrush>("SliderR")]
    [DependencyProperty<LinearGradientBrush>("SliderG")]
    [DependencyProperty<LinearGradientBrush>("SliderB")]
    [DependencyProperty<LinearGradientBrush>("SliderH")]
    [DependencyProperty<LinearGradientBrush>("SliderS")]
    [DependencyProperty<LinearGradientBrush>("SliderL")]
    [DependencyProperty<LinearGradientBrush>("SliderA")]
    [DependencyProperty<string>("TextRgb")]
    [DependencyProperty<string>("TextHsl")]
    public partial class ColorDialog : Window, IWpfNiceDialog
    {
        public bool? MyDialogResult { get; private set; }

        protected bool HandleTextEvents { get; private set; }

        protected bool IsDialogMode { get; private set; }

        public ColorDialog(HslRgbColor previousColor = null, bool asDialog = true)
        {
            IsDialogMode = asDialog;

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
            CreateColorPalette();

            if (!asDialog)
            {
                btnOK.Visibility = Visibility.Collapsed;
                btnCancel.Content = "_Close";
                Title = "Clowd - Color Viewer";
            }
            else
            {
                Title = "Clowd - Color Picker";
            }

            const double stop = 1d / 6d;
            SliderH = new LinearGradientBrush()
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                GradientStops = new GradientStopCollection()
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

            HandleSet(txtHex, ColorTextHelper.FromHex, (c) => CurrentColor = HslRgbColor.FromColor(c));
            HandleSet(txtClrR, int.Parse, (c) => CurrentColor.R = c);
            HandleSet(txtClrG, int.Parse, (c) => CurrentColor.G = c);
            HandleSet(txtClrB, int.Parse, (c) => CurrentColor.B = c);
            HandleSet(txtClrA, double.Parse, (c) => CurrentColor.Alpha = c / 100d);
            HandleSet(txtClrH, double.Parse, (c) => CurrentColor.Hue = c);
            HandleSet(txtClrS, double.Parse, (c) => CurrentColor.Saturation = c / 100d);
            HandleSet(txtClrL, double.Parse, (c) => CurrentColor.Lightness = c / 100d);

            UpdateBrushes();
        }

        partial void OnCurrentColorChanged(HslRgbColor oldValue, HslRgbColor newValue)
        {
            if (oldValue != null)
                newValue.PropertyChanged -= ColorPropertyChanged;

            if (newValue != null)
                newValue.PropertyChanged += ColorPropertyChanged;

            if (IsInitialized) UpdateBrushes();
        }

        private void ColorPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (IsInitialized) UpdateBrushes();
        }

        private void UpdateBrushes()
        {
            var rgb = CurrentColor.ToColor();

            HslRgbColor hsl;
            Color start, end;

            // R
            start = end = rgb;
            start.R = 0;
            end.R = 255;
            SliderR = new LinearGradientBrush(start, end, 0);

            // G
            start = end = rgb;
            start.G = 0;
            end.G = 255;
            SliderG = new LinearGradientBrush(start, end, 0);

            // B
            start = end = rgb;
            start.B = 0;
            end.B = 255;
            SliderB = new LinearGradientBrush(start, end, 0);

            // A
            start = end = rgb;
            start.A = 0;
            end.A = 255;
            SliderA = new LinearGradientBrush(start, end, 0);

            // S
            start = Color.FromArgb(rgb.A, 128, 128, 128);
            hsl = CurrentColor.Clone();
            hsl.Saturation = 1;
            end = hsl.ToColor();
            SliderS = new LinearGradientBrush(start, end, 0);

            // L
            hsl = CurrentColor.Clone();
            hsl.Lightness = 0.5;
            SliderL = new LinearGradientBrush()
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                GradientStops = new GradientStopCollection()
                {
                    new GradientStop(Color.FromArgb(rgb.A, 0, 0, 0), 0),
                    new GradientStop(hsl.ToColor(), 0.5),
                    new GradientStop(Color.FromArgb(rgb.A, 255, 255, 255), 1),
                }
            };

            TextRgb = ColorTextHelper.GetRgb(CurrentColor);
            TextHsl = ColorTextHelper.GetHsl(CurrentColor);
            UpdateTextComponents();
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
            pathPrevColor.Cursor = (PreviousColor != HslRgbColor.Transparent && PreviousColor != clr) ? Cursors.Hand : Cursors.Arrow;
            HandleTextEvents = true;
        }

        private void CopyCanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.Handled = true;
            e.CanExecute = !IsDialogMode;
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonDown(e);

            if (e.Source.GetType() != typeof(TextBox))
            {
                // reset focus if clicking on anything other than textbox
                Keyboard.Focus(tabReset);
            }
        }

        private void CreateColorPalette()
        {
            ColorPalette.Children.Clear();
            var colors = ColorPalettes.PaintPalette.Select(c => Color.FromArgb(c.A, c.R, c.G, c.B));
            foreach (var c in colors)
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
                Close();
            }
        }

        private void HandleSet<T>(TextBox txt, Func<string, T> parse, Action<T> set)
        {
            txt.TextChanged += (s, e) =>
            {
                try
                {
                    if (!HandleTextEvents) return;
                    set(parse(txt.Text));
                }
                catch {; }
            };

            txt.LostKeyboardFocus += (s, e) =>
            {
                UpdateTextComponents(false);
            };
        }

        private void CopyHexExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            ClipboardWpf.SetText(ColorTextHelper.GetHex(CurrentColor));
            Close();
        }

        private void CopyRgbExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            ClipboardWpf.SetText(ColorTextHelper.GetRgb(CurrentColor));
            Close();
        }

        private void CopyHslExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            ClipboardWpf.SetText(ColorTextHelper.GetHsl(CurrentColor));
            Close();
        }

        private void OKClicked(object sender, RoutedEventArgs e)
        {
            MyDialogResult = true;
            Close();
        }

        private void CloseClicked(object sender, RoutedEventArgs e)
        {
            MyDialogResult = false;
            Close();
        }

        private void PrevColorClicked(object sender, MouseButtonEventArgs e)
        {
            if (PreviousColor != HslRgbColor.Transparent)
                CurrentColor = PreviousColor;
        }
    }
}
