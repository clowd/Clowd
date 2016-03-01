using System;
using System.Windows;
using System.Windows.Media;

namespace DrawToolsLib
{
    public class GraphicsArrow : GraphicsLine
    {
        public GraphicsArrow(Point start, Point end, double lineWidth, Color objectColor, double actualScale)
            :base(start, end, lineWidth, objectColor, actualScale)
        {
        }

        public override void Draw(DrawingContext drawingContext)
        {
            if (drawingContext == null)
                throw new ArgumentNullException(nameof(drawingContext));

            var tipLength = ActualLineWidth * 8;
            var lineVector = lineEnd - lineStart;
            var lineLength = lineVector.Length;
            lineVector.Normalize();

            tipLength = Math.Min(lineLength / 3, tipLength);
            lineLength -= tipLength / 2;
            if (lineLength > 0)
            {
                drawingContext.DrawLine(
                    new Pen(new SolidColorBrush(ObjectColor), ActualLineWidth),
                    lineStart,
                    lineStart + lineLength * lineVector);
            }

            var rotate = Matrix.Identity;
            rotate.Rotate(165);
            var pt1 = lineEnd + rotate.Transform(lineVector * tipLength);
            rotate.Rotate(-165 * 2);
            var pt2 = lineEnd + rotate.Transform(lineVector * tipLength);
            drawingContext.DrawGeometry(new SolidColorBrush(ObjectColor), new Pen(new SolidColorBrush(ObjectColor), 1),
                new PathGeometry(new[] { new PathFigure(lineEnd, new[] { new LineSegment(pt2, true), new LineSegment(pt1, true) }, true) }));

            base.Draw(drawingContext);
        }
    }
}
