using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Clowd.UI.Converters;
using Clowd.UI.Helpers;
using Clowd.Util;
using DependencyPropertyGenerator;

namespace Clowd.UI.Dialogs.ColorPicker
{
    [DependencyProperty<HslRgbColor>("CurrentColor")]
    [DependencyProperty<SolidColorBrush>("CurrentBrush")]
    [DependencyProperty<Color>("NoAlphaColor")]
    [DependencyProperty<Color>("HueOnlyColor")]
    public partial class MiniColorDialog : UserControl
    {
        public event EventHandler Cancelled;

        protected bool HandleTextEvents { get; private set; }

        public Action<Color> ColorSelectFn { get; set; }

        public Window ParentWindow { get; set; }

        public MiniColorDialog()
        {
            InitializeComponent();

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
                txtHex.Text = ColorTextHelper.GetHex(CurrentColor);
            };

            CanvasBackground.MouseDown += CanvasBackground_MouseDown;
            CanvasBackground.MouseMove += CanvasBackground_MouseMove;
            CanvasBackground.MouseUp += CanvasBackground_MouseUp;
        }

        private void CanvasBackground_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (CanvasBackground.IsMouseCaptured)
                CurrentColor = GetColorFromCanvasPoint(e.GetPosition(CanvasBackground));
            CanvasBackground.ReleaseMouseCapture();
        }

        private void CanvasBackground_MouseMove(object sender, MouseEventArgs e)
        {
            if (CanvasBackground.IsMouseCaptured)
                CurrentColor = GetColorFromCanvasPoint(e.GetPosition(CanvasBackground));
        }

        private void CanvasBackground_MouseDown(object sender, MouseButtonEventArgs e)
        {
            CurrentColor = GetColorFromCanvasPoint(e.GetPosition(CanvasBackground));
            CanvasBackground.CaptureMouse();
        }

        partial void OnCurrentColorChanged(HslRgbColor oldValue, HslRgbColor newValue)
        {
            if (oldValue != null)
            {
                newValue.PropertyChanged -= ColorPropertyChanged;
            }

            if (newValue != null)
            {
                newValue.PropertyChanged += ColorPropertyChanged;
                if (IsInitialized) Update();
            }
        }

        private void ColorPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (IsInitialized) Update();
        }

        private void Update()
        {
            var hsl = CurrentColor;
            var rgb = hsl.ToColor();
            CurrentBrush = new SolidColorBrush(rgb);

            rgb.A = 255;
            NoAlphaColor = rgb;
            HueOnlyColor = new HslRgbColor(hsl.Hue, 1, 0.5, 1).ToColor();

            HandleTextEvents = false;
            txtHex.Text = ColorTextHelper.GetHex(hsl);
            HandleTextEvents = true;

            // convert HSL to HSB
            // https://stackoverflow.com/questions/3423214/convert-hsb-hsv-color-to-hsl
            var v = hsl.Saturation * Math.Min(hsl.Lightness, 1 - hsl.Lightness) + hsl.Lightness;
            var s = v != 0 ? 2 - 2 * hsl.Lightness / v : hsl.Saturation;
            var x = (CanvasBackground.ActualWidth * s) - (CanvasPointer.ActualWidth / 2);
            var y = (CanvasBackground.ActualHeight * (1 - v)) - (CanvasPointer.ActualHeight / 2);
            Canvas.SetLeft(CanvasPointer, x);
            Canvas.SetTop(CanvasPointer, y);
        }

        private HslRgbColor GetColorFromCanvasPoint(Point pt)
        {
            // convert HSB to HSL
            // https://stackoverflow.com/questions/3423214/convert-hsb-hsv-color-to-hsl
            var rx = Math.Min(1, Math.Max(0, pt.X / CanvasBackground.ActualWidth));
            var ry = Math.Min(1, Math.Max(0, pt.Y / CanvasBackground.ActualHeight));
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
            var result = await clr.ShowAsNiceDialogAsync(ParentWindow);

            if (result == true && ColorSelectFn != default)
                ColorSelectFn(clr.CurrentColor.ToColor());
        }
    }
}
