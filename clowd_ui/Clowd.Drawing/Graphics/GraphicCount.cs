using System;
using Avalonia;
using Avalonia.Media;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Numeric Step", Skills = Skill.Stroke | Skill.Color | Skill.Font | Skill.Angle)]
    public class GraphicCount : GraphicText
    {
        public GraphicCount()
        { }

        public GraphicCount(Color objectColor, double lineWidth, Point point, string? body = null)
            : base(objectColor, lineWidth, point, 0, body ?? "#")
        {
        }

        protected override void DrawObjectImpl(DrawingContext context, bool showText)
        {
            var rotateMatrix =
                Matrix.CreateTranslation(-CenterOfRotation.X, -CenterOfRotation.Y) *
                Matrix.CreateRotation(Angle * Math.PI / 180.0) *
                Matrix.CreateTranslation(CenterOfRotation.X, CenterOfRotation.Y);

            using (context.PushTransform(rotateMatrix))
            {
                var lineBrush = new Pen(new SolidColorBrush(ObjectColor), LineWidth);
                Point center = new Point((Left + Right) / 2.0, (Top + Bottom) / 2.0);

                var ubounds = UnrotatedBounds;
                var bradius = Math.Min(ubounds.Height / 2, ubounds.Width / 2);

                context.DrawRectangle(
                    Brushes.White,
                    lineBrush,
                    new Rect(UnrotatedBounds.Left + (LineWidth / 2),
                        UnrotatedBounds.Top + (LineWidth / 2),
                        Math.Max(1, UnrotatedBounds.Right - UnrotatedBounds.Left - LineWidth),
                        Math.Max(1, UnrotatedBounds.Bottom - UnrotatedBounds.Top - LineWidth)),
                    bradius, bradius);

                if (showText)
                {
                    var form = CreateFormattedText();
                    form.TextAlignment = TextAlignment.Center;
                    var textPos = new Point(center.X - form.Width / 2, center.Y - form.Height / 2);
                    context.DrawText(form, textPos);
                }
            }
        }

        protected override void DrawDashedBorder(DrawingContext ctx, Rect rect, double lineWidth = 2)
        {
            // Numeric badges intentionally don't show a dashed selection border.
        }

        internal override void Normalize()
        {
            base.Normalize();
            // Make sure the badge stays at least square so the rounded rect renders as a circle
            // when the text is short.
            var test = Left + (Bottom - Top);
            Right = Math.Max(Right, test);
        }

        public override Rect Bounds
        {
            get
            {
                var a = (Right - Left) / 2;
                var b = (Bottom - Top) / 2;
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

            // Ellipse hit-test (matches GraphicEllipse).
            var ub = UnrotatedBounds;
            var cx = (ub.Left + ub.Right) / 2;
            var cy = (ub.Top + ub.Bottom) / 2;
            var a = ub.Width / 2;
            var b = ub.Height / 2;
            if (a <= 0 || b <= 0) return false;

            var nx = (point.X - cx) / a;
            var ny = (point.Y - cy) / b;
            var tol = (Math.Max(LineWidth, 8) / 2) / Math.Min(a, b);
            return nx * nx + ny * ny <= (1 + tol) * (1 + tol);
        }
    }
}
