using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DrawToolsLib.Graphics;

namespace DrawToolsLib
{
    /// <summary>
    /// Base class for all tools which create new graphic object
    /// </summary>
    abstract class ToolObject : Tool
    {
        /// <summary>
        /// Tool cursor.
        /// </summary>
        protected Cursor ToolCursor { get; set; }


        /// <summary>
        /// Left mouse is released.
        /// New object is created and resized.
        /// </summary>
        public override void OnMouseUp(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            if (drawingCanvas.Count > 0)
            {
                drawingCanvas[drawingCanvas.Count - 1].Normalize();
                drawingCanvas.AddCommandToHistory(new CommandAdd(drawingCanvas[drawingCanvas.Count - 1]));
            }

            drawingCanvas.Tool = ToolType.Pointer;
            drawingCanvas.Cursor = HelperFunctions.DefaultCursor;
            drawingCanvas.ReleaseMouseCapture();
        }

        /// <summary>
        /// Add new object to drawing canvas.
        /// Function is called when user left-clicks drawing canvas,
        /// and one of ToolObject-derived tools is active.
        /// </summary>
        protected static void AddNewObject(DrawingCanvas drawingCanvas, GraphicBase o)
        {
            drawingCanvas.UnselectAll();
            o.IsSelected = true;
            drawingCanvas.GraphicsList.Add(o);
            drawingCanvas.CaptureMouse();
        }

        /// <summary>
        /// Set cursor
        /// </summary>
        public override void SetCursor(DrawingCanvas drawingCanvas)
        {
            drawingCanvas.Cursor = this.ToolCursor;
        }

    }
}
