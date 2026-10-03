using Avalonia;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    /// <summary>
    /// Places a new sticky note centered on the press point (dragging before release carries it
    /// along), then opens it for editing, like a new text.
    /// </summary>
    internal class ToolStickyNote : ToolText
    {
        public ToolStickyNote() : base(() => CursorResources.Text)
        { }

        protected override GraphicText CreateGraphic(DrawingCanvas canvas, Point pt) => new GraphicStickyNote(canvas, pt);

        protected override void PlaceGraphic(GraphicText graphic, Point pt) => ((GraphicStickyNote)graphic).CenterOn(pt);
    }
}
