using System;
using System.Windows;
using System.Windows.Media;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Ellipse", Skills = Skill.Stroke | Skill.Color | Skill.Angle)]
    public class GraphicEllipse : GraphicRectangle
    {
        protected GraphicEllipse()
        { }

        public GraphicEllipse(Color objectColor, double lineWidth, Rect rect)
            : base(objectColor, lineWidth, rect)
        { }

        public GraphicEllipse(Color objectColor, double lineWidth, Rect rect, double angle = 0)
            : base(objectColor, lineWidth, rect, angle)
        { }

        internal override void DrawRectangle(DrawingContext drawingContext)
        {
            Point center = new Point((Left + Right) / 2.0, (Top + Bottom) / 2.0);
            double radiusX = (Right - Left) / 2.0 - LineWidth / 2;
            double radiusY = (Bottom - Top) / 2.0 - LineWidth / 2;

            var brush = new SolidColorBrush(ObjectColor);
            drawingContext.DrawEllipse(
                null,
                new Pen(brush, LineWidth),
                center,
                radiusX,
                radiusY);
        }

        public override Rect Bounds
        {
            get
            {
                var a = (Right - Left) / 2; // one axis’s radius
                var b = (Bottom - Top) / 2; // the other axis’s radius
                var cos = Math.Cos(Angle * Math.PI / 180);
                var sin = Math.Sin(Angle * Math.PI / 180);
                var x = Math.Sqrt(a * a * cos * cos + b * b * sin * sin);
                var y = Math.Sqrt(a * a * sin * sin + b * b * cos * cos);
                return new Rect(
                    (Left + Right) / 2.0 - x,
                    (Top + Bottom) / 2.0 - y,
                    2 * x,
                    2 * y);
            }
        }

        internal override bool Contains(Point point)
        {
            point = UnapplyRotation(point);
            if (IsSelected)
                return UnrotatedBounds.Contains(point);

            EllipseGeometry g = new EllipseGeometry(UnrotatedBounds);
            return g.FillContains(point) || g.StrokeContains(new Pen(Brushes.Black, LineWidth), point);
        }
    }
}
