using System;
using System.Globalization;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Text", Skills = Skill.Color | Skill.Font | Skill.Angle)]
    public class GraphicText : GraphicRectangle
    {
        public const int TextPadding = 15;

        [JsonIgnore]
        public bool Editing
        {
            get => _editing;
            set => Set(ref _editing, value);
        }

        public string Body
        {
            get => _body;
            set => SetAndNormalize(ref _body, value);
        }

        public string FontName
        {
            get => _fontName;
            set => SetAndNormalize(ref _fontName, value);
        }

        public double FontSize
        {
            get => _fontSize;
            set => SetAndNormalize(ref _fontSize, value);
        }

        public FontStyle FontStyle
        {
            get => _fontStyle;
            set => SetAndNormalize(ref _fontStyle, value);
        }

        public FontWeight FontWeight
        {
            get => _fontWeight;
            set => SetAndNormalize(ref _fontWeight, value);
        }

        public FontStretch FontStretch
        {
            get => _fontStretch;
            set => SetAndNormalize(ref _fontStretch, value);
        }

        private string _body = string.Empty;
        private string _fontName = "Segoe UI";
        private double _fontSize = 12;
        private FontStyle _fontStyle = FontStyle.Normal;
        private FontWeight _fontWeight = FontWeight.Normal;
        private FontStretch _fontStretch = FontStretch.Normal;
        private bool _editing;

        private static readonly Random _rnd = new Random();

        private static readonly Color[] _colors = new Color[]
        {
            Color.FromRgb(255, 255, 203),
            Color.FromRgb(229, 203, 228),
            Color.FromRgb(203, 228, 222),
        };

        private static int _nextColor = 0;

        public GraphicText()
        { }

        public GraphicText(Color objectColor, double lineWidth, Point point, double angle = 0, string? body = null)
            : base(objectColor, lineWidth, new Rect(point, new Size(1, 1)), angle)
        {
            Body = body ?? "Double-click to edit notes.\r\nUse Shift+Enter for new lines.";
        }

        /// <summary>Picks the next "sticky note" pastel colour for default text graphics.</summary>
        public static Color NextDefaultColor()
        {
            var c = _colors[_nextColor];
            _nextColor = (_nextColor + 1) % _colors.Length;
            return c;
        }

        /// <summary>Returns a small random rotation in degrees so notes feel hand-placed.</summary>
        public static double RandomTilt() => _rnd.NextDouble() * 8 - 4;

        internal override int HandleCount => 1;

        internal override Point GetHandle(int handleNumber, DpiScale uiscale)
        {
            // In this class, handle #1 is the rotation handle. In the base class, this is handle #9.
            if (handleNumber == 1)
                return base.GetHandle(9, uiscale);
            return base.GetHandle(0, uiscale);
        }

        internal override Avalonia.Input.Cursor GetHandleCursor(int handleNumber)
        {
            // TODO Phase 12: replace with CursorResources.Rotate
            return handleNumber == 1
                ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                : HelperFunctions.DefaultCursor;
        }

        internal override void MoveHandleTo(Point point, int handleNumber)
        {
            base.MoveHandleTo(point, handleNumber == 1 ? 9 : 0);
        }

        internal override void Draw(DrawingContext context, DpiScale uiscale)
        {
            // if editing (TextBox is visible) we hide the text but keep the background
            DrawObjectImpl(context, !Editing);
            if (IsSelected && !Editing)
            {
                // Selection UI has to follow the rotation, same as for a plain
                // GraphicRectangle. DrawObjectImpl already pushes its own
                // transform; push a matching one here for the trackers / border.
                var rotateMatrix =
                    Matrix.CreateTranslation(-CenterOfRotation.X, -CenterOfRotation.Y) *
                    Matrix.CreateRotation(Angle * Math.PI / 180.0) *
                    Matrix.CreateTranslation(CenterOfRotation.X, CenterOfRotation.Y);

                using (context.PushTransform(rotateMatrix))
                {
                    DrawRotationTracker(context, new Point(Right, ((Bottom - Top) / 2) + Top), GetHandleRectangle(1, uiscale), uiscale);
                    DrawDashedBorder(context, UnrotatedBounds);
                }
            }
        }

        internal override void DrawObject(DrawingContext context)
        {
            DrawObjectImpl(context, true);
        }

        protected virtual void DrawObjectImpl(DrawingContext context, bool showText)
        {
            var rotateMatrix =
                Matrix.CreateTranslation(-CenterOfRotation.X, -CenterOfRotation.Y) *
                Matrix.CreateRotation(Angle * Math.PI / 180.0) *
                Matrix.CreateTranslation(CenterOfRotation.X, CenterOfRotation.Y);

            using (context.PushTransform(rotateMatrix))
            {
                context.DrawRectangle(new SolidColorBrush(ObjectColor), null, UnrotatedBounds);
                if (showText)
                {
                    var form = CreateFormattedText();
                    context.DrawText(form, new Point(Left + TextPadding, Top + TextPadding));
                }
            }
        }

        internal override void Activate(object canvas)
        {
            // Raise a TextEditRequested event on the canvas. The shell (e.g.
            // EditorWindow) owns the overlay TextBox and is responsible for
            // positioning / focus / commit. We just flag Editing so the
            // graphic's Draw() method hides the baked-in text while the
            // TextBox overlay is visible.
            if (canvas is DrawingCanvas dc)
            {
                Editing = true;
                dc.RequestTextEdit(this);
            }
        }

        internal override void Normalize()
        {
            base.Normalize();
            var form = CreateFormattedText();
            Right = Left + form.Width + (TextPadding * 2);
            Bottom = Top + form.Height + (TextPadding * 2);
        }

        protected virtual FormattedText CreateFormattedText()
        {
            string txt = Body;
            if (string.IsNullOrEmpty(txt))
                txt = " ";

            if (Editing && (Body.EndsWith('\r') || Body.EndsWith('\n')))
                txt += "_";

            return new FormattedText(
                txt,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily(FontName), FontStyle, FontWeight, FontStretch),
                FontSize,
                new SolidColorBrush(Color.FromArgb(255, 0, 0, 0)));
        }
    }
}
