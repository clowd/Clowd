using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// A 1 px rule between two groups of a strip: <see cref="TrayTokens.DividerLength"/> long across the
    /// strip axis (upright in a row, flat in a column), filled with <see cref="TrayTokens.GroupDivider"/>.
    /// Painted in <see cref="Render"/> and centred there, so a parent that stretches the control across the
    /// axis does not stretch the line with it.
    /// </summary>
    public sealed class TrayDivider : Control, ITrayOrientable
    {
        private const double Thickness = 1;

        public static readonly StyledProperty<Orientation> OrientationProperty =
            AvaloniaProperty.Register<TrayDivider, Orientation>(nameof(Orientation), Orientation.Horizontal);

        static TrayDivider()
        {
            AffectsMeasure<TrayDivider>(OrientationProperty);
            AffectsRender<TrayDivider>(OrientationProperty);
            IsHitTestVisibleProperty.OverrideDefaultValue<TrayDivider>(false);
        }

        public Orientation Orientation
        {
            get => GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            return Orientation == Orientation.Horizontal
                ? new Size(Thickness, TrayTokens.DividerLength)
                : new Size(TrayTokens.DividerLength, Thickness);
        }

        public override void Render(DrawingContext context)
        {
            var size = Orientation == Orientation.Horizontal
                ? new Size(Thickness, TrayTokens.DividerLength)
                : new Size(TrayTokens.DividerLength, Thickness);
            var origin = new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2);
            context.FillRectangle(TrayTokens.GroupDivider, new Rect(origin, size));
        }
    }
}
