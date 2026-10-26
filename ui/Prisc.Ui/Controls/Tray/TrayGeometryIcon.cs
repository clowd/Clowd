using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// Paints an arbitrary filled <see cref="Avalonia.Media.Geometry"/> — an app icon resource rather
    /// than a <see cref="TrayGlyph"/> — its shape bounds fitted uniformly into the control and centred on both axes.
    /// <para>
    /// Shape-fitting is the opposite of <see cref="TrayGlyphIcon"/>'s canvas scaling, and deliberately so:
    /// the app's icon resources come on unrelated canvases (24, 26, 1024 units…), so the canvas carries no
    /// common grid to preserve, and the shape bounds are the only measure they all agree on. The
    /// maths is the app's <c>GlyphIcon</c>'s, so an icon looks the same here as it does in the editor.
    /// </para>
    /// </summary>
    public sealed class TrayGeometryIcon : Control
    {
        /// <summary>The geometry to fill. Null (or one with empty bounds) paints nothing.</summary>
        public static readonly StyledProperty<Geometry> GeometryProperty =
            AvaloniaProperty.Register<TrayGeometryIcon, Geometry>(nameof(Geometry));

        public static readonly StyledProperty<IBrush> ForegroundProperty =
            AvaloniaProperty.Register<TrayGeometryIcon, IBrush>(nameof(Foreground), TrayTokens.FgBrush);

        static TrayGeometryIcon()
        {
            AffectsRender<TrayGeometryIcon>(GeometryProperty, ForegroundProperty);
            IsHitTestVisibleProperty.OverrideDefaultValue<TrayGeometryIcon>(false);
        }

        public Geometry Geometry
        {
            get => GetValue(GeometryProperty);
            set => SetValue(GeometryProperty, value);
        }

        public IBrush Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // as TrayGlyphIcon: the explicit Width/Height when the owner set one, else the icon box
            var width = double.IsNaN(Width) ? TrayTokens.IconSize : Width;
            var height = double.IsNaN(Height) ? TrayTokens.IconSize : Height;
            return new Size(width, height);
        }

        public override void Render(DrawingContext context)
        {
            var geometry = Geometry;
            var brush = Foreground;
            var shape = geometry?.Bounds ?? default;
            if (brush == null || shape.Width <= 0 || shape.Height <= 0)
                return;

            var scale = Math.Min(Bounds.Width / shape.Width, Bounds.Height / shape.Height);
            var offset = new Point(
                (Bounds.Width - shape.Width * scale) / 2 - shape.X * scale,
                (Bounds.Height - shape.Height * scale) / 2 - shape.Y * scale);

            // one fill op, so a partial opacity (a disabled slot's .4) never composites with itself
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
                context.DrawGeometry(brush, null, geometry);
        }
    }
}
