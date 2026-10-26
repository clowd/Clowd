using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Prisc.UI.Dialogs.ColorPicker
{
    /// <summary>
    /// A harmony preset button's face (brief §7.2.3): the preset's dots (<see cref="ColorHarmony.Glyph"/>),
    /// each joined to the centre by a thin line, the base at the top, and every dot painted in the
    /// color that preset would make from the current base hue — so the buttons preview the result.
    /// </summary>
    public class HarmonyGlyph : Control
    {
        /// <summary>The glyph's box; the dots sit on a circle of <see cref="GlyphRadius"/> inside it.</summary>
        public const double GlyphSize = 48;

        private const double GlyphRadius = 16;
        private const double DotDiameter = 9;
        private const double DotRing = 1.25;

        private static readonly IPen DotRingPen = new Pen(Brushes.White, DotRing);

        public static readonly StyledProperty<HarmonyPreset> PresetProperty =
            AvaloniaProperty.Register<HarmonyGlyph, HarmonyPreset>(nameof(Preset));

        public HarmonyPreset Preset
        {
            get => GetValue(PresetProperty);
            set => SetValue(PresetProperty, value);
        }

        public static readonly StyledProperty<double> BaseHueProperty =
            AvaloniaProperty.Register<HarmonyGlyph, double>(nameof(BaseHue));

        /// <summary>The palette's base hue, in degrees: what the dots are colored from.</summary>
        public double BaseHue
        {
            get => GetValue(BaseHueProperty);
            set => SetValue(BaseHueProperty, value);
        }

        public static readonly StyledProperty<IBrush> LineBrushProperty =
            AvaloniaProperty.Register<HarmonyGlyph, IBrush>(nameof(LineBrush));

        /// <summary>The spokes' color: a faint veil of the theme's ink.</summary>
        public IBrush LineBrush
        {
            get => GetValue(LineBrushProperty);
            set => SetValue(LineBrushProperty, value);
        }

        static HarmonyGlyph()
        {
            AffectsRender<HarmonyGlyph>(PresetProperty, BaseHueProperty, LineBrushProperty);
            WidthProperty.OverrideDefaultValue<HarmonyGlyph>(GlyphSize);
            HeightProperty.OverrideDefaultValue<HarmonyGlyph>(GlyphSize);
            IsHitTestVisibleProperty.OverrideDefaultValue<HarmonyGlyph>(false);
        }

        public override void Render(DrawingContext context)
        {
            var dots = ColorHarmony.Glyph(Preset);
            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var linePen = LineBrush is { } brush ? new Pen(brush, DotRing) : null;

            // a dot's place: its hue offset turned from the top (counter-clockwise, as the wheel's
            // hues run), out by its fraction of the radius
            Point Place(HarmonyGlyphDot dot)
            {
                var angle = (dot.HueOffset + 90) * Math.PI / 180;
                var r = GlyphRadius * dot.Radius;
                return new Point(center.X + r * Math.Cos(angle), center.Y - r * Math.Sin(angle));
            }

            if (linePen != null)
            {
                foreach (var dot in dots)
                    context.DrawLine(linePen, center, Place(dot));
            }

            const double radius = DotDiameter / 2;
            foreach (var dot in dots)
            {
                var at = Place(dot);
                context.DrawEllipse(new SolidColorBrush(dot.ColorFor(BaseHue)), null, at, radius, radius);
                context.DrawEllipse(null, DotRingPen, at, radius - DotRing / 2, radius - DotRing / 2);
            }
        }
    }
}
