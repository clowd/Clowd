using System;
using Avalonia;
using Avalonia.Media;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    // no Stroke/DashStyle skills: this type has no stroke, only a fill (LineWidth is fixed at 0).
    [GraphicDesc("Filled Rectangle", Skills = Skill.Color | Skill.Angle | Skill.Radius)]
    public class GraphicFilledRectangle : GraphicRectangle
    {
        protected GraphicFilledRectangle()
        { }

        public GraphicFilledRectangle(Color objectColor, Rect unrotatedBounds, double angle = 0)
            : base(objectColor, 0, unrotatedBounds, angle, false)
        { }

        // PORT NOTE (RenderResources): fill brush only — bounds/hit-test inherit GraphicRectangle's
        // cached ComputeBounds unchanged.
        internal override void DrawRectangle(DrawingContext drawingContext)
        {
            var rect = new Rect(UnrotatedBounds.Left,
                                UnrotatedBounds.Top,
                                Math.Max(1, UnrotatedBounds.Right - UnrotatedBounds.Left),
                                Math.Max(1, UnrotatedBounds.Bottom - UnrotatedBounds.Top));
            var radius = ClampCornerRadius(rect);

            drawingContext.DrawRectangle(
                RenderResources.GetBrush(ObjectColor),
                null,
                rect,
                radius, radius);
        }
    }
}
