﻿using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Clowd.Util;
using DependencyPropertyGenerator;

namespace Clowd.UI.Dialogs.ColorPicker
{
    [DependencyProperty<HslRgbColor>("CurrentColor", DefaultBindingMode = DefaultBindingMode.TwoWay)]
    public partial class ColorWheel : Canvas
    {
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
                _inner.Effect = new ColorWheelShader();

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

        partial void OnCurrentColorChanged(HslRgbColor oldValue, HslRgbColor newValue)
        {
            if (oldValue != null)
                newValue.PropertyChanged -= ColorPropertyChanged;

            if (newValue != null)
            {
                newValue.PropertyChanged += ColorPropertyChanged;
                UpdateCursor();
            }
        }

        private void ColorPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            UpdateCursor();
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
            if (CurrentColor == null) return;
            _cursor.Background = new SolidColorBrush(CurrentColor.ToColor());
            var lc = GetColorLocation(CurrentColor);
            SetLeft(_cursor, lc.X - HalfCursorSize);
            SetTop(_cursor, lc.Y - HalfCursorSize);
        }

        protected virtual Point GetColorLocation(HslRgbColor color)
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

            CurrentColor = new HslRgbColor(angle, 1, lightness, CurrentColor.Alpha);
        }
    }
}
