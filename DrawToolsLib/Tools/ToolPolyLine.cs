using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;
using System.Windows.Media.Imaging;
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
            newPolyLine.IsSelected = false;

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.PushTransform(new TranslateTransform(-newPolyLine.Bounds.X, -newPolyLine.Bounds.Y));
                newPolyLine.Draw(context);
            }

            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
            var bitmap = new RenderTargetBitmap((int)newPolyLine.Bounds.Width, (int)newPolyLine.Bounds.Height, 96, 96, PixelFormats.Default);
            bitmap.Render(visual);

            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            File.Delete(path);
            using (var fs = new FileStream(path, FileMode.Create))
                encoder.Save(fs);

            drawingCanvas.GraphicsList.RemoveAt(drawingCanvas.GraphicsList.Count - 1);
            drawingCanvas.AddGraphic(new GraphicsImage(drawingCanvas, newPolyLine.Bounds, path) { IsSelected = true });

            newPolyLine = null;
            base.OnMouseUp(drawingCanvas, e);
        }
    }
}
