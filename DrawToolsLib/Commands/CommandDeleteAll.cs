using System.Linq;
using DrawToolsLib.Graphics;

namespace DrawToolsLib
{
    internal class CommandDeleteAll : CommandBase
    {
        private readonly GraphicsBase[] _cloneList;

        // Create this command BEFORE applying Delete All function.
        public CommandDeleteAll(DrawingCanvas drawingCanvas)
        {
            _cloneList = drawingCanvas.GraphicsList
                .OfType<GraphicsVisual>()
                .Select(g => g.Graphic)
                .ToArray();
        }

        public override void Undo(DrawingCanvas drawingCanvas)
        {
            foreach (GraphicsBase o in _cloneList)
            {
                drawingCanvas.GraphicsList.Add(o.CreateVisual());
            }
        }

        public override void Redo(DrawingCanvas drawingCanvas)
        {
            drawingCanvas.GraphicsList.Clear();
        }
    }
}
