using System.Linq;
using System.Windows.Media;

namespace DrawToolsLib
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
        PropertiesGraphicsBase[] _listBefore;
        PropertiesGraphicsBase[] _listAfter;

        // Create this command BEFORE operation.
        public CommandChangeState(DrawingCanvas drawingCanvas)
        {
            _listBefore = drawingCanvas.GraphicsList
                .OfType<GraphicsBase>()
                .Select(g => g.CreateSerializedObject())
                .ToArray();
        }

        // Call this function AFTER operation.
        public void NewState(DrawingCanvas drawingCanvas)
        {
            _listAfter = drawingCanvas.GraphicsList
                .OfType<GraphicsBase>()
                .Select(g => g.CreateSerializedObject())
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

        private static void ReplaceObjects(VisualCollection graphicsList, PropertiesGraphicsBase[] list)
        {
            for (int i = 0; i < graphicsList.Count; i++)
            {
                PropertiesGraphicsBase replacement = null;

                foreach (PropertiesGraphicsBase o in list)
                {
                    if (o.ID == ((GraphicsBase)graphicsList[i]).Id)
                    {
                        replacement = o;
                        break;
                    }
                }

                if (replacement != null)
                {
                    // Replace object with its clone
                    graphicsList.RemoveAt(i);
                    graphicsList.Insert(i, replacement.CreateGraphics());
                }
            }
        }
    }
}
