using System;
using System.Collections.Generic;
using Avalonia;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    /// <summary>
    /// The eraser (draw on screen only): the pointer tool's hover and marquee, but what it lands
    /// on is deleted rather than selected. Hovering outlines the graphic a click would remove; a
    /// drag draws the same marquee as the pointer and outlines every graphic it fully contains;
    /// the release deletes them as one history step. It reuses <see cref="ToolPointer.MakeHitTest"/>
    /// and <see cref="ToolPointer.GraphicsInMarquee"/>, so what it hits is exactly what the pointer
    /// would select. Nothing is ever selected, and the tool stays active after the release in
    /// every mode (there is no pointer to revert to on the overlay).
    /// </summary>
    internal sealed class ToolEraser : ToolBase
    {
        /// <summary>A press that moves less than this (screen px) is a click, not a marquee.</summary>
        internal const double DragThreshold = 3;

        private Point _downPt;
        private GraphicBase _downHit;
        private bool _dragging;

        // the marquee is kept by reference: the list's last item is not reliable while the
        // collection is being mutated under us
        private GraphicSelectionRectangle _marquee;

        public ToolEraser() : base(() => CursorResources.Eraser, SnapMode.None)
        { }

        public override void SetCursor(DrawingCanvas canvas)
        {
            canvas.Cursor = CursorFn();
            // whatever the previous tool left selected would otherwise keep its handles while the
            // eraser hovers around it
            canvas.UnselectAll();
        }

        public override void OnMouseDown(DrawingCanvas canvas, PointerState s, int clickCount)
        {
            if (!s.LeftPressed)
                return;

            if (_marquee != null)
                AbortOperation(canvas); // a release that never arrived

            canvas.SetHoveredGraphic(null); // the hover cue is for the mouse-up state only
            canvas.UnselectAll();
            canvas.CaptureMouse(s.Pointer);

            _downPt = s.Position;
            _downHit = canvas.ToolPointer.MakeHitTest(canvas, _downPt, out _);

            var rect = HelperFunctions.CreateRectSafe(_downPt.X, _downPt.Y, _downPt.X + 1, _downPt.Y + 1);
            _marquee = new GraphicSelectionRectangle(rect);
            canvas.GraphicsList.Add(_marquee);
            _dragging = false;
        }

        public override void OnMouseMove(DrawingCanvas canvas, PointerState s)
        {
            // Exclude all cases except left button on/off.
            if (s.MiddlePressed || s.RightPressed)
            {
                canvas.SetHoveredGraphic(null);
                return;
            }

            var pt = s.Position;

            // left button up: only the hover outline — no Move or resize cursors, the eraser
            // never edits what it is over
            if (!s.LeftPressed)
            {
                canvas.SetHoveredGraphic(canvas.ToolPointer.MakeHitTest(canvas, pt, out _));
                return;
            }

            if (_marquee == null)
                return;

            if (!_dragging)
            {
                var dx = pt.X - _downPt.X;
                var dy = pt.Y - _downPt.Y;
                if (Math.Sqrt(dx * dx + dy * dy) > DragThreshold * canvas.CanvasUiElementScale.DpiScaleX)
                    _dragging = true;
            }

            if (_dragging)
            {
                _marquee.MoveHandleTo(pt, 5);
                canvas.SetHoveredGraphics(ToolPointer.GraphicsInMarquee(canvas.GraphicsList, MarqueeBounds()));
            }
        }

        // fully overridden: the base would replay the move, require a capture, and end the gesture
        // by reverting to the pointer
        public override void OnMouseUp(DrawingCanvas canvas, PointerState s)
        {
            if (_marquee == null)
            {
                AbortOperation(canvas);
                return;
            }

            Rect? marquee = _dragging ? MarqueeBounds() : null;
            canvas.GraphicsList.Remove(_marquee);
            _marquee = null;
            canvas.SetHoveredGraphics(null);

            var targets = ResolveTargets(canvas, _downHit, marquee);
            _downHit = null;
            _dragging = false;

            canvas.ReleaseMouseCapture();
            canvas.DeleteGraphics(targets); // one step, or none

            // re-hover whatever is under the pointer now that the targets are gone
            canvas.SetHoveredGraphic(canvas.ToolPointer.MakeHitTest(canvas, s.Position, out _));
        }

        /// <summary>
        /// What a release deletes: the graphics a marquee fully contains, or else the graphic the
        /// press landed on (if it is still in the list), or nothing.
        /// </summary>
        internal static IReadOnlyList<GraphicBase> ResolveTargets(DrawingCanvas canvas, GraphicBase downHit, Rect? marquee)
        {
            if (marquee is { } rect)
                return ToolPointer.GraphicsInMarquee(canvas.GraphicsList, rect);

            if (downHit != null && canvas.GraphicsList.Contains(downHit))
                return new[] { downHit };

            return Array.Empty<GraphicBase>();
        }

        public override void AbortOperation(DrawingCanvas canvas)
        {
            if (_marquee != null)
            {
                canvas.GraphicsList.Remove(_marquee); // a no-op if the list was already cleared
                _marquee = null;
            }

            canvas.SetHoveredGraphics(null);
            _downHit = null;
            _dragging = false;
        }

        // the marquee is dragged by its bottom-right handle, so it is inverted whenever the pointer
        // is above or left of the press; the rule needs it the right way round
        private Rect MarqueeBounds() =>
            HelperFunctions.CreateRectSafe(_marquee.Left, _marquee.Top, _marquee.Right, _marquee.Bottom);
    }
}
