using System;
using Avalonia;
using Avalonia.Media;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Arrow", Skills = Skill.Stroke | Skill.Color | Skill.DashStyle)]
    public class GraphicArrow : GraphicLine
    {
        protected GraphicArrow()
        { }

        public GraphicArrow(Color objectColor, double lineWidth, Point start, Point end)
            : base(objectColor, lineWidth, start, end)
        { }

        // PORT NOTE (ComputeBounds): the old Bounds getter body moves here; the cached base Bounds
        // getter now serves reads.
        // decision #26: WPF combined the widened shaft and the tip triangle into a single Union geometry.
        // Bounds here are computed as (shaft render bounds) ∪ (tip fill bounds), which is the same rect the
        // WPF union produced. (A CombinedGeometry of an open line and a filled triangle is not reliable for
        // bounds under Skia path-ops, so the union is done on the rects instead.) The measuring pens carry
        // the ink's round caps, which extend half the stroke width past the shaft's free end.
        protected override Rect ComputeBounds()
        {
            if (!TryGetControlPoint(out var control))
            {
                ComputeArrowParts(out Point shaftEnd, out bool hasShaft, out Geometry tip);
                var bounds = tip.Bounds;
                if (hasShaft)
                    bounds = bounds.Union(new LineGeometry(LineStart, shaftEnd).GetRenderBounds(
                        RenderResources.GetPen(default, LineWidth, lineCap: PenLineCap.Round)));
                return bounds;
            }

            // curved: the drawn shaft is a sub-curve of the full LineStart→LineEnd bezier, so the
            // already-cached full curve (the one Contains uses) bounds it — no second measurement.
            ComputeCurvedParts(control, out _, out Geometry curvedTip);
            return curvedTip.Bounds.Union(GetLineGeometry().GetRenderBounds(
                RenderResources.GetPen(default, LineWidth, lineCap: PenLineCap.Round)));
        }

        internal override void DrawObject(DrawingContext ctx)
        {
            // decision #26: shaft via DrawLine + filled triangle StreamGeometry (opaque colors → visually
            // identical to the WPF union fill). PORT NOTE (RenderResources): cached pen/brush; the tip
            // StreamGeometry is cached (see ComputeArrowParts) and shared with ComputeBounds.
            // round caps + dash apply to the shaft only — the tip stays a solid filled triangle
            var pen = RenderResources.GetPen(ObjectColor, LineWidth, StrokeDash, PenLineCap.Round);
            if (!TryGetControlPoint(out var control))
            {
                ComputeArrowParts(out Point shaftEnd, out bool hasShaft, out Geometry tip);
                if (hasShaft)
                    ctx.DrawLine(pen, LineStart, shaftEnd);
                ctx.DrawGeometry(RenderResources.GetBrush(ObjectColor), null, tip);
                return;
            }

            ComputeCurvedParts(control, out Geometry shaft, out Geometry curvedTip);
            if (shaft != null)
                ctx.DrawGeometry(null, pen, shaft);
            ctx.DrawGeometry(RenderResources.GetBrush(ObjectColor), null, curvedTip);
        }

        // The scalar shaft parts (shaftEnd/hasShaft) are cheap struct math and recomputed each call;
        // the expensive tip StreamGeometry is cached in RenderCache.SecondaryGeometry and cleared with
        // the Geometry aspect. (RenderCache.Geometry stays reserved for the inherited full-line Contains.)
        private void ComputeArrowParts(out Point shaftEnd, out bool hasShaft, out Geometry tip)
        {
            var tipLength = LineWidth * 8;
            var lineVector = new Vector(LineEnd.X - LineStart.X, LineEnd.Y - LineStart.Y);
            var lineLength = lineVector.Length;
            if (lineLength > 0)
                lineVector = lineVector.Normalize();

            tipLength = Math.Min(lineLength / 3, tipLength);
            lineLength -= tipLength / 2;

            hasShaft = lineLength > 0;
            shaftEnd = LineStart + (lineVector * lineLength);

            tip = RenderCache.SecondaryGeometry;
            if (tip != null)
                return;

            tip = BuildTip(lineVector, tipLength);
            RenderCache.SecondaryGeometry = tip;
        }

        /// <summary>
        /// Curved counterpart of <see cref="ComputeArrowParts"/>. The drawn shaft is the sub-curve
        /// t∈[0,tEnd] split off with de Casteljau, where tEnd is walked back from the end by
        /// tipLength/2 of ARC length — the curved equivalent of the straight path's
        /// `lineLength -= tipLength/2`, so the head covers where the shaft stops and nothing pokes
        /// through it. The tip triangle is built around the curve's tangent at t=1 (parallel to
        /// LineEnd-control) rather than the chord direction, so the head points where the ink
        /// actually arrives. Both geometries are cached (TertiaryGeometry = shaft,
        /// SecondaryGeometry = tip) and are dropped together with the Geometry aspect; the tip slot
        /// is the fill sentinel because a degenerate arrow legitimately has a null shaft.
        /// </summary>
        private void ComputeCurvedParts(Point control, out Geometry shaft, out Geometry tip)
        {
            shaft = RenderCache.TertiaryGeometry;
            tip = RenderCache.SecondaryGeometry;
            if (tip != null)
                return;

            var start = LineStart;
            var end = LineEnd;

            // walk the curve as a polyline: gives both its length (the straight path uses the chord
            // length for the same purpose) and the parameter at a given arc length
            Span<double> cumulative = stackalloc double[CurveSampleCount + 1];
            cumulative[0] = 0;
            var previous = start;
            for (int i = 1; i <= CurveSampleCount; i++)
            {
                var sample = EvalQuadratic(start, control, end, (double)i / CurveSampleCount);
                cumulative[i] = cumulative[i - 1] + Distance(previous, sample);
                previous = sample;
            }

            var curveLength = cumulative[CurveSampleCount];
            var tipLength = Math.Min(curveLength / 3, LineWidth * 8);
            var shaftLength = curveLength - tipLength / 2;

            if (shaftLength > 0)
            {
                // de Casteljau split at tEnd — the t∈[0,tEnd] piece is itself an exact quadratic
                var tEnd = ParameterAtLength(cumulative, shaftLength);
                var q1 = Lerp(start, control, tEnd);
                var q2 = Lerp(q1, Lerp(control, end, tEnd), tEnd);
                shaft = BuildQuadratic(start, q1, q2);
            }

            // B'(1) = 2*(end - control); only its direction matters here
            var tangent = new Vector(end.X - control.X, end.Y - control.Y);
            if (tangent.Length > 0)
                tangent = tangent.Normalize();

            tip = BuildTip(tangent, tipLength);

            RenderCache.TertiaryGeometry = shaft;
            RenderCache.SecondaryGeometry = tip;
        }

        private Geometry BuildTip(Vector direction, double tipLength)
        {
            const int tipAngle = 165;

            // decision #29: Matrix.Rotate + Transform(Vector) → transform by Matrix.CreateRotation.
            // (Avalonia has no Vector*Matrix operator; Point*Matrix is identical here since a pure
            // rotation matrix carries no translation.)
            var tipVector = new Point(direction.X * tipLength, direction.Y * tipLength);
            var pt1 = LineEnd + (tipVector * Matrix.CreateRotation(Matrix.ToRadians(tipAngle)));
            var pt2 = LineEnd + (tipVector * Matrix.CreateRotation(Matrix.ToRadians(-tipAngle)));

            var arrow = new StreamGeometry();
            using (var gctx = arrow.Open())
            {
                gctx.BeginFigure(LineEnd, true);
                gctx.LineTo(pt2);
                gctx.LineTo(pt1);
                gctx.EndFigure(true);
            }

            return arrow;
        }
    }
}
