using System.Globalization;
using System.Linq;
using Avalonia;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    /// <summary>
    /// Places the next numbered step badge centered on the press point; dragging before release
    /// pulls its arrow out to the pointer (a release inside the badge leaves it arrowless). The
    /// number then opens for editing, like a new text note.
    /// </summary>
    internal class ToolCount : ToolText
    {
        private GraphicCount _current;

        public ToolCount() : base(() => CursorResources.Numerical, SnapMode.All)
        { }

        protected override void OnMouseDownImpl(DrawingCanvas canvas, Point pt)
        {
            var maxNum = canvas.GraphicsList
                .OfType<GraphicCount>()
                .Select(g => int.TryParse(g.Body, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max();

            _current = new GraphicCount(canvas, pt, (maxNum + 1).ToString(CultureInfo.InvariantCulture));
            canvas.GraphicsList.Add(_current);
        }

        protected override void OnMouseMoveImpl(DrawingCanvas canvas, Point pt)
        {
            _current?.MoveHandleTo(pt, GraphicCount.ArrowHandle);
        }

        protected override void OnMouseUpImpl(DrawingCanvas canvas)
        {
            if (_current != null)
            {
                // CreateTextBox adds command history etc.
                CreateTextBox(_current, canvas, true);
            }

            _current = null;
        }

        public override void AbortOperation(DrawingCanvas canvas)
        {
            if (_current != null)
            {
                canvas.GraphicsList.Remove(_current);
                _current = null;
            }

            base.AbortOperation(canvas);
        }
    }
}
