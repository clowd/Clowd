using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Text", Skills = Skill.Color | Skill.Font | Skill.Angle)]
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

        private string _body;
        private string _fontName = "Segoe UI";
        private double _fontSize = 12;
        private FontStyle _fontStyle = FontStyle.Normal;
        private FontWeight _fontWeight = FontWeight.Normal;
        private FontStretch _fontStretch = FontStretch.Normal;
        [Transient] private bool _editing; // not persisted by GraphicsSerializer

        private static Random _rnd = new Random();

        private static Color[] _colors = new Color[]
        {
            Color.FromRgb(255, 255, 203), Color.FromRgb(229, 203, 228), Color.FromRgb(203, 228, 222),
        };

        private static int _nextColor = 0;

        protected GraphicText()
        { }

        public GraphicText(DrawingCanvas canvas, Point point)
            : this(_colors[_nextColor], canvas.LineWidth, point, _rnd.NextDouble() * 8 - 4)
        {
            _nextColor = (_nextColor + 1) % _colors.Length;
            if (!canvas.ObjectColorAuto)
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
            Body = body ?? "Double-Click to edit notes.\r\nUse Shift+Enter for new lines.";
        }

        // PORT NOTE (aspect map entry): text shaping inputs invalidate the cached FormattedText
        // (Text) on top of the geometry/bounds/shadow a shape change implies. Editing is
        // transient and left to the conservative default (it repaints, and CreateFormattedText's
        // key already accounts for the editing trailing-newline suffix).
        internal override void DeclarePropertyEffects(Dictionary<string, InvalidationAspects> map)
        {
            base.DeclarePropertyEffects(map);
            const InvalidationAspects text =
                InvalidationAspects.Bounds | InvalidationAspects.Geometry | InvalidationAspects.Shadow | InvalidationAspects.Text;
            map[nameof(Body)] = text;
            map[nameof(FontName)] = text;
            map[nameof(FontSize)] = text;
            map[nameof(FontStyle)] = text;
            map[nameof(FontWeight)] = text;
            map[nameof(FontStretch)] = text;
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
            return handleNumber == 1 ? CursorResources.Rotate : HelperFunctions.DefaultCursor;
        }

        internal override void MoveHandleTo(Point point, int handleNumber)
        {
            // In this class, handle #1 is the rotation handle. In the base class, this is handle #9 because #1–8 are used for resizing.
            base.MoveHandleTo(point, handleNumber == 1 ? 9 : 0);
        }

        internal override void Draw(DrawingContext context, DpiScale uiscale)
        {
            // if editing (TextBox is visible) we hide the text / selection ui.
            // Transform-scoping rule (§2.1): the rotation scope is owned here, and the trackers are drawn inside it.
            using (context.PushTransform(MatrixHelper.Rotation(Angle, CenterOfRotation)))
            {
                DrawObjectImpl(context, !Editing);
                if (IsSelected && !Editing)
                {
                    DrawRotationTracker(context, new Point(Right, ((Bottom - Top) / 2) + Top), GetHandleRectangle(1, uiscale), uiscale);
                    DrawDashedBorder(context, UnrotatedBounds);
                }
            }
        }

        internal override void DrawObject(DrawingContext context)
        {
            // DrawObject is called directly when drawing to an off-screen surface. we always want to render text
            using (context.PushTransform(MatrixHelper.Rotation(Angle, CenterOfRotation)))
                DrawObjectImpl(context, true);
        }

        protected virtual void DrawObjectImpl(DrawingContext context, bool showText)
        {
            // NOTE: unlike WPF, the rotation transform is pushed by the callers (Draw/DrawObject), not here.
            context.DrawRectangle(RenderResources.GetBrush(ObjectColor), null, UnrotatedBounds);
            if (showText)
            {
                var form = CreateFormattedText();
                context.DrawText(form, new Point(Left + TextPadding, Top + TextPadding));
            }
        }

        internal override void Activate(DrawingCanvas canvas)
        {
            canvas.ToolText.CreateTextBox(this, canvas, false);
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
            // trailing whitespace is truncated from height measurements.
            // this '_' won't get rendered while Editing=true, but it will allow us to calculate the correct rectangle bounds
            string txt = Body;

            // we should still be able to measure if you've just done Ctrl+A, Bksp
            if (String.IsNullOrEmpty(txt))
                txt = " ";

            if (Editing && (Body.EndsWith('\r') || Body.EndsWith('\n')))
                txt += "_";

            // PORT NOTE (Text cache): shaping is the expensive step and Normalize()+Draw both call
            // this per keystroke — cache the FormattedText in RenderCache keyed by the full shaping
            // input (the effective text incl. the editing suffix, plus the font 5-tuple). Normalize
            // and Draw thus share the ONE instance, so their measurements are identical by
            // construction. The key guards correctness even for aspects not cleared by the map
            // (e.g. transient Editing toggles); the Text aspect clear is the fast common path.
            var key = (txt, FontName, FontSize, FontStyle, FontWeight, FontStretch);
            if (RenderCache.Text is { } cached && key.Equals(RenderCache.TextKey))
                return cached;

            // decision #31: WPF FormattedText(…, Ideal, pixelsPerDip) → Avalonia FormattedText
            var form = new FormattedText(
                txt,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontUtil.CreateSafe(FontName), FontStyle, FontWeight, FontStretch),
                FontSize,
                RenderResources.GetBrush(Color.FromArgb(255, 0, 0, 0)));
            RenderCache.Text = form;
            RenderCache.TextKey = key;
            return form;
        }
    }
}
