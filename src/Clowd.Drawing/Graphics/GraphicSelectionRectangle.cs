using System;
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
            : base(Colors.Black, 0, rect, 0, false)
        { }

        internal override void Draw(DrawingContext drawingContext, DpiScale uiscale)
        {
            var lineWidth = 1 * uiscale.DpiScaleX;
            Rect rect = Bounds;
            if (lineWidth == 1)
            {
                // if dpi 100% lets try to make this line sharp.
                // TODO: make this work at other DPI's
                // TODO: make this scale with the canvas zoom, not with dpi
                rect = new Rect(
                    Math.Round(Bounds.Left) - 0.5,
                    Math.Round(Bounds.Top) - 0.5,
                    Math.Round(Bounds.Width),
                    Math.Round(Bounds.Height)
                );
            }

            DashStyle dashStyle = new DashStyle();
            dashStyle.Dashes.Add(4);
            Pen dashedPen = new Pen(Brushes.Black, lineWidth);
            dashedPen.DashStyle = dashStyle;

            drawingContext.DrawRectangle(
                null,
                new Pen(Brushes.White, lineWidth),
                rect);

            drawingContext.DrawRectangle(
                null,
                dashedPen,
                rect);
        }
    }
}
