using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Prisc.UI.Dialogs.ColorPicker
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

        public static readonly StyledProperty<double> CornerRadiusProperty =
            AvaloniaProperty.Register<ColorPaletteItem, double>(nameof(CornerRadius));

        /// <summary>Rounds the chip (the color dialog's 4px chips). A rounded chip also draws its
        /// own checkerboard under a translucent color, and its <see cref="EdgeBrush"/> hairline;
        /// the square ones leave both to the panel they sit on, as they always have.</summary>
        public double CornerRadius
        {
            get => GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        public static readonly StyledProperty<IBrush> EdgeBrushProperty =
            AvaloniaProperty.Register<ColorPaletteItem, IBrush>(nameof(EdgeBrush));

        /// <summary>A rounded chip's 1px inner edge, so a chip the color of the surface under it
        /// still has an outline.</summary>
        public IBrush EdgeBrush
        {
            get => GetValue(EdgeBrushProperty);
            set => SetValue(EdgeBrushProperty, value);
        }

        public event EventHandler<ColorSelectedEventArgs> Clicked;

        const double _penThicknes = 1;
        Pen _blackPen = new Pen(Brushes.Black, _penThicknes);
        Pen _whitePen = new Pen(Brushes.White, _penThicknes);

        static ColorPaletteItem()
        {
            AffectsRender<ColorPaletteItem>(ColorProperty, IsSelectedProperty, CornerRadiusProperty, EdgeBrushProperty);
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

            if (CornerRadius > 0)
            {
                RenderRounded(drawingContext, width, height);
                return;
            }

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

        private void RenderRounded(DrawingContext context, double width, double height)
        {
            var radius = CornerRadius;
            var bounds = new Rect(0, 0, width, height);

            if (Color.A < 255)
                context.DrawRectangle(Prisc.Util.CheckerBrushes.Light, null, bounds, radius, radius);
            context.DrawRectangle(new SolidColorBrush(Color), null, bounds, radius, radius);

            // hover and selection: the same white-over-black double edge as the square chips,
            // following the corner
            if (IsPointerOver || IsSelected)
            {
                context.DrawRectangle(null, IsPointerOver ? _blackPen : _whitePen, bounds.Deflate(0.5), radius - 0.5, radius - 0.5);
                context.DrawRectangle(null, IsPointerOver ? _whitePen : _blackPen, bounds.Deflate(1.5), radius - 1.5, radius - 1.5);
            }
            else if (EdgeBrush != null)
            {
                context.DrawRectangle(null, new Pen(EdgeBrush, 1), bounds.Deflate(0.5), radius - 0.5, radius - 0.5);
            }
        }
    }
}
