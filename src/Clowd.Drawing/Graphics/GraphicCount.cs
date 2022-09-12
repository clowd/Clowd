using System;
using System.Windows;
using System.Windows.Media;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Numeric Step", Skills = Skill.Stroke | Skill.Color | Skill.Font | Skill.Angle)]
    public class GraphicCount : GraphicText
    {
        protected GraphicCount()
        { }

        public GraphicCount(DrawingCanvas canvas, Point point, string body = null)
            : this(canvas.ObjectColor, canvas.LineWidth, point, body)
        {
            FontName = canvas.TextFontFamilyName;
            FontSize = canvas.TextFontSize;
            FontStretch = canvas.TextFontStretch;
            FontStyle = canvas.TextFontStyle;
            FontWeight = canvas.TextFontWeight;
        }

        public GraphicCount(Color objectColor, double lineWidth, Point point, string body = null)
            : base(objectColor, lineWidth, point, 0, body)
        {
            Body = body ?? "#";
        }

        protected override void DrawObjectImpl(DrawingContext context, bool showText)
        {
            context.PushTransform(new RotateTransform(Angle, CenterOfRotation.X, CenterOfRotation.Y));

            var lineBrush = new Pen(new SolidColorBrush(ObjectColor), LineWidth);
            Point center = new Point((Left + Right) / 2.0, (Top + Bottom) / 2.0);

            var ubounds = UnrotatedBounds;
            var bradius = Math.Min(ubounds.Height / 2, ubounds.Width / 2);
            
            context.DrawRoundedRectangle(
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
                center.Y -= (form.Height / 2);
                context.DrawText(form, center);
            }
        }

        protected override void DrawDashedBorder(DrawingContext ctx, Rect rect, double lineWidth = 2)
        {
            // do nothing
        }

        internal override void Normalize()
        {
            base.Normalize();
            var test = Left + Bottom - Top;
            Right = Math.Max(Right, test);
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
