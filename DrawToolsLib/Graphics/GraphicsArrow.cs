using System;
using System.Windows;
using System.Windows.Media;

namespace DrawToolsLib.Graphics
{
    [Serializable]
    public class GraphicsArrow : GraphicsLine
    {
        protected GraphicsArrow()
        {
        }
        public GraphicsArrow(DrawingCanvas canvas, Point start, Point end)
            : base(canvas, start, end)
        {
        }

        public GraphicsArrow(double scale, Color objectColor, double lineWidth, Point start, Point end)
            : base(scale, objectColor, lineWidth, start, end)
        {
        }

        internal override void Draw(DrawingContext drawingContext)
        {
            if (drawingContext == null)
                throw new ArgumentNullException(nameof(drawingContext));

            var tipLength = ActualLineWidth * 8;
            var lineVector = LineEnd - LineStart;
            var lineLength = lineVector.Length;
            lineVector.Normalize();

            tipLength = Math.Min(lineLength / 3, tipLength);
            lineLength -= tipLength / 2;
            if (lineLength > 0)
            {
                drawingContext.DrawLine(
                    new Pen(new SolidColorBrush(ObjectColor), ActualLineWidth),
                    LineStart,
                    LineStart + lineLength * lineVector);
            }

            const int tipAngle = 165;

            var rotate = Matrix.Identity;
            rotate.Rotate(tipAngle);
            var pt1 = LineEnd + rotate.Transform(lineVector * tipLength);
            rotate.Rotate(-tipAngle * 2);
            var pt2 = LineEnd + rotate.Transform(lineVector * tipLength);
            drawingContext.DrawGeometry(new SolidColorBrush(ObjectColor), new Pen(new SolidColorBrush(ObjectColor), 1),
                new PathGeometry(new[] { new PathFigure(LineEnd, new[] { new LineSegment(pt2, true), new LineSegment(pt1, true) }, true) }));

            base.Draw(drawingContext);
        }

        public override GraphicsBase Clone()
        {
            return new GraphicsArrow(ActualScale, ObjectColor, LineWidth, LineStart, LineEnd) { ObjectId = ObjectId };
        }
    }
}
