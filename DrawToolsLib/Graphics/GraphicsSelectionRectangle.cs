using System;
using System.Windows;
using System.Windows.Media;


namespace DrawToolsLib.Graphics
{
    /// <summary>
    /// Selection Rectangle graphics object, used for group selection.
    /// Instance of this class should be created only for group selection
    /// and removed immediately after group selection finished.
    /// </summary>
    internal class GraphicsSelectionRectangle : GraphicsRectangle
    {
        public GraphicsSelectionRectangle(DrawingCanvas canvas, Rect rect) 
            : base(canvas, rect, false)
        {
        }
        public GraphicsSelectionRectangle(double scale, Color objectColor, double lineWidth, Rect rect) 
            : base(scale, objectColor, lineWidth, rect, false)
        {
        }

        internal override void Draw(DrawingContext drawingContext)
        {
            drawingContext.DrawRectangle(
                null,
                new Pen(Brushes.White, ActualLineWidth),
                Bounds);

            DashStyle dashStyle = new DashStyle();
            dashStyle.Dashes.Add(4);

            Pen dashedPen = new Pen(Brushes.Black, ActualLineWidth);
            dashedPen.DashStyle = dashStyle;

            drawingContext.DrawRectangle(
                null,
                dashedPen,
                Bounds);
        }
    }
}
