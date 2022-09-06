using System;
using System.Windows;
using System.Windows.Input;
using Clowd.Drawing.Graphics;
using RT.Util.Geometry;

namespace Clowd.Drawing.Tools
{
    enum DraggableSnapMode
    {
        None = 0,
        Diagonal = 1,
        All = 2,
    }

    internal class ToolDraggable<T> : ToolBase where T : GraphicBase
    {
        private readonly Func<Point, T> _create;
        private readonly Action<Point, T> _update;
        private readonly Action<T> _end;
        private readonly DraggableSnapMode _snapMode;

        private T _instance;
        private Point _start;

        public ToolDraggable(Cursor cursor, Func<Point, T> create, Action<Point, T> update)
            : this(cursor, create, update, null)
        { }

        public ToolDraggable(Cursor cursor, Func<Point, T> create, Action<Point, T> update, Action<T> end = null, DraggableSnapMode snapMode = DraggableSnapMode.None)
            : base(cursor)
        {
            _create = create;
            _update = update;
            _end = end;
            _snapMode = snapMode;
        }

        internal override ToolActionType ActionType => ToolActionType.Object;

        public override void OnMouseDown(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            base.OnMouseDown(canvas, e);
            Point point = e.GetPosition(canvas);
            _start = point;
            _instance = _create(point);
            _instance.IsSelected = true;
            canvas.GraphicsList.Add(_instance);
        }

        private Point GetPoint(DrawingCanvas canvas, MouseEventArgs e)
        {
            // snap the point to a 45deg angle (maybe)
            if (_snapMode != DraggableSnapMode.None && (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.LeftShift)))
            {
                return HelperFunctions.SnapPointToCommonAngle(_start, e.GetPosition(canvas), _snapMode == DraggableSnapMode.Diagonal);
            }
            
            return e.GetPosition(canvas);
        }

        public override void OnMouseMove(DrawingCanvas canvas, MouseEventArgs e)
        {
            base.OnMouseMove(canvas, e);
            if (_instance != null)
            {
                Point point = GetPoint(canvas, e);
                _update?.Invoke(point, _instance);
            }
        }

        public override void OnMouseUp(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            base.OnMouseUp(canvas, e);
            if (_instance != null)
            {
                Point point = GetPoint(canvas, e);
                _update?.Invoke(point, _instance);
                _end?.Invoke(_instance);
                _instance.Normalize();
                canvas.AddCommandToHistory();
                _instance = null;
            }
        }
    }
}
