using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    /// <summary>
    /// A dimension line: the inherited line stroked in the object color with round caps, capped with
    /// perpendicular ticks at both ends, plus a capsule label at the midpoint — slightly
    /// translucent white with a faint 1px outline and black text — reading the length and the angle from horizontal.
    /// The length reads in canvas pixels unless the canvas it is on has a unit override
    /// (<see cref="MeasureUnitService"/>), which every measure on that canvas shares. Persists only
    /// <see cref="Scale"/>, the label size multiplier, beyond the line — the endpoints are the rest
    /// of the state, everything drawn is derived from them.
    /// </summary>
    [GraphicDesc("Measure", Skills = Skill.Stroke | Skill.Color | Skill.Scale)]
    public class GraphicMeasure : GraphicLine
    {
        public const double MinScale = 0.5;
        public const double MaxScale = 4;

        // the label is a readout, not ink: it stays legible at any stroke weight, so its font,
        // padding and gap never scale with LineWidth — only with Scale, at which these are the
        // Scale 1 values.
        private const string LabelFontName = EditorFonts.Text;
        private const double LabelFontSize = 18;
        private const double LabelPaddingX = 12;
        private const double LabelPaddingY = 4.5;
        private const double LabelGap = 7.5;

        private static readonly Color LabelBackColor = Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF);
        private static readonly Color LabelBorderColor = Color.FromArgb(0x40, 0, 0, 0);
        private static readonly Color LabelTextColor = Colors.Black;
        private const double LabelBorderWidth = 1;

        protected GraphicMeasure()
        { }

        public GraphicMeasure(Color objectColor, double lineWidth, Point start, Point end, double scale = 1)
            : base(objectColor, lineWidth, start, end)
        {
            Scale = scale;
        }

        /// <summary>Multiplies the label's font size, padding and gap from the line.</summary>
        public double Scale
        {
            get => _scale;
            set
            {
                if (double.IsFinite(value))
                    Set(ref _scale, Math.Clamp(Math.Round(value, 2), MinScale, MaxScale));
            }
        }

        /// <summary>The length of the line in canvas pixels.</summary>
        public double PixelLength => GetDelta().Length;

        private double _scale = 1;

        // the units of the canvas this measure is on (null off-canvas: pixels). Not document
        // state of the graphic — the canvas owns it and re-lays-out every measure when it changes.
        [Transient] private MeasureUnitService _units;

        internal override void OnAttached(DrawingCanvas canvas)
        {
            if (ReferenceEquals(_units, canvas.MeasureUnits))
                return;
            _units = canvas.MeasureUnits;
            RenderCache.Clear(InvalidationAspects.Bounds | InvalidationAspects.Geometry | InvalidationAspects.Text);
        }

        // opt out of the inherited curve handle (3): the ticks, label text and label placement all
        // derive from a straight LineStart→LineEnd chord, so a bowed measure line would read wrong.
        // With the handle hidden, CurveOffset can never leave its 0 default on this type.
        internal override int HandleCount => 2;

        // PORT NOTE (aspect map entry): the label string is derived from the endpoints, so on top of
        // the inherited Bounds|Geometry an endpoint change must also drop the cached
        // FormattedText. Move() stays exempt by construction — a pure translation changes neither
        // length nor angle, so the _translating path's Geometry-only clear keeps the right label.
        internal override void DeclarePropertyEffects(Dictionary<string, InvalidationAspects> map)
        {
            base.DeclarePropertyEffects(map);
            const InvalidationAspects shape = InvalidationAspects.Bounds | InvalidationAspects.Geometry |
                                              InvalidationAspects.Text;
            map[nameof(LineStart)] = shape;
            map[nameof(LineEnd)] = shape;
            map[nameof(Scale)] = shape;
        }

        // PORT NOTE (ComputeBounds): bounds drive invalidation and the export size, so the ticks and
        // the label pill must be inside them — the shaft-only rect from GraphicLine would clip the
        // label and leave ghosts behind a drag. Union of rects rather than a combined geometry (the
        // pill is an unstroked fill and the ticks are an open figure; see GraphicArrow decision #26).
        protected override Rect ComputeBounds()
        {
            var pen = RenderResources.GetPen(default, LineWidth, lineCap: PenLineCap.Round);
            var bounds = GetLineGeometry().GetRenderBounds(pen).Union(GetTickGeometry().GetRenderBounds(pen));
            ComputeLabel(out _, out var pill);
            return bounds.Union(pill);
        }

        internal override void DrawObject(DrawingContext ctx)
        {
            var pen = RenderResources.GetPen(ObjectColor, LineWidth, lineCap: PenLineCap.Round);
            ctx.DrawLine(pen, LineStart, LineEnd);
            ctx.DrawGeometry(null, pen, GetTickGeometry());

            ComputeLabel(out var text, out var pill);
            // the outline is inset half its width so it stays inside the pill (and the bounds)
            var outline = pill.Deflate(LabelBorderWidth / 2);
            var radius = outline.Height / 2; // a capsule
            ctx.DrawRectangle(RenderResources.GetBrush(LabelBackColor), RenderResources.GetPen(LabelBorderColor, LabelBorderWidth),
                              outline, radius, radius);
            ctx.DrawText(text, new Point(pill.X + LabelPaddingX * _scale, pill.Y + LabelPaddingY * _scale));
        }

        /// <summary>Total length of an end tick, perpendicular to the line and centered on the
        /// endpoint. Tracks the stroke weight so a heavy line does not swallow its own caps, and is
        /// clamped so the caps stay readable at 1px and never grow into a cross at 8px.</summary>
        private double TickLength => Math.Clamp(LineWidth * 4, 8, 16);

        // Both ticks live in ONE open StreamGeometry cached in RenderCache.SecondaryGeometry and
        // cleared with the Geometry aspect (RenderCache.Geometry stays reserved for the inherited
        // full-line Contains corridor — the same split GraphicArrow uses for its tip).
        private Geometry GetTickGeometry()
        {
            if (RenderCache.SecondaryGeometry is { } cached)
                return cached;

            var halfTick = GetDirection() * (TickLength / 2);
            var normal = new Vector(-halfTick.Y, halfTick.X);

            var ticks = new StreamGeometry();
            using (var gctx = ticks.Open())
            {
                gctx.BeginFigure(LineStart - normal, false);
                gctx.LineTo(LineStart + normal);
                gctx.EndFigure(false);
                gctx.BeginFigure(LineEnd - normal, false);
                gctx.LineTo(LineEnd + normal);
                gctx.EndFigure(false);
            }

            RenderCache.SecondaryGeometry = ticks;
            return ticks;
        }

        /// <summary>Unit vector along the line; a degenerate (zero-length) line reports horizontal so
        /// the ticks and the label placement stay defined on the very first pointer-down.</summary>
        private Vector GetDirection()
        {
            var v = GetDelta();
            var length = v.Length;
            return length > 0 ? v / length : new Vector(1, 0);
        }

        // Avalonia's Point-Point yields a Point, not a Vector — build the delta explicitly (same
        // idiom as GraphicArrow.ComputeArrowParts).
        private Vector GetDelta() => new Vector(LineEnd.X - LineStart.X, LineEnd.Y - LineStart.Y);

        // Resolves the label text and the pill rect that encloses it. Shared by ComputeBounds and
        // DrawObject so the two can never disagree about where the pill sits; only the FormattedText
        // is cached (the rect is struct math over it).
        private void ComputeLabel(out FormattedText text, out Rect pill)
        {
            var direction = GetDirection();
            var units = _units?.Current;
            text = GetLabelText(units != null
                ? FormatLabel(PixelLength / units.PixelsPerUnit, units.Unit, direction)
                : FormatLabel(PixelLength, null, direction));

            var width = text.Width + (LabelPaddingX * _scale * 2);
            var height = text.Height + (LabelPaddingY * _scale * 2);

            // offset the pill perpendicular to the line, always on the -Y side, so it clears both the
            // stroke and the ticks and lands on the same side no matter which way the line was drawn.
            // A perfectly vertical line has no -Y side, so it breaks the tie on +X for the same reason.
            var normal = new Vector(-direction.Y, direction.X);
            if (normal.Y > 0 || (normal.Y == 0 && normal.X < 0))
                normal = -normal;

            var clearance = Math.Max(LineWidth / 2, TickLength / 2) + (LabelGap * _scale) + (height / 2);
            var center = LineStart + (GetDelta() / 2) + (normal * clearance);
            pill = new Rect(center.X - (width / 2), center.Y - (height / 2), width, height);
        }

        /// <summary>Formats "&lt;length&gt;&lt;unit&gt; &lt;angle&gt;°". Without a unit the length is
        /// whole canvas pixels (graphic coordinate units) — measuring a screenshot means measuring the
        /// image, not the zoomed view; with one it keeps up to two decimals. The angle is measured
        /// from horizontal in (-180, 180], counter-clockwise positive, so the screen-space Y (which
        /// grows downward) is negated.</summary>
        private static string FormatLabel(double length, string unit, Vector direction)
        {
            var degrees = Math.Round(Math.Atan2(-direction.Y, direction.X) * 180 / Math.PI);
            if (degrees == 0) degrees = 0; // collapse -0 so a flat line never reads "-0°"
            if (degrees == -180) degrees = 180; // a right-to-left drag reads 180°, not -180°
            if (unit == null)
                return string.Format(CultureInfo.InvariantCulture, "{0:0}px {1:0}°", Math.Round(length), degrees);
            return string.Format(CultureInfo.InvariantCulture, "{0}{1} {2:0}°", MeasureUnits.FormatLength(length), unit, degrees);
        }

        // The FormattedText (RenderCache.Text) is keyed on the label string and the scale — the
        // font 5-tuple and colors are constant for this type, so those are the whole shaping input.
        // ComputeBounds fills this slot too, which is a permitted sidecar write; it must never raise
        // PropertyChanged.
        private FormattedText GetLabelText(string label)
        {
            var key = (label, _scale);
            if (RenderCache.Text is { } cached && key.Equals(RenderCache.TextKey))
                return cached;

            var form = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontUtil.CreateSafe(LabelFontName), FontStyle.Normal, FontWeight.SemiBold),
                LabelFontSize * _scale,
                RenderResources.GetBrush(LabelTextColor));

            RenderCache.Text = form;
            RenderCache.TextKey = key;
            return form;
        }
    }
}
