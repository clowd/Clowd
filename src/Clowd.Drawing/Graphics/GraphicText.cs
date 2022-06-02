using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Clowd.Drawing.Tools;
using RT.Serialization;

namespace Clowd.Drawing.Graphics
{
    public class GraphicText : GraphicRectangle
    {
        public const int TextPadding = 15;

        public bool Editing
        {
            get => _editing;
            set => Set(ref _editing, value);
        }

        public string Body
        {
            get => _body;
            set
            {
                if (Set(ref _body, value))
                    Normalize();
            }
        }

        public string FontName
        {
            get => _fontName;
            set => Set(ref _fontName, value);
        }

        public double FontSize
        {
            get => _fontSize;
            set => Set(ref _fontSize, value);
        }

        public FontStyle FontStyle
        {
            get => _fontStyle;
            set => Set(ref _fontStyle, value);
        }

        public FontWeight FontWeight
        {
            get => _fontWeight;
            set => Set(ref _fontWeight, value);
        }

        public FontStretch FontStretch
        {
            get => _fontStretch;
            set => Set(ref _fontStretch, value);
        }

        private string _body;
        private string _fontName = "Segoe UI";
        private double _fontSize = 12;
        private FontStyle _fontStyle = FontStyles.Normal;
        private FontWeight _fontWeight = FontWeights.Normal;
        private FontStretch _fontStretch = FontStretches.Normal;
        [ClassifyIgnore] private bool _editing;

        private static Random _rnd = new Random();

        private static Color[] _colors = new Color[]
        {
            // Color.FromRgb(210,210,210),
            Color.FromRgb(255, 255, 203), Color.FromRgb(229, 203, 228), Color.FromRgb(203, 228, 222),
        };

        private static int _nextColor = 0;

        protected GraphicText()
        { }

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
            : base(objectColor, lineWidth, new Rect(point, new Size(1, 1)), angle)
        {
            Body = body ?? "Double-click to edit note.";
        }

        internal override int HandleCount => 1;

        internal override Point GetHandle(int handleNumber, DpiScale uiscale)
        {
            // In this class, handle #1 is the rotation handle. In the base class, this is handle #9 because #1–8 are used for resizing.
            if (handleNumber == 1)
                return base.GetHandle(9, uiscale);
            return base.GetHandle(0, uiscale);
        }

        internal override Cursor GetHandleCursor(int handleNumber)
        {
            return handleNumber == 1 ? Resource.CursorRotate : HelperFunctions.DefaultCursor;
        }

        internal override void MoveHandleTo(Point point, int handleNumber)
        {
            // In this class, handle #1 is the rotation handle. In the base class, this is handle #9 because #1–8 are used for resizing.
            base.MoveHandleTo(point, handleNumber == 1 ? 9 : 0);
        }

        internal override void Draw(DrawingContext context, DpiScale uiscale)
        {
            // if editing (TextBox is visible) we hide the text / selection ui
            DrawObjectImpl(context, !Editing);
            if (IsSelected && !Editing)
            {
                DrawRotationTracker(context, new Point(Right, ((Bottom - Top) / 2) + Top), GetHandleRectangle(1, uiscale), uiscale);
                DrawDashedBorder(context, UnrotatedBounds);
            }
        }

        internal override void DrawObject(DrawingContext context)
        {
            // DrawObject is called directly when drawing to an off-screen surface. we always want to render text
            DrawObjectImpl(context, true);
        }

        private void DrawObjectImpl(DrawingContext context, bool showText)
        {
            context.PushTransform(new RotateTransform(Angle, CenterOfRotation.X, CenterOfRotation.Y));
            context.DrawRectangle(new SolidColorBrush(ObjectColor), null, UnrotatedBounds);
            if (showText)
            {
                var form = CreateFormattedText();
                context.DrawText(form, new Point(Left + TextPadding, Top + TextPadding));
            }
        }

        internal override void Activate(DrawingCanvas canvas)
        {
            (canvas.GetTool(ToolType.Text) as ToolText).CreateTextBox(this, canvas, false);
        }

        internal override void Normalize()
        {
            base.Normalize();
            if (!String.IsNullOrEmpty(Body))
            {
                var form = CreateFormattedText();
                Right = Left + form.Width + (TextPadding * 2);
                Bottom = Top + form.Height + (TextPadding * 2);
            }
        }

        internal FormattedText CreateFormattedText()
        {
            // trailing whitespace is truncated from height measurements. 
            // this '_' won't get rendered while Editing=true, but it will allow us to calculate the correct rectangle bounds
            string txt = Body;
            if (Editing && (Body.EndsWith('\r') || Body.EndsWith('\n')))
                txt += "_";

            // should we use VisualTreeHelper.GetDpi(this).PixelsPerDip ?
            var pixelsPerDip = new DpiScale(1, 1).PixelsPerDip;

            return new FormattedText(
                txt,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily(FontName), FontStyle, FontWeight, FontStretch),
                FontSize,
                new SolidColorBrush(Color.FromArgb(255, 0, 0, 0)),
                null,
                TextFormattingMode.Ideal,
                pixelsPerDip);
        }
    }
}
