using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DrawToolsLib.Filters;
using DrawToolsLib.Graphics;

namespace DrawToolsLib.Tools
{
    internal class ToolFilter<T> : ToolObjectNew
        where T : FilterBase
    {
        private FilterBase _filter;
        private DrawingBrush _brush;
        private CommandChangeState _state;

        public ToolFilter(Cursor cursor) : base(cursor)
        {
            _brush = new DrawingBrush()
            {
                Color = Colors.White,
                Hardness = 0.7,
                Radius = 16
            };
        }

        public override void OnMouseDown(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            base.OnMouseDown(canvas, e);
            var point = e.GetPosition(canvas);
            _state = new CommandChangeState(canvas);
            Start(canvas, point);
        }
        public override void OnMouseMove(DrawingCanvas canvas, MouseEventArgs e)
        {
            base.OnMouseMove(canvas, e);
            var point = e.GetPosition(canvas);
            Move(canvas, point);
        }
        public override void OnMouseUp(DrawingCanvas canvas, MouseButtonEventArgs e)
        {
            base.OnMouseUp(canvas, e);
            End();
            if (_state != null)
            {
                _state.NewState(canvas);
                canvas.AddCommandToHistory(_state);
            }
        }

        private void Start(DrawingCanvas canvas, Point point)
        {
            if (_filter != null)
                End();

            int handleNum;
            var clicked = canvas.ToolPointer.MakeHitTest(canvas, point, out handleNum);
            if (clicked is GraphicImage)
            {
                var gImg = clicked as GraphicImage;
                gImg.Flatten();
                _filter = CreateFilter(canvas, gImg);
                _filter.Handle(_brush, point);
            }
        }

        private void Move(DrawingCanvas canvas, Point point)
        {
            if (!canvas.IsMouseCaptured)
                return;

            int handleNum;
            var clicked = canvas.ToolPointer.MakeHitTest(canvas, point, out handleNum);
            if (clicked is GraphicImage)
            {
                if (_filter == null)
                {
                    Start(canvas, point);
                }
                if (_filter.Source != clicked)
                {
                    End();
                    Start(canvas, point);
                }
                else
                {
                    _filter.Handle(_brush, point);
                }
            }
        }

        private void End()
        {
            _filter?.Close();
            _filter = null;
        }

        private FilterBase CreateFilter(DrawingCanvas canvas, GraphicImage image)
        {
            return (FilterBase)Activator.CreateInstance(typeof(T), new object[] { canvas, image });
        }
    }
}
