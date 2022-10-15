using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clowd.Util;
using DependencyPropertyGenerator;

namespace Clowd.UI.Dialogs.ColorPicker
{
    [DependencyProperty<double>("Value", AffectsRender = true, DefaultBindingMode = DefaultBindingMode.TwoWay)]
    [DependencyProperty<double>("ValueMax", AffectsRender = true)]
    [DependencyProperty<Brush>("SliderBrush", AffectsRender = true)]
    public partial class ColorSlider : Border
    {
        protected void HandleMouse()
        {
            var pos = Mouse.GetPosition(this);
            Value = Math.Max(Math.Min(pos.X / ActualWidth, 1), 0) * ValueMax;
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
            drawingContext.DrawRoundedRectangle(SliderBrush, null, bounds, radius, radius);

            // draw cursor triangle
            var pos = ActualWidth * (Value / ValueMax);
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

            drawingContext.DrawGeometry(Brushes.Black, new Pen(Brushes.White, 1), geo);
        }
    }
}
