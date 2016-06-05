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
            Point p = e.GetPosition(drawingCanvas);

            newPolyLine = new GraphicPolyLine(drawingCanvas, new Point(p.X + 1, p.Y + 1));

            drawingCanvas.UnselectAll();
            newPolyLine.IsSelected = true;
            drawingCanvas.GraphicsList.Add(newPolyLine);
            drawingCanvas.CaptureMouse();
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
            newPolyLine.FinishDrawing();
            newPolyLine.IsSelected = false;

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.PushTransform(new TranslateTransform(-newPolyLine.Bounds.X, -newPolyLine.Bounds.Y));
                newPolyLine.Draw(context);
            }

            var bitmap = new RenderTargetBitmap((int)newPolyLine.Bounds.Width, (int)newPolyLine.Bounds.Height, 96, 96, PixelFormats.Default);
            bitmap.Render(visual);

            drawingCanvas.GraphicsList.RemoveAt(drawingCanvas.GraphicsList.Count - 1);
            drawingCanvas.AddGraphic(new GraphicImage(drawingCanvas, newPolyLine.Bounds, bitmap) { IsSelected = true });

            newPolyLine = null;
            base.OnMouseUp(drawingCanvas, e);
        }
    }
}
