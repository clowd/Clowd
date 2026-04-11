using System;
using Avalonia;
using Avalonia.Media;

namespace Clowd.Drawing.Graphics
{
    [GraphicDesc("Arrow", Skills = Skill.Stroke | Skill.Color)]
    public class GraphicArrow : GraphicLine
    {
        public GraphicArrow()
        { }

        public GraphicArrow(Color objectColor, double lineWidth, Point start, Point end)
            : base(objectColor, lineWidth, start, end)
        { }

        internal override void DrawObject(DrawingContext ctx)
        {
            var dx = LineEnd.X - LineStart.X;
            var dy = LineEnd.Y - LineStart.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-6) return;

            var ux = dx / length;
            var uy = dy / length;

            // Match the WPF original: tip is min(line/3, lineWidth*8), and the
            // shaft retracts by half the tip length so the triangle isn't drawn
            // over a perpendicular shaft end.
            var tipLength = Math.Min(length / 3, LineWidth * 8);
            var shaftLength = Math.Max(0, length - tipLength / 2);

            var brush = new SolidColorBrush(ObjectColor);

            // Shaft — round caps so the start (tail) is a soft dot and the
            // far end tucks cleanly into the base of the triangle.
            var shaftPen = new Pen(brush, LineWidth)
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            var shaftEnd = new Point(LineStart.X + ux * shaftLength, LineStart.Y + uy * shaftLength);
            ctx.DrawLine(shaftPen, LineStart, shaftEnd);

            // Triangle: apex at LineEnd; base at LineEnd - tipLength * unit ± perpendicular * (tipLength/2)
            var halfWidth = tipLength * 0.5;
            var px = -uy;
            var py = ux;

            var baseMid = new Point(LineEnd.X - ux * tipLength, LineEnd.Y - uy * tipLength);
            var b1 = new Point(baseMid.X + px * halfWidth, baseMid.Y + py * halfWidth);
            var b2 = new Point(baseMid.X - px * halfWidth, baseMid.Y - py * halfWidth);

            var head = new StreamGeometry();
            using (var sgc = head.Open())
            {
                sgc.BeginFigure(LineEnd, isFilled: true);
                sgc.LineTo(b1);
                sgc.LineTo(b2);
                sgc.EndFigure(isClosed: true);
            }

            // Fill + stroke the triangle with the same colour. The round-joined
            // stroke bevels the apex and the two base corners by roughly
            // LineWidth/4 px, giving the "slight rounding" the user asked for
            // without resorting to a hand-rolled filleted path.
            var headPen = new Pen(brush, Math.Max(LineWidth * 0.5, 1.0))
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            ctx.DrawGeometry(brush, headPen, head);
        }
    }
}
