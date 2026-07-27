using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    /// <summary>
    /// A dimension line: the inherited line stroked in the object color, capped with perpendicular
    /// ticks at both ends, plus a fixed-size label pill at the midpoint reading the length in canvas
    /// pixels and the angle from horizontal. Adds no persisted field — the endpoints ARE the state,
    /// everything drawn is derived from them.
    /// </summary>
    [GraphicDesc("Measure", Skills = Skill.Stroke | Skill.Color)]
    public class GraphicMeasure : GraphicLine
    {
        // the label is a readout, not ink: it stays legible at any stroke weight, so its font,
        // padding and corner radius are constants and never scale with LineWidth.
        private const string LabelFontName = "Segoe UI";
        private const double LabelFontSize = 11;
        private const double LabelPaddingX = 5;
        private const double LabelPaddingY = 2;
        private const double LabelCornerRadius = 4;
        private const double LabelGap = 4;

        private static readonly Color LabelBackColor = Color.FromArgb(0xCC, 0, 0, 0);
        private static readonly Color LabelTextColor = Colors.White;

        protected GraphicMeasure()
        { }

        public GraphicMeasure(Color objectColor, double lineWidth, Point start, Point end)
            : base(objectColor, lineWidth, start, end)
        { }

        // opt out of the inherited curve handle (3): the ticks, label text and label placement all
        // derive from a straight LineStart→LineEnd chord, so a bowed measure line would read wrong.
        // With the handle hidden, CurveOffset can never leave its 0 default on this type.
        internal override int HandleCount => 2;

        // PORT NOTE (aspect map entry): the label string is derived from the endpoints, so on top of
        // the inherited Bounds|Geometry|Shadow an endpoint change must also drop the cached
        // FormattedText. Move() stays exempt by construction — a pure translation changes neither
        // length nor angle, so the _translating path's Geometry-only clear keeps the right label.
        internal override void DeclarePropertyEffects(Dictionary<string, InvalidationAspects> map)
        {
            base.DeclarePropertyEffects(map);
            const InvalidationAspects shape = InvalidationAspects.Bounds | InvalidationAspects.Geometry |
                                              InvalidationAspects.Shadow | InvalidationAspects.Text;
            map[nameof(LineStart)] = shape;
            map[nameof(LineEnd)] = shape;
        }

        // PORT NOTE (ComputeBounds): bounds drive invalidation and the export size, so the ticks and
        // the label pill must be inside them — the shaft-only rect from GraphicLine would clip the
        // label and leave ghosts behind a drag. Union of rects rather than a combined geometry (the
        // pill is an unstroked fill and the ticks are an open figure; see GraphicArrow decision #26).
        protected override Rect ComputeBounds()
        {
            var pen = RenderResources.GetPen(default, LineWidth);
            var bounds = GetLineGeometry().GetRenderBounds(pen).Union(GetTickGeometry().GetRenderBounds(pen));
            ComputeLabel(out _, out var pill);
            return bounds.Union(pill);
        }

        internal override void DrawObject(DrawingContext ctx)
        {
            var pen = RenderResources.GetPen(ObjectColor, LineWidth);
            ctx.DrawLine(pen, LineStart, LineEnd);
            ctx.DrawGeometry(null, pen, GetTickGeometry());

            ComputeLabel(out var text, out var pill);
            ctx.DrawRectangle(RenderResources.GetBrush(LabelBackColor), null, pill, LabelCornerRadius, LabelCornerRadius);
            ctx.DrawText(text, new Point(pill.X + LabelPaddingX, pill.Y + LabelPaddingY));
        }

        /// <summary>Total length of an end tick, perpendicular to the line and centred on the
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
            text = GetLabelText(FormatLabel(GetDelta().Length, direction));

            var width = text.Width + (LabelPaddingX * 2);
            var height = text.Height + (LabelPaddingY * 2);

            // offset the pill perpendicular to the line, always on the -Y side, so it clears both the
            // stroke and the ticks and lands on the same side no matter which way the line was drawn.
            // A perfectly vertical line has no -Y side, so it breaks the tie on +X for the same reason.
            var normal = new Vector(-direction.Y, direction.X);
            if (normal.Y > 0 || (normal.Y == 0 && normal.X < 0))
                normal = -normal;

            var clearance = Math.Max(LineWidth / 2, TickLength / 2) + LabelGap + (height / 2);
            var center = LineStart + (GetDelta() / 2) + (normal * clearance);
            pill = new Rect(center.X - (width / 2), center.Y - (height / 2), width, height);
        }

        /// <summary>Formats "&lt;length&gt;px &lt;angle&gt;°". Length is in canvas pixels (graphic
        /// coordinate units) — measuring a screenshot means measuring the image, not the zoomed view.
        /// The angle is measured from horizontal in (-180, 180], counter-clockwise positive, so the
        /// screen-space Y (which grows downward) is negated.</summary>
        private static string FormatLabel(double length, Vector direction)
        {
            var degrees = Math.Round(Math.Atan2(-direction.Y, direction.X) * 180 / Math.PI);
            if (degrees == 0) degrees = 0; // collapse -0 so a flat line never reads "-0°"
            if (degrees == -180) degrees = 180; // a right-to-left drag reads 180°, not -180°
            return string.Format(CultureInfo.InvariantCulture, "{0:0}px {1:0}°", Math.Round(length), degrees);
        }

        // The FormattedText (RenderCache.Text) is keyed on the label string alone — the font 5-tuple
        // is constant for this type, so the string is the whole shaping input. ComputeBounds fills
        // this slot too, which is a permitted sidecar write; it must never raise PropertyChanged.
        private FormattedText GetLabelText(string label)
        {
            if (RenderCache.Text is { } cached && string.Equals(label, RenderCache.TextKey as string, StringComparison.Ordinal))
                return cached;

            var form = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily(LabelFontName)),
                LabelFontSize,
                RenderResources.GetBrush(LabelTextColor));

            RenderCache.Text = form;
            RenderCache.TextKey = label;
            return form;
        }
    }
}
