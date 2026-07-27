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

        private Point _lineStart;
        private Point _lineEnd;
        private double _curveOffset;

        // handle 1/2 are LineStart/LineEnd (a numbering other code depends on — e.g. the line and
        // arrow tools create with MoveHandleTo(point, 2)); the curve handle is appended as 3.
        // GraphicMeasure overrides HandleCount back to 2 to opt out (its ticks and label derive
        // from a straight chord).
        private const int MidHandle = 3;

        // dragging the mid handle back within this many units of the chord snaps to exactly
        // straight, so a curved line can be restored to the straight fast path by hand
        private const double StraightSnapDistance = 1.0;

        // segments used to walk the curve when converting between arc length and the bezier
        // parameter (only runs when the cached geometries are refilled, never per pointer event)
        protected const int CurveSampleCount = 32;

        protected GraphicLine()
        { }

        public GraphicLine(Color objectColor, double lineWidth, Point start, Point end)
            : base(objectColor, lineWidth)
        {
            _lineStart = start;
            _lineEnd = end;
        }

        // PORT NOTE (aspect map entry): LineStart/LineEnd/CurveOffset define the shape, so they
        // invalidate Bounds|Geometry|Shadow. GraphicArrow inherits this map (it adds no persisted
        // property).
        internal override void DeclarePropertyEffects(Dictionary<string, InvalidationAspects> map)
        {
            base.DeclarePropertyEffects(map);
            const InvalidationAspects shape = InvalidationAspects.Bounds | InvalidationAspects.Geometry | InvalidationAspects.Shadow;
            map[nameof(LineStart)] = shape;
            map[nameof(LineEnd)] = shape;
            map[nameof(CurveOffset)] = shape;
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
        // CurveOffset is chord-relative, so it survives the translation untouched.
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
        internal override void MoveHandleTo(Point point, int handleNumber)
        {
            if (handleNumber == MidHandle)
            {
                if (!TryGetChordNormal(out var normal))
                    return; // a zero-length chord has no normal to project onto — nothing to bow around

                var mid = ChordMidpoint();
                var offset = (point.X - mid.X) * normal.X + (point.Y - mid.Y) * normal.Y;

                if (Math.Abs(offset) < StraightSnapDistance)
                    offset = 0;

                CurveOffset = offset;
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
        /// The bezier control point implied by <see cref="CurveOffset"/>: chordMid + 2*offset*normal.
        /// The factor 2 is the quadratic's on-curve/control relation — B(0.5) lands halfway between
        /// the chord midpoint and the control point — so doubling here puts the curve's own midpoint
        /// (and therefore the mid handle) exactly CurveOffset away from the chord. False means
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
            control = new Point(mid.X + 2 * _curveOffset * normal.X, mid.Y + 2 * _curveOffset * normal.Y);
            return true;
        }

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

        protected static Geometry BuildQuadratic(Point start, Point control, Point end)
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
        protected static double ParameterAtLength(ReadOnlySpan<double> cumulative, double length)
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

        protected static Point EvalQuadratic(Point start, Point control, Point end, double t)
        {
            var mt = 1 - t;
            var a = mt * mt;
            var b = 2 * mt * t;
            var c = t * t;
            return new Point(a * start.X + b * control.X + c * end.X,
                             a * start.Y + b * control.Y + c * end.Y);
        }

        protected static Point Lerp(Point from, Point to, double t) =>
            new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);

        protected static double Distance(Point a, Point b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
