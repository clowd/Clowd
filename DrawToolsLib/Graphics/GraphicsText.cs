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

        [XmlIgnore]
        public bool Editing
        {
            get { return _editing; }
            set
            {
                _editing = value;
                OnPropertyChanged(nameof(Editing));
            }
        }
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
        public string FontName
        {
            get { return _fontName; }
            set
            {
                _fontName = value;
                OnPropertyChanged(nameof(FontName));
            }
        }
        public double FontSize
        {
            get { return _fontSize; }
            set
            {
                _fontSize = value;
                OnPropertyChanged(nameof(FontSize));
            }
        }
        public FontStyle FontStyle
        {
            get { return _fontStyle; }
            set
            {
                _fontStyle = value;
                OnPropertyChanged(nameof(FontStyle));
            }
        }
        public FontWeight FontWeight
        {
            get { return _fontWeight; }
            set
            {
                _fontWeight = value;
                OnPropertyChanged(nameof(FontWeight));
            }
        }
        public FontStretch FontStretch
        {
            get { return _fontStretch; }
            set
            {
                _fontStretch = value;
                OnPropertyChanged(nameof(FontStretch));
            }
        }

        private int _angle;
        private string _body;
        private string _fontName = "Arial";
        private double _fontSize = 12;
        private FontStyle _fontStyle = FontStyles.Normal;
        private FontWeight _fontWeight = FontWeights.Normal;
        private FontStretch _fontStretch = FontStretches.Normal;
        private bool _editing;

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
                Color.FromRgb(229, 203, 228),
                Color.FromRgb(203, 228, 222),
                Color.FromRgb(213, 198, 157)
            };

            ObjectColor = colors[rnd.Next(0, colors.Length - 1)];
            Angle = rnd.Next(-4, 4);
            Body = "double click to edit note.";
        }
        public GraphicsText(Color objectColor, double lineWidth, Point point, int angle, string body)
            : base(objectColor, lineWidth, new Rect(point, new Size(1, 1)))
        {
            Angle = angle;
            Body = body;
        }
        protected GraphicsText()
        {
        }

        internal override int HandleCount => 0;

        internal override void Draw(DrawingContext context)
        {
            var form = CreateFormattedText();

            this.Right = this.Left + form.Width + (Padding * 2);
            this.Bottom = this.Top + form.Height + (Padding * 2);

            context.PushTransform(new RotateTransform(Angle, Left, Top));

            context.DrawRectangle(new SolidColorBrush(ObjectColor), null, Bounds);

            if (IsSelected)
                DrawDashedBorder(context);

            if (!Editing)
                context.DrawText(form, new Point(this.Left + Padding, this.Top + Padding));
        }

        internal FormattedText CreateFormattedText()
        {
            return new FormattedText(
                Body,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily(FontName), FontStyle, FontWeight, FontStretch),
                FontSize,
                new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)));
        }

        public override GraphicsBase Clone()
        {
            return new GraphicsText(ObjectColor, LineWidth, Bounds.TopLeft, Angle, Body) { ObjectId = ObjectId };
        }
    }
}
