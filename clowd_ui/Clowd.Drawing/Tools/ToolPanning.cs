using Avalonia;
using Avalonia.Input;

namespace Clowd.Drawing.Tools
{
    internal class ToolPanning : ToolBase
    {
        // system SizeAll cursor (not a .cur resource) — per §2.3
        private static readonly Cursor _sizeAllCursor = new Cursor(StandardCursorType.SizeAll);

        private PixelPoint _panStart;

        public ToolPanning() : base(() => _sizeAllCursor, SnapMode.None)
        {
        }

        public override void OnMouseDown(DrawingCanvas canvas, PointerState s, int clickCount)
        {
            canvas.IsPanning = true;
            _panStart = canvas.PointToScreen(s.Position);
            canvas.CaptureMouse(s.Pointer);
        }

        public override void OnMouseMove(DrawingCanvas canvas, PointerState s)
        {
            // Synthetic Shift replays (Pointer == null) carry the canvas-local point cached at the
            // last real move. Panning has moved the canvas transform under that point since, so
            // PointToScreen now resolves it to a different screen pixel and the pan would step even
            // though the pointer never moved — and Shift is itself a pan key, so its auto-repeat
            // used to drive the view continuously while the mouse sat still.
            if (s.Pointer == null)
                return;

            if (canvas.IsPanning)
            {
                // deltas must be tracked in screen pixels: canvas-relative positions shift whenever
                // ContentOffset moves the canvas under the pointer, which feeds back into the next
                // delta and makes the pan oscillate.
                var pos = canvas.PointToScreen(s.Position);
                var dpiZoom = canvas.DpiZoom;
                canvas.ContentOffset = new Point(
                    canvas.ContentOffset.X + ((pos.X - _panStart.X) / dpiZoom),
                    canvas.ContentOffset.Y + ((pos.Y - _panStart.Y) / dpiZoom));
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
