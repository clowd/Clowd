using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Rendering
{
    /// <summary>
    /// Export render host for <see cref="GraphicCollection.DrawGraphicsToBitmap"/> (final-design
    /// §A.2). A single Control pinned at (0,0) spanning the FULL bitmap size whose Render runs the
    /// same <see cref="SceneRenderer"/> pass as the screen — but with DrawChrome = false and a
    /// content-space → bitmap-space translation offset — so export matches the on-screen artwork
    /// by construction.
    ///
    /// The control MUST span the whole bitmap and sit at (0,0): RenderTargetBitmap.Render culls
    /// any visual whose arranged rect does not intersect the target, so the graphic-space
    /// translation happens INSIDE the pass (SceneRenderOptions.Offset), never by offsetting this
    /// control (quirk A, canvas-core §6.3). This replaces the throwaway per-graphic GraphicVisual
    /// forest and its per-export shadow re-bake — shadows come from cached full-res sprites.
    /// Never attached to the live visual tree; Measure/Arrange are driven manually by the caller.
    /// </summary>
    internal sealed class SceneVisual : Control
    {
        private readonly IReadOnlyList<GraphicBase> _graphics;
        private readonly ShadowSpriteCache _shadows;
        private readonly SceneRenderOptions _options;

        public SceneVisual(int width, int height, IReadOnlyList<GraphicBase> graphics, ShadowSpriteCache shadows,
                           in SceneRenderOptions options)
        {
            // pinned full-bitmap size so RenderTargetBitmap.Render never culls the content
            Width = width;
            Height = height;

            _graphics = graphics;
            _shadows = shadows;
            _options = options;
        }

        public override void Render(DrawingContext context) => SceneRenderer.Render(context, _graphics, _shadows, in _options);
    }
}
