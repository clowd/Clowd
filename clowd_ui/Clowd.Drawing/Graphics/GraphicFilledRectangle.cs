using System;
using Avalonia;
using Avalonia.Media;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Filled Rectangle", Skills = Skill.Stroke | Skill.Color | Skill.Angle)]
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
            drawingContext.DrawRectangle(
                RenderResources.GetBrush(ObjectColor),
                null,
                new Rect(UnrotatedBounds.Left,
                         UnrotatedBounds.Top,
                         Math.Max(1, UnrotatedBounds.Right - UnrotatedBounds.Left),
                         Math.Max(1, UnrotatedBounds.Bottom - UnrotatedBounds.Top)));
        }
    }
}
