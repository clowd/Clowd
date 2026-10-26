using System;
using Avalonia;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Ink;

namespace Clowd.Drawing.Tools
{
    /// <summary>
    /// The highlighter: the brush's gesture, laying down a <see cref="GraphicHighlighter"/> stroke
    /// (a slanted rectangular chisel tip) instead of round ink, each stroke with a slant of its
    /// own. The cursor is the tip's outline, upright.
    /// </summary>
    internal class ToolHighlighter : ToolBrush
    {
        // the tip's outline at its on-screen size; DrawingCanvas re-applies it when the stroke
        // width or the zoom changes (one canvas unit is ContentScale device pixels)
        public override void SetCursor(DrawingCanvas canvas)
        {
            var tip = ChiselStrokeBuilder.TipSize(canvas.LineWidth);
            canvas.Cursor = BrushCursor.GetRect(tip.Width * canvas.ContentScale, tip.Height * canvas.ContentScale);
        }

        protected override GraphicBrush CreateStroke(DrawingCanvas canvas, Point origin) =>
            new GraphicHighlighter(canvas, origin, Random.Shared.Next());
    }
}
