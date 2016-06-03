using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;
using DrawToolsLib.Graphics;


namespace DrawToolsLib
{
    internal class ToolLine : ToolObject
    {
        public ToolLine()
        {
            MemoryStream stream = new MemoryStream(Properties.Resources.Line);
            ToolCursor = new Cursor(stream);
        }

        public override void OnMouseDown(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(drawingCanvas);

            AddNewObject(drawingCanvas, new GraphicLine(drawingCanvas, p, new Point(p.X + 1, p.Y + 1)));
        }

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
