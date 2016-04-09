using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Xml.Serialization;

namespace DrawToolsLib.Graphics
{
    public class GraphicsText : GraphicsRectangle
    {
        [XmlIgnore]
        public int Padding { get; } = 15;

        public int Angle
        {
            get { return _angle; }
            set
            {
                _angle = value;
                OnPropertyChanged(nameof(Angle));
            }
        }
        public string Body
        {
            get { return _body; }
            set
            {
                _body = value;
                OnPropertyChanged(nameof(Body));
            }
        }

        private int _angle;
        private string _body;

        public GraphicsText(DrawingCanvas canvas, Point point)
            : this(canvas.ObjectColor, canvas.LineWidth, point)
        {
        }
        public GraphicsText(Color objectColor, double lineWidth, Point point)
            : base(objectColor, lineWidth, new Rect(point, new Size(1, 1)))
        {
            Random rnd = new Random();

            var colors = new Color[]
            {
                Color.FromRgb(255, 255, 203),
                Color.FromRgb(255, 255, 203),
                Color.FromRgb(229, 203, 228),
                Color.FromRgb(203, 228, 222),
                Color.FromRgb(213, 198, 157)
            };

            ObjectColor = colors[rnd.Next(0, colors.Length - 1)];
            Angle = rnd.Next(-4, 4);
            Body = "double click to edit note.";
        }
        protected GraphicsText()
        {
        }

        internal override void Draw(DrawingContext context)
        {
            var form = CreateFormattedText();

            this.Right = this.Left + form.Width + (Padding * 2);
            this.Bottom = this.Top + form.Height + (Padding * 2);

            //context.PushTransform(new RotateTransform(Angle, (this.Right - this.Left) / 2 + this.Left, (this.Bottom - this.Top) / 2 + this.Top));
            context.PushTransform(new RotateTransform(Angle, Left, Top));

            drawShadow3(context, Color.FromArgb(140, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), Bounds, 4);

            context.DrawRectangle(new SolidColorBrush(ObjectColor), null, Bounds);
            context.DrawText(form, new Point(this.Left + Padding, this.Top + Padding));
        }
        private void drawShadow3(DrawingContext context, Color foreg, Color backg, Rect rect, double depth)
        {
            var bottom_brush = new LinearGradientBrush(foreg, backg, 90);
            var bottom_path = new RectangleGeometry(new Rect(new Point(rect.Left + depth, rect.Bottom), new Size(rect.Width - depth, depth)));

            var right_brush = new LinearGradientBrush(foreg, backg, 0);
            var right_path = new RectangleGeometry(new Rect(new Point(rect.Right, rect.Top + depth), new Size(depth, rect.Height - depth)));

            RadialGradientBrush bottomright_brush = new RadialGradientBrush(foreg, backg)
            {
                Center = new Point(0, 0),
                GradientOrigin = new Point(0, 0),
                RadiusX = 1,
                RadiusY = 1,
            };
            var bottomright_path = new RectangleGeometry(new Rect(rect.BottomRight, new Size(depth, depth)));

            RadialGradientBrush topright_brush = new RadialGradientBrush(foreg, backg)
            {
                Center = new Point(0, 1),
                GradientOrigin = new Point(0, 1),
                RadiusX = 1,
                RadiusY = 1,
            };
            var topright_path = new RectangleGeometry(new Rect(rect.TopRight, new Size(depth, depth)));

            RadialGradientBrush bottomleft_brush = new RadialGradientBrush(foreg, backg)
            {
                Center = new Point(1, 0),
                GradientOrigin = new Point(1, 0),
                RadiusX = 1,
                RadiusY = 1,
            };
            var bottomleft_path = new RectangleGeometry(new Rect(rect.BottomLeft, new Size(depth, depth)));

            context.DrawGeometry(bottom_brush, null, bottom_path);
            context.DrawGeometry(right_brush, null, right_path);
            context.DrawGeometry(bottomright_brush, null, bottomright_path);
            context.DrawGeometry(topright_brush, null, topright_path);
            context.DrawGeometry(bottomleft_brush, null, bottomleft_path);
        }

        internal FormattedText CreateFormattedText()
        {
            return CreateFormattedText(Body);
        }
        internal FormattedText CreateFormattedText(string text)
        {
            var typeface = new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Normal, new FontStretch());

            var formatted = new FormattedText(
                Body,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                12,
                new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)));

            return formatted;
        }
    }
}
