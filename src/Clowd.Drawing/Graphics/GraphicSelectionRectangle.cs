using System.Windows;
using System.Windows.Media;


namespace Clowd.Drawing.Graphics
{
    /// <summary>
    /// Selection Rectangle graphics object, used for group selection.
    /// Instance of this class should be created only for group selection
    /// and removed immediately after group selection finished.
    /// </summary>
    internal class GraphicSelectionRectangle : GraphicRectangle
    {
        public GraphicSelectionRectangle(DrawingCanvas canvas, Rect rect)
            : base(canvas, rect, false)
        {
            Effect = null;
        }
        public GraphicSelectionRectangle(Color objectColor, double lineWidth, Rect rect)
            : base(objectColor, lineWidth, rect, false)
        {
            Effect = null;
        }

        internal override void Draw(DrawingContext drawingContext)
        {
            drawingContext.DrawRectangle(
                null,
                new Pen(Brushes.White, 1),
                Bounds);

            DashStyle dashStyle = new DashStyle();
            dashStyle.Dashes.Add(4);

            Pen dashedPen = new Pen(Brushes.Black, 1);
            dashedPen.DashStyle = dashStyle;

            drawingContext.DrawRectangle(
                null,
                dashedPen,
                Bounds);
        }
    }
}
