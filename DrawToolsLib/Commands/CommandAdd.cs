using System.Linq;
using DrawToolsLib.Graphics;

namespace DrawToolsLib
{
    internal class CommandAdd : CommandBase
    {
        private readonly GraphicsBase[] _graphics;

        public CommandAdd(params GraphicsBase[] newObjects)
        {
            _graphics = newObjects;
        }

        public override void Undo(DrawingCanvas drawingCanvas)
        {
            foreach (var g in _graphics)
            {
                GraphicsBase objectToDelete = drawingCanvas.GraphicsList
                    .FirstOrDefault(b => b.ObjectId == g.ObjectId);

                if (objectToDelete != null)
                {
                    drawingCanvas.GraphicsList.Remove(objectToDelete);
                }
            }
        }

        public override void Redo(DrawingCanvas drawingCanvas)
        {
            drawingCanvas.UnselectAll();
            foreach (var g in _graphics)
            {
                g.IsSelected = true;
                drawingCanvas.GraphicsList.Add(g);
            }
        }
    }
}
