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
    /// <summary>
    /// Interaction logic for ColorEditor.xaml
    /// </summary>
    public partial class ColorEditor : Window
    {
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
                ColorPalette.Children.Add(new Border { Background = new SolidColorBrush(c) });
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
            DependencyProperty.Register("CurrentColor", typeof(Color), typeof(ColorWheel), new PropertyMetadata(Colors.Black, CurrentColorPropertyChanged));

        private static void CurrentColorPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
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
            _cursor.Background = new SolidColorBrush(CurrentColor);
            var lc = GetColorLocation(CurrentColor);
            SetLeft(_cursor, lc.X - HalfCursorSize);
            SetTop(_cursor, lc.Y - HalfCursorSize);
        }

        protected Point GetColorLocation(Color color)
        {
            return GetColorLocation(HSLColor.FromRGB(color));
        }

        protected virtual Point GetColorLocation(HSLColor color)
        {
            double angle = color.Hue * Math.PI / 180;
            double radius = ActualWidth / 2 - BorderMargin;
            radius *= color.Saturation;
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
            double saturation = distance / radius;

            if (point.X < (ActualWidth / 2))
            {
                angle = 180 - angle;
            }

            if (point.Y > (ActualHeight / 2))
            {
                angle = 360 - angle;
            }

            HSLColor newColor = new HSLColor() { Hue = angle, Lightness = 0.5, Saturation = saturation };
            CurrentColor = newColor.ToRGB();
        }

        private class ColorWheelEffect : ShaderEffect
        {
            public ColorWheelEffect()
            {
                this.PixelShader = new PixelShader { UriSource = new Uri("pack://application:,,,/UI/Dialogs/ColorWheelShader.cso", UriKind.Absolute) };
            }
        }
    }
}
