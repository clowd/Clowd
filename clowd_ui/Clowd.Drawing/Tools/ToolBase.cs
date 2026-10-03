using System;
using Avalonia;
using Avalonia.Input;

namespace Clowd.Drawing.Tools
{
    internal enum SnapMode
    {
        None = 0,
        Diagonal = 1,
        All = 2,
    }

    /// <summary>
    /// Carries everything tools need from a pointer event. DrawingCanvas caches the last PointerState so
    /// Shift up/down can replay a synthetic move (replaces WPF's synthesized MouseMove); DrawingCanvas
    /// updates Modifiers on key events. Pointer and Args are null for synthetic replays (capture state
    /// unchanged then, and no new input sample to read). Timestamp is the event's millisecond clock.
    /// </summary>
    internal readonly record struct PointerState(
        Point Position,
        KeyModifiers Modifiers,
        bool LeftPressed,
        bool MiddlePressed,
        bool RightPressed,
        IPointer Pointer,
        ulong Timestamp,
        PointerEventArgs Args)
    {
        public static PointerState From(PointerEventArgs e, Visual relativeTo)
        {
            var pp = e.GetCurrentPoint(relativeTo);
            return new PointerState(
                pp.Position,
                e.KeyModifiers,
                pp.Properties.IsLeftButtonPressed,
                pp.Properties.IsMiddleButtonPressed,
                pp.Properties.IsRightButtonPressed,
                e.Pointer,
                e.Timestamp,
                e);
        }
    }

    internal abstract class ToolBase
    {
        protected Point LastMouseDownPt;
        protected Point LastMouseMovePt;

        protected readonly Func<Cursor> CursorFn;
        private readonly SnapMode _snapMode;

        protected ToolBase(Func<Cursor> cursorFn, SnapMode snapMode = SnapMode.None)
        {
            CursorFn = cursorFn;
            _snapMode = snapMode;
        }

        public virtual void OnMouseDown(DrawingCanvas canvas, PointerState s, int clickCount)
        {
            if (!s.LeftPressed)
                return;

            canvas.CaptureMouse(s.Pointer);
            canvas.UnselectAll();

            var pt = s.Position;
            LastMouseDownPt = pt;
            OnMouseDownImpl(canvas, pt);
        }

        public virtual void OnMouseMove(DrawingCanvas canvas, PointerState s)
        {
            if (canvas.IsMouseCaptured)
            {
                var pt = s.Position;

                // snap the point to a 45deg angle (maybe).
                // decision #9: both shifts snap (KeyModifiers.Shift); the WPF left-shift-only bug is fixed deliberately.
                if (_snapMode != SnapMode.None && (s.Modifiers & KeyModifiers.Shift) != 0)
                {
                    pt = HelperFunctions.SnapPointToCommonAngle(LastMouseDownPt, pt, _snapMode == SnapMode.Diagonal);
                }

                LastMouseMovePt = pt;
                OnMouseMoveImpl(canvas, pt);
            }
        }

        public virtual void OnMouseUp(DrawingCanvas canvas, PointerState s)
        {
            if (canvas.IsMouseCaptured)
            {
                OnMouseMove(canvas, s);
                canvas.ReleaseMouseCapture();
                OnMouseUpImpl(canvas);
            }
            else
            {
                AbortOperation(canvas);
            }

            canvas.Tool = ToolType.Pointer;
            canvas.Cursor = HelperFunctions.DefaultCursor;
        }

        protected virtual void OnMouseDownImpl(DrawingCanvas canvas, Point pt)
        { }

        protected virtual void OnMouseMoveImpl(DrawingCanvas canvas, Point pt)
        { }

        protected virtual void OnMouseUpImpl(DrawingCanvas canvas)
        { }

        public virtual void AbortOperation(DrawingCanvas canvas)
        { }

        /// <summary>Finalizes any multi-gesture edit the tool is holding (a pen path still being
        /// extended) so that history, a tool switch or an undo never sees a half-built graphic.
        /// Called by DrawingCanvas for the outgoing tool in OnToolChanged and before Undo/Redo.</summary>
        public virtual void CommitPending(DrawingCanvas canvas) { }

        /// <summary>A bare key (no modifiers) routed from the editor's tunnel handler. Return true
        /// to swallow it; false lets the editor's own Escape/Delete/tool-letter handling run.</summary>
        public virtual bool OnKeyDown(DrawingCanvas canvas, Key key) => false;

        public virtual void SetCursor(DrawingCanvas canvas)
        {
            canvas.Cursor = CursorFn();
        }
    }
}
