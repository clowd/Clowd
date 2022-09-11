using System;
using System.Windows;
using System.Windows.Input;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Tools
{
    internal class ToolDraggable<T> : ToolBase where T : GraphicBase
    {
        internal override ToolActionType ActionType => ToolActionType.Object;

        private readonly Func<Point, T> _create;
        private readonly Action<Point, T> _update;
        private readonly Action<T> _end;

        private T _instance;

        public ToolDraggable(Cursor cursor, Func<Point, T> create, Action<Point, T> update, Action<T> end = null, SnapMode snapMode = SnapMode.None)
            : base(cursor, snapMode)
        {
            _create = create;
            _update = update;
            _end = end;
        }

        protected override void OnMouseDownImpl(DrawingCanvas canvas, Point pt)
        {
            _instance = _create(pt);
            _instance.IsSelected = true;
            canvas.GraphicsList.Add(_instance);
        }

        protected override void OnMouseMoveImpl(DrawingCanvas canvas, Point pt)
        {
            if (_instance != null)
            {
                _update?.Invoke(pt, _instance);
            }
        }

        protected override void OnMouseUpImpl(DrawingCanvas canvas)
        {
            if (_instance != null)
            {
                _end?.Invoke(_instance);
                _instance.Normalize();
                canvas.AddCommandToHistory();
                _instance = null;
            }
        }

        public override void AbortOperation(DrawingCanvas canvas)
        {
            if (_instance != null)
            {
                canvas.GraphicsList.Remove(_instance);
                _instance = null;
            }
        }
    }
}
