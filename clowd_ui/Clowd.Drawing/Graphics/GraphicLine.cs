using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Line", Skills = Skill.Stroke | Skill.Color | Skill.DashStyle)]
    public class GraphicLine : GraphicBase
    {
        public Point LineStart
        {
            get => _lineStart;
            set => Set(ref _lineStart, value);
        }

        public Point LineEnd
        {
            get => _lineEnd;
            set => Set(ref _lineEnd, value);
        }

        /// <summary>
        /// How far the line bows away from the straight LineStart→LineEnd chord, in canvas units,
        /// measured at the middle of the chord along its left-hand normal. 0 — the field default,
        /// and therefore what an absent JSON property deserializes to — is a straight line, so
        /// sessions written before curved lines existed load unchanged.
        ///
        /// Deliberately a scalar offset relative to the chord rather than an absolute control
        /// point: the bow then survives Move and endpoint drags without any extra bookkeeping (a
        /// translation moves the chord and the offset still describes the same shape, so the
        /// Move fast path stays valid as written).
        /// </summary>
        public double CurveOffset
        {
            get => _curveOffset;
            set => Set(ref _curveOffset, value);
        }

        /// <summary>
        /// How far the bow's midpoint (the mid handle) slides along the chord away from the chord
        /// midpoint, as a fraction of the chord length (positive toward LineEnd), so the bend can
        /// lean toward one end instead of always being symmetric. Clamped to ±<see cref="MaxCurveSkew"/>
        /// by the handle drag; 0 — the default, and what older sessions deserialize to — is the
        /// symmetric bow. Stored relative to the chord length for the same reason CurveOffset is
        /// chord-relative: it survives Move and endpoint drags unchanged. Ignored while straight.
        /// </summary>
        public double CurveSkew
        {
            get => _curveSkew;
            set => Set(ref _curveSkew, value);
        }

        private Point _lineStart;
        private Point _lineEnd;
        private double _curveOffset;
        private double _curveSkew;

        // handle 1/2 are LineStart/LineEnd (a numbering other code depends on — e.g. the line and
        // arrow tools create with MoveHandleTo(point, 2)); the curve handle is appended as 3.
        // GraphicMeasure overrides HandleCount back to 2 to opt out (its ticks and label derive
        // from a straight chord).
        private const int MidHandle = 3;

        // dragging the mid handle back within this many units of the chord snaps to exactly
        // straight, so a curved line can be restored to the straight fast path by hand
        private const double StraightSnapDistance = 1.0;

        // the mid handle may slide this far (as a fraction of the chord length) either side of the
        // chord midpoint — enough for a lopsided bend without letting the curve fold back on itself
        internal const double MaxCurveSkew = 0.25;

        // Shift-drag quantizes the bow to multiples of this fraction of the chord length (and drops
        // the skew), so a bend is easy to straighten exactly or repeat across several lines
        internal const double SnappedOffsetStep = 0.1;

        // segments used to walk the curve when converting between arc length and the bezier
        // parameter (only runs when the cached geometries are refilled, never per pointer event)
        internal const int CurveSampleCount = 32;

        protected GraphicLine()
        { }

        public GraphicLine(Color objectColor, double lineWidth, Point start, Point end)
            : base(objectColor, lineWidth)
        {
            _lineStart = start;
            _lineEnd = end;
        }

        // PORT NOTE (aspect map entry): LineStart/LineEnd/CurveOffset/CurveSkew define the shape, so they
        // invalidate Bounds|Geometry. GraphicArrow inherits this map (it adds no persisted
        // property).
        internal override void DeclarePropertyEffects(Dictionary<string, InvalidationAspects> map)
        {
            base.DeclarePropertyEffects(map);
            const InvalidationAspects shape = InvalidationAspects.Bounds | InvalidationAspects.Geometry;
            map[nameof(LineStart)] = shape;
            map[nameof(LineEnd)] = shape;
            map[nameof(CurveOffset)] = shape;
            map[nameof(CurveSkew)] = shape;
        }

        // PORT NOTE (ComputeBounds): the old Bounds getter body moves here; the cached base Bounds
        // getter now serves reads. decision #25: widened-geometry bounds replaced by GetRenderBounds
        // with a LineWidth pen. Shares the one cached geometry (line or quadratic) with
        // Contains/DrawObject. The measuring pen carries the same ROUND caps the ink is stroked
        // with — round caps extend half the stroke width past each endpoint, and a flat pen would
        // clip that ink out of the bounds.
        protected override Rect ComputeBounds()
        {
            return GetLineGeometry().GetRenderBounds(RenderResources.GetPen(default, LineWidth, lineCap: PenLineCap.Round));
        }

        internal override int HandleCount => 3;

        // PORT NOTE (RenderResources): min-8px hit thickness preserved; the black pen only defines
        // the widened hit corridor (color is irrelevant to StrokeContains) so it comes from the cache.
        internal override bool Contains(Point point)
        {
            return GetLineGeometry().StrokeContains(RenderResources.GetPen(Colors.Black, Math.Max(LineWidth, 8)), point);
        }

        internal override Point GetHandle(int handleNumber, DpiScale uiscale)
        {
            if (handleNumber == MidHandle)
            {
                // the curve handle sits ON the ink (the t=0.5 point), not on the bezier control
                // point, so it stays under the pointer while dragging
                return TryGetControlPoint(out var control)
                    ? EvalQuadratic(LineStart, control, LineEnd, 0.5)
                    : ChordMidpoint();
            }

            return handleNumber == 1 ? LineStart : LineEnd;
        }

        // PORT NOTE (_translating fast path): pure translation offsets the cached bounds once and
        // clears only the Geometry aspect (shadow/text survive). Fields are set directly and a single
        // bare raise is emitted — the existing Move raise pattern is a contract and is unchanged.
        // CurveOffset/CurveSkew are chord-relative, so they survive the translation untouched.
        internal override void Move(double deltaX, double deltaY)
        {
            _translating = true;
            try
            {
                _lineStart = new Point(LineStart.X + deltaX, LineStart.Y + deltaY);
                _lineEnd = new Point(LineEnd.X + deltaX, LineEnd.Y + deltaY);
                RenderCache.TranslateCachedBounds(deltaX, deltaY);
                OnPropertyChanged();
            }
            finally
            {
                _translating = false;
            }
        }

        // PORT NOTE (Move/MoveHandleTo raise pattern): every handle raises through a property
        // setter — one named raise per pointer event is what the history engine turns into undo
        // steps.
        internal override void MoveHandleTo(Point point, int handleNumber) =>
            MoveHandleTo(point, handleNumber, KeyModifiers.None);

        /// <summary>
        /// Shift on the mid handle snaps to a symmetric bow in <see cref="SnappedOffsetStep"/>
        /// steps; endpoint handles ignore the modifiers (their angle snap lives in ToolPointer).
        /// </summary>
        internal void MoveHandleTo(Point point, int handleNumber, KeyModifiers modifiers)
        {
            if (handleNumber == MidHandle)
            {
                if (!TryGetChordNormal(out var normal))
                    return; // a zero-length chord has no normal to project onto — nothing to bow around

                var length = ChordLength();
                var tangent = new Vector(normal.Y, -normal.X);
                var mid = ChordMidpoint();
                var dx = point.X - mid.X;
                var dy = point.Y - mid.Y;
                var offset = dx * normal.X + dy * normal.Y;
                var skew = (dx * tangent.X + dy * tangent.Y) / length;

                if ((modifiers & KeyModifiers.Shift) != 0)
                {
                    var step = SnappedOffsetStep * length;
                    offset = Math.Round(offset / step) * step;
                    skew = 0;
                }
                else
                {
                    skew = Math.Clamp(skew, -MaxCurveSkew, MaxCurveSkew);
                }

                if (Math.Abs(offset) < StraightSnapDistance)
                    offset = 0;

                // a straight line has no bend to lean, so it always carries the symmetric default
                if (offset == 0)
                    skew = 0;

                CurveOffset = offset;
                CurveSkew = skew;
                return;
            }

            if (handleNumber == 1) LineStart = point;
            else LineEnd = point;
        }

        internal override Cursor GetHandleCursor(int handleNumber) => CursorResources.SizeAll;

        internal override void DrawObject(DrawingContext ctx)
        {
            // decision #25: the WPF widened-geometry fill is replaced by a stroked line. Round caps
            // on the ink; the dash applies to the ink pen only — bounds/hit-test pens stay solid.
            var pen = RenderResources.GetPen(ObjectColor, LineWidth, StrokeDash, PenLineCap.Round);
            if (TryGetControlPoint(out _))
                ctx.DrawGeometry(null, pen, GetLineGeometry());
            else
                ctx.DrawLine(pen, LineStart, LineEnd);
        }

        internal override void DrawHoverOutline(DrawingContext ctx, IPen pen) => ctx.DrawGeometry(null, pen, GetLineGeometry());

        // Cached full-length geometry (RenderCache.Geometry slot) — the straight LineGeometry, or
        // the full LineStart→control→LineEnd quadratic when curved — shared by
        // Bounds/Contains/DrawObject. GraphicArrow reuses this via the inherited Contains (a full
        // corridor that still covers the stretch under its tip); its shaft/tip parts live in
        // ComputeArrowParts/ComputeCurvedParts and do not touch this slot.
        protected virtual Geometry GetLineGeometry()
        {
            return RenderCache.Geometry ??= TryGetControlPoint(out var control)
                ? BuildQuadratic(_lineStart, control, _lineEnd)
                : (Geometry)new LineGeometry(_lineStart, _lineEnd);
        }

        /// <summary>
        /// The bezier control point implied by <see cref="CurveOffset"/> and <see cref="CurveSkew"/>:
        /// chordMid + 2*(offset*normal + skew*length*tangent). The factor 2 is the quadratic's
        /// on-curve/control relation — B(0.5) lands halfway between the chord midpoint and the
        /// control point — so doubling here puts the curve's own midpoint (and therefore the mid
        /// handle) exactly at the offset/skew position the drag stored. False means
        /// straight (offset 0, or a degenerate zero-length chord that has no normal), i.e. every
        /// caller must take the untouched straight fast path.
        /// </summary>
        protected bool TryGetControlPoint(out Point control)
        {
            if (_curveOffset == 0 || !TryGetChordNormal(out var normal))
            {
                control = default;
                return false;
            }

            var mid = ChordMidpoint();
            var along = _curveSkew * ChordLength();
            control = new Point(mid.X + 2 * (_curveOffset * normal.X + along * normal.Y),
                                mid.Y + 2 * (_curveOffset * normal.Y - along * normal.X));
            return true;
        }

        private double ChordLength() => Distance(LineStart, LineEnd);

        protected Point ChordMidpoint() => new Point((LineStart.X + LineEnd.X) / 2, (LineStart.Y + LineEnd.Y) / 2);

        protected bool TryGetChordNormal(out Vector normal)
        {
            var chord = new Vector(LineEnd.X - LineStart.X, LineEnd.Y - LineStart.Y);
            var length = chord.Length;
            if (length <= 0)
            {
                normal = default;
                return false;
            }

            normal = new Vector(-chord.Y / length, chord.X / length);
            return true;
        }

        internal static Geometry BuildQuadratic(Point start, Point control, Point end)
        {
            var geometry = new StreamGeometry();
            using (var gctx = geometry.Open())
            {
                gctx.BeginFigure(start, false);
                gctx.QuadraticBezierTo(control, end);
                gctx.EndFigure(false);
            }

            return geometry;
        }

        /// <summary>Bezier parameter at <paramref name="length"/> along the sampled polyline.</summary>
        internal static double ParameterAtLength(ReadOnlySpan<double> cumulative, double length)
        {
            int segments = cumulative.Length - 1;
            for (int i = 1; i <= segments; i++)
            {
                if (cumulative[i] < length)
                    continue;

                var span = cumulative[i] - cumulative[i - 1];
                var fraction = span > 0 ? (length - cumulative[i - 1]) / span : 0;
                return (i - 1 + fraction) / segments;
            }

            return 1;
        }

        internal static Point EvalQuadratic(Point start, Point control, Point end, double t)
        {
            var mt = 1 - t;
            var a = mt * mt;
            var b = 2 * mt * t;
            var c = t * t;
            return new Point(a * start.X + b * control.X + c * end.X,
                             a * start.Y + b * control.Y + c * end.Y);
        }

        internal static Point Lerp(Point from, Point to, double t) =>
            new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);

        internal static double Distance(Point a, Point b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
