using System;
using Avalonia;
using Avalonia.Media;
using Clowd.Drawing.Rendering;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Ellipse", Skills = Skill.Stroke | Skill.Color | Skill.Angle)]
    public class GraphicEllipse : GraphicRectangle
    {
        protected GraphicEllipse()
        { }

        public GraphicEllipse(Color objectColor, double lineWidth, Rect rect, double angle = 0)
            : base(objectColor, lineWidth, rect, angle)
        { }

        // PORT NOTE (RenderResources): draw path never allocates a brush/pen — the pen thickness
        // matches the old `new Pen(new SolidColorBrush(ObjectColor), LineWidth)`.
        internal override void DrawRectangle(DrawingContext drawingContext)
        {
            Point center = new Point((Left + Right) / 2.0, (Top + Bottom) / 2.0);
            double radiusX = (Right - Left) / 2.0 - LineWidth / 2;
            double radiusY = (Bottom - Top) / 2.0 - LineWidth / 2;

            drawingContext.DrawEllipse(
                null,
                RenderResources.GetPen(ObjectColor, LineWidth),
                center,
                radiusX,
                radiusY);
        }

        // PORT NOTE (ComputeBounds): closed-form rotated-ellipse AABB, moved verbatim from the old
        // Bounds override; the base cached getter now serves reads.
        protected override Rect ComputeBounds()
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

        internal override bool Contains(Point point)
        {
            point = UnapplyRotation(point);
            if (IsSelected)
                return UnrotatedBounds.Contains(point); // bounding-box hit rule while selected

            // Cache the EllipseGeometry (Geometry aspect) so per-event hover hit-tests are
            // allocation-free; it is rebuilt only when the shape/bounds change.
            var g = (EllipseGeometry)(RenderCache.Geometry ??= new EllipseGeometry(UnrotatedBounds));
            return g.FillContains(point) || g.StrokeContains(RenderResources.GetPen(Colors.Black, LineWidth), point);
        }
    }
}
