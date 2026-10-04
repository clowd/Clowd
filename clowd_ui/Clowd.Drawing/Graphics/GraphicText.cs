using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    /// <summary>
    /// Free-form text: the body drawn in <see cref="Foreground"/> on an optional background fill,
    /// the object color — transparent by default. (Sticky notes are <see cref="GraphicStickyNote"/>.)
    ///
    /// The object color is the fill, not the text color, because that is what it has always been:
    /// text was once a note card filled with it, with black text. Documents saved then have no
    /// foreground field and load with its black default, so they render exactly as they did.
    /// </summary>
    [GraphicDesc("Text", Skills = Skill.Color | Skill.Fill | Skill.Font | Skill.Angle)]
    public class GraphicText : GraphicRectangle
    {
        /// <summary>Inset of the body from the bounds of text with a visible fill.</summary>
        public const int TextPadding = 15;

        /// <summary>Inset of the body from the bounds of text with no fill: just enough that the
        /// selection border does not touch the glyphs.</summary>
        public const int PlainTextPadding = 4;

        /// <summary>The text color.</summary>
        public Color Foreground
        {
            get => _foreground;
            set
            {
                // as for ObjectColor: only the alpha feeds the shadow silhouette (the text casts it)
                if (_foreground != value && _foreground.A != value.A)
                    RenderCache.Clear(InvalidationAspects.Shadow);
                Set(ref _foreground, value);
            }
        }

        /// <summary>The background fill; fully transparent for none.</summary>
        public override Color ObjectColor
        {
            get => base.ObjectColor;
            set
            {
                // a visible fill gets the roomier padding, so showing or hiding it resizes the box
                // (not during construction: the base sets the color before there is a body)
                var hadFill = HasFill;
                base.ObjectColor = value;
                if (_body != null && HasFill != hadFill)
                    Normalize();
            }
        }

        internal bool HasFill => ObjectColor.A > 0;

        /// <summary>Text casts a drop shadow only from a visible fill: a shadow under bare
        /// letters on the page just looks smudged. (The fill's alpha toggling this re-bakes the
        /// shadow by itself; see <see cref="GraphicBase.ObjectColor"/>.)</summary>
        public override bool DropShadowEffect
        {
            get => base.DropShadowEffect && HasShadowSurface;
            set => base.DropShadowEffect = value;
        }

        /// <summary>Whether there is something besides bare letters to cast the drop shadow.</summary>
        internal virtual bool HasShadowSurface => HasFill;

        internal override string ColorPropertyName => nameof(Foreground);

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
        private Color _foreground = Colors.Black; // absent from documents saved before it existed

        private static Random _rnd = new Random();

        // auto-fill cycles new text through note-card pastels
        private static Color[] _autoFills = new Color[]
        {
            Color.FromRgb(255, 255, 203), Color.FromRgb(229, 203, 228), Color.FromRgb(203, 228, 222),
        };

        private static int _nextAutoFill = 0;

        protected GraphicText()
        { }

        public GraphicText(DrawingCanvas canvas, Point point)
            : this(canvas.ObjectFill, canvas.LineWidth, point)
        {
            if (canvas.ObjectFillAuto)
            {
                ObjectColor = _autoFills[_nextAutoFill];
                _nextAutoFill = (_nextAutoFill + 1) % _autoFills.Length;
                // a slight tilt, so cards dropped one after another look hand-placed
                Angle = _rnd.NextDouble() * 8 - 4;
            }

            Foreground = canvas.ObjectColor;
            FontName = canvas.TextFontFamilyName;
            FontSize = canvas.TextFontSize;
            FontStretch = canvas.TextFontStretch;
            FontStyle = canvas.TextFontStyle;
            FontWeight = canvas.TextFontWeight;
        }

        /// <param name="objectColor">The background fill (transparent for none); the text is
        /// <see cref="Foreground"/>, black unless set.</param>
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
            map[nameof(Foreground)] = InvalidationAspects.Text; // the setter clears Shadow itself when alpha changes
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

        /// <summary>Color the body text is drawn in. Also used by the in-place editor so typing
        /// looks like the committed text.</summary>
        internal virtual Color TextColor => Foreground;

        /// <summary>Whether the drop shadow is cast by the body text. Such a shadow goes stale as
        /// soon as editing starts, so it is hidden (and not re-baked) until the edit commits.</summary>
        internal virtual bool ShadowIncludesText => true;

        /// <summary>Inset of the body from the bounds, where the in-place editor sits.</summary>
        internal double Padding => HasFill ? TextPadding : PlainTextPadding;

        protected virtual void DrawObjectImpl(DrawingContext context, bool showText)
        {
            // NOTE: unlike WPF, the rotation transform is pushed by the callers (Draw/DrawObject), not here.
            if (HasFill)
                context.DrawRectangle(RenderResources.GetBrush(ObjectColor), null, UnrotatedBounds);
            if (showText)
            {
                var form = CreateFormattedText();
                context.DrawText(form, new Point(Left + Padding, Top + Padding));
            }
        }

        internal override void Activate(DrawingCanvas canvas)
        {
            canvas.ToolText.CreateTextBox(this, canvas, false);
        }

        internal override void Normalize()
        {
            // size first, so the base re-centers the rotation on the new bounds (keeping a rotated
            // text's top-left corner in place as it grows)
            var size = MeasureBox();
            Right = Left + size.Width;
            Bottom = Top + size.Height;
            base.Normalize();
        }

        /// <summary>The bounds size the current body needs.</summary>
        protected virtual Size MeasureBox()
        {
            var form = CreateFormattedText();
            return new Size(form.Width + Padding * 2, form.Height + Padding * 2);
        }

        /// <summary>The body as laid out: never empty (so Ctrl+A, Bksp still measures a line), and
        /// while editing a trailing newline gets a '_' — trailing whitespace is dropped from height
        /// measurements, and the bounds must already include the caret's new line. The '_' is
        /// never drawn: the in-place editor shows the text while Editing.</summary>
        protected string LayoutText
        {
            get
            {
                string txt = Body;
                if (String.IsNullOrEmpty(txt))
                    txt = " ";
                if (Editing && (txt.EndsWith('\r') || txt.EndsWith('\n')))
                    txt += "_";
                return txt;
            }
        }

        protected virtual FormattedText CreateFormattedText()
        {
            var txt = LayoutText;

            // PORT NOTE (Text cache): shaping is the expensive step and Normalize()+Draw both call
            // this per keystroke — cache the FormattedText in RenderCache keyed by the full shaping
            // input (the effective text incl. the editing suffix, the font 5-tuple and the text
            // color). Normalize and Draw thus share the ONE instance, so their measurements are
            // identical by construction. The key guards correctness even for aspects not cleared by
            // the map (e.g. transient Editing toggles, or an ObjectColor change flipping the text
            // color); the Text aspect clear is the fast common path.
            var textColor = TextColor;
            var key = (txt, FontName, FontSize, FontStyle, FontWeight, FontStretch, textColor);
            if (RenderCache.Text is { } cached && key.Equals(RenderCache.TextKey))
                return cached;

            // decision #31: WPF FormattedText(…, Ideal, pixelsPerDip) → Avalonia FormattedText
            var form = new FormattedText(
                txt,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontUtil.CreateSafe(FontName), FontStyle, FontWeight, FontStretch),
                FontSize,
                RenderResources.GetBrush(textColor));
            RenderCache.Text = form;
            RenderCache.TextKey = key;
            return form;
        }
    }
}
