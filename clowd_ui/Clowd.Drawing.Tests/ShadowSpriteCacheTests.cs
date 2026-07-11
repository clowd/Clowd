using System;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Rendering;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// Pins the sprite-shadow cache contract (final-design §A.3): a pure translation reuses the
    /// baked sprite (bounds-relative bake — no re-bake on Move), a Shadow-aspect change (color /
    /// geometry) bumps ShadowRev and forces a re-bake, selection never invalidates a sprite, a
    /// zoom-bucket change re-bakes crisper, export forces a bucket-1 full-res sprite, and the byte
    /// budget evicts the least-recently-drawn unpinned sprite.
    /// </summary>
    public class ShadowSpriteCacheTests
    {
        private static GraphicRectangle Shadowed(Rect r)
        {
            var g = new GraphicRectangle(Colors.Red, 2, r); // dropShadowEffect defaults to true
            Assert.True(g.DropShadowEffect);
            return g;
        }

        private static ShadowSpriteCache.Sprite Bake(ShadowSpriteCache cache, GraphicBase g, double scale = 1.0)
        {
            cache.BakeNext(new[] { g }, scale, false);
            Assert.True(cache.TryGet(g, out var s));
            return s;
        }

        [AvaloniaFact]
        public void Translation_ReusesSprite_WithoutRebaking()
        {
            var cache = new ShadowSpriteCache();
            var g = Shadowed(new Rect(20, 20, 40, 30));

            Assert.True(cache.NeedsBake(g));
            var s1 = Bake(cache, g);
            var rev = g.ShadowRev;

            g.Move(15, -10);

            Assert.Equal(rev, g.ShadowRev);            // a pure translation never bumps ShadowRev
            Assert.False(cache.NeedsBake(g));
            Assert.False(cache.BakeNext(new[] { (GraphicBase)g }, 1.0, false)); // nothing pending
            Assert.True(cache.TryGet(g, out var s2));
            Assert.Same(s1, s2);                       // same bitmap, reused at the new position

            // and the blit rect follows the moved graphic (bounds-relative origin)
            var dest = s2.GetDestRect(g);
            Assert.True(Math.Abs(dest.Left - (g.Bounds.Left + s2.Origin.X)) < 1e-6);
            Assert.True(Math.Abs(dest.Top - (g.Bounds.Top + s2.Origin.Y)) < 1e-6);
        }

        [AvaloniaFact]
        public void ColorAlphaChange_BumpsShadowRev_AndRebakes()
        {
            var cache = new ShadowSpriteCache();
            var g = Shadowed(new Rect(20, 20, 40, 30));
            var s1 = Bake(cache, g);
            var rev = g.ShadowRev;

            g.ObjectColor = Color.FromArgb(128, 0, 255, 0); // ALPHA change → silhouette changes → re-bake

            Assert.True(g.ShadowRev > rev);
            Assert.True(cache.NeedsBake(g));

            var s2 = Bake(cache, g);
            Assert.NotSame(s1, s2);
            Assert.Equal(g.ShadowRev, s2.ShadowRev);
        }

        [AvaloniaFact]
        public void SameAlphaColorChange_DoesNotRebake()
        {
            // the sprite is baked from the ink's alpha silhouette only — an opaque-to-opaque color
            // scrub must not burn a full-res bake per slider tick for a bitwise-identical sprite
            var cache = new ShadowSpriteCache();
            var g = Shadowed(new Rect(20, 20, 40, 30));
            var s1 = Bake(cache, g);
            var rev = g.ShadowRev;

            g.ObjectColor = Colors.Green; // alpha 255 → 255

            Assert.Equal(rev, g.ShadowRev);
            Assert.False(cache.NeedsBake(g));
            Assert.False(cache.BakeNext(new[] { (GraphicBase)g }, 1.0, false));
            Assert.True(cache.TryGet(g, out var s2));
            Assert.Same(s1, s2);
        }

        [AvaloniaFact]
        public void GeometryChange_BumpsShadowRev_AndRebakes()
        {
            var cache = new ShadowSpriteCache();
            var g = Shadowed(new Rect(20, 20, 40, 30));
            var s1 = Bake(cache, g);
            var rev = g.ShadowRev;

            g.Right += 25; // resize → Bounds|Geometry|Shadow

            Assert.True(g.ShadowRev > rev);
            Assert.True(cache.NeedsBake(g));
            var s2 = Bake(cache, g);
            Assert.NotSame(s1, s2);
        }

        [AvaloniaFact]
        public void Selection_DoesNotInvalidateSprite()
        {
            var cache = new ShadowSpriteCache();
            var g = Shadowed(new Rect(20, 20, 40, 30));
            var s1 = Bake(cache, g);
            var rev = g.ShadowRev;

            g.IsSelected = true;

            Assert.Equal(rev, g.ShadowRev);   // selection is None-aspect — never queues a re-bake
            Assert.False(cache.NeedsBake(g));
            Assert.False(cache.BakeNext(new[] { (GraphicBase)g }, 1.0, false));
            Assert.True(cache.TryGet(g, out var s2));
            Assert.Same(s1, s2);
        }

        [AvaloniaFact]
        public void ZoomBucketChange_Rebakes_AtHigherScale()
        {
            var cache = new ShadowSpriteCache();
            var g = Shadowed(new Rect(20, 20, 40, 30));

            var s1 = Bake(cache, g, 1.0);
            Assert.Equal(1.0, s1.ZoomBucket);

            // ContentScale 2.0 → bucket 2 (final-design §0.3): the stored bucket-1 sprite is no
            // longer current, so the validator re-bakes it crisper
            Assert.Equal(2.0, ShadowSpriteCache.BucketForScale(2.0));
            var s2 = Bake(cache, g, 2.0);

            Assert.NotSame(s1, s2);
            Assert.Equal(2.0, s2.ZoomBucket);
            Assert.True(s2.BakeScale > s1.BakeScale);
        }

        [AvaloniaFact]
        public void Export_ForcesBucketOneFullResSprite()
        {
            var cache = new ShadowSpriteCache();
            var g = Shadowed(new Rect(20, 20, 40, 30));

            // current on-screen sprite is a high-zoom bucket-2 bake
            var zoomed = Bake(cache, g, 2.0);
            Assert.Equal(2.0, zoomed.ZoomBucket);

            var full = cache.GetOrBakeFullRes(g);
            Assert.Equal(1.0, full.ZoomBucket);      // export always bakes at b=1, full target res
            Assert.False(full.InteractiveCapped);
            Assert.NotSame(zoomed, full);

            Assert.True(cache.TryGet(g, out var stored));
            Assert.Same(full, stored);               // the full-res bake replaces the stored sprite
        }

        [AvaloniaFact]
        public void PinnedSet_NeverExceedsTwiceTheByteBudget_AndPressureCappedSpritesStayCurrent()
        {
            // pressure valve + hard ceiling: a document whose LIVE pinned set alone would blow
            // past the budget must (a) never retain more than 2×MaxBytes, and (b) settle — the
            // pressure-capped (1024px) sprites count as current at rest, so BakeNext eventually
            // returns false instead of re-baking the same sprites forever.
            var byId = new System.Collections.Generic.Dictionary<string, GraphicBase>();
            var cache = new ShadowSpriteCache(id => byId.TryGetValue(id, out var g) ? g : null);

            var graphics = new GraphicBase[10];
            for (int i = 0; i < graphics.Length; i++)
            {
                graphics[i] = Shadowed(new Rect(0, 0, 1800, 1800)); // ~13.4 MB each uncapped
                byId[graphics[i].Id] = graphics[i];
            }

            int ticks = 0;
            bool more = true;
            while (more)
            {
                Assert.True(++ticks <= graphics.Length * 8, "BakeNext never settled — pressure-capped sprites are being perpetually re-baked");
                more = cache.BakeNext(graphics, 1.0, false);
                Assert.True(cache.TotalBytes <= 2 * ShadowSpriteCache.MaxBytes,
                            $"retained sprite bytes {cache.TotalBytes} exceed the 2× budget hard ceiling");
            }

            // settled: every live shadowed graphic has a current sprite and no bake is pending
            Assert.False(cache.BakeNext(graphics, 1.0, false));
            foreach (var g in graphics)
            {
                Assert.False(cache.NeedsBake(g));
                Assert.True(cache.TryGet(g, out var s));
                Assert.False(s.InteractiveCapped); // pressure cap must NOT mark sprites interactive (that would churn at rest)
            }
        }

        [AvaloniaFact]
        public void ByteBudget_EvictsLeastRecentlyDrawnUnpinnedSprite()
        {
            // null resolver → no live graphic is pinned, so the byte budget may evict anything
            var cache = new ShadowSpriteCache();
            var big = new GraphicBase[3];
            for (int i = 0; i < big.Length; i++)
                big[i] = Shadowed(new Rect(0, 0, 1800, 1800)); // ~13.4 MB each; 3 exceed the 32 MB budget

            // bake one per call (BakeNext bakes at most one); the third pushes over budget
            cache.BakeNext(big, 1.0, false); // big[0]
            cache.BakeNext(big, 1.0, false); // big[1]
            cache.BakeNext(big, 1.0, false); // big[2] → evict the oldest (big[0])

            Assert.False(cache.TryGet(big[0], out _)); // least-recently-drawn, evicted
            Assert.True(cache.TryGet(big[1], out _));
            Assert.True(cache.TryGet(big[2], out _));
        }
    }
}
