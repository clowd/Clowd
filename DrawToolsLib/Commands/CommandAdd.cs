using System.Linq;
using DrawToolsLib.Graphics;

namespace DrawToolsLib
{
    internal class CommandAdd : CommandBase
    {
        private readonly GraphicsBase[] _graphics;

        public CommandAdd(params GraphicsVisual[] newObjects)
        {
            _graphics = newObjects.Select(s => s.Graphic).ToArray();
        }

        public override void Undo(DrawingCanvas drawingCanvas)
        {
            foreach (var g in _graphics)
            {
                GraphicsVisual objectToDelete = drawingCanvas.GraphicsList.Cast<GraphicsVisual>()
                    .FirstOrDefault(b => b.ObjectId == g.ObjectId);

                if (objectToDelete != null)
                {
                    drawingCanvas.GraphicsList.Remove(objectToDelete);
                    objectToDelete.Dispose();
                }
            }
        }

        public override void Redo(DrawingCanvas drawingCanvas)
        {
            HelperFunctions.UnselectAll(drawingCanvas);
            foreach (var g in _graphics)
            {
                var vis = g.CreateVisual();
                vis.IsSelected = true;
                drawingCanvas.GraphicsList.Add(vis);
            }
        }
    }
}
