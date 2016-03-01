using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;


namespace DrawToolsLib
{
    /// <summary>
    /// Polyline tool
    /// </summary>
    class ToolPolyLine : ToolObject
    {
        private double lastX;
        private double lastY;
        private GraphicsPolyLine newPolyLine;
        private const double minDistance = 15;


        public ToolPolyLine()
        {
            MemoryStream stream = new MemoryStream(Properties.Resources.Pencil);
            ToolCursor = new Cursor(stream);
        }

        /// <summary>
        /// Create new object
        /// </summary>
        public override void OnMouseDown(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(drawingCanvas);

            newPolyLine = new GraphicsPolyLine(
                new Point[]
                {
                    p,
                    new Point(p.X + 1, p.Y + 1)
                },
                drawingCanvas.LineWidth,
                drawingCanvas.ObjectColor,
                drawingCanvas.ActualScale);

            AddNewObject(drawingCanvas, newPolyLine);

            lastX = p.X;
            lastY = p.Y;
        }

        /// <summary>
        /// Set cursor and resize new polyline
        /// </summary>
        public override void OnMouseMove(DrawingCanvas drawingCanvas, MouseEventArgs e)
        {
            drawingCanvas.Cursor = ToolCursor;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            if ( ! drawingCanvas.IsMouseCaptured )
            {
                return;
            }

            if ( newPolyLine == null )
            {
                return;         // precaution
            }

            Point p = e.GetPosition(drawingCanvas);

            double distance = (p.X - lastX) * (p.X - lastX) + (p.Y - lastY) * (p.Y - lastY);

            double d = drawingCanvas.ActualScale <= 0 ? 
                minDistance * minDistance :
                minDistance * minDistance / drawingCanvas.ActualScale;

            if ( distance < d)
            {
                // Distance between last two points is less than minimum -
                // move last point
                newPolyLine.MoveHandleTo(p, newPolyLine.HandleCount);
            }
            else
            {
                // Add new segment
                newPolyLine.AddPoint(p);

                lastX = p.X;
                lastY = p.Y;
            }
        }

        public override void OnMouseUp(DrawingCanvas drawingCanvas, MouseButtonEventArgs e)
        {
            newPolyLine = null;

            base.OnMouseUp(drawingCanvas, e);
        }
    }
}
