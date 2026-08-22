using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// A control drawing a geometry's glyph centered in its bounds. Not a <see cref="Avalonia.Controls.Shapes.Path"/>
    /// with <c>Stretch.Uniform</c>: that scales the geometry's ink bounds to fit but aligns the ink
    /// to the top-left of the element, so a glyph wider than tall (an eye, a camera) hangs above
    /// the vertical center of whatever it sits next to. Here the ink is centered on both axes,
    /// whatever box it was drawn in.
    /// </summary>
    internal sealed class GlyphIcon : Control
    {
        private readonly Geometry _geometry;
        private readonly IBrush _brush;

        public GlyphIcon(Geometry geometry, IBrush brush)
        {
            _geometry = geometry;
            _brush = brush;
        }

        public override void Render(DrawingContext context)
        {
            var bounds = _geometry?.Bounds ?? default;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return; // a missing icon simply draws nothing, as a null-Data Path does

            var scale = Math.Min(Bounds.Width / bounds.Width, Bounds.Height / bounds.Height);
            var offset = new Point(
                (Bounds.Width - bounds.Width * scale) / 2 - bounds.X * scale,
                (Bounds.Height - bounds.Height * scale) / 2 - bounds.Y * scale);

            using (context.PushTransform(Matrix.CreateScale(scale, scale) *
                                         Matrix.CreateTranslation(offset.X, offset.Y)))
            {
                context.DrawGeometry(_brush, null, _geometry);
            }
        }
    }
}
