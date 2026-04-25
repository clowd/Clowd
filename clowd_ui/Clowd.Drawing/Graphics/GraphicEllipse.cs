using System;
using Avalonia;
using Avalonia.Media;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Ellipse", Skills = Skill.Stroke | Skill.Color | Skill.Angle)]
    public class GraphicEllipse : GraphicRectangle
    {
        public GraphicEllipse()
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

            if (radiusX <= 0 || radiusY <= 0) return;

            var pen = new Pen(new SolidColorBrush(ObjectColor), LineWidth);
            drawingContext.DrawEllipse(null, pen, center, radiusX, radiusY);
        }

        public override Rect Bounds
        {
            get
            {
                var a = (Right - Left) / 2; // one axis's radius
                var b = (Bottom - Top) / 2; // the other axis's radius
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

            // While selected, give the user a bigger hit target so they can
            // grab the body even from inside the ellipse's empty corners.
            if (IsSelected)
                return UnrotatedBounds.Contains(point);

            // Standard ellipse equation: ((x - cx)/a)^2 + ((y - cy)/b)^2 <= 1
            var ub = UnrotatedBounds;
            var cx = (ub.Left + ub.Right) / 2;
            var cy = (ub.Top + ub.Bottom) / 2;
            var a = ub.Width / 2;
            var b = ub.Height / 2;
            if (a <= 0 || b <= 0) return false;

            var nx = (point.X - cx) / a;
            var ny = (point.Y - cy) / b;
            // Inflate the test by a stroke-half tolerance so the rim hits.
            var tol = (Math.Max(LineWidth, 8) / 2) / Math.Min(a, b);
            return nx * nx + ny * ny <= (1 + tol) * (1 + tol);
        }
    }
}
