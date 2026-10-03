using Avalonia;
using Avalonia.Media;

namespace Clowd.Drawing.Graphics
{
    /// <summary>
    /// A tapered arrow; the look lives in <see cref="ArrowShape"/>, shared with the numeric step
    /// badge's pointer.
    ///
    /// Cache slots: SecondaryGeometry = head (the fill sentinel), TertiaryGeometry = shaft (null
    /// when the arrow is too short to have one). Both clear with the Geometry aspect.
    /// RenderCache.Geometry stays reserved for the inherited full-line Contains corridor.
    /// </summary>
    [GraphicDesc("Arrow", Skills = Skill.Stroke | Skill.Color | Skill.DashStyle)]
    public class GraphicArrow : GraphicLine
    {
        protected GraphicArrow()
        { }

        public GraphicArrow(Color objectColor, double lineWidth, Point start, Point end)
            : base(objectColor, lineWidth, start, end)
        { }

        protected override Rect ComputeBounds()
        {
            GetParts(out var shaft, out var head);
            return ArrowShape.GetBounds(shaft, head, LineWidth, StrokeDash);
        }

        internal override void DrawObject(DrawingContext ctx)
        {
            GetParts(out var shaft, out var head);
            ArrowShape.Draw(ctx, shaft, head, ObjectColor, LineWidth, StrokeDash);
        }

        private void GetParts(out Geometry shaft, out Geometry head)
        {
            shaft = RenderCache.TertiaryGeometry;
            head = RenderCache.SecondaryGeometry;
            if (head != null)
                return;

            Point? control = TryGetControlPoint(out var c) ? c : null;
            ArrowShape.Build(LineStart, LineEnd, control, LineWidth, StrokeDash != null, out shaft, out head);

            RenderCache.TertiaryGeometry = shaft;
            RenderCache.SecondaryGeometry = head;
        }
    }
}
