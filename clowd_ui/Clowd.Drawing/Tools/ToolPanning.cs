using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Clowd.Drawing.Tools
{
    internal class ToolPanning : ToolBase
    {
        // system SizeAll cursor (not a .cur resource) — per §2.3
        private static readonly Cursor _sizeAllCursor = new Cursor(StandardCursorType.SizeAll);

        private Point _panStartRoot;

        public ToolPanning() : base(() => _sizeAllCursor, SnapMode.None)
        {
        }

        private static Point CanvasToRoot(DrawingCanvas canvas, Point canvasPt) =>
            canvas.TranslatePoint(canvasPt, (Visual)TopLevel.GetTopLevel(canvas) ?? canvas) ?? canvasPt;

        public override void OnMouseDown(DrawingCanvas canvas, PointerState s, int clickCount)
        {
            canvas.IsPanning = true;
            _panStartRoot = CanvasToRoot(canvas, s.Position);
            canvas.CaptureMouse(s.Pointer);
        }

        public override void OnMouseMove(DrawingCanvas canvas, PointerState s)
        {
            // Synthetic Shift replays (Pointer == null) carry the canvas-local point cached at the
            // last real move, resolved through the transform as it is now, so a replay is a
            // zero-delta step. Bail out anyway: when no root point was cached the replay falls back
            // to a stale canvas-local point, which would resolve to a different root position and
            // step the pan even though the pointer never moved — and Shift is itself a pan key, so
            // its auto-repeat would drive the view continuously while the mouse sat still.
            if (s.Pointer == null)
                return;

            if (canvas.IsPanning)
            {
                // Deltas are tracked in root-window space, in DOUBLE precision, the same as
                // ToolPointer's drag bookkeeping. Root space is immune to the canvas transform
                // moving under the pointer mid-pan (canvas-local positions shift as ContentOffset
                // moves, which feeds back into the next delta and makes the pan oscillate), and it
                // is already in the DIP units ContentOffset uses — no DPI conversion, so no factor
                // to get wrong. The previous screen-space scheme divided by RenderScaling to undo
                // the scaling PointToScreen applies, but that only holds on Windows: the macOS
                // backend converts through AppKit, whose screen coordinates are logical points with
                // no scaling applied, so on a Retina display the pan ran at half the cursor's speed.
                // Screen space also rounds to whole pixels on every event, quantizing slow drags.
                var pos = CanvasToRoot(canvas, s.Position);
                canvas.ContentOffset = new Point(
                    canvas.ContentOffset.X + (pos.X - _panStartRoot.X),
                    canvas.ContentOffset.Y + (pos.Y - _panStartRoot.Y));
                _panStartRoot = pos;
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
