using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using System.Globalization;



namespace DrawToolsLib
{
    /// <summary>
    /// Changing objects order: move to front/back.
    /// 
    /// Command keeps list of object IDs before and after operation.
    /// Using these lists, it is possible to undo/redo Move to ... operation.
    /// </summary>
    class CommandChangeOrder : CommandBase
    {
        // Object IDs before operation
        List<int> listBefore;

        // Object IDs after operation
        List<int> listAfter;


        // Create this command BEFORE operation.
        public CommandChangeOrder(DrawingCanvas drawingCanvas)
        {
            FillList(drawingCanvas.GraphicsList, ref listBefore);
        }

        // Call this function AFTER operation.
        public void NewState(DrawingCanvas drawingCanvas)
        {
            FillList(drawingCanvas.GraphicsList, ref listAfter);
        }

        /// <summary>
        /// Restore order to its state before change.
        /// </summary>
        public override void Undo(DrawingCanvas drawingCanvas)
        {
            ChangeOrder(drawingCanvas.GraphicsList, listBefore);
        }

        /// <summary>
        /// Restore order to its state after change.
        /// </summary>
        public override void Redo(DrawingCanvas drawingCanvas)
        {
            ChangeOrder(drawingCanvas.GraphicsList, listAfter);
        }

        // Fill list of Ids from graphics list
        private static void FillList(VisualCollection graphicsList, ref List<int> listToFill)
        {
            listToFill = new List<int>();

            foreach (GraphicsBase g in graphicsList)
            {
                listToFill.Add(g.Id);
            }
        }

        // Set selection order in graphicsList according to list of IDs
        private static void ChangeOrder(VisualCollection graphicsList, List<int> indexList)
        {
            List<GraphicsBase> tmpList = new List<GraphicsBase>();

            // Read indexList, find every element in graphicsList by ID
            // and move it to tmpList.

            foreach(int id in indexList)
            {
                GraphicsBase objectToMove = null;

                foreach(GraphicsBase g in graphicsList)
                {
                    if ( g.Id == id)
                    {
                        objectToMove = g;
                        break;
                    }
                }

                if ( objectToMove != null )
                {
                    // Move
                    tmpList.Add(objectToMove);
                    graphicsList.Remove(objectToMove);
                }
            }

            // Now tmpList contains objects in correct order.
            // Read tmpList and add all its elements back to graphicsList.

            foreach(GraphicsBase g in tmpList)
            {
                graphicsList.Add(g);
            }
        }


        /// <summary>
        /// Dump (for debugging)
        /// </summary>
        [Conditional("DEBUG")]
        static void Dump(List<int> list, string header)
        {
            Trace.WriteLine("");
            Trace.WriteLine(header);
            Trace.WriteLine("");

            string s = "";

            foreach (int n in list)
            {
                s += n.ToString(CultureInfo.InvariantCulture);
                s += " ";
            }

            Trace.WriteLine(s);
        }

    }
}
