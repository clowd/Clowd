using System;
using System.Collections.Generic;
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
using Clowd.UI.Converters;
using Clowd.UI.Helpers;
using Clowd.Util;

namespace Clowd.UI.Dialogs.ColorPicker
{
    public partial class ColorDialog : Window, IWpfNiceDialog
    {
        public Color CurrentColor
        {
            get { return (Color)GetValue(CurrentColorProperty); }
            set { SetValue(CurrentColorProperty, value); }
        }

        public static readonly DependencyProperty CurrentColorProperty =
            DependencyProperty.Register("CurrentColor", typeof(Color), typeof(ColorDialog),
                new PropertyMetadata(Colors.White, (d, e) => ((ColorDialog)d).OnCurrentColorChanged()));

        public Color PreviousColor
        {
            get { return (Color)GetValue(PreviousColorProperty); }
            set { SetValue(PreviousColorProperty, value); }
        }

        public static readonly DependencyProperty PreviousColorProperty =
            DependencyProperty.Register("PreviousColor", typeof(Color), typeof(ColorDialog),
                new PropertyMetadata(Colors.Transparent));

        public bool? MyDialogResult { get; private set; }

        protected bool HandleTextEvents { get; private set; }

        protected bool IsDialogMode { get; private set; }

        public ColorDialog(Color? previousColor = null, bool asDialog = true)
        {
            InitializeComponent();
            CreateColorPalette();

            HandleRgbSet(txtClrR, (c, i) => Color.FromArgb(c.A, i, c.G, c.B));
            HandleRgbSet(txtClrG, (c, i) => Color.FromArgb(c.A, c.R, i, c.B));
            HandleRgbSet(txtClrB, (c, i) => Color.FromArgb(c.A, c.R, c.G, i));
            HandleRgbSet(txtClrA, (c, i) => Color.FromArgb(i, c.R, c.G, c.B));
            HandleHslSet(txtClrH, (c, i) => c.Hue = i);
            HandleHslSet(txtClrS, (c, i) => c.Saturation = i / 100d);
            HandleHslSet(txtClrL, (c, i) => c.Lightness = i / 100d);

            IsDialogMode = asDialog;

            if (previousColor.HasValue)
            {
                PreviousColor = previousColor.Value;
                CurrentColor = previousColor.Value;
            }

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

            OnCurrentColorChanged();
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

        protected void OnCurrentColorChanged()
        {
            HandleTextEvents = false;
            var hsl = HSLColor.FromRGB(CurrentColor);
            if (!txtClrR.IsFocused) txtClrR.Text = CurrentColor.R.ToString();
            if (!txtClrG.IsFocused) txtClrG.Text = CurrentColor.G.ToString();
            if (!txtClrB.IsFocused) txtClrB.Text = CurrentColor.B.ToString();
            if (!txtClrA.IsFocused) txtClrA.Text = CurrentColor.A.ToString();
            if (!txtClrH.IsFocused) txtClrH.Text = Math.Floor(hsl.Hue).ToString();
            if (!txtClrS.IsFocused) txtClrS.Text = Math.Floor(hsl.Saturation * 100).ToString();
            if (!txtClrL.IsFocused) txtClrL.Text = Math.Floor(hsl.Lightness * 100).ToString();
            pathPrevColor.Cursor = (PreviousColor != Colors.Transparent && PreviousColor != CurrentColor) ? Cursors.Hand : Cursors.Arrow;
            HandleTextEvents = true;
        }

        private void CreateColorPalette()
        {
            ColorPalette.Children.Clear();
            var colors = ColorPalettes.PaintPalette.Select(c => Color.FromArgb(c.A, c.R, c.G, c.B));
            foreach (var c in colors)
            {
                var item = new ColorPaletteItem { Background = new SolidColorBrush(c), IsTabStop = false };
                item.MouseDown += ColorPaletteItemSelected;
                ColorPalette.Children.Add(item);
            }
        }

        private void ColorPaletteItemSelected(object sender, MouseButtonEventArgs e)
        {
            if (sender is ColorPaletteItem item && item.Background is SolidColorBrush brush)
            {
                CurrentColor = brush.Color;
                if (e.ClickCount >= 2)
                {
                    MyDialogResult = true;
                    Close();
                }
            }
        }

        private void HandleRgbSet(TextBox txt, Func<Color, byte, Color> thunk)
        {
            txt.TextChanged += (s, e) =>
            {
                try
                {
                    if (!HandleTextEvents) return;
                    var i = int.Parse(txt.Text);
                    if (i > 255 || i < 0) return;
                    CurrentColor = thunk(CurrentColor, (byte)i);
                }
                catch {; }
            };
        }

        private void HandleHslSet(TextBox txt, Action<HSLColor, double> thunk)
        {
            txt.TextChanged += (s, e) =>
            {
                try
                {
                    if (!HandleTextEvents) return;
                    var hsl = HSLColor.FromRGB(CurrentColor);
                    thunk(hsl, double.Parse(txt.Text));
                    CurrentColor = hsl.ToRGB();
                }
                catch {; }
            };
        }

        private void CopyHexExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            Clipboard.SetText(ColorTextHelper.GetHex(CurrentColor));
            Close();
        }

        private void CopyRgbExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            Clipboard.SetText(ColorTextHelper.GetRgb(CurrentColor));
            Close();
        }

        private void CopyHslExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            Clipboard.SetText(ColorTextHelper.GetHsl(CurrentColor));
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
            if (PreviousColor != Colors.Transparent)
                CurrentColor = PreviousColor;
        }
    }

    public class ColorPaletteItem : Control
    {
        const double _penThicknes = 1;
        Pen _blackPen = new Pen(Brushes.Black, _penThicknes);
        Pen _whitePen = new Pen(Brushes.White, _penThicknes);

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            drawingContext.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));

            if (IsMouseOver)
            {
                drawingContext.DrawRectangle(null, _blackPen, new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1));
                drawingContext.DrawRectangle(null, _whitePen, new Rect(1.5, 1.5, ActualWidth - 3, ActualHeight - 3));
            }
        }
    }
}
