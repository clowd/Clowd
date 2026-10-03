using Avalonia;
using Avalonia.Media.Imaging;

namespace Clowd.Drawing.Rendering
{
    /// <summary>
    /// A graphic whose ink only grows at one end while a gesture is in progress (the brush while
    /// a stroke is being drawn) and can therefore keep its shadow sprite current for the cost of
    /// the growth instead of a full re-bake per frame. <see cref="ShadowSpriteCache"/> takes this
    /// path for a stale sprite during a tool drag whenever <see cref="CanBakeShadowIncrementally"/>
    /// is true; the sprite it stores is anchored (positioned from
    /// <see cref="Graphics.GraphicBase.ShadowAnchor"/>, never stretched) and flagged interactively
    /// capped, so the usual drag-end validation replaces it with a clean full bake at rest.
    /// </summary>
    internal interface IIncrementalShadow
    {
        bool CanBakeShadowIncrementally { get; }

        /// <summary>
        /// Brings the in-place sprite up to date with the current ink and returns it: the same
        /// bitmap as last time when it still fits, a larger one when the ink outgrew it. The
        /// bitmap is <paramref name="bakeScale"/> pixels per canvas unit, at most
        /// <paramref name="maxDimension"/> on a side, and sits at ShadowAnchor +
        /// <paramref name="originFromAnchor"/> (shadow offset included), like
        /// <see cref="ShadowRenderer.Render"/>'s origin is relative to the bounds.
        /// </summary>
        WriteableBitmap BakeShadowIncrementally(double zoomBucket, int maxDimension, out Vector originFromAnchor, out double bakeScale);
    }
}
