using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DrawToolsLib.Graphics;

namespace DrawToolsLib.Tools
{
    internal class ToolDraggable<T> : ToolBase
        where T : GraphicBase
    {
        private readonly Func<Point, T> _create;
        private readonly Action<Point, T> _update;
        private readonly Action<T> _end;

        internal T _instance;
        internal Point _start;

        public ToolDraggable(Cursor cursor, Func<Point, T> create, Action<Point, T> update)
            : this(cursor, create, update, null)
        {
        }

        public ToolDraggable(Cursor cursor, Func<Point, T> create, Action<Point, T> update, Action<T> end)
            : base(cursor)
        {
            _create = create;
            _update = update;
            _end = end;
        }

        public override void OnMouseDown(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            base.OnMouseDown(canvas, e);
            Point point = e.GetPosition(canvas);
            _instance = _create(point);
            _instance.IsSelected = true;
            canvas.GraphicsList.Add(_instance);
        }

        public override void OnMouseMove(DrawingCanvas canvas, MouseEventArgs e)
        {
            base.OnMouseMove(canvas, e);
            Point point = e.GetPosition(canvas);
            if (_instance != null)
                _update?.Invoke(point, _instance);
        }

        public override void OnMouseUp(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            base.OnMouseUp(canvas, e);
            Point point = e.GetPosition(canvas);
            if (_instance != null)
            {
                _update?.Invoke(point, _instance);
                _end?.Invoke(_instance);
                _instance.Normalize();
                canvas.AddCommandToHistory(new CommandAdd(_instance));
                _instance = null;
            }
        }
    }
}
