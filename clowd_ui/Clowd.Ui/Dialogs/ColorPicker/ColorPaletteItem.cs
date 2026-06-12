using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

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

    public partial class ColorPaletteItem : Control
    {
        public static readonly StyledProperty<Color> ColorProperty =
            AvaloniaProperty.Register<ColorPaletteItem, Color>(nameof(Color));

        public Color Color
        {
            get => GetValue(ColorProperty);
            set => SetValue(ColorProperty, value);
        }

        public static readonly StyledProperty<bool> IsSelectedProperty =
            AvaloniaProperty.Register<ColorPaletteItem, bool>(nameof(IsSelected));

        public bool IsSelected
        {
            get => GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        public event EventHandler<ColorSelectedEventArgs> Clicked;

        const double _penThicknes = 1;
        Pen _blackPen = new Pen(Brushes.Black, _penThicknes);
        Pen _whitePen = new Pen(Brushes.White, _penThicknes);

        static ColorPaletteItem()
        {
            AffectsRender<ColorPaletteItem>(ColorProperty, IsSelectedProperty);
        }

        public ColorPaletteItem()
        {
        }

        public ColorPaletteItem(Color color)
        {
            this.Color = color;
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            base.OnPointerEntered(e);
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Clicked?.Invoke(this, new ColorSelectedEventArgs(this.Color, e.ClickCount));
        }

        public override void Render(DrawingContext drawingContext)
        {
            var width = Bounds.Width;
            var height = Bounds.Height;

            drawingContext.DrawRectangle(new SolidColorBrush(Color), null, new Rect(0, 0, width, height));

            if (IsPointerOver)
            {
                drawingContext.DrawRectangle(null, _blackPen, new Rect(0.5, 0.5, width - 1, height - 1));
                drawingContext.DrawRectangle(null, _whitePen, new Rect(1.5, 1.5, width - 3, height - 3));
            }
            else if (IsSelected)
            {
                drawingContext.DrawRectangle(null, _whitePen, new Rect(0.5, 0.5, width - 1, height - 1));
                drawingContext.DrawRectangle(null, _blackPen, new Rect(1.5, 1.5, width - 3, height - 3));
            }
        }
    }
}
