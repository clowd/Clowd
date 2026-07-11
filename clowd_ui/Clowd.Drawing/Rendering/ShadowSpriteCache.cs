using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;
using Clowd.Drawing.Graphics;

namespace Clowd.Drawing.Rendering
{
    /// <summary>
    /// Per-document cache of CPU-baked drop-shadow sprites (final-design §A.3 — fixes R1/R7;
    /// Visual.Effect is never used). Sprites are baked by <see cref="ShadowRenderer"/> RELATIVE
    /// to the graphic's bounds origin, so a pure translation reuses the bitmap at its new
    /// position, and are keyed on (Id, ShadowRev, zoomBucket): bumping ShadowRev (any
    /// Shadow-aspect property change) or crossing a zoom bucket simply makes the stored sprite
    /// stop matching — it keeps DRAWING, stretched to the current bounds (invisible for a soft
    /// r=5 shadow), until the frame validator re-bakes it, at most one bake per tick.
    ///
    /// Sprites are NOT evicted when a graphic leaves the collection: the history engine retains
    /// deleted instances and re-inserts them on undo, and §B.4 counts the shadow sprite among
    /// the caches that survive an undo-of-delete. The byte-budget LRU bounds the total either
    /// way (a sprite unused across ~32 MB of newer bakes ages out).
    ///
    /// Memory-pressure valve: the pinned live set (see ctor remarks) is bounded only by document
    /// size, so two safety rails apply. (1) While over budget, NEW bakes cap at
    /// <see cref="InteractiveMaxDimension"/> instead of <see cref="MaxDimension"/> — such sprites
    /// still count as current at rest (no re-bake churn) and refresh full-res on their next
    /// natural invalidation once pressure recedes. (2) A hard ceiling at 2× the budget may evict
    /// even pinned sprites (tail-first): on pathological documents a shadow briefly drops and
    /// re-bakes one per validator tick — bounded degradation instead of unbounded memory.
    ///
    /// UI-thread only (bakes and lookups both run on the dispatcher), so no locking.
    /// </summary>
    internal sealed class ShadowSpriteCache
    {
        /// <summary>Byte budget for retained sprites; least-recently-drawn are dropped first.</summary>
        internal const long MaxBytes = 32 * 1024 * 1024;

        /// <summary>Hard cap on sprite pixel dimensions, always applied (bake scale reduced to fit).</summary>
        internal const int MaxDimension = 2048;

        /// <summary>Cap while a tool drag is active, bounding the worst-case mid-drag bake to a
        /// few ms; the validator re-bakes at full target resolution once the drag ends.</summary>
        internal const int InteractiveMaxDimension = 1024;

        internal sealed class Sprite
        {
            public WriteableBitmap Bitmap;

            /// <summary>Sprite top-left relative to the graphic's Bounds top-left, canvas units,
            /// shadow offset included (the bounds-relative half of translation-for-free).</summary>
            public Vector Origin;

            /// <summary>Sprite pixels per canvas unit (may be below ZoomBucket when capped).</summary>
            public double BakeScale;

            /// <summary>The graphic's bounds size at bake time — stale sprites stretch by the
            /// ratio of current to baked size until the validator re-bakes.</summary>
            public Size BakedBoundsSize;

            public int ShadowRev;
            public double ZoomBucket;

            /// <summary>Baked under <see cref="InteractiveMaxDimension"/> during a tool drag;
            /// counts as stale once the drag ends (re-baked full-res at rest).</summary>
            public bool InteractiveCapped;

            public long Bytes;
            internal string Id;
            internal LinkedListNode<Sprite> LruNode;

            /// <summary>
            /// Canvas-space rect to blit the sprite into for the graphic's CURRENT bounds. For a
            /// current sprite this is exact (Bounds.TopLeft + Origin, pixels ÷ BakeScale); a
            /// stale sprite is stretched proportionally so the shadow follows a resize until the
            /// re-bake lands.
            /// </summary>
            public Rect GetDestRect(GraphicBase graphic)
            {
                var bounds = graphic.Bounds;
                var sx = BakedBoundsSize.Width > 0 ? bounds.Width / BakedBoundsSize.Width : 1.0;
                var sy = BakedBoundsSize.Height > 0 ? bounds.Height / BakedBoundsSize.Height : 1.0;
                var px = Bitmap.PixelSize;
                return new Rect(bounds.Left + Origin.X * sx,
                                bounds.Top + Origin.Y * sy,
                                px.Width / BakeScale * sx,
                                px.Height / BakeScale * sy);
            }
        }

        private readonly Dictionary<string, Sprite> _byId = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private readonly LinkedList<Sprite> _lru = new LinkedList<Sprite>(); // most recently drawn first
        private readonly Func<string, GraphicBase> _resolveLiveGraphic;
        private long _totalBytes;
        private int _bakeCursor; // round-robin start index for BakeNext

        /// <param name="resolveLiveGraphic">Resolves an id to the graphic currently IN the owning
        /// collection (null if absent). Used by eviction to pin the live working set: a sprite
        /// that is current for a live shadowed graphic is re-drawn every frame, so evicting it
        /// would only force an immediate re-bake — with a whole document over budget that
        /// degenerates into a permanent bake/evict thrash. The byte budget therefore normally
        /// evicts only stale and orphaned sprites; because the pinned set is bounded only by the
        /// document's shadowed area (≤ 16 MB per sprite via the dimension cap), the pressure
        /// valve (class doc) caps new bakes while over budget and hard-caps the total at 2× the
        /// budget, evicting pinned sprites tail-first past that ceiling.</param>
        public ShadowSpriteCache(Func<string, GraphicBase> resolveLiveGraphic = null)
        {
            _resolveLiveGraphic = resolveLiveGraphic;
        }

        /// <summary>
        /// The bake bucket for a canvas zoom (final-design §0.3, committed): sprites bake at
        /// b ∈ {1, 1.5, 2} so shadows stay crisp at high zoom without per-zoom re-bakes.
        /// </summary>
        public static double BucketForScale(double contentScale) =>
            contentScale <= 1.05 ? 1.0 : contentScale <= 1.55 ? 1.5 : 2.0;

        /// <summary>
        /// The current sprite for this graphic, possibly stale (SceneRenderer draws whatever is
        /// here — stale-stretch — and the validator handles re-baking). Pure and allocation-free;
        /// never bakes. False only when the graphic has never been baked (its shadow appears one
        /// validator tick later).
        /// </summary>
        public bool TryGet(GraphicBase graphic, out Sprite sprite)
        {
            if (_byId.TryGetValue(graphic.Id, out sprite))
            {
                Touch(sprite);
                return true;
            }

            return false;
        }

        /// <summary>
        /// O(1) probe used by the collection's invalidation funnel: true if this graphic's sprite
        /// is missing or its ShadowRev no longer matches (a real Shadow-aspect change, as opposed
        /// to the per-raise Shadow flag in the static aspect map, which also fires for pure
        /// translations that never bump the rev). Bucket/cap staleness is flagged separately by
        /// the Dpi/drag-end paths.
        /// </summary>
        public bool NeedsBake(GraphicBase graphic) =>
            graphic.DropShadowEffect
            && (!_byId.TryGetValue(graphic.Id, out var sprite) || sprite.ShadowRev != graphic.ShadowRev);

        /// <summary>
        /// Bakes AT MOST ONE missing/stale sprite (called by the frame validator, §A.4 step 3).
        /// Returns true if more stale sprites remain after this call, in which case the caller
        /// schedules another validation tick — large invalidations resolve one bake per frame
        /// while the stale sprites keep drawing stretched.
        /// </summary>
        public bool BakeNext(IReadOnlyList<GraphicBase> graphics, double contentScale, bool isToolDragActive)
        {
            var bucket = BucketForScale(contentScale);
            bool baked = false;
            // round-robin: scan from a persistent cursor so a continuously-invalidated early
            // graphic (typing, scrubbing) cannot starve later stale sprites of the per-frame slot.
            // The start is captured so the mid-loop cursor update cannot shift this call's scan.
            int start = _bakeCursor;
            for (int k = 0; k < graphics.Count; k++)
            {
                var i = (start + k) % graphics.Count;
                var g = graphics[i];
                if (!g.DropShadowEffect)
                    continue;
                if (g is GraphicText { Editing: true })
                    continue; // never bake mid-edit text (SceneRenderer hides its shadow too); the Editing=false raise re-bakes at commit
                if (_byId.TryGetValue(g.Id, out var sprite) && IsCurrent(sprite, g, bucket, isToolDragActive))
                    continue;
                if (baked)
                    return true; // a second pending bake exists — more work for the next tick

                Bake(g, bucket, isToolDragActive);
                _bakeCursor = i + 1;
                baked = true;
            }

            return false;
        }

        /// <summary>
        /// Export seam (final-design §A.2, consumed by WP7): returns a bucket-1 sprite at full
        /// target resolution (hard dimension cap only), baking synchronously if the stored sprite
        /// is missing, stale, interactively capped or bucketed — so the export look is exactly
        /// the resting screen look.
        /// </summary>
        public Sprite GetOrBakeFullRes(GraphicBase graphic)
        {
            if (_byId.TryGetValue(graphic.Id, out var sprite) && IsCurrent(sprite, graphic, 1.0, isToolDragActive: false))
            {
                Touch(sprite);
                return sprite;
            }

            return Bake(graphic, 1.0, isToolDragActive: false);
        }

        public void Remove(string id)
        {
            if (_byId.TryGetValue(id, out var sprite))
                RemoveEntry(sprite);
        }

        public void Clear()
        {
            _byId.Clear();
            _lru.Clear();
            _totalBytes = 0;
        }

        /// <summary>Total bytes of retained sprites (tests assert the 2× budget hard ceiling).</summary>
        internal long TotalBytes => _totalBytes;

        private static bool IsCurrent(Sprite sprite, GraphicBase graphic, double bucket, bool isToolDragActive) =>
            sprite.ShadowRev == graphic.ShadowRev
            && sprite.ZoomBucket == bucket
            && (!sprite.InteractiveCapped || isToolDragActive);

        private Sprite Bake(GraphicBase graphic, double bucket, bool isToolDragActive)
        {
            // memory-pressure valve (class doc): while over budget, new bakes cap at the
            // interactive dimension. This feeds restScale (NOT the InteractiveCapped flag), so a
            // pressure-capped sprite counts as current at rest — no perpetual re-bake churn; it
            // refreshes full-res on its next natural invalidation once pressure recedes.
            var effectiveMax = _totalBytes > MaxBytes ? InteractiveMaxDimension : MaxDimension;
            var restScale = ShadowRenderer.ClampBakeScale(graphic, bucket, effectiveMax);
            var scale = isToolDragActive
                ? ShadowRenderer.ClampBakeScale(graphic, restScale, InteractiveMaxDimension)
                : restScale;

            var rev = graphic.ShadowRev; // read before the (pure) bake for key consistency
            var bitmap = ShadowRenderer.Render(graphic, scale, out var origin);

            Remove(graphic.Id); // release any previous sprite for this graphic

            var sprite = new Sprite
            {
                Id = graphic.Id,
                Bitmap = bitmap,
                Origin = origin,
                BakeScale = scale,
                BakedBoundsSize = graphic.Bounds.Size,
                ShadowRev = rev,
                ZoomBucket = bucket,
                InteractiveCapped = scale < restScale,
                Bytes = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4,
            };
            sprite.LruNode = _lru.AddFirst(sprite);
            _byId[sprite.Id] = sprite;
            _totalBytes += sprite.Bytes;

            EvictOverBudget(bucket, isToolDragActive, sprite);

            return sprite;
        }

        /// <summary>
        /// One tail-to-head sweep dropping least-recently-drawn sprites until the byte budget is
        /// met, skipping the sprite just baked and any sprite pinned by the live working set (see
        /// the ctor remarks). If everything left is pinned the budget may overshoot — but only up
        /// to the hard ceiling of 2× the budget: past that, pinned sprites are evicted too
        /// (tail-first), so a pathological document briefly drops shadows and re-bakes them one
        /// per validator tick instead of growing without bound.
        /// </summary>
        private void EvictOverBudget(double bucket, bool isToolDragActive, Sprite justBaked)
        {
            var node = _lru.Last;
            while (node != null && _totalBytes > MaxBytes)
            {
                var previous = node.Previous;
                var sprite = node.Value;
                if (!ReferenceEquals(sprite, justBaked) && !IsPinned(sprite, bucket, isToolDragActive))
                    RemoveEntry(sprite);
                node = previous;
            }

            // hard ceiling: past 2× the budget even pinned sprites go, tail-first
            node = _lru.Last;
            while (node != null && _totalBytes > 2 * MaxBytes)
            {
                var previous = node.Previous;
                var sprite = node.Value;
                if (!ReferenceEquals(sprite, justBaked))
                    RemoveEntry(sprite);
                node = previous;
            }
        }

        private bool IsPinned(Sprite sprite, double bucket, bool isToolDragActive)
        {
            var graphic = _resolveLiveGraphic?.Invoke(sprite.Id);
            return graphic != null && graphic.DropShadowEffect && IsCurrent(sprite, graphic, bucket, isToolDragActive);
        }

        private void Touch(Sprite sprite)
        {
            if (!ReferenceEquals(_lru.First, sprite.LruNode))
            {
                _lru.Remove(sprite.LruNode);
                _lru.AddFirst(sprite.LruNode);
            }
        }

        private void RemoveEntry(Sprite sprite)
        {
            _byId.Remove(sprite.Id);
            _lru.Remove(sprite.LruNode);
            _totalBytes -= sprite.Bytes;
            // the WriteableBitmap is NOT disposed here: the compositor may still replay a frame
            // recording that references it; the GC reclaims it once the next re-record drops the
            // last reference.
        }
    }
}
