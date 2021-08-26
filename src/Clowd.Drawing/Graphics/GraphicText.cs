using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;

namespace Clowd.Drawing.Graphics
{
    public class GraphicText : GraphicRectangle
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
        public string Body
        {
            get { return _body; }
            set
            {
                _body = value;
                var form = CreateFormattedText();
                Right = Left + form.Width;
                Bottom = Top + form.Height;
                OnPropertyChanged(nameof(Body));
                OnPropertyChanged(nameof(Right));
                OnPropertyChanged(nameof(Bottom));
                OnPropertyChanged(nameof(Bounds));
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

        private string _body;
        private string _fontName = "Segoe UI";
        private double _fontSize = 12;
        private FontStyle _fontStyle = FontStyles.Normal;
        private FontWeight _fontWeight = FontWeights.Normal;
        private FontStretch _fontStretch = FontStretches.Normal;
        private bool _editing;

        private static Random _rnd = new Random();
        private static Color[] _colors = new Color[]
        {
            // Color.FromRgb(210,210,210),
            Color.FromRgb(255, 255, 203),
            Color.FromRgb(229, 203, 228),
            Color.FromRgb(203, 228, 222),
        };
        private static int _nextColor = 0;

        public GraphicText(DrawingCanvas canvas, Point point)
            : this(_colors[_nextColor], canvas.LineWidth, point, _rnd.NextDouble() * 8 - 4)
        {
            _nextColor = (_nextColor + 1) % _colors.Length;
            if (canvas.ObjectColor.A != 0)
                ObjectColor = canvas.ObjectColor;
            FontName = canvas.TextFontFamilyName;
            FontSize = canvas.TextFontSize;
            FontStretch = canvas.TextFontStretch;
            FontStyle = canvas.TextFontStyle;
            FontWeight = canvas.TextFontWeight;
        }
        public GraphicText(Color objectColor, double lineWidth, Point point, double angle = 0, string body = null)
            : base(objectColor, lineWidth, new Rect(point, new Size(1, 1)))
        {
            Angle = angle;
            Body = body ?? "Double-click to edit note.";
        }
        protected GraphicText()
        {
        }

        internal override int HandleCount => 1;

        internal override Point GetHandle(int handleNumber)
        {
            // In this class, handle #1 is the rotation handle. In the base class, this is handle #9 because #1–8 are used for resizing.
            if (handleNumber == 1)
                return base.GetHandle(9);
            return base.GetHandle(0);
        }

        internal override Cursor GetHandleCursor(int handleNumber)
        {
            return handleNumber == 1 ? Cursors.Cross : HelperFunctions.DefaultCursor;
        }

        internal override void MoveHandleTo(Point point, int handleNumber)
        {
            // In this class, handle #1 is the rotation handle. In the base class, this is handle #9 because #1–8 are used for resizing.
            base.MoveHandleTo(point, handleNumber == 1 ? 9 : 0);
        }

        internal override void Draw(DrawingContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var form = CreateFormattedText();

            Right = Left + form.Width + (Padding * 2);
            Bottom = Top + form.Height + (Padding * 2);

            context.PushTransform(new RotateTransform(Angle, (Left + Right) / 2, (Top + Bottom) / 2));
            context.DrawRectangle(new SolidColorBrush(ObjectColor), null, UnrotatedBounds);

            // don't draw border / rotation controls or text if the textbox is visible
            if (!Editing)
            {
                if (IsSelected)
                {
                    DrawRotationTracker(context, new Point(Right, ((Bottom - Top) / 2) + Top), GetHandleRectangle(1));
                    DrawDashedBorder(context, UnrotatedBounds);
                }

                context.DrawText(form, new Point(Left + Padding, Top + Padding));
            }

            context.Pop();
        }

        internal FormattedText CreateFormattedText()
        {
            // trailing whitespace is truncated from height measurements. 
            // this '_' won't get rendered while Editing=true, but it will allow us to calculate the correct rectangle bounds
            string txt = Body;
            if (Editing && (Body.EndsWith('\r') || Body.EndsWith('\n')))
                txt += "_";

            return new FormattedText(
                txt,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily(FontName), FontStyle, FontWeight, FontStretch),
                FontSize,
                new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)),
                null,
                TextFormattingMode.Display);
        }

        public override GraphicBase Clone()
        {
            return new GraphicText(ObjectColor, LineWidth, Bounds.TopLeft, Angle, Body) { ObjectId = ObjectId };
        }
    }
}
