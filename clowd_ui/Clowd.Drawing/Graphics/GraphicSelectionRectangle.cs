using System;
using Avalonia;
using Avalonia.Media;

namespace Clowd.Drawing.Graphics
{
    /// <summary>
    /// Selection rectangle helper used by <c>ToolPointer</c> for group
    /// selection. Created at the start of a rubber-band drag and removed
    /// once the user releases the mouse. Marked as scaffolding so it's
    /// excluded from artwork bounds and serialization.
    /// </summary>
    internal class GraphicSelectionRectangle : GraphicRectangle
    {
        public GraphicSelectionRectangle(Rect rect)
            : base(Colors.Black, 0, rect, 0, false)
        { }

        public override bool IsScaffolding => true;

        internal override void Draw(DrawingContext drawingContext, DpiScale uiscale)
        {
            var lineWidth = 1 * uiscale.DpiScaleX;
            Rect rect = Bounds;
            if (Math.Abs(lineWidth - 1) < 1e-6)
            {
                // crisp 1px line at 100% DPI
                rect = new Rect(
                    Math.Round(Bounds.Left) - 0.5,
                    Math.Round(Bounds.Top) - 0.5,
                    Math.Round(Bounds.Width),
                    Math.Round(Bounds.Height));
            }

            var whitePen = new Pen(Brushes.White, lineWidth);
            drawingContext.DrawRectangle(null, whitePen, rect);

            var dashedPen = new Pen(Brushes.Black, lineWidth)
            {
                DashStyle = new DashStyle(new double[] { 4 }, 0)
            };
            drawingContext.DrawRectangle(null, dashedPen, rect);
        }
    }
}
