using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clowd.Util;

namespace Clowd.UI.Dialogs.ColorPicker
{
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
}
