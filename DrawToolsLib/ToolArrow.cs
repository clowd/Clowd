using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;


namespace DrawToolsLib
{
    /// <summary>
    /// Arrow tool
    /// </summary>
    class ToolArrow : ToolObject
    {
        public ToolArrow()
        {
            MemoryStream stream = new MemoryStream(Properties.Resources.Arrow);
            ToolCursor = new Cursor(stream);
        }

        /// <summary>
        /// Create new object
        /// </summary>
        public override void OnMouseDown(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(drawingCanvas);

            AddNewObject(drawingCanvas,
                new GraphicsArrow(
                p,
                new Point(p.X + 1, p.Y + 1),
                drawingCanvas.LineWidth,
                drawingCanvas.ObjectColor,
                drawingCanvas.ActualScale));

        }

        /// <summary>
        /// Set cursor and resize new object.
        /// </summary>
        public override void OnMouseMove(DrawingCanvas drawingCanvas, MouseEventArgs e)
        {
            drawingCanvas.Cursor = ToolCursor;

            if (e.LeftButton == MouseButtonState.Pressed && drawingCanvas.IsMouseCaptured)
            {
                drawingCanvas[drawingCanvas.Count - 1].MoveHandleTo(
                    e.GetPosition(drawingCanvas), 2);
            }
        }
    }
}
