using System;
using Avalonia;
using Avalonia.Media;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Arrow", Skills = Skill.Stroke | Skill.Color)]
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
        // bounds under Skia path-ops, so the union is done on the rects instead.)
        protected override Rect ComputeBounds()
        {
            ComputeArrowParts(out Point shaftEnd, out bool hasShaft, out Geometry tip);
            var bounds = tip.Bounds;
            if (hasShaft)
                bounds = bounds.Union(new LineGeometry(LineStart, shaftEnd).GetRenderBounds(RenderResources.GetPen(default, LineWidth)));
            return bounds;
        }

        internal override void DrawObject(DrawingContext ctx)
        {
            // decision #26: shaft via DrawLine + filled triangle StreamGeometry (opaque colors → visually
            // identical to the WPF union fill). PORT NOTE (RenderResources): cached pen/brush; the tip
            // StreamGeometry is cached (see ComputeArrowParts) and shared with ComputeBounds.
            ComputeArrowParts(out Point shaftEnd, out bool hasShaft, out Geometry tip);
            if (hasShaft)
                ctx.DrawLine(RenderResources.GetPen(ObjectColor, LineWidth), LineStart, shaftEnd);
            ctx.DrawGeometry(RenderResources.GetBrush(ObjectColor), null, tip);
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

            const int tipAngle = 165;

            // decision #29: Matrix.Rotate + Transform(Vector) → transform by Matrix.CreateRotation.
            // (Avalonia has no Vector*Matrix operator; Point*Matrix is identical here since a pure
            // rotation matrix carries no translation.)
            var tipVector = new Point(lineVector.X * tipLength, lineVector.Y * tipLength);
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

            RenderCache.SecondaryGeometry = arrow;
            tip = arrow;
        }
    }
}
