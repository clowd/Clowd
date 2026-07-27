using Avalonia;
using Avalonia.Media;

namespace Clowd.Drawing.Rendering
{
    /// <summary>
    /// Per-graphic transient sidecar of derived render state (final-design §A.5/§C.2). It lives
    /// in a [Transient] field on GraphicBase, so it is never serialized and every deserialized
    /// graphic starts with an empty cache. Slots are lazily (re)filled by the owning graphic and
    /// dropped by <see cref="Clear"/>, which is driven by the static property→aspect maps.
    ///
    /// PORT NOTE (cache slots): a ported graphic stores its expensive derived objects here, never
    /// in fields of its own (new fields would leak into the serialization contract unless marked
    /// [Transient]). Slot assignments by type:
    ///   GraphicLine      → Geometry (LineGeometry) + GeometryBounds (stroke-widened bounds)
    ///   GraphicArrow     → SecondaryGeometry (tip triangle); Geometry = the full-line Contains
    ///                      corridor (inherited straight LineGeometry, or the curved bezier when
    ///                      CurveOffset != 0); TertiaryGeometry = the shortened drawn shaft, curved
    ///                      arrows only; GeometryBounds unused (bounds via CachedBounds; a straight
    ///                      shaft is scalar math + DrawLine and needs no slot)
    ///   GraphicMeasure   → SecondaryGeometry (both end ticks in one open figure) + Text/TextKey
    ///                      (the length/angle label, keyed on the label string); Geometry =
    ///                      inherited GraphicLine full-line Contains corridor
    ///   GraphicEllipse   → Geometry (EllipseGeometry for Contains)
    ///   GraphicPolyLine  → GeometryBounds (fitted render bounds) + GeometryTransform (mapping)
    ///   GraphicText/Count→ Text (FormattedText) + TextKey (its input tuple); Count also uses
    ///                      Geometry for its ellipse hit geometry
    /// Fill slots lazily inside Bounds/Contains/DrawObject; NEVER raise PropertyChanged while
    /// filling — Avalonia forbids invalidation during render (the polyline "_final" rule,
    /// generalized to the whole pass).
    /// </summary>
    internal sealed class GraphicRenderCache
    {
        /// <summary>Cached result of GraphicBase.ComputeBounds(). Null = recompute on next read.</summary>
        public Rect? CachedBounds;

        /// <summary>
        /// Monotonic shadow revision, bumped by <see cref="Clear"/> when the Shadow aspect is
        /// invalidated. The shadow sprite cache (WP4) keys sprites on (Id, ShadowRev, zoomBucket),
        /// so a bump simply makes old sprites stop matching. Pure translation never bumps it —
        /// bounds-relative sprites move for free.
        /// </summary>
        public int ShadowRev;

        // ---- Geometry aspect ----
        public Geometry Geometry;
        public Geometry SecondaryGeometry;
        public Geometry TertiaryGeometry;
        public Rect? GeometryBounds;
        public MatrixTransform GeometryTransform;

        // ---- Text aspect ----
        public FormattedText Text;
        public object TextKey;

        public void Clear(InvalidationAspects aspects)
        {
            if (aspects == InvalidationAspects.None)
                return;

            if ((aspects & InvalidationAspects.Bounds) != 0)
                CachedBounds = null;

            if ((aspects & InvalidationAspects.Geometry) != 0)
            {
                Geometry = null;
                SecondaryGeometry = null;
                TertiaryGeometry = null;
                GeometryBounds = null;
                GeometryTransform = null;
            }

            if ((aspects & InvalidationAspects.Shadow) != 0)
                ShadowRev++; // sprites live in the id-keyed ShadowSpriteCache; the bump un-matches them

            if ((aspects & InvalidationAspects.Text) != 0)
            {
                Text = null;
                TextKey = null;
            }

            // ImageCache has no slots here: GraphicImage owns _imageSource/_imageObscured and
            // clears them itself (setter side-effects today; OnFieldsRestored after WP3c).
        }

        /// <summary>
        /// The Move() fast path: a pure translation shifts the cached bounds instead of
        /// invalidating them (final-design §A.4). No-op when the bounds were never computed.
        /// </summary>
        public void TranslateCachedBounds(double deltaX, double deltaY)
        {
            if (CachedBounds is { } b)
                CachedBounds = b.Translate(new Vector(deltaX, deltaY));
        }
    }
}
