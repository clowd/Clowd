using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Clowd.UI.Converters;
using Clowd.UI.Helpers;
using Clowd.Util;

namespace Clowd.UI.Dialogs
{
    public partial class ColorEditor : Window, IWpfNiceDialog
    {
        public Color CurrentColor
        {
            get { return (Color)GetValue(CurrentColorProperty); }
            set { SetValue(CurrentColorProperty, value); }
        }

        public static readonly DependencyProperty CurrentColorProperty =
            DependencyProperty.Register("CurrentColor", typeof(Color), typeof(ColorEditor),
                new PropertyMetadata(Colors.White, (d, e) => ((ColorEditor)d).OnCurrentColorChanged()));

        public Color PreviousColor
        {
            get { return (Color)GetValue(PreviousColorProperty); }
            set { SetValue(PreviousColorProperty, value); }
        }

        public static readonly DependencyProperty PreviousColorProperty =
            DependencyProperty.Register("PreviousColor", typeof(Color), typeof(ColorEditor),
                new PropertyMetadata(Colors.Transparent));

        public bool? MyDialogResult { get; private set; }

        protected bool HandleTextEvents { get; private set; }

        protected bool IsDialogMode { get; private set; }

        public ColorEditor(Color? previousColor = null, bool asDialog = true)
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
            var colors = Cyotek.Windows.Forms.ColorPalettes.PaintPalette.Select(c => Color.FromArgb(c.A, c.R, c.G, c.B));
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

    public class ColorSlider : Border
    {
        public Color CurrentColor
        {
            get { return (Color)GetValue(CurrentColorProperty); }
            set { SetValue(CurrentColorProperty, value); }
        }

        public static readonly DependencyProperty CurrentColorProperty =
            DependencyProperty.Register("CurrentColor", typeof(Color), typeof(ColorSlider),
                new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush ThumbBrush
        {
            get { return (Brush)GetValue(ThumbBrushProperty); }
            set { SetValue(ThumbBrushProperty, value); }
        }

        public static readonly DependencyProperty ThumbBrushProperty =
            DependencyProperty.Register("ThumbBrush", typeof(Brush), typeof(ColorSlider),
                new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

        public string ColorPart
        {
            get { return (string)GetValue(ColorPartProperty); }
            set { SetValue(ColorPartProperty, value); }
        }

        public static readonly DependencyProperty ColorPartProperty =
            DependencyProperty.Register("ColorPart", typeof(string), typeof(ColorSlider),
                new FrameworkPropertyMetadata("A", FrameworkPropertyMetadataOptions.AffectsRender));

        Brush _hueBrush;

        public ColorSlider()
        {
            const double stop = 1d / 6d;
            _hueBrush = new LinearGradientBrush()
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
        }

        protected Brush GetBackgroundColorBrush()
        {
            Color clr = CurrentColor;
            Color start, end;
            HSLColor hsl;

            switch (ColorPart)
            {
                case "R":
                    start = Color.FromArgb(clr.A, 0, clr.G, clr.B);
                    end = Color.FromArgb(clr.A, 255, clr.G, clr.B);
                    break;
                case "G":
                    start = Color.FromArgb(clr.A, clr.R, 0, clr.B);
                    end = Color.FromArgb(clr.A, clr.R, 255, clr.B);
                    break;
                case "B":
                    start = Color.FromArgb(clr.A, clr.R, clr.G, 0);
                    end = Color.FromArgb(clr.A, clr.R, clr.G, 255);
                    break;
                case "A":
                    start = Color.FromArgb(0, clr.R, clr.G, clr.B);
                    end = Color.FromArgb(255, clr.R, clr.G, clr.B);
                    break;
                case "H":
                    return _hueBrush;
                case "S":
                    start = Color.FromArgb(CurrentColor.A, 128, 128, 128);
                    hsl = HSLColor.FromRGB(CurrentColor);
                    hsl.Saturation = 1;
                    end = hsl.ToRGB();
                    break;
                case "L":
                    hsl = HSLColor.FromRGB(CurrentColor);
                    hsl.Lightness = 0.5;
                    return new LinearGradientBrush()
                    {
                        StartPoint = new Point(0, 0.5),
                        EndPoint = new Point(1, 0.5),
                        GradientStops = new GradientStopCollection()
                        {
                            new GradientStop(Color.FromArgb(CurrentColor.A, 0, 0, 0), 0),
                            new GradientStop(hsl.ToRGB(), 0.5),
                            new GradientStop(Color.FromArgb(CurrentColor.A, 255, 255, 255), 1),
                        }
                    };
            }

            return new LinearGradientBrush(start, end, 0);
        }

        protected double GetPartValue()
        {
            return ColorPart switch
            {
                "R" => CurrentColor.R / 255d,
                "G" => CurrentColor.G / 255d,
                "B" => CurrentColor.B / 255d,
                "A" => CurrentColor.A / 255d,
                "H" => HSLColor.FromRGB(CurrentColor).Hue / 359d,
                "S" => HSLColor.FromRGB(CurrentColor).Saturation,
                "L" => HSLColor.FromRGB(CurrentColor).Lightness,
                _ => throw new InvalidOperationException("Invalid color part property"),
            };
        }

        protected void SetPartValue(double value)
        {
            HSLColor hsl;
            var clr = CurrentColor;
            switch (ColorPart)
            {
                case "R":
                    CurrentColor = Color.FromArgb(clr.A, (byte)(value * 255), clr.G, clr.B);
                    break;
                case "G":
                    CurrentColor = Color.FromArgb(clr.A, clr.R, (byte)(value * 255), clr.B);
                    break;
                case "B":
                    CurrentColor = Color.FromArgb(clr.A, clr.R, clr.G, (byte)(value * 255));
                    break;
                case "A":
                    CurrentColor = Color.FromArgb((byte)(value * 255), clr.R, clr.G, clr.B);
                    break;
                case "H":
                    hsl = new HSLColor()
                    {
                        Hue = value * 359d,
                        Lightness = 0.5,
                        Saturation = 1,
                    };
                    CurrentColor = hsl.ToRGB();
                    break;
                case "S":
                    hsl = HSLColor.FromRGB(CurrentColor);
                    hsl.Saturation = value;
                    CurrentColor = hsl.ToRGB();
                    break;
                case "L":
                    hsl = HSLColor.FromRGB(CurrentColor);
                    hsl.Lightness = value;
                    CurrentColor = hsl.ToRGB();
                    break;
            }
        }

        protected void HandleMouse()
        {
            var pos = Mouse.GetPosition(this);
            SetPartValue(Math.Max(Math.Min(pos.X / ActualWidth, 1), 0));
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            CaptureMouse();
            HandleMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (IsMouseCaptured)
                HandleMouse();
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            ReleaseMouseCapture();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            // draw background
            var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
            var radius = CornerRadius.TopLeft;
            drawingContext.DrawRoundedRectangle(Background, null, bounds, radius, radius);
            drawingContext.DrawRoundedRectangle(GetBackgroundColorBrush(), null, bounds, radius, radius);

            // draw cursor triangle
            var pos = ActualWidth * GetPartValue();
            const int triSize = 10;
            const int halfTriSize = triSize / 2;

            var geo = new PathGeometry()
            {
                Figures = new PathFigureCollection()
                {
                    new PathFigure()
                    {
                        IsClosed = true,
                        IsFilled = true,
                        StartPoint = new Point(pos, ActualHeight - halfTriSize),
                        Segments = new PathSegmentCollection()
                        {
                            new LineSegment(new Point(pos - halfTriSize, ActualHeight + halfTriSize), true),
                            new LineSegment(new Point(pos + halfTriSize, ActualHeight + halfTriSize), true),
                        }
                    }
                }
            };

            Pen border = null;

            if (ThumbBrush is SolidColorBrush br)
            {
                var inverse = Color.FromArgb(
                    255,
                    (byte)(255 - br.Color.R),
                    (byte)(255 - br.Color.G),
                    (byte)(255 - br.Color.B));

                border = new Pen(new SolidColorBrush(inverse), 1);
            }

            drawingContext.DrawGeometry(ThumbBrush, border, geo);
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

    public class ColorWheel : Canvas
    {
        public Color CurrentColor
        {
            get { return (Color)GetValue(CurrentColorProperty); }
            set { SetValue(CurrentColorProperty, value); }
        }

        public static readonly DependencyProperty CurrentColorProperty =
            DependencyProperty.Register("CurrentColor", typeof(Color), typeof(ColorWheel), new PropertyMetadata(Colors.White, CurrentColorPropertyChanged));

        private static void CurrentColorPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue == e.NewValue) return;
            var me = (ColorWheel)d;
            me.UpdateCursor();
        }

        Path _border;
        Rectangle _inner;
        Border _cursor;

        private const int CursorSize = 10;
        private const int HalfCursorSize = CursorSize / 2;

        public ColorWheel()
        {
            _border = new Path() { Fill = Brushes.White };
            _border.SetBinding(Path.FillProperty, new Binding(nameof(Background)) { Source = this });

            _inner = new Rectangle() { Fill = Brushes.Black };
            if (!App.IsDesignMode)
                _inner.Effect = new ColorWheelEffect();

            _cursor = new Border()
            {
                BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
                Width = CursorSize, Height = CursorSize,
                RenderTransform = new RotateTransform(45),
                RenderTransformOrigin = new Point(0.5, 0.5),
            };

            Children.Add(_inner);
            Children.Add(_border);
            Children.Add(_cursor);

            this.SizeChanged += (_, _) => Arrange();
            Arrange();
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            var pt = Mouse.GetPosition(this);
            if (e.LeftButton == MouseButtonState.Pressed && IsPointInWheel(pt))
            {
                CaptureMouse();
                SetColor(pt);
            }
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
                SetColor(Mouse.GetPosition(this));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (IsMouseCaptured)
                SetColor(Mouse.GetPosition(this));
        }

        private void Arrange()
        {
            _border.Width = ActualWidth;
            _border.Height = ActualHeight;
            _inner.Width = ActualWidth;
            _inner.Height = ActualHeight;

            if (ActualWidth > 0 && ActualHeight > 0)
            {
                _border.Data = new CombinedGeometry(
                        GeometryCombineMode.Exclude,
                        new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)),
                        new EllipseGeometry(new Rect(1, 1, ActualWidth - 2, ActualHeight - 2)));
            }

            UpdateCursor();
        }

        private void UpdateCursor()
        {
            _cursor.Background = new SolidColorBrush(Color.FromRgb(CurrentColor.R, CurrentColor.G, CurrentColor.B));
            var lc = GetColorLocation(CurrentColor);
            SetLeft(_cursor, lc.X - HalfCursorSize);
            SetTop(_cursor, lc.Y - HalfCursorSize);
        }

        protected Point GetColorLocation(Color color)
        {
            var h1 = HSLColor.FromRGB(color);
            return GetColorLocation(h1);
        }

        protected virtual Point GetColorLocation(HSLColor color)
        {
            double angle = color.Hue * Math.PI / 180;
            double radius = ActualWidth / 2;

            if (color.Saturation == 0)
            {
                radius = 0;
            }
            else
            {
                double mult = 1 - (Math.Max(0.5, color.Lightness) / 0.5 - 1);
                radius *= mult;
            }

            return this.GetColorLocation(angle, radius);
        }

        protected Point GetColorLocation(double angleR, double radius)
        {
            double x = (ActualWidth / 2) + Math.Cos(angleR) * radius;
            double y = (ActualHeight / 2) - Math.Sin(angleR) * radius;
            return new Point(x, y);
        }

        protected bool IsPointInWheel(Point point)
        {
            // http://my.safaribooksonline.com/book/programming/csharp/9780672331985/graphics-with-windows-forms-and-gdiplus/ch17lev1sec21
            Point normalized = new Point(point.X - (ActualWidth / 2), point.Y - (ActualHeight / 2));
            double radius = ActualWidth / 2;
            return normalized.X * normalized.X + normalized.Y * normalized.Y <= radius * radius;
        }

        protected virtual void SetColor(Point point)
        {
            double radius = ActualWidth / 2;
            double dx = Math.Abs(point.X - (ActualWidth / 2));
            double dy = Math.Abs(point.Y - (ActualHeight / 2));
            double angle = Math.Atan(dy / dx) / Math.PI * 180;
            double distance = Math.Pow(Math.Pow(dx, 2) + Math.Pow(dy, 2), 0.5);
            double saturation = Math.Min(1, distance / radius);
            double lightness = 1 - (saturation * 0.5);

            if (point.X < (ActualWidth / 2))
            {
                angle = 180 - angle;
            }

            if (point.Y > (ActualHeight / 2))
            {
                angle = 360 - angle;
            }

            HSLColor newColor = new HSLColor() { Hue = angle, Lightness = lightness, Saturation = 1 };
            CurrentColor = newColor.ToRGB();
        }

        //protected virtual void SetColor(Point uv)
        //{
        //    // https://developer.download.nvidia.com/cg/length.html
        //    double length(Point v)
        //    {
        //        double dot(Point a, Point b)
        //        {
        //            return a.X * b.X + a.Y * b.Y;
        //        }
        //        return Math.Sqrt(dot(v, v));
        //    }


        //    // same impl as ColorWheelShader.hlsl
        //    const double value = 1d;
        //    uv = new Point(uv.X / ActualWidth, uv.Y / ActualHeight);
        //    uv = new Point(2 * uv.X - 1, 2 * uv.Y - 1);
        //    uv = new Point(uv.X, uv.Y / -1);
        //    double saturation = length(uv);
        //    double hue = 3 * (Math.PI - Math.Atan2(uv.Y, -uv.X)) / Math.PI;
        //    double chroma = value * saturation;
        //    double second = chroma * (1 - Math.Abs(hue % 2.0 - 1));

        //    (double r, double g, double b) rgb;

        //    if (hue < 1)
        //        rgb = (chroma, second, 0);
        //    else if (hue < 2)
        //        rgb = (second, chroma, 0);
        //    else if (hue < 3)
        //        rgb = (0, chroma, second);
        //    else if (hue < 4)
        //        rgb = (0, second, chroma);
        //    else if (hue < 5)
        //        rgb = (second, 0, chroma);
        //    else
        //        rgb = (chroma, 0, second);

        //    var m = (value - chroma);

        //    byte r = (byte)((rgb.r + m) * 255);
        //    byte g = (byte)((rgb.g + m) * 255);
        //    byte b = (byte)((rgb.b + m) * 255);

        //    CurrentColor = Color.FromRgb(r, g, b);
        //}

        private class ColorWheelEffect : ShaderEffect
        {
            public ColorWheelEffect()
            {
                this.PixelShader = new PixelShader { UriSource = new Uri("pack://application:,,,/UI/Dialogs/ColorWheelShader.cso", UriKind.Absolute) };
            }
        }
    }
}
