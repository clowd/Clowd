using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Clowd.Drawing.Ink
{
    /// <summary>
    /// Paints a highlighter stroke the way a marker inks paper: multiplied into what is under it,
    /// so white turns the ink's colour and dark detail (text) stays dark and crisp rather than
    /// being washed over. Multiply can only darken, which would leave the ink all but invisible on
    /// a dark background, so a faint screen pass (<see cref="GlowStrength"/>) lightens it back
    /// toward the ink: a visible band on dark artwork, next to no change on light. Avalonia has
    /// no blend mode for geometry, so this is a Skia draw: the stroke's pieces are filled opaque
    /// into a layer, and the layer is blended down at the colour's alpha — once multiplied, then
    /// once screened — so overlaps within the stroke never darken, and the alpha is the ink's
    /// strength. Drawn wherever the scene is drawn (screen, export, thumbnails), all of which
    /// are Skia; anywhere else there is nothing to blend onto and it draws nothing.
    /// </summary>
    internal sealed class HighlighterDrawOperation : ICustomDrawOperation
    {
        /// <summary>The screen pass's share of the ink's alpha.</summary>
        internal const double GlowStrength = 0.22;

        private readonly ChiselStrokeBuilder.Run[] _runs;
        private readonly Color _color;

        /// <param name="runs">The stroke's pieces, local to the current transform.</param>
        /// <param name="bounds">Their bounds, in the same space.</param>
        public HighlighterDrawOperation(ChiselStrokeBuilder.Run[] runs, Rect bounds, Color color)
        {
            _runs = runs;
            Bounds = bounds;
            _color = color;
        }

        public Rect Bounds { get; }

        public bool HitTest(Point p) => false; // the canvas hit-tests graphics itself

        public bool Equals(ICustomDrawOperation other) => false;

        public void Dispose()
        { }

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature == null || _runs.Length == 0)
                return;

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            if (canvas == null)
                return;

            var b = Bounds.Inflate(1);
            var rect = new SKRect((float)b.Left, (float)b.Top, (float)b.Right, (float)b.Bottom);
            using var ink = new SKPaint { Color = new SKColor(_color.R, _color.G, _color.B), IsAntialias = true, Style = SKPaintStyle.Fill };

            Pass(canvas, rect, ink, SKBlendMode.Multiply, _color.A);
            Pass(canvas, rect, ink, SKBlendMode.Screen, (byte)Math.Round(_color.A * GlowStrength));
        }

        private void Pass(SKCanvas canvas, SKRect rect, SKPaint ink, SKBlendMode mode, byte alpha)
        {
            using var layer = new SKPaint { BlendMode = mode, Color = new SKColor(255, 255, 255, alpha) };
            canvas.SaveLayer(rect, layer);
            foreach (var run in _runs)
                canvas.DrawPath(run.GetPath(), ink);
            canvas.Restore();
        }
    }
}
