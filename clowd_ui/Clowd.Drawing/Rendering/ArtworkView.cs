using Avalonia.Controls;
using Avalonia.Media;

namespace Clowd.Drawing.Rendering
{
    /// <summary>
    /// The single retained visual for the whole artwork (final-design §A.1): background fill,
    /// baked shadow sprites, graphics and selection chrome all record in one
    /// <see cref="SceneRenderer"/> pass. Replaces the per-graphic GraphicVisuals and the
    /// ArtworkBackgroundVisual — one InvalidateVisual per frame re-records the document; pan
    /// re-records nothing (transform-only), zoom re-records once.
    /// </summary>
    internal sealed class ArtworkView : Control
    {
        private readonly DrawingCanvas _canvas;

        public ArtworkView(DrawingCanvas canvas)
        {
            _canvas = canvas;

            // visual-only child (no logical parent): it cannot inherit the canvas Cursor, so it
            // must not win pointer hit-tests or the cursor resets to the system default within
            // the artwork bounds. Mouse input is handled by the _clickable surface instead
            // (final-design §A.1 / contract #13); graphic hit-testing stays geometry-based.
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context)
        {
            var list = _canvas.GraphicsList;
            if (list == null)
                return;

            var options = new SceneRenderOptions(
                UiScale: list.Dpi,
                DrawChrome: true,
                Offset: default,
                Background: null,
                ArtworkBackground: _canvas.ArtworkBackground,
                // no-raise read: Render must stay pure (a PropertyChanged here could re-enter
                // invalidation mid-render); the pending validator tick performs the raise
                ContentBounds: list.GetContentBoundsForRender());

            SceneRenderer.Render(context, list.GraphicsSnapshot(), list.ShadowCache, in options);
        }
    }
}
