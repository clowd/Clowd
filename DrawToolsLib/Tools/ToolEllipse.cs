using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;
using DrawToolsLib.Graphics;


namespace DrawToolsLib
{
    internal class ToolEllipse : ToolRectangleBase
    {
        public ToolEllipse()
        {
            MemoryStream stream = new MemoryStream(Properties.Resources.Ellipse);
            ToolCursor = new Cursor(stream);
        }

        public override void OnMouseDown(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            Point point = e.GetPosition(drawingCanvas);
            var rect = HelperFunctions.CreateRectSafe(point.X, point.Y, point.X + 1, point.Y + 1);
            AddNewObject(drawingCanvas, new GraphicsEllipse(drawingCanvas, rect));
        }
    }
}
