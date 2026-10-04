using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// A 26 px tile showing one centred dot of <see cref="DotDiameter"/> — a choice whose meaning is a
    /// size, previewed at that size in a given colour. <see cref="IsActive"/> marks the current choice
    /// with a light fill and a 1 px inset edge (TrayDotButton.axaml).
    /// <para>
    /// The fill, edge and corner are the ordinary <see cref="TemplatedControl"/> Background,
    /// BorderBrush, BorderThickness and CornerRadius, written by the theme's state selectors only, and
    /// painted here in <see cref="Render"/>: the control has no template. A dark dot gets the same 1 px
    /// hairline a <see cref="TraySwatch"/> wears, so it never vanishes into the tray.
    /// </para>
    /// </summary>
    public class TrayDotButton : Button
    {
        public static readonly StyledProperty<double> DotDiameterProperty =
            AvaloniaProperty.Register<TrayDotButton, double>(nameof(DotDiameter), 8);

        public static readonly StyledProperty<Color> DotColorProperty =
            AvaloniaProperty.Register<TrayDotButton, Color>(nameof(DotColor), Colors.White);

        /// <summary>"This is the current choice". Written by the owner, as <see cref="TrayButton.IsActive"/> is.</summary>
        public static readonly StyledProperty<bool> IsActiveProperty =
            AvaloniaProperty.Register<TrayDotButton, bool>(nameof(IsActive));

        static TrayDotButton()
        {
            ControlThemes.EnsureRegistered();
            AffectsRender<TrayDotButton>(DotDiameterProperty, DotColorProperty, BackgroundProperty,
                BorderBrushProperty, BorderThicknessProperty, CornerRadiusProperty);
        }

        public double DotDiameter
        {
            get => GetValue(DotDiameterProperty);
            set => SetValue(DotDiameterProperty, value);
        }

        public Color DotColor
        {
            get => GetValue(DotColorProperty);
            set => SetValue(DotColorProperty, value);
        }

        public bool IsActive
        {
            get => GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            var corner = CornerRadius;

            // always painted, transparent at rest, so the whole tile is hit-testable
            context.DrawRectangle(Background ?? Brushes.Transparent, null, new RoundedRect(bounds, corner));

            // an inset edge: the pen is centred on its path, so the path runs half a line width inside
            var edge = BorderThickness.Left;
            var edgeBrush = BorderBrush;
            if (edge > 0 && edgeBrush != null)
            {
                var half = edge / 2;
                var inset = new RoundedRect(bounds.Deflate(half),
                    new CornerRadius(
                        Math.Max(0, corner.TopLeft - half), Math.Max(0, corner.TopRight - half),
                        Math.Max(0, corner.BottomRight - half), Math.Max(0, corner.BottomLeft - half)));
                context.DrawRectangle(null, new Pen(edgeBrush, edge), inset);
            }

            TrayDots.Paint(context, bounds.Center, DotDiameter, DotColor, alwaysEdged: false);
        }
    }
}
