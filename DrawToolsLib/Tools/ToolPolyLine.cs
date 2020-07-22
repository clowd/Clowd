using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;
using System.Windows.Media.Imaging;
using DrawToolsLib.Graphics;

namespace DrawToolsLib.Tools
{
    internal class ToolPolyLine : ToolBase
    {
        private GraphicPolyLine newPolyLine;

        public ToolPolyLine() : base(new Cursor(new MemoryStream(Properties.Resources.Pencil)))
        {
        }

        public override void OnMouseDown(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            base.OnMouseDown(drawingCanvas, e);

            Point p = e.GetPosition(drawingCanvas);
            newPolyLine = new GraphicPolyLine(drawingCanvas, new Point(p.X + 1, p.Y + 1));
            drawingCanvas.GraphicsList.Add(newPolyLine);
        }

        public override void OnMouseMove(DrawingCanvas drawingCanvas, MouseEventArgs e)
        {
            if (newPolyLine == null || e.LeftButton != MouseButtonState.Pressed || !drawingCanvas.IsMouseCaptured)
                return;

            Point p = e.GetPosition(drawingCanvas);
            newPolyLine.AddPoint(p);
        }

        public override void OnMouseUp(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            base.OnMouseUp(drawingCanvas, e);

            Point p = e.GetPosition(drawingCanvas);

            newPolyLine.AddPoint(p);
            newPolyLine.IsSelected = true;
            newPolyLine.FinishDrawing();
            newPolyLine.Normalize();

            drawingCanvas.AddCommandToHistory(new CommandAdd(newPolyLine));
            newPolyLine = null;
        }
    }
}
