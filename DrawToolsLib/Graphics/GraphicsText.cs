using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;

namespace DrawToolsLib.Graphics
{
    [Serializable]
    public class GraphicsText : GraphicsRectangle
    {
        [XmlIgnore]
        public Typeface Typeface => _typeface.GetTypeface();

        public string FontFamily
        {
            get { return _typeface.FontFamily; }
            set
            {
                if (value == _typeface.FontFamily) return;
                _typeface.FontFamily = value;
                OnPropertyChanged(nameof(FontFamily));
            }
        }
        public string FontStyle
        {
            get { return _typeface.FontStyle; }
            set
            {
                if (value == _typeface.FontStyle) return;
                _typeface.FontStyle = value;
                OnPropertyChanged(nameof(FontStyle));
            }
        }
        public string FontWeight
        {
            get { return _typeface.FontWeight; }
            set
            {
                if (value == _typeface.FontWeight) return;
                _typeface.FontWeight = value;
                OnPropertyChanged(nameof(FontWeight));
            }
        }
        public string FontStretch
        {
            get { return _typeface.FontStretch; }
            set
            {
                if (value == _typeface.FontStretch) return;
                _typeface.FontStretch = value;
                OnPropertyChanged(nameof(FontStretch));
            }
        }

        public double FontSize
        {
            get { return _fontSize; }
            set
            {
                if (value.Equals(_fontSize)) return;
                _fontSize = value;
                OnPropertyChanged();
            }
        }
        public string Text
        {
            get { return _text; }
            set
            {
                if (value == _text) return;
                _text = value;
                OnPropertyChanged();
            }
        }

        private TypefaceString _typeface;
        private double _fontSize;
        private string _text;

        protected GraphicsText()
        {
            _typeface = new TypefaceString();
        }
        public GraphicsText(DrawingCanvas canvas, Rect rect, Typeface typeFace, double fontSize, string text)
            : this(canvas.ActualScale, canvas.ObjectColor, canvas.LineWidth, rect, typeFace, fontSize, text)
        {
        }
        public GraphicsText(double scale, Color objectColor, double lineWidth, Rect rect,
            Typeface typeFace, double fontSize, string text) : base(scale, objectColor, lineWidth, rect)
        {
            _typeface = TypefaceString.FromTypeface(typeFace);
            _fontSize = fontSize;
            _text = text;
        }
        public GraphicsText(DrawingCanvas canvas, Rect rect, TypefaceString typeFace, double fontSize, string text)
           : this(canvas.ActualScale, canvas.ObjectColor, canvas.LineWidth, rect, typeFace, fontSize, text)
        {
        }
        public GraphicsText(double scale, Color objectColor, double lineWidth, Rect rect,
            TypefaceString typeFace, double fontSize, string text) : base(scale, objectColor, lineWidth, rect)
        {
            _typeface = typeFace;
            _fontSize = fontSize;
            _text = text;
        }

        internal override void DrawRectangle(DrawingContext drawingContext)
        {
            if (drawingContext == null)
                throw new ArgumentNullException(nameof(drawingContext));

            Rect rect = Bounds;
            drawingContext.PushClip(new RectangleGeometry(rect));
            drawingContext.DrawRectangle(Brushes.LightYellow, new Pen(Brushes.Black, 1), rect);
            drawingContext.DrawText(CreateFormattedText(), new Point(rect.Left, rect.Top));

            drawingContext.Pop();

            if (IsSelected)
            {
                drawingContext.DrawRectangle(
                    null,
                    new Pen(new SolidColorBrush(ObjectColor), ActualLineWidth),
                    rect);
            }

            // Draw tracker
        }
        internal override bool Contains(Point point)
        {
            return Bounds.Contains(point);
        }

        public override GraphicsBase Clone()
        {
            return new GraphicsText(ActualScale, ObjectColor, LineWidth, Bounds, _typeface, FontSize, Text) { ObjectId = ObjectId };
        }

        private FormattedText CreateFormattedText()
        {
            if (String.IsNullOrEmpty(_typeface.FontFamily))
                _typeface.FontFamily = Properties.Settings.Default.DefaultFontFamily;

            if (_text == null)
                _text = "";

            if (_fontSize <= 0.0)
                _fontSize = 12.0;

            Typeface typeface = _typeface.GetTypeface();

            var formatted = new FormattedText(
                Text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                new SolidColorBrush(ObjectColor));

            formatted.MaxTextWidth = Bounds.Width;
            return formatted;
        }
        public class TypefaceString
        {
            public string FontFamily { get; set; }
            public string FontStyle { get; set; }
            public string FontWeight { get; set; }
            public string FontStretch { get; set; }

            public TypefaceString()
            {
            }

            public TypefaceString(string family, string style, string weight, string stretch)
            {
                FontFamily = family;
                FontStyle = style;
                FontWeight = weight;
                FontStretch = stretch;
            }

            public Typeface GetTypeface()
            {
                return new Typeface(new FontFamily(FontFamily),
                    FontConversions.FontStyleFromString(FontStyle),
                    FontConversions.FontWeightFromString(FontWeight),
                    FontConversions.FontStretchFromString(FontStretch));
            }
            public static TypefaceString FromTypeface(Typeface tface)
            {
                return new TypefaceString()
                {
                    FontFamily = tface.FontFamily.ToString(),
                    FontStretch = FontConversions.FontStretchToString(tface.Stretch),
                    FontStyle = FontConversions.FontStyleToString(tface.Style),
                    FontWeight = FontConversions.FontWeightToString(tface.Weight)
                };
            }
        }
    }
}
