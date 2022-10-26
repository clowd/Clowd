using System;
using System.Windows;
using System.Windows.Input;

namespace Clowd.Drawing.Tools
{
    internal class ToolPanning : ToolBase
    {
        private Point _panStart;

        public ToolPanning() : base(() => Cursors.SizeAll, SnapMode.None)
        {
        }

        public override void OnMouseDown(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            canvas.IsPanning = true;
            _panStart = e.GetPosition(canvas);
            canvas.CaptureMouse();
        }

        public override void OnMouseMove(DrawingCanvas canvas, MouseEventArgs e)
        {
            if (canvas.IsPanning)
            {
                canvas.ContentOffset += (e.GetPosition(canvas) - _panStart) * canvas.ContentScale;
                _panStart = e.GetPosition(canvas);
            }
        }

        public override void OnMouseUp(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            AbortOperation(canvas);
        }

        public override void AbortOperation(DrawingCanvas canvas)
        {
            canvas.IsPanning = false;
            canvas.ReleaseMouseCapture();
        }
    }
}
