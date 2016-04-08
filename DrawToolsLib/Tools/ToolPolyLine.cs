using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;
using DrawToolsLib.Graphics;


namespace DrawToolsLib
{
    internal class ToolPolyLine : ToolObject
    {
        private GraphicsPolyLine newPolyLine;

        public ToolPolyLine()
        {
            MemoryStream stream = new MemoryStream(Properties.Resources.Pencil);
            ToolCursor = new Cursor(stream);
        }

        public override void OnMouseDown(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(drawingCanvas);

            newPolyLine = new GraphicsPolyLine(drawingCanvas, new Point(p.X + 1, p.Y + 1));

            AddNewObject(drawingCanvas, newPolyLine);
        }

        public override void OnMouseMove(DrawingCanvas drawingCanvas, MouseEventArgs e)
        {
            if (newPolyLine == null || e.LeftButton != MouseButtonState.Pressed || !drawingCanvas.IsMouseCaptured)
                return;

            drawingCanvas.Cursor = ToolCursor;

            Point p = e.GetPosition(drawingCanvas);

            newPolyLine.AddPoint(p);
        }

        public override void OnMouseUp(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            newPolyLine.FinishDrawing();
            newPolyLine = null;
            base.OnMouseUp(drawingCanvas, e);
        }
    }
}
