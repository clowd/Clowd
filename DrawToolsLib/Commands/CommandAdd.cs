using System.Linq;

namespace DrawToolsLib
{
    internal class CommandAdd : CommandBase
    {
        private readonly PropertiesGraphicsBase[] _graphics;

        public CommandAdd(params GraphicsBase[] newObjects)
        {
            _graphics = newObjects.Select(s => s.CreateSerializedObject()).ToArray();
        }

        public override void Undo(DrawingCanvas drawingCanvas)
        {
            foreach (var g in _graphics)
            {
                GraphicsBase objectToDelete = drawingCanvas.GraphicsList.Cast<GraphicsBase>()
                    .FirstOrDefault(b => b.Id == g.ID);

                if (objectToDelete != null)
                    drawingCanvas.GraphicsList.Remove(objectToDelete);
            }
        }

        public override void Redo(DrawingCanvas drawingCanvas)
        {
            HelperFunctions.UnselectAll(drawingCanvas);
            foreach (var g in _graphics)
                drawingCanvas.GraphicsList.Add(g.CreateGraphics());
        }
    }
}
