using System.Linq;

namespace DrawToolsLib
{
    internal class CommandDeleteAll : CommandBase
    {
        private readonly PropertiesGraphicsBase[] _cloneList;

        // Create this command BEFORE applying Delete All function.
        public CommandDeleteAll(DrawingCanvas drawingCanvas)
        {
            _cloneList = drawingCanvas.GraphicsList
                .OfType<GraphicsBase>()
                .Select(g => g.CreateSerializedObject())
                .ToArray();
        }

        public override void Undo(DrawingCanvas drawingCanvas)
        {
            foreach (PropertiesGraphicsBase o in _cloneList)
            {
                drawingCanvas.GraphicsList.Add(o.CreateGraphics());
            }
        }

        public override void Redo(DrawingCanvas drawingCanvas)
        {
            drawingCanvas.GraphicsList.Clear();
        }
    }
}
