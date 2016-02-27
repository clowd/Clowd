using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;



namespace DrawToolsLib
{
    /// <summary>
    /// Delete command.
    /// Applied to list selection.
    /// </summary>
    class CommandDelete : CommandBase
    {
        List<PropertiesGraphicsBase> cloneList;    // contains selected items which are deleted
        List<int> indexes;                         // contains indexes of deleted items

        // Create this command BEFORE applying Delete function.
        public CommandDelete(DrawingCanvas drawingCanvas)
        {
            cloneList = new List<PropertiesGraphicsBase>();
            indexes = new List<int>();

            // Make clone of the list selection.

            int currentIndex = 0;

            foreach (GraphicsBase g in drawingCanvas.Selection)
            {
                cloneList.Add(g.CreateSerializedObject());
                indexes.Add(currentIndex);

                currentIndex++;
            }
        }

        /// <summary>
        /// Restore deleted objects
        /// </summary>
        public override void Undo(DrawingCanvas drawingCanvas)
        {
            // Insert all objects from cloneList to GraphicsList

            int currentIndex = 0;
            int indexToInsert;

            foreach (PropertiesGraphicsBase o in cloneList)
            {
                indexToInsert = indexes[currentIndex];

                if ( indexToInsert >=0  &&  indexToInsert <= drawingCanvas.GraphicsList.Count )   // "<=" is correct !
                {
                    drawingCanvas.GraphicsList.Insert(indexToInsert, o.CreateGraphics());
                }
                else
                {
                    // Bug: we should not be here.
                    // Add to the end anyway.
                    drawingCanvas.GraphicsList.Add(o.CreateGraphics());

                    System.Diagnostics.Trace.WriteLine("CommandDelete.Undo - incorrect index");
                }

                currentIndex++;
            }

            drawingCanvas.RefreshClip();
        }

        /// <summary>
        /// Delete objects again.
        /// </summary>
        public override void Redo(DrawingCanvas drawingCanvas)
        {
            // Delete from list all objects kept in cloneList.
            // Use object IDs for deleting, don't beleive to objects order.

            int n = drawingCanvas.GraphicsList.Count;

            for (int i = n - 1; i >= 0; i--)
            {
                bool toDelete = false;
                GraphicsBase currentObject = (GraphicsBase)drawingCanvas.GraphicsList[i];

                foreach (PropertiesGraphicsBase o in cloneList)
                {
                    if (o.ID == currentObject.Id)
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
