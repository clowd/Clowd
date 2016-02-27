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
    /// Add new object command
    /// </summary>
    class CommandAdd : CommandBase
    {
        PropertiesGraphicsBase newObjectClone;

        // Create this command with DrawObject instance added to the list
        public CommandAdd(GraphicsBase newObject)
            : base()
        {
            // Keep copy of added object
            this.newObjectClone = newObject.CreateSerializedObject();
        }

        /// <summary>
        /// Delete added object
        /// </summary>
        public override void Undo(DrawingCanvas drawingCanvas)
        {
            // Find object to delete by its ID.
            // Don't use objects order in the list.


            GraphicsBase objectToDelete = null;

            // Remove object from the list
            foreach(GraphicsBase b in drawingCanvas.GraphicsList)
            {
                if ( b.Id == newObjectClone.ID )
                {
                    objectToDelete = b;
                    break;
                }
            }

            if ( objectToDelete != null )
            {
                drawingCanvas.GraphicsList.Remove(objectToDelete);
            }
        }

        /// <summary>
        /// Add object again
        /// </summary>
        public override void Redo(DrawingCanvas drawingCanvas)
        {
            HelperFunctions.UnselectAll(drawingCanvas);

            // Create full object from the clone and add it to list
            drawingCanvas.GraphicsList.Add(newObjectClone.CreateGraphics());

            // Object created from the clone doesn't contain clip information,
            // refresh it.
            drawingCanvas.RefreshClip();
        }
    }
}
