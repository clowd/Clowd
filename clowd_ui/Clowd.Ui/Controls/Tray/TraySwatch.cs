using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// One colour on a strip: a <see cref="TrayTokens.SwatchDot"/> dot centred in a 26 px tile, and —
    /// when <see cref="RingBrush"/> is set — a ring around it, a <see cref="TrayTokens.SwatchGap"/> gap
    /// out. The gap is left unpainted, so it shows the tray behind the tile.
    /// <para>
    /// The ring is a styled property rather than a state so the theme owns it: white for the selected
    /// colour, the accent while the pointer previews another one (TraySwatch.axaml). The owner writes
    /// <see cref="IsSelected"/>, as it writes <see cref="TrayButton.IsActive"/>: the selection follows
    /// the owner's state, not the click. A <see cref="Button"/> so <c>Click</c> and <c>Command</c> work;
    /// it has no template, everything is painted in <see cref="Render"/>.
    /// </para>
    /// </summary>
    public class TraySwatch : Button
    {
        public static readonly StyledProperty<Color> DotColorProperty =
            AvaloniaProperty.Register<TraySwatch, Color>(nameof(DotColor), Colors.White);

        public static readonly StyledProperty<bool> IsSelectedProperty =
            AvaloniaProperty.Register<TraySwatch, bool>(nameof(IsSelected));

        /// <summary>The ring around the dot. Null paints none. Set by the theme, not by owners.</summary>
        public static readonly StyledProperty<IBrush> RingBrushProperty =
            AvaloniaProperty.Register<TraySwatch, IBrush>(nameof(RingBrush));

        static TraySwatch()
        {
            ControlThemes.EnsureRegistered();
            AffectsRender<TraySwatch>(DotColorProperty, RingBrushProperty, BackgroundProperty);
        }

        public Color DotColor
        {
            get => GetValue(DotColorProperty);
            set => SetValue(DotColorProperty, value);
        }

        public bool IsSelected
        {
            get => GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        public IBrush RingBrush
        {
            get => GetValue(RingBrushProperty);
            set => SetValue(RingBrushProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            // the (transparent) background is what makes the whole tile, not just the dot, hit-testable
            var bounds = new Rect(Bounds.Size);
            context.FillRectangle(Background ?? Brushes.Transparent, bounds);

            var center = bounds.Center;
            var radius = TrayTokens.SwatchDot / 2;
            var ring = RingBrush;
            if (ring != null)
            {
                // the pen is centred on its path, so the path runs through the middle of the ring
                var ringRadius = radius + TrayTokens.SwatchGap + TrayTokens.SwatchRing / 2;
                context.DrawEllipse(null, new Pen(ring, TrayTokens.SwatchRing), center, ringRadius, ringRadius);
            }

            TrayDots.Paint(context, center, TrayTokens.SwatchDot, DotColor, alwaysEdged: true);
        }
    }

    /// <summary>The colour dot common to <see cref="TraySwatch"/> and <see cref="TrayDotButton"/>.</summary>
    internal static class TrayDots
    {
        /// <summary>Below this WCAG relative luminance a dot is too close to the graphite tray to have
        /// a visible edge of its own.</summary>
        public const double DarkLuminance = 0.08;

        /// <summary>
        /// Fills a <paramref name="diameter"/> dot at <paramref name="center"/> and, when it is
        /// <paramref name="alwaysEdged"/> or dark, paints a 1 px <see cref="TrayTokens.DotHairline"/>
        /// just inside its edge, so a near-black colour still reads as a dot on the tray.
        /// </summary>
        public static void Paint(DrawingContext context, Point center, double diameter, Color color, bool alwaysEdged)
        {
            if (diameter <= 0)
                return;

            var radius = diameter / 2;
            context.DrawEllipse(new ImmutableSolidColorBrush(color), null, center, radius, radius);

            if ((alwaysEdged || RelativeLuminance(color) < DarkLuminance) && radius > 1)
                context.DrawEllipse(null, new Pen(TrayTokens.DotHairline, 1), center, radius - 0.5, radius - 0.5);
        }

        /// <summary>WCAG 2 relative luminance of the colour's RGB, ignoring alpha.</summary>
        public static double RelativeLuminance(Color color)
        {
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

            static double Linear(byte channel)
            {
                var c = channel / 255.0;
                return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
        }
    }
}
