using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// Draws a <see cref="TrayGlyph"/> by scaling its 24-unit CANVAS to the control's bounds — not its
    /// ink bounds. That distinction is the whole point: ink-fitting (what <c>Path</c> with
    /// <c>Stretch.Uniform</c> and the app's <c>GlyphIcon</c> do) makes a wide glyph and a tall glyph come
    /// out at different visual weights, which is why every old call site carried its own hand-tuned icon
    /// size. Here every glyph of the set lands on the same grid at the same stroke weight, so a 16 px
    /// slot is simply 16 px everywhere.
    /// <para>
    /// The stroke pen is created inside the scale transform, so the spec's 1.8 canvas units come out at
    /// 1.2 px in a 16 px box without any per-size arithmetic.
    /// </para>
    /// </summary>
    public sealed class TrayGlyphIcon : Control
    {
        /// <summary>The glyph to draw. Null draws nothing, exactly as a Path with no Data does.</summary>
        public static readonly StyledProperty<TrayGlyph> GlyphProperty =
            AvaloniaProperty.Register<TrayGlyphIcon, TrayGlyph>(nameof(Glyph));

        public static readonly StyledProperty<IBrush> ForegroundProperty =
            AvaloniaProperty.Register<TrayGlyphIcon, IBrush>(nameof(Foreground), TrayTokens.FgBrush);

        static TrayGlyphIcon()
        {
            AffectsRender<TrayGlyphIcon>(GlyphProperty, ForegroundProperty);
        }

        public TrayGlyph Glyph
        {
            get => GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        public IBrush Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // The glyph has no intrinsic size beyond its canvas: report the explicit Width/Height when the
            // owner set one, else the spec's 16 px icon box.
            var width = double.IsNaN(Width) ? TrayTokens.IconSize : Width;
            var height = double.IsNaN(Height) ? TrayTokens.IconSize : Height;
            return new Size(width, height);
        }

        public override void Render(DrawingContext context)
        {
            var glyph = Glyph;
            var brush = Foreground;
            if (glyph == null || brush == null)
                return;

            var scale = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
            if (scale <= 0)
                return;

            // Centre the scaled canvas: for a square slot this is a no-op, and for a non-square one it
            // keeps the glyph on the slot's centre instead of hanging off its top-left corner.
            var offsetX = (Bounds.Width - 24.0 * scale) / 2;
            var offsetY = (Bounds.Height - 24.0 * scale) / 2;

            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
            {
                // Exactly one fill op and one stroke op. Partial opacity (spec §5: .45 for an "off" toggle,
                // .4 for a disabled slot) is applied per drawing op, so two overlapping ops composite to
                // 1−(1−α)² — the micOff/eyeOff slash crossing and the cam cone meeting its body would
                // come out ≈.69/.63 instead of .45/.4. One geometry per paint cannot stack with itself.
                var fill = glyph.FillGeometry;
                if (fill != null)
                    context.DrawGeometry(brush, null, fill);

                var stroke = glyph.StrokeGeometry;
                if (stroke == null)
                    return;

                var pen = new Pen(brush, glyph.StrokeWidth)
                {
                    LineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };
                context.DrawGeometry(null, pen, stroke);
            }
        }
    }
}
