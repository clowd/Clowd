using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DrawToolsLib.Tools
{
    internal abstract class ToolBase
    {
        protected readonly Cursor Cursor;

        internal abstract ToolActionType ActionType { get; }

        protected ToolBase(Cursor cursor)
        {
            Cursor = cursor;
        }

        public virtual void OnMouseDown(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            canvas.CaptureMouse();
            canvas.UnselectAll();
        }

        public virtual void OnMouseUp(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            canvas.Tool = ToolType.Pointer;
            canvas.Cursor = HelperFunctions.DefaultCursor;
            canvas.ReleaseMouseCapture();
        }

        public virtual void OnMouseMove(DrawingCanvas canvas, MouseEventArgs e)
        {

        }

        public virtual void SetCursor(DrawingCanvas canvas)
        {
            canvas.Cursor = Cursor;
        }
    }
}
