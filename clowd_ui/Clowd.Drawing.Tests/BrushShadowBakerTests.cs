using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Ink;
using Clowd.Drawing.Rendering;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// The in-place shadow of a stroke being drawn: the windowed re-blurs land on exactly what a
    /// whole-plane redo gives (across plane growth and a scale cap), the sprite matches the full
    /// bake's shadow at the same canvas positions, the sprite cache takes the incremental path
    /// only during a drag and replaces the sprite with a clean full bake at rest, and a moved
    /// stroke's anchored sprite moves with it unstretched.
    /// </summary>
    public class BrushShadowBakerTests
    {
        static BrushShadowBakerTests()
        {
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
        }

        /// <summary>A brush being drawn: the samples are added as the tool would (not published).</summary>
        private static GraphicBrush Drawing(Point origin, IEnumerable<Point> canvasPoints, double lineWidth = 6, Color? color = null)
        {
            var g = new GraphicBrush(color ?? Colors.Black, lineWidth, origin);
            double t = 0;
            foreach (var p in canvasPoints)
                g.AddSample(p, t += 1.3, 0.5, notify: false);
            g.NotifySamplesChanged();
            return g;
        }

        private static IEnumerable<Point> Wander(int n, double step, int seed = 5)
        {
            var rnd = new Random(seed);
            double x = 100, y = 100, vx = step, vy = step / 3;
            for (int i = 0; i < n; i++)
            {
                vx = Math.Clamp(vx + (rnd.NextDouble() - 0.5) * step * 0.3, -step, step);
                vy = Math.Clamp(vy + (rnd.NextDouble() - 0.5) * step * 0.3, -step, step);
                x += vx; y += vy;
                if (x < 20 || x > 520) vx = -vx; // keep it within ~500 units: under the interactive cap at scale 1
                if (y < 20 || y > 420) vy = -vy;
                yield return new Point(x, y);
            }
        }

        private static byte[] ReadAlpha(WriteableBitmap bitmap)
        {
            var px = bitmap.PixelSize;
            var buf = new byte[px.Width * px.Height * 4];
            using (var fb = bitmap.Lock())
                Marshal.Copy(fb.Address, buf, 0, buf.Length);
            var alpha = new byte[px.Width * px.Height];
            for (int i = 0; i < alpha.Length; i++)
                alpha[i] = buf[i * 4 + 3];
            return alpha;
        }

        /// <summary>Drives a stroke sample by sample, baking after each event, and returns the baker.</summary>
        private static BrushShadowBaker Drive(GraphicBrush g, IEnumerable<Point> points, int maxDimension, Action<BrushShadowBaker, int> each = null)
        {
            var baker = new BrushShadowBaker(g);
            double t = g.GetSamples()[^1].T;
            int i = 0;
            foreach (var p in points)
            {
                if (g.AddSample(p, t += 1.3, 0.5))
                {
                    baker.Bake(1.0, maxDimension, out _, out _);
                    each?.Invoke(baker, ++i);
                }
            }

            return baker;
        }

        /// <summary>The stroke's bounds are not monotonic (the start rule swallows the opening
        /// points, a dot's resting radius thins once the pointer moves, the tip's cap wanders), so a
        /// reserve outgrown on one side can ask for a plane whose origin lies right of / below the
        /// old one. The carried plane must still contain the old one rather than copy to a negative
        /// index. Fuzzed over jittery, jumpy strokes at several zooms and caps.</summary>
        [AvaloniaFact]
        public void ShrinkingBounds_NeverMoveTheCarriedPlanePastTheOldOne()
        {
            for (int seed = 0; seed < 40; seed++)
            {
                var rnd = new Random(seed);
                double width = 1 + rnd.NextDouble() * 20;
                var start = new Point(300, 300);
                var g = Drawing(start, new[] { start }, lineWidth: width);
                var baker = new BrushShadowBaker(g);
                double zoom = new[] { 0.5, 1.0, 2.0 }[seed % 3];
                int cap = seed % 4 == 0 ? 240 : ShadowSpriteCache.InteractiveMaxDimension;

                double t = 0, x = start.X, y = start.Y;
                for (int i = 0; i < 300; i++)
                {
                    // mostly tiny jitter near the start, with occasional long flicks in any direction
                    double step = rnd.NextDouble() < 0.08 ? 40 + rnd.NextDouble() * 120 : rnd.NextDouble() * 3;
                    double a = rnd.NextDouble() * 2 * Math.PI;
                    x = Math.Clamp(x + Math.Cos(a) * step, 0, 600);
                    y = Math.Clamp(y + Math.Sin(a) * step, 0, 600);
                    t += 1 + rnd.NextDouble() * 30;
                    if (g.AddSample(new Point(x, y), t, 0.5))
                    {
                        var stroke = g.GetStroke();
                        baker.Bake(zoom, cap, out _, out _);
                        Assert.True(baker.PlaneRect.Contains(stroke.Bounds), $"seed {seed} sample {i}");
                    }
                }
            }
        }

        [AvaloniaFact]
        public void WindowedUpdates_LandOnTheWholePlaneRedo()
        {
            var all = Wander(900, 1.6).ToList();
            var g = Drawing(all[0], all.Take(2));
            int relayouts = 0;
            int width = 0;

            var baker = Drive(g, all.Skip(2), ShadowSpriteCache.InteractiveMaxDimension, (b, i) =>
            {
                if (b.PlaneWidth != width)
                {
                    relayouts++;
                    width = b.PlaneWidth;
                }

                if (i % 150 == 0)
                {
                    var incremental = (byte[])b.BlurredPlane.Clone();
                    b.RedoAllNextBake();
                    b.Bake(1.0, ShadowSpriteCache.InteractiveMaxDimension, out _, out _);
                    Assert.True(incremental.SequenceEqual(b.BlurredPlane), $"the plane drifted from a full redo at sample {i}");
                }
            });

            // the stroke outgrew its first reserve at least once, so the carry-over was exercised
            Assert.True(relayouts >= 2, $"{relayouts} layouts");
            Assert.Equal(1.0, baker.Scale);
        }

        [AvaloniaFact]
        public void UnderTheDimensionCap_TheScaleDrops_AndTheUpdatesStillLand()
        {
            var all = Wander(700, 2.2, seed: 9).ToList();
            var g = Drawing(all[0], all.Take(2));

            var baker = Drive(g, all.Skip(2), 240, (b, i) =>
            {
                Assert.True(Math.Max(b.PlaneWidth, b.PlaneHeight) <= 240, $"{b.PlaneWidth}x{b.PlaneHeight}");
                if (i % 100 == 0)
                {
                    var incremental = (byte[])b.BlurredPlane.Clone();
                    b.RedoAllNextBake();
                    b.Bake(1.0, 240, out _, out _);
                    Assert.True(incremental.SequenceEqual(b.BlurredPlane), $"drifted at sample {i}");
                }
            });

            Assert.True(baker.Scale < 1.0, $"scale {baker.Scale}");
        }

        [AvaloniaFact]
        public void TheSprite_IsTheFullBakesShadow_AtTheSameCanvasPositions()
        {
            var all = Wander(600, 1.8, seed: 2).ToList();
            var g = Drawing(all[0], all.Take(2), color: Color.FromArgb(160, 20, 40, 60));
            var baker = Drive(g, all.Skip(2), ShadowSpriteCache.InteractiveMaxDimension);

            var incremental = baker.Bake(1.0, ShadowSpriteCache.InteractiveMaxDimension, out var originFromAnchor, out var scale);
            Assert.Equal(1.0, scale);
            var full = ShadowRenderer.Render(g, 1.0, out var originFromBounds);

            var incAlpha = ReadAlpha(incremental);
            var fullAlpha = ReadAlpha(full);
            var incRect = new Rect(g.Origin.X + originFromAnchor.X, g.Origin.Y + originFromAnchor.Y,
                                   incremental.PixelSize.Width, incremental.PixelSize.Height);
            var fullRect = new Rect(g.Bounds.Left + originFromBounds.X, g.Bounds.Top + originFromBounds.Y,
                                    full.PixelSize.Width, full.PixelSize.Height);
            Assert.True(incRect.Contains(fullRect.Inflate(-1)), $"incremental {incRect} does not cover full {fullRect}");

            // the two sprites are laid out on different (sub-pixel) grids; sample the full bake's
            // pixel centres in the incremental one — the blur has no gradient sharper than ~25/px
            long count = 0, sum = 0;
            int max = 0, nonzero = 0;
            for (int y = 0; y < full.PixelSize.Height; y++)
            {
                for (int x = 0; x < full.PixelSize.Width; x++)
                {
                    double cx = fullRect.X + x + 0.5, cy = fullRect.Y + y + 0.5;
                    int ix = (int)Math.Floor(cx - incRect.X), iy = (int)Math.Floor(cy - incRect.Y);
                    if (ix < 0 || iy < 0 || ix >= incremental.PixelSize.Width || iy >= incremental.PixelSize.Height)
                        continue;
                    int a = fullAlpha[y * full.PixelSize.Width + x];
                    int b = incAlpha[iy * incremental.PixelSize.Width + ix];
                    int d = Math.Abs(a - b);
                    count++;
                    sum += d;
                    max = Math.Max(max, d);
                    if (a > 0) nonzero++;
                }
            }

            Assert.True(nonzero > 2000, $"{nonzero} shadow pixels");
            Assert.True(max <= 24, $"max difference {max}");
            Assert.True(sum / (double)count < 1.0, $"mean difference {sum / (double)count:F2}");

            // translucent ink casts a lighter shadow in both (the blur of a stroke this thin never
            // saturates, so the peaks — on different sub-pixel grids — are compared to each other,
            // and both sit under the ink's alpha)
            Assert.InRange(incAlpha.Max(), fullAlpha.Max() - 8, fullAlpha.Max() + 8);
            Assert.True(fullAlpha.Max() <= 160 * ShadowRenderer.ShadowAlpha / 255 + 1, $"peak {fullAlpha.Max()}");
            Assert.True(fullAlpha.Max() > 128 * ShadowRenderer.ShadowAlpha / 255 / 2, $"peak {fullAlpha.Max()}");
        }

        [AvaloniaFact]
        public void TheCache_BakesInPlaceDuringADrag_AndFullAtRest()
        {
            var cache = new ShadowSpriteCache();
            var all = Wander(400, 1.5, seed: 4).ToList();
            var g = Drawing(all[0], all.Take(2));
            var list = new[] { (GraphicBase)g };

            // drawing: every event stales the sprite and the tick bakes it in place, same bitmap
            cache.BakeNext(list, 1.0, isToolDragActive: true);
            Assert.True(cache.TryGet(g, out var s1));
            Assert.True(s1.Anchored);
            Assert.True(s1.InteractiveCapped);

            double t = 3;
            int events = 0;
            foreach (var p in all.Skip(2))
            {
                if (!g.AddSample(p, t += 1.3, 0.5))
                    continue; // deduped: nothing changed, nothing to bake
                Assert.True(cache.NeedsBake(g));
                cache.BakeNext(list, 1.0, isToolDragActive: true);
                Assert.False(cache.NeedsBake(g));
                events++;
            }

            Assert.True(events > 300, $"{events} events");

            Assert.True(cache.TryGet(g, out var s2));
            Assert.True(s2.Anchored);
            Assert.Equal(g.ShadowRev, s2.ShadowRev);
            var dest = s2.GetDestRect(g);
            Assert.True(dest.Contains(g.Bounds.Inflate(-1)), $"sprite {dest} does not cover the ink {g.Bounds}");

            // a translation moves the anchored sprite with the ink, unstretched
            g.Move(30, -20);
            Assert.True(cache.TryGet(g, out var moved));
            Assert.Same(s2, moved);
            Assert.Equal(dest.Translate(new Vector(30, -20)), moved.GetDestRect(g));

            // the release: the samples are published, the drag ends, the rest tick re-bakes fully
            g.EndStroke();
            Assert.False(cache.BakeNext(list, 1.0, isToolDragActive: false));
            Assert.True(cache.TryGet(g, out var s3));
            Assert.NotSame(s2, s3);
            Assert.False(s3.Anchored);
            Assert.False(s3.InteractiveCapped);
            Assert.False(cache.NeedsBake(g));
        }
    }
}
