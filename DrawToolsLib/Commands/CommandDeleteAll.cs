using System.Linq;
using DrawToolsLib.Graphics;

namespace DrawToolsLib
{
    internal class CommandDeleteAll : CommandBase
    {
        private readonly GraphicBase[] _cloneList;

        // Create this command BEFORE applying Delete All function.
        public CommandDeleteAll(DrawingCanvas drawingCanvas)
        {
            _cloneList = drawingCanvas.GraphicsList
                .ToArray();
        }

        public override void Undo(DrawingCanvas drawingCanvas)
        {
            foreach (GraphicBase o in _cloneList)
            {
                drawingCanvas.GraphicsList.Add(o);
            }
        }

        public override void Redo(DrawingCanvas drawingCanvas)
        {
            drawingCanvas.GraphicsList.Clear();
        }
    }
}
