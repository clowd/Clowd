using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Clowd.Drawing.Graphics
{
    public class GraphicFilledRectangle : GraphicRectangle
    {
        protected GraphicFilledRectangle()
        {
        }

        public GraphicFilledRectangle(Color objectColor, Rect unrotatedBounds, double angle = 0)
            : base(objectColor, 0, unrotatedBounds, false, angle, false)
        {
        }

        internal override void DrawRectangle(DrawingContext drawingContext)
        {
            var brush = new SolidColorBrush(ObjectColor);
            drawingContext.DrawRectangle(
                brush,
                null,
                new Rect(UnrotatedBounds.Left,
                    UnrotatedBounds.Top,
                    Math.Max(1, UnrotatedBounds.Right - UnrotatedBounds.Left),
                    Math.Max(1, UnrotatedBounds.Bottom - UnrotatedBounds.Top)));
        }
    }
}
