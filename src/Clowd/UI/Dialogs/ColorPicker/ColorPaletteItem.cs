using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clowd.Util;

namespace Clowd.UI.Dialogs.ColorPicker
{
    public class ColorSelectedEventArgs : EventArgs
    {
        public Color SelectedColor { get; set; }
        public int ClickCount { get; }

        public ColorSelectedEventArgs(Color selectedColor, int clickCount)
        {
            SelectedColor = selectedColor;
            ClickCount = clickCount;
        }
    }

    public class ColorPaletteItem : Control
    {
        public event EventHandler<ColorSelectedEventArgs> Clicked;

        const double _penThicknes = 1;
        private readonly Color _color;
        Pen _blackPen = new Pen(Brushes.Black, _penThicknes);
        Pen _whitePen = new Pen(Brushes.White, _penThicknes);

        public ColorPaletteItem(Color color)
        {
            _color = color;
            IsTabStop = false;
            Background = new SolidColorBrush(_color);
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            InvalidateVisual();
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            Clicked?.Invoke(this, new ColorSelectedEventArgs(_color, e.ClickCount));
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            drawingContext.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));

            if (IsMouseOver)
            {
                drawingContext.DrawRectangle(null, _blackPen, new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1));
                drawingContext.DrawRectangle(null, _whitePen, new Rect(1.5, 1.5, ActualWidth - 3, ActualHeight - 3));
            }
        }
    }
}
