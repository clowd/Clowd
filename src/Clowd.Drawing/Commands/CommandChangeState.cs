using System.Linq;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Commands
{
    /// <summary>
    /// Changing state of existing objects:
    /// move, resize, change properties.
    /// 
    /// This command is always applied to the list selection.
    /// It keeps selection clone before and after operation.
    /// Undo/Redo operations replace objects in the list
    /// using selection clone.
    /// </summary>
    internal class CommandChangeState : CommandBase
    {
        GraphicBase[] _listBefore;
        GraphicBase[] _listAfter;

        // Create this command BEFORE operation.
        public CommandChangeState(DrawingCanvas drawingCanvas)
        {
            _listBefore = drawingCanvas.GraphicsList
                .Select(g => g.Clone())
                .ToArray();
        }

        // Call this function AFTER operation.
        public void NewState(DrawingCanvas drawingCanvas)
        {
            _listAfter = drawingCanvas.GraphicsList
                .Select(g => g.Clone())
                .ToArray();
        }

        public override void Undo(DrawingCanvas drawingCanvas)
        {
            ReplaceObjects(drawingCanvas.GraphicsList, _listBefore);
        }

        public override void Redo(DrawingCanvas drawingCanvas)
        {
            ReplaceObjects(drawingCanvas.GraphicsList, _listAfter);
        }

        private static void ReplaceObjects(GraphicCollection graphicsList, GraphicBase[] list)
        {
            for (int i = 0; i < graphicsList.Count; i++)
            {
                GraphicBase replacement = null;

                foreach (GraphicBase o in list)
                {
                    if (o.ObjectId == graphicsList[i].ObjectId)
                    {
                        replacement = o;
                        break;
                    }
                }

                if (replacement != null)
                {
                    // Replace object with its clone
                    graphicsList.RemoveAt(i);
                    graphicsList.Insert(i, replacement);
                }
            }
        }
    }
}
