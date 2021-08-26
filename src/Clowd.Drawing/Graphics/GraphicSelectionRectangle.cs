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
        public GraphicSelectionRectangle(Rect rect)
            : base(Colors.Black, 0, rect, false, 0, false)
        {
        }

        internal override void Draw(DrawingContext drawingContext, DpiScale uiscale)
        {
            var lineWidth = 1 * uiscale.DpiScaleX;

            drawingContext.DrawRectangle(
                null,
                new Pen(Brushes.White, lineWidth),
                Bounds);

            DashStyle dashStyle = new DashStyle();
            dashStyle.Dashes.Add(4);

            Pen dashedPen = new Pen(Brushes.Black, lineWidth);
            dashedPen.DashStyle = dashStyle;

            drawingContext.DrawRectangle(
                null,
                dashedPen,
                Bounds);
        }
    }
}
