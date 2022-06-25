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
using Clowd.Util;

namespace Clowd.UI.Dialogs
{
    public partial class ColorEditor : Window
    {
        public Color CurrentColor
        {
            get { return (Color)GetValue(CurrentColorProperty); }
            set { SetValue(CurrentColorProperty, value); }
        }

        public static readonly DependencyProperty CurrentColorProperty =
            DependencyProperty.Register("CurrentColor", typeof(Color), typeof(ColorEditor), new PropertyMetadata(Colors.White));

        public ColorEditor()
        {
            InitializeComponent();
            CreateColorPalette();
        }

        private void CreateColorPalette()
        {
            ColorPalette.Children.Clear();
            var colors = Cyotek.Windows.Forms.ColorPalettes.PaintPalette.Select(c => Color.FromArgb(c.A, c.R, c.G, c.B));
            foreach (var c in colors)
            {
                var item = new ColorPaletteItem { Background = new SolidColorBrush(c) };
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

        public string ColorPart
        {
            get { return (string)GetValue(ColorPartProperty); }
            set { SetValue(ColorPartProperty, value); }
        }

        public static readonly DependencyProperty ColorPartProperty =
            DependencyProperty.Register("ColorPart", typeof(string), typeof(ColorSlider), 
                new FrameworkPropertyMetadata("A", FrameworkPropertyMetadataOptions.AffectsRender));

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
                    start = Colors.Pink;
                    end = Colors.Pink;
                    break;
                case "S":
                    hsl = HSLColor.FromRGB(CurrentColor);
                    hsl.Saturation = 0;
                    start = hsl.ToRGB();
                    hsl.Saturation = 1;
                    end = hsl.ToRGB();
                    break;
                case "L":
                    hsl = HSLColor.FromRGB(CurrentColor);
                    hsl.Lightness = 0;
                    start = hsl.ToRGB();
                    hsl.Lightness = 1;
                    end = hsl.ToRGB();
                    break;
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
                "H" => HSLColor.FromRGB(CurrentColor).Hue / 360d,
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
                    hsl = HSLColor.FromRGB(CurrentColor);
                    hsl.Hue = value * 360d;
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

            var whitePen = new Pen(Brushes.White, 1);
            drawingContext.DrawGeometry(Brushes.Black, whitePen, geo);
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

        Ellipse _border;
        Ellipse _inner;
        Border _cursor;

        bool _mouseDown;

        private const int BorderMargin = 1;
        private const int CursorSize = 10;
        private const int HalfCursorSize = CursorSize / 2;

        public ColorWheel()
        {
            _border = new Ellipse() { StrokeThickness = 2, Stroke = Brushes.White };
            _inner = new Ellipse() { Fill = Brushes.Black };
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
            var pt = Mouse.GetPosition(this);
            if (e.LeftButton == MouseButtonState.Pressed && IsPointInWheel(pt))
            {
                CaptureMouse();
                _mouseDown = true;
                SetColor(pt);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            if (_mouseDown)
            {
                ReleaseMouseCapture();
                _mouseDown = false;
                SetColor(Mouse.GetPosition(this));
            }

            base.OnMouseUp(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_mouseDown)
            {
                SetColor(Mouse.GetPosition(this));
            }
            base.OnMouseMove(e);
        }

        private void Arrange()
        {
            SetLeft(_border, 0);
            SetTop(_border, 0);
            _border.Width = ActualWidth;
            _border.Height = ActualHeight;

            SetLeft(_inner, BorderMargin);
            SetTop(_inner, BorderMargin);
            _inner.Width = Math.Max(0, ActualWidth - BorderMargin * 2);
            _inner.Height = Math.Max(0, ActualHeight - BorderMargin * 2);

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
            double radius = ActualWidth / 2 - BorderMargin;

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
            double radius = ActualWidth / 2 - BorderMargin;
            return normalized.X * normalized.X + normalized.Y * normalized.Y <= radius * radius;
        }

        protected virtual void SetColor(Point point)
        {
            double radius = ActualWidth / 2 - BorderMargin; // 100
            double dx = Math.Abs(point.X - (ActualWidth / 2) - BorderMargin); // 250 - 100 - 2 = 150
            double dy = Math.Abs(point.Y - (ActualHeight / 2) - BorderMargin); // 100 - 100 = 0
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
