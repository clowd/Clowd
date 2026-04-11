using Avalonia;
using Avalonia.Input;

namespace Clowd.Drawing.Tools
{
    internal class ToolPanning : ToolBase
    {
        private Point _panStartScreen;
        private bool _panning;

        public ToolPanning() : base(() => new Cursor(StandardCursorType.SizeAll), SnapMode.None)
        {
        }

        public override void OnPointerPressed(DrawingCanvas canvas, PointerPressedEventArgs e)
        {
            var props = e.GetCurrentPoint(canvas).Properties;
            if (!props.IsLeftButtonPressed)
                return;

            _panning = true;
            _panStartScreen = e.GetPosition(canvas);
            canvas.CaptureMouse(e.Pointer);
        }

        public override void OnPointerMoved(DrawingCanvas canvas, PointerEventArgs e)
        {
            if (_panning)
            {
                var pt = e.GetPosition(canvas);
                var delta = pt - _panStartScreen;
                canvas.PanByScreenDelta(delta.X, delta.Y);
                _panStartScreen = pt;
            }
        }

        public override void OnPointerReleased(DrawingCanvas canvas, PointerReleasedEventArgs e)
        {
            AbortOperation(canvas);
        }

        public override void AbortOperation(DrawingCanvas canvas)
        {
            _panning = false;
            if (canvas.IsMouseCaptured)
                canvas.ReleaseMouseCapture();
        }
    }
}
