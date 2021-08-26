using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Clowd.Drawing.Graphics
{
    [Serializable]
    public class GraphicFilledRectangle : GraphicRectangle
    {
        protected GraphicFilledRectangle()
        {
            Effect = null;
        }

        public GraphicFilledRectangle(DrawingCanvas canvas, Rect rect)
            : base(canvas, rect)
        {
            Effect = null;
        }

        public GraphicFilledRectangle(Color objectColor, Rect unrotatedBounds, double angle)
            : base(objectColor, 0, unrotatedBounds, false, angle)
        {
            Effect = null;
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

        public override GraphicBase Clone()
        {
            return new GraphicFilledRectangle(ObjectColor, UnrotatedBounds, Angle) { ObjectId = ObjectId };
        }
    }
}
