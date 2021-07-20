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

        public override Rect Bounds
        {
            get
            {
                var points = new[] { new Point(Left, Top), new Point(Right, Top), new Point(Left, Bottom), new Point(Right, Bottom) };
                var rotated = points.Select(ApplyRotation).ToArray();
                var l = rotated.Min(p => p.X);
                var t = rotated.Min(p => p.Y);
                var r = rotated.Max(p => p.X);
                var b = rotated.Max(p => p.Y);
                return new Rect(l, t, r - l, b - t);
            }
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
