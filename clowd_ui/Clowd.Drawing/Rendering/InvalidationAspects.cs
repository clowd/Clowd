using System;

namespace Clowd.Drawing.Rendering
{
    /// <summary>
    /// The classes of cached state a property change can invalidate (final-design §C.2). Each
    /// graphic type declares a static property→aspect map (see
    /// <see cref="Graphics.GraphicBase.DeclarePropertyEffects"/>) so a change costs exactly what
    /// it affects: selecting a graphic invalidates nothing, recoloring it invalidates only its
    /// shadow sprite, and moving an edge invalidates bounds + geometry + shadow.
    /// </summary>
    [Flags]
    internal enum InvalidationAspects
    {
        None = 0,

        /// <summary>The graphic's cached canvas-space AABB (rotation + stroke widening included).</summary>
        Bounds = 1 << 0,

        /// <summary>Cached draw/hit-test geometry slots (and their paired bounds/transforms).</summary>
        Geometry = 1 << 1,

        /// <summary>The baked shadow sprite: bumps ShadowRev so the sprite cache stops matching.</summary>
        Shadow = 1 << 2,

        /// <summary>Cached FormattedText (font shaping + measurement).</summary>
        Text = 1 << 3,

        /// <summary>GraphicImage's decoded-source/obscure-overlay caches (owned and cleared by
        /// GraphicImage itself — the sidecar has no slots for these).</summary>
        ImageCache = 1 << 4,

        All = Bounds | Geometry | Shadow | Text | ImageCache,
    }
}
