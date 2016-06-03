using System.Collections.Generic;
using DrawToolsLib.Graphics;

namespace DrawToolsLib
{
    // TODO: the implementation of this class needs to be looked at later when somebody has time.
    internal class CommandDelete : CommandBase
    {
        List<GraphicBase> cloneList;    // contains selected items which are deleted
        List<int> indexes;                         // contains indexes of deleted items

        // Create this command BEFORE applying Delete function.
        public CommandDelete(DrawingCanvas drawingCanvas)
        {
            cloneList = new List<GraphicBase>();
            indexes = new List<int>();

            // Make clone of the list selection.

            int currentIndex = 0;

            foreach (GraphicBase g in drawingCanvas.Selection)
            {
                cloneList.Add(g);
                indexes.Add(currentIndex);

                currentIndex++;
            }
        }

        public override void Undo(DrawingCanvas drawingCanvas)
        {
            // Insert all objects from cloneList to GraphicsList

            int currentIndex = 0;
            int indexToInsert;

            foreach (GraphicBase o in cloneList)
            {
                indexToInsert = indexes[currentIndex];

                if (indexToInsert >= 0 && indexToInsert <= drawingCanvas.GraphicsList.Count)   // "<=" is correct !
                {
                    drawingCanvas.GraphicsList.Insert(indexToInsert, o);
                }
                else
                {
                    // Bug: we should not be here.
                    // Add to the end anyway.
                    drawingCanvas.GraphicsList.Add(o);

                    System.Diagnostics.Trace.WriteLine("CommandDelete.Undo - incorrect index");
                }

                currentIndex++;
            }
        }

        public override void Redo(DrawingCanvas drawingCanvas)
        {
            // Delete from list all objects kept in cloneList.
            // Use object IDs for deleting, don't beleive to objects order.

            int n = drawingCanvas.GraphicsList.Count;

            for (int i = n - 1; i >= 0; i--)
            {
                bool toDelete = false;
                GraphicBase currentObject = drawingCanvas.GraphicsList[i];

                foreach (GraphicBase o in cloneList)
                {
                    if (o.ObjectId == currentObject.ObjectId)
                    {
                        toDelete = true;
                        break;
                    }
                }

                if (toDelete)
                {
                    drawingCanvas.GraphicsList.RemoveAt(i);
                }
            }
        }
    }
}
