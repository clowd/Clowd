using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    /// <summary>
    /// A square sticky note that lays itself out: the body is centered and wrapped, drawn as large
    /// as fits (up to a cap), and only once it has shrunk to the smallest comfortable size does the
    /// note grow to make room. The note is never smaller than its default square and never
    /// anything but square, so it has no resize handles; <see cref="Scale"/> multiplies every
    /// layout constant (the default square, the inset and both font size limits) at once.
    ///
    /// The paper color cycles per note, and the text is a deep shade of the paper's own hue. Each
    /// note picks one of <see cref="LiftStyleCount"/> corner looks at random (never the previous
    /// note's): mostly lifted corners — the paper edge pulls in a touch, darkens toward the curl
    /// and casts a longer contact shadow below — plus a strong curl and a folded-over dog-ear
    /// whose flap shades the card under it. That shading is drawn by the note itself; the usual
    /// drop shadow is cast by the paper's outline alone (see <see cref="DrawShadowSilhouette"/>),
    /// never by the text, so it stays put while typing.
    ///
    /// Cache slots: Geometry = paper outline, SecondaryGeometry = dog-ear flap. Text = the fitted body.
    /// </summary>
    [GraphicDesc("Sticky Note", Skills = Skill.FontFamily | Skill.Scale | Skill.Angle)]
    public class GraphicStickyNote : GraphicText
    {
        public const double MinScale = 0.5;
        public const double MaxScale = 4;

        /// <summary>Number of corner looks; new notes pick one at random.</summary>
        public const int LiftStyleCount = 7;

        // layout at Scale 1, in canvas units. A note is at least MinSide square with the text
        // Inset from the paper edge; the text is drawn as large as MaxFontSize while it fits and
        // shrinks to MinFontSize before the note grows (by GrowStep at a time) instead.
        private const double MinSide = 180;
        private const double Inset = 18;
        private const double MaxFontSize = 30;
        private const double MinFontSize = 15;
        private const double GrowStep = 1.08;

        // font sizes are fitted to this granularity, so a keystroke rarely nudges the size
        private const double FontSizeStep = 0.5;

        /// <summary>Notes are lettered bolder than plain text — they are read at a glance.</summary>
        public static readonly FontWeight NoteWeight = FontWeight.SemiBold;

        // paper colors, cycled per note: butter, rose, mint, sky
        private static readonly Color[] _papers =
        {
            Color.FromRgb(0xFF, 0xF1, 0xB8), Color.FromRgb(0xFB, 0xD5, 0xE2), Color.FromRgb(0xCD, 0xEF, 0xDF), Color.FromRgb(0xD3, 0xE6, 0xFD),
        };

        private static int _nextPaper;

        private static int _lastLook = -1;

        // per lift style: how far the bottom-left and bottom-right corners lift (0 flat, 1 slight,
        // 2 pronounced, 3 curled), or whether the bottom-right corner is folded over instead
        private readonly record struct Look(int LiftLeft, int LiftRight, bool DogEar = false);

        private static readonly Look[] _looks =
        {
            new(0, 1), new(1, 0), new(0, 2), new(2, 0), new(1, 1), new(0, 0, DogEar: true), new(3, 0),
        };

        // per lift level 1-3: corner rise and how much further than that the contact shadow
        // spreads (× side), its blur (× reach, see DrawContactShadow), alpha, and tilt in degrees
        private static readonly (double Rise, double Spread, double Blur, byte Alpha, double Tilt)[] _liftLevels =
        {
            (0.02, 0.006, 0.045, 0x48, 3), (0.04, 0.01, 0.05, 0x4C, 4), (0.065, 0.012, 0.055, 0x50, 5),
        };

        // the dog-ear folds this much of the side over (each way from the corner)
        private const double DogEarSize = 0.14;

        // the glue strip along the top edge reads a touch darker, the free end a touch lighter
        private static readonly IBrush PaperShade = new ImmutableLinearGradientBrush(
            new[]
            {
                new ImmutableGradientStop(0, Color.FromArgb(0x10, 0, 0, 0)),
                new ImmutableGradientStop(0.14, Color.FromArgb(0x00, 0, 0, 0)),
                new ImmutableGradientStop(0.55, Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)),
                new ImmutableGradientStop(1, Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            },
            startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
            endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));

        // a lifted corner curls away from the light: [corner (0 left, 1 right), lift - 1]
        private static readonly IBrush[,] CornerShade =
        {
            { CreateCornerShade(0, 0x16), CreateCornerShade(0, 0x26), CreateCornerShade(0, 0x3C) },
            { CreateCornerShade(1, 0x16), CreateCornerShade(1, 0x26), CreateCornerShade(1, 0x3C) },
        };

        // the back of the dog-ear's flap catches the light: a sheen brightest at the tip (its
        // bounds' top-left), fading toward the crease (the bounds' center), with just a touch of
        // shade in the bend itself
        private static readonly IBrush FlapShade = new ImmutableLinearGradientBrush(
            new[]
            {
                new ImmutableGradientStop(0, Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
                new ImmutableGradientStop(0.8, Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                new ImmutableGradientStop(1, Color.FromArgb(0x14, 0, 0, 0)),
            },
            startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
            endPoint: new RelativePoint(0.5, 0.5, RelativeUnit.Relative));

        // the dog-ear's shadow on the card: a band parallel to the crease, clear until a little
        // short of the flap and darkening into the folded corner (relative to the paper's bounds)
        private static readonly IBrush FoldShadow = new ImmutableLinearGradientBrush(
            new[]
            {
                new ImmutableGradientStop(0, Color.FromArgb(0x00, 0, 0, 0)),
                new ImmutableGradientStop(1, Color.FromArgb(0x48, 0, 0, 0)),
            },
            startPoint: new RelativePoint(0.78, 0.78, RelativeUnit.Relative),
            endPoint: new RelativePoint(1, 1, RelativeUnit.Relative));

        public double Scale
        {
            get => _scale;
            set
            {
                if (double.IsFinite(value))
                    SetAndNormalize(ref _scale, Math.Clamp(Math.Round(value, 2), MinScale, MaxScale));
            }
        }

        public int LiftStyle
        {
            get => _liftStyle;
            set => SetAndNormalize(ref _liftStyle, Math.Clamp(value, 0, LiftStyleCount - 1)); // a dog-ear moves the text
        }

        private double _scale = 1;
        private int _liftStyle;

        // the last fit, keyed by everything it depends on (rebuilt on demand after a load)
        [Transient] private object _fitKey;
        [Transient] private double _fitFontSize;
        [Transient] private double _fitSide;

        protected GraphicStickyNote()
        { }

        public GraphicStickyNote(DrawingCanvas canvas, Point center)
            : this(_papers[_nextPaper], center, canvas.ObjectScale, NextLook(), canvas.TextFontFamilyName)
        {
            _nextPaper = (_nextPaper + 1) % _papers.Length;

            // stuck on by hand, never quite straight
            Angle = Math.Round((Random.Shared.NextDouble() * 2 - 1) * MaxPlacementTilt, 1);
        }

        /// <summary>New notes are placed tilted by up to this many degrees either way.</summary>
        public const double MaxPlacementTilt = 1.8;

        /// <summary>A random look, never the one the previous note got.</summary>
        internal static int NextLook()
        {
            var look = Random.Shared.Next(_lastLook < 0 ? LiftStyleCount : LiftStyleCount - 1);
            if (_lastLook >= 0 && look >= _lastLook)
                look++;
            return _lastLook = look;
        }

        public GraphicStickyNote(Color paper, Point center, double scale = 1, int liftStyle = 0, string fontName = null, string body = null)
            : base(paper, 0, center, 0, body)
        {
            FontName = fontName ?? EditorFonts.Text;
            FontWeight = NoteWeight;
            Scale = scale;
            LiftStyle = liftStyle;
            CenterOn(center);
        }

        /// <summary>Moves the note so its center lies on <paramref name="center"/>.</summary>
        internal void CenterOn(Point center)
        {
            Move(center.X - (Left + Right) / 2, center.Y - (Top + Bottom) / 2);
        }

        internal override void DeclarePropertyEffects(Dictionary<string, InvalidationAspects> map)
        {
            base.DeclarePropertyEffects(map);
            map[nameof(Scale)] = InvalidationAspects.Bounds | InvalidationAspects.Geometry | InvalidationAspects.Shadow | InvalidationAspects.Text;
            map[nameof(LiftStyle)] = InvalidationAspects.Bounds | InvalidationAspects.Geometry | InvalidationAspects.Shadow | InvalidationAspects.Text;
        }

        internal override Color TextColor => GetInk(ObjectColor);

        // text on a note must read at least this well (WCAG AAA for body text)
        internal const double InkContrast = 7;

        /// <summary>
        /// The ink for a paper: a deep shade of the paper's own hue (dark gold on butter, berry on
        /// rose, ...), darkened until it reaches <see cref="InkContrast"/> against the paper. A dark
        /// paper gets a pale tint of its hue instead; a gray one, a neutral.
        /// </summary>
        internal static Color GetInk(Color paper)
        {
            var (h, s, l) = ToHsl(paper);
            var darkInk = RenderResources.GetLuminance(paper) > 0.18;
            s = Math.Min(s, 0.75);

            var ink = darkInk ? Colors.Black : Colors.White;
            for (double li = darkInk ? 0.32 : 0.86; li >= 0 && li <= 1; li += darkInk ? -0.02 : 0.02)
            {
                var candidate = FromHsl(h, s, li);
                if (RenderResources.GetContrastRatio(candidate, paper) >= InkContrast)
                {
                    ink = candidate;
                    break;
                }
            }

            return ink;
        }

        private static (double H, double S, double L) ToHsl(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double l = (max + min) / 2, d = max - min;
            if (d == 0)
                return (0, 0, l);

            var s = d / (1 - Math.Abs(2 * l - 1));
            var h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            return ((h * 60 + 360) % 360, s, l);
        }

        private static Color FromHsl(double h, double s, double l)
        {
            var c = (1 - Math.Abs(2 * l - 1)) * s;
            var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            var m = l - c / 2;
            var (r, g, b) = h switch
            {
                < 60 => (c, x, 0d),
                < 120 => (x, c, 0d),
                < 180 => (0d, c, x),
                < 240 => (0d, x, c),
                < 300 => (x, 0d, c),
                _ => (c, 0d, x),
            };

            static byte Channel(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
            return Color.FromRgb(Channel(r + m), Channel(g + m), Channel(b + m));
        }

        // the drop shadow is the paper's, which typing never changes
        internal override bool ShadowIncludesText => false;

        internal override bool HasShadowSurface => true;

        /// <summary>The font size the body is currently fitted at.</summary>
        internal double FittedFontSize => Fit().FontSize;

        /// <summary>Where the body is laid out, unrotated, in canvas space — the in-place editor
        /// sits here (it wraps at this width and centers each line, as the drawn text does).</summary>
        internal Rect TextRect
        {
            get
            {
                var form = CreateFormattedText();
                var inset = Inset * _scale;
                var side = Right - Left;
                var area = side - inset - BottomInset(side);
                return new Rect(Left + inset, Top + inset + (area - form.Height) / 2, side - inset * 2, form.Height);
            }
        }

        protected override Size MeasureBox()
        {
            var side = Fit().Side;
            return new Size(side, side);
        }

        protected override FormattedText CreateFormattedText()
        {
            var (fontSize, side) = Fit();
            var txt = LayoutText;
            var width = side - Inset * _scale * 2;
            var color = TextColor;

            // keyed like GraphicText's, plus the fitted size and wrap width
            var key = (txt, FontName, fontSize, width, FontStyle, FontWeight, FontStretch, color);
            if (RenderCache.Text is { } cached && key.Equals(RenderCache.TextKey))
                return cached;

            var form = Layout(txt, fontSize, width, RenderResources.GetBrush(color));
            RenderCache.Text = form;
            RenderCache.TextKey = key;
            return form;
        }

        /// <summary>Inset of the text from the bottom edge: the usual inset, or with a dog-ear,
        /// enough to keep the text above the fold.</summary>
        private double BottomInset(double side)
        {
            var inset = Inset * _scale;
            return _looks[_liftStyle].DogEar ? Math.Max(inset, side * DogEarSize + inset * 0.3) : inset;
        }

        private Typeface NoteTypeface => new Typeface(FontUtil.CreateSafe(FontName), FontStyle, FontWeight, FontStretch);

        private FormattedText Layout(string txt, double fontSize, double width, IBrush brush)
        {
            return new FormattedText(txt, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, NoteTypeface, fontSize, brush)
            {
                MaxTextWidth = width,
                TextAlignment = TextAlignment.Center,
                // FormattedText defaults to WPF's word-ellipsis trimming, which elides a word wider
                // than the note; break it across lines instead, as the editor does
                Trimming = TextTrimming.None,
            };
        }

        /// <summary>
        /// The note size and font size for the current body. The note stays at its minimum square
        /// while the text fits there at the smallest font size, growing a step at a time
        /// otherwise; then the font size is the largest that fits, preferring one at which no word
        /// has to break across lines (a word too long even at the smallest size is broken).
        /// Height and word widths only grow with the font size, so the largest fit is found by
        /// bisection.
        /// </summary>
        private (double FontSize, double Side) Fit()
        {
            var txt = LayoutText;
            var key = (txt, FontName, FontStyle, FontWeight, FontStretch, _scale, _liftStyle);
            if (key.Equals(_fitKey))
                return (_fitFontSize, _fitSide);

            var inset = Inset * _scale;
            var minFont = MinFontSize * _scale;
            var steps = (int)Math.Round((MaxFontSize - MinFontSize) * _scale / FontSizeStep);

            bool HeightFits(double fontSize, double side)
            {
                return Layout(txt, fontSize, side - inset * 2, null).Height <= side - inset - BottomInset(side);
            }

            // the widest word, per unit of font size (glyph advances scale linearly)
            var words = String.Join('\n', txt.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            var wordWidthPerSize = words.Length == 0 ? 0 : Layout(words, 100, double.PositiveInfinity, null).Width / 100;

            var side = MinSide * _scale;
            for (int i = 0; i < 64 && !HeightFits(minFont, side); i++)
                side = Math.Ceiling(side * GrowStep);

            var innerWidth = side - inset * 2;
            bool Fits(int step)
            {
                var fontSize = minFont + step * FontSizeStep;
                return wordWidthPerSize * fontSize <= innerWidth && HeightFits(fontSize, side);
            }

            // bisect for the largest step that fits; step 0 is the floor even if it does not
            int lo = 0, hi = steps;
            if (Fits(hi))
            {
                lo = hi;
            }
            else
            {
                while (hi - lo > 1)
                {
                    var mid = (lo + hi) / 2;
                    if (Fits(mid))
                        lo = mid;
                    else
                        hi = mid;
                }
            }

            _fitKey = key;
            _fitFontSize = minFont + lo * FontSizeStep;
            _fitSide = side;
            return (_fitFontSize, _fitSide);
        }

        protected override void DrawObjectImpl(DrawingContext context, bool showText)
        {
            // NOTE: the rotation is pushed by the callers (GraphicText.Draw/DrawObject)
            var paper = new Rect(Left, Top, Right - Left, Bottom - Top);
            var look = _looks[_liftStyle];
            DrawAmbientShadow(context, paper);
            DrawContactShadow(context, paper, look.LiftLeft, false);
            DrawContactShadow(context, paper, look.LiftRight, true);

            var outline = GetPaperGeometry();
            var paperBrush = RenderResources.GetBrush(ObjectColor);
            context.DrawGeometry(paperBrush, null, outline);
            context.DrawGeometry(PaperTexture, null, outline);
            context.DrawGeometry(PaperShade, null, outline);
            if (look.LiftLeft > 0)
                context.DrawGeometry(CornerShade[0, look.LiftLeft - 1], null, outline);
            if (look.LiftRight > 0)
                context.DrawGeometry(CornerShade[1, look.LiftRight - 1], null, outline);

            if (showText)
            {
                // the text leans with a lifted corner (the in-place editor stays flat)
                var text = TextRect;
                using (context.PushTransform(GetTextLean(text, look)))
                    context.DrawText(CreateFormattedText(), text.TopLeft);
            }

            if (look.DogEar)
                DrawDogEar(context, outline, paperBrush);
        }

        /// <summary>
        /// A slight perspective on the text toward the lifted corner(s): the text block's bottom
        /// corner on a lifted side moves up and in by a share of that corner's rise, its other
        /// corners stay put, so the lines lean as if printed on the curling paper.
        /// </summary>
        private Matrix GetTextLean(Rect text, Look look)
        {
            if (look.LiftLeft == 0 && look.LiftRight == 0)
                return Matrix.Identity;

            var side = Right - Left;
            Vector Lean(int lift, double inward)
            {
                if (lift == 0)
                    return default;
                var rise = _liftLevels[lift - 1].Rise * side;
                return new Vector(inward * rise * 0.3, -rise * 0.6);
            }

            // corners in order: top-left, top-right, bottom-right, bottom-left
            var br = text.BottomRight + Lean(look.LiftRight, -1);
            var bl = text.BottomLeft + Lean(look.LiftLeft, 1);
            var toUnit = Matrix.CreateTranslation(-text.Left, -text.Top) * Matrix.CreateScale(1 / text.Width, 1 / text.Height);
            return toUnit * SquareToQuad(text.TopLeft, text.TopRight, br, bl);
        }

        /// <summary>The projective transform taking the unit square's corners (0,0), (1,0), (1,1),
        /// (0,1) to <paramref name="p0"/>..<paramref name="p3"/> (Heckbert's square-to-quad).</summary>
        private static Matrix SquareToQuad(Point p0, Point p1, Point p2, Point p3)
        {
            double dx1 = p1.X - p2.X, dx2 = p3.X - p2.X, dx3 = p0.X - p1.X + p2.X - p3.X;
            double dy1 = p1.Y - p2.Y, dy2 = p3.Y - p2.Y, dy3 = p0.Y - p1.Y + p2.Y - p3.Y;
            var det = dx1 * dy2 - dx2 * dy1;
            var g = (dx3 * dy2 - dx2 * dy3) / det;
            var h = (dx1 * dy3 - dx3 * dy1) / det;

            // row-vector layout: x' = (a·u + b·v + c) / w, y' = (d·u + e·v + f) / w, w = g·u + h·v + 1
            return new Matrix(
                p1.X - p0.X + g * p1.X, p1.Y - p0.Y + g * p1.Y, g,
                p3.X - p0.X + h * p3.X, p3.Y - p0.Y + h * p3.Y, h,
                p0.X, p0.Y, 1);
        }

        /// <summary>
        /// The folded-over corner: first the flap's shadow on the card — a gradient across the
        /// whole paper, parallel to the crease, darkening into the corner — then the flap itself,
        /// the paper's back, with a sheen that is brightest at its tip.
        /// </summary>
        private void DrawDogEar(DrawingContext ctx, Geometry outline, IBrush paperBrush)
        {
            var flap = GetFlapGeometry();
            ctx.DrawGeometry(FoldShadow, null, outline);
            ctx.DrawGeometry(paperBrush, null, flap);
            ctx.DrawGeometry(PaperTexture, null, flap);
            ctx.DrawGeometry(FlapShade, null, flap);
        }

        internal override void DrawHoverOutline(DrawingContext ctx, IPen pen)
        {
            using (ctx.PushTransform(MatrixHelper.Rotation(Angle, CenterOfRotation)))
                ctx.DrawGeometry(null, pen, GetPaperGeometry());
        }

        internal override void DrawShadowSilhouette(DrawingContext ctx)
        {
            using (ctx.PushTransform(MatrixHelper.Rotation(Angle, CenterOfRotation)))
            {
                ctx.DrawGeometry(Brushes.Black, null, GetPaperGeometry());
                if (_looks[_liftStyle].DogEar)
                    ctx.DrawGeometry(Brushes.Black, null, GetFlapGeometry());
            }
        }

        /// <summary>
        /// The paper outline: square, except that a lifted bottom corner sits a little higher and
        /// further in (it is curling toward the viewer), the bottom edge easing back down to full
        /// height between the corners — and a dog-eared corner is cut off along the crease.
        /// </summary>
        private Geometry GetPaperGeometry()
        {
            if (RenderCache.Geometry is { } cached)
                return cached;

            var look = _looks[_liftStyle];
            double l = Left, t = Top, r = Right, b = Bottom;
            var side = r - l;
            var riseLeft = look.LiftLeft > 0 ? _liftLevels[look.LiftLeft - 1].Rise * side : 0;
            var riseRight = look.LiftRight > 0 ? _liftLevels[look.LiftRight - 1].Rise * side : 0;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(l, t), true);
                ctx.LineTo(new Point(r, t));
                if (look.DogEar)
                {
                    var fold = side * DogEarSize;
                    ctx.LineTo(new Point(r, b - fold));
                    ctx.LineTo(new Point(r - fold, b));
                    ctx.LineTo(new Point(l, b));
                }
                else
                {
                    ctx.LineTo(new Point(r - riseRight * 0.5, b - riseRight));
                    ctx.CubicBezierTo(new Point(r - side * 0.35, b), new Point(l + side * 0.35, b), new Point(l + riseLeft * 0.5, b - riseLeft));
                }

                ctx.EndFigure(true);
            }

            RenderCache.Geometry = geometry;
            return geometry;
        }

        /// <summary>
        /// The dog-ear's flap: the cut-off corner mirrored over the crease, its tip pulled back a
        /// little toward the crease and its edges bowed, as folded paper never lies quite flat.
        /// </summary>
        private Geometry GetFlapGeometry()
        {
            if (RenderCache.SecondaryGeometry is { } cached)
                return cached;

            double r = Right, b = Bottom;
            var fold = (r - Left) * DogEarSize;
            var start = new Point(r, b - fold);
            var end = new Point(r - fold, b);
            var crease = new Point(r - fold / 2, b - fold / 2);
            var tip = crease + (new Point(r - fold, b - fold) - crease) * 0.9;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(start, true);
                ctx.QuadraticBezierTo(new Point((start.X + tip.X) / 2 + fold * 0.04, (start.Y + tip.Y) / 2 + fold * 0.06), tip);
                ctx.QuadraticBezierTo(new Point((end.X + tip.X) / 2 + fold * 0.06, (end.Y + tip.Y) / 2 + fold * 0.04), end);
                ctx.EndFigure(true);
            }

            RenderCache.SecondaryGeometry = geometry;
            return geometry;
        }

        /// <summary>
        /// The soft shadow a lifted corner casts onto the page beneath it: a blurred box tucked
        /// under the paper, tilted so its low end sits just inside the lifted corner. Its spread
        /// fills the gap the corner left as it rose, and only the blur reaches past where the
        /// flat edge would be, so the shadow hugs the note and fades out toward the middle of the
        /// bottom edge.
        ///
        /// No box shadow offset: Avalonia applies it after the whole transform (the note's
        /// rotation and the canvas zoom alike), so on a rotated note it would point the wrong
        /// way. Spread and blur follow the transform.
        /// </summary>
        private void DrawContactShadow(DrawingContext ctx, Rect paper, int lift, bool right)
        {
            if (lift == 0)
                return;

            var side = paper.Width;
            var level = _liftLevels[lift - 1];

            // the blur follows the note up to a point, so a big note's corner does not look
            // lifted off the page by inches
            var reach = Math.Min(side, MinSide * _scale * 1.5);
            var rise = level.Rise * side;

            // the tilted box's low corner stays inside the paper, clear of the raised edge, and in
            // from the side by its spread and blur, so the shadow only shows below the note
            var spread = rise + level.Spread * side;
            var blur = reach * level.Blur;
            var box = new Size(side * 0.5, side * 0.2);
            var tilt = level.Tilt * Math.PI / 180;
            var drop = Math.Sin(tilt) * box.Width / 2 + (1 - Math.Cos(tilt)) * box.Height / 2;
            var sideInset = spread + blur + side * 0.02;
            var x = right ? paper.Right - sideInset - box.Width : paper.Left + sideInset;
            var y = paper.Bottom - rise - side * 0.01 - drop - box.Height;
            var caster = new Rect(new Point(x, y), box);

            var shadow = new BoxShadow
            {
                Spread = spread,
                Blur = blur,
                Color = Color.FromArgb(level.Alpha, 0, 0, 0),
            };

            // clockwise drops the right end, so a right corner tilts clockwise and a left one back
            using (ctx.PushTransform(MatrixHelper.Rotation(level.Tilt * (right ? 1 : -1), caster.Center)))
            {
                // the box itself is hidden under the paper (a fill is needed for anything to draw)
                ctx.DrawRectangle(RenderResources.GetBrush(Color.FromArgb(1, 0, 0, 0)), null, new RoundedRect(caster), new BoxShadows(shadow));
            }
        }

        /// <summary>
        /// The soft, wide shadow of paper resting on a surface, under the tight drop shadow every
        /// graphic gets. Lit from above: the casting box sits low in the paper, so the shadow
        /// shows below and a little to the sides but never above. (No offset — see
        /// <see cref="DrawContactShadow"/>.)
        /// </summary>
        private void DrawAmbientShadow(DrawingContext ctx, Rect paper)
        {
            var side = paper.Width;
            var reach = Math.Min(side, MinSide * _scale * 1.5);

            // inside the paper everywhere, even under a lifted corner or the dog-ear's cut
            var caster = new Rect(paper.Left + side * 0.08, paper.Top + side * 0.25, side * 0.84, side * 0.67);
            var shadow = new BoxShadow
            {
                Spread = side * 0.06,
                Blur = reach * 0.12,
                Color = Color.FromArgb(0x30, 0, 0, 0),
            };

            ctx.DrawRectangle(RenderResources.GetBrush(Color.FromArgb(1, 0, 0, 0)), null, new RoundedRect(caster), new BoxShadows(shadow));
        }

        // paper grain: a faint tiling noise of lighter and darker specks over the paper (and the
        // dog-ear's flap), so it is not flat digital color. Two texels per canvas unit, so it
        // stays fine when zoomed in.
        private const int GrainTexels = 256;
        private const double GrainTile = 128;
        private const byte GrainAlpha = 0x0A;

        private static IBrush _paperTexture;

        private static IBrush PaperTexture => _paperTexture ??= CreatePaperTexture();

        private static IBrush CreatePaperTexture()
        {
            // tileable value noise (8-texel cells, wrapping) blended with per-texel noise
            const int cells = GrainTexels / 8;
            var rnd = new Random(1234);
            var lattice = new double[cells, cells];
            for (int y = 0; y < cells; y++)
                for (int x = 0; x < cells; x++)
                    lattice[x, y] = rnd.NextDouble() * 2 - 1;

            var pixels = new byte[GrainTexels * GrainTexels * 4];
            for (int y = 0; y < GrainTexels; y++)
            {
                for (int x = 0; x < GrainTexels; x++)
                {
                    double fx = x / 8.0, fy = y / 8.0;
                    int x0 = (int)fx, y0 = (int)fy;
                    double tx = fx - x0, ty = fy - y0;
                    tx = tx * tx * (3 - 2 * tx);
                    ty = ty * ty * (3 - 2 * ty);
                    double v00 = lattice[x0 % cells, y0 % cells], v10 = lattice[(x0 + 1) % cells, y0 % cells];
                    double v01 = lattice[x0 % cells, (y0 + 1) % cells], v11 = lattice[(x0 + 1) % cells, (y0 + 1) % cells];
                    var smooth = (v00 * (1 - tx) + v10 * tx) * (1 - ty) + (v01 * (1 - tx) + v11 * tx) * ty;
                    var v = Math.Clamp(smooth * 0.35 + (rnd.NextDouble() * 2 - 1) * 0.65, -1, 1);

                    // premultiplied BGRA: white specks lighten, black specks darken
                    var a = (byte)Math.Round(Math.Abs(v) * GrainAlpha);
                    var c = v > 0 ? a : (byte)0;
                    var i = (y * GrainTexels + x) * 4;
                    pixels[i] = c;
                    pixels[i + 1] = c;
                    pixels[i + 2] = c;
                    pixels[i + 3] = a;
                }
            }

            var bitmap = new WriteableBitmap(new PixelSize(GrainTexels, GrainTexels), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var buffer = bitmap.Lock())
            {
                for (int y = 0; y < GrainTexels; y++)
                    System.Runtime.InteropServices.Marshal.Copy(pixels, y * GrainTexels * 4, buffer.Address + y * buffer.RowBytes, GrainTexels * 4);
            }

            return new ImageBrush(bitmap)
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.Fill,
                DestinationRect = new RelativeRect(0, 0, GrainTile, GrainTile, RelativeUnit.Absolute),
            }.ToImmutable();
        }

        private static IBrush CreateCornerShade(double cornerX, byte alpha)
        {
            // runs from the opposite top corner (clear) to the lifted bottom corner (shaded)
            return new ImmutableLinearGradientBrush(
                new[]
                {
                    new ImmutableGradientStop(0.6, Color.FromArgb(0, 0, 0, 0)),
                    new ImmutableGradientStop(1, Color.FromArgb(alpha, 0, 0, 0)),
                },
                startPoint: new RelativePoint(1 - cornerX, 0, RelativeUnit.Relative),
                endPoint: new RelativePoint(cornerX, 1, RelativeUnit.Relative));
        }
    }
}
