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
    /// Changing state of existing objects:
    /// move, resize, change properties.
    /// 
    /// This command is always applied to the list selection.
    /// It keeps selection clone before and after operation.
    /// Undo/Redo operations replace objects in the list
    /// using selection clone.
    /// </summary>
    class CommandChangeState : CommandBase
    {
        // Selected object(s) before operation
        List<PropertiesGraphicsBase> listBefore;

        // Selected object(s) after operation
        List<PropertiesGraphicsBase> listAfter;


        // Create this command BEFORE operation.
        public CommandChangeState(DrawingCanvas drawingCanvas)
        {
            // Keep objects state before operation.
            FillList(drawingCanvas.GraphicsList, ref listBefore);
        }

        // Call this function AFTER operation.
        public void NewState(DrawingCanvas drawingCanvas)
        {
            // Keep objects state after operation.
            FillList(drawingCanvas.GraphicsList, ref listAfter);
        }

        /// <summary>
        /// Restore selection to its state before change.
        /// </summary>
        public override void Undo(DrawingCanvas drawingCanvas)
        {
            // Replace all objects in the list with objects from listBefore
            ReplaceObjects(drawingCanvas.GraphicsList, listBefore);

            drawingCanvas.RefreshClip();
        }

        /// <summary>
        /// Restore selection to its state after change.
        /// </summary>
        public override void Redo(DrawingCanvas drawingCanvas)
        {
            // Replace all objects in the list with objects from listAfter
            ReplaceObjects(drawingCanvas.GraphicsList, listAfter);

            drawingCanvas.RefreshClip();
        }

        // Replace objects in graphicsList with objects from clone list
        private static void ReplaceObjects(VisualCollection graphicsList, List<PropertiesGraphicsBase> list)
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

        // Fill list from selection
        private static void FillList(VisualCollection graphicsList, ref List<PropertiesGraphicsBase> listToFill)
        {
            listToFill = new List<PropertiesGraphicsBase>();

            foreach (GraphicsBase g in graphicsList)
            {
                if ( g.IsSelected )
                {
                    listToFill.Add(g.CreateSerializedObject());
                }
            }
        }
    }
}
