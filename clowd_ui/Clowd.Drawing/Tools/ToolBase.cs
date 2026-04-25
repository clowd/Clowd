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

    internal abstract class ToolBase
    {
        protected Point LastMouseDownPt { get; set; }
        protected Point LastMouseMovePt { get; set; }

        protected readonly Func<Cursor> CursorFn;
        private readonly SnapMode _snapMode;

        protected ToolBase(Func<Cursor> cursorFn, SnapMode snapMode = SnapMode.None)
        {
            CursorFn = cursorFn;
            _snapMode = snapMode;
        }

        public virtual void OnPointerPressed(DrawingCanvas canvas, PointerPressedEventArgs e)
        {
            var props = e.GetCurrentPoint(canvas).Properties;
            if (!props.IsLeftButtonPressed)
                return;

            canvas.CaptureMouse(e.Pointer);
            canvas.UnselectAll();

            var pt = canvas.ToContentPoint(e.GetPosition(canvas));
            LastMouseDownPt = pt;
            OnMouseDownImpl(canvas, pt);
        }

        public virtual void OnPointerMoved(DrawingCanvas canvas, PointerEventArgs e)
        {
            if (canvas.IsMouseCaptured)
            {
                var pt = canvas.ToContentPoint(e.GetPosition(canvas));

                // snap the point to a 45deg angle (maybe)
                if (_snapMode != SnapMode.None && (e.KeyModifiers & KeyModifiers.Shift) != 0)
                {
                    pt = HelperFunctions.SnapPointToCommonAngle(LastMouseDownPt, pt, _snapMode == SnapMode.Diagonal);
                }

                LastMouseMovePt = pt;
                OnMouseMoveImpl(canvas, pt);
            }
        }

        public virtual void OnPointerReleased(DrawingCanvas canvas, PointerReleasedEventArgs e)
        {
            if (canvas.IsMouseCaptured)
            {
                OnPointerMoved(canvas, e);
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

        public virtual void SetCursor(DrawingCanvas canvas)
        {
            canvas.Cursor = CursorFn();
        }
    }
}
