using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DrawToolsLib.Tools
{
    internal abstract class ToolObjectNew : Tool
    {
        private readonly Cursor _cursor;

        protected ToolObjectNew(Cursor cursor)
        {
            _cursor = cursor;
        }

        public override void OnMouseDown(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            canvas.CaptureMouse();
            canvas.UnselectAll();
        }

        public override void OnMouseUp(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            canvas.Tool = ToolType.Pointer;
            canvas.Cursor = HelperFunctions.DefaultCursor;
            canvas.ReleaseMouseCapture();
        }

        public override void OnMouseMove(DrawingCanvas drawingCanvas, MouseEventArgs e)
        {

        }

        public override void SetCursor(DrawingCanvas canvas)
        {
            canvas.Cursor = _cursor;
        }
    }
}
