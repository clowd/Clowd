using System.Linq;
using DrawToolsLib.Graphics;

namespace DrawToolsLib
{
    internal class CommandAdd : CommandBase
    {
        private readonly GraphicBase[] _graphics;

        public CommandAdd(params GraphicBase[] newObjects)
        {
            _graphics = newObjects.Select(n => n.Clone()).ToArray();
        }

        public override void Undo(DrawingCanvas drawingCanvas)
        {
            foreach (var g in _graphics)
            {
                GraphicBase objectToDelete = drawingCanvas.GraphicsList
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
