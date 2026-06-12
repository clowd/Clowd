using Avalonia;
using Avalonia.Input;

namespace Clowd.Drawing.Tools
{
    internal class ToolPanning : ToolBase
    {
        // system SizeAll cursor (not a .cur resource) — per §2.3
        private static readonly Cursor _sizeAllCursor = new Cursor(StandardCursorType.SizeAll);

        private Point _panStart;

        public ToolPanning() : base(() => _sizeAllCursor, SnapMode.None)
        {
        }

        public override void OnMouseDown(DrawingCanvas canvas, PointerState s, int clickCount)
        {
            canvas.IsPanning = true;
            _panStart = s.Position;
            canvas.CaptureMouse(s.Pointer);
        }

        public override void OnMouseMove(DrawingCanvas canvas, PointerState s)
        {
            if (canvas.IsPanning)
            {
                var pos = s.Position;
                canvas.ContentOffset = new Point(
                    canvas.ContentOffset.X + ((pos.X - _panStart.X) * canvas.ContentScale),
                    canvas.ContentOffset.Y + ((pos.Y - _panStart.Y) * canvas.ContentScale));
                _panStart = pos;
            }
        }

        public override void OnMouseUp(DrawingCanvas canvas, PointerState s)
        {
            // unlike other tools, panning does NOT revert to ToolType.Pointer on mouse-up
            AbortOperation(canvas);
        }

        public override void AbortOperation(DrawingCanvas canvas)
        {
            canvas.IsPanning = false;
            canvas.ReleaseMouseCapture();
        }
    }
}
