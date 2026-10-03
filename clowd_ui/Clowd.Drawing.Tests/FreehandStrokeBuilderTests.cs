using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Ink;
using Clowd.Drawing.Rendering;
using Xunit;
using Sample = Clowd.Drawing.Graphics.GraphicBrush.Sample;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// The incremental outline is the batch outline: fed one sample at a time, or in uneven
    /// bursts, the builder's edge vertices are bitwise those of a batch pass over the same
    /// samples at every length, its chunking is a function of the samples alone (live == cold),
    /// and the union of its pieces fills what the single batch figure fills — through
    /// self-crossings, with translucent ink painted once, at every chunk boundary.
    /// </summary>
    public class FreehandStrokeBuilderTests
    {
        private const double Size = 12; // the default LineWidth 6
        private const double Frame = FreehandStroke.RefFrameMs;

        private static Sample S(double x, double y, double t) => new Sample(new Point(x, y), t);

        /// <summary>A dense scribble like a fast mouse: ~1 ms per sample, wandering with
        /// reversals in a box, so corner caps, the end-noise trim and the start rule all fire.</summary>
        private static Sample[] Scribble(int n, int seed = 1234)
        {
            var raw = new Sample[n];
            var rnd = new Random(seed);
            double x = 0, y = 0, vx = 1, vy = 0.3;
            for (int i = 0; i < n; i++)
            {
                vx += (rnd.NextDouble() - 0.5) * 0.4;
                vy += (rnd.NextDouble() - 0.5) * 0.4;
                vx = Math.Clamp(vx, -2.5, 2.5);
                vy = Math.Clamp(vy, -2.5, 2.5);
                if (i % 97 == 0) { vx = -vx; vy = -vy; } // a sharp reversal now and then
                x += vx; y += vy;
                if (x < 0 || x > 400) vx = -vx;
                if (y < 0 || y > 300) vy = -vy;
                raw[i] = S(x, y, i * (0.8 + rnd.NextDouble() * 0.6));
            }

            return raw;
        }

        private static Sample[] Lemniscate(double a, int n)
        {
            var raw = new Sample[n + 1];
            for (int i = 0; i <= n; i++)
            {
                double th = 2 * Math.PI * i / n;
                double d = 1 + Math.Sin(th) * Math.Sin(th);
                raw[i] = S(a * Math.Cos(th) / d, a * Math.Sin(th) * Math.Cos(th) / d, i * Frame / 4);
            }

            return raw;
        }

        private static (List<Point> Left, List<Point> Right) BatchEdges(ReadOnlySpan<Sample> raw)
        {
            var points = new List<FreehandStroke.StrokePoint>();
            var left = new List<Point>();
            var right = new List<Point>();
            var outline = new List<Point>();
            FreehandStroke.GetStrokePoints(raw, Size, points);
            FreehandStroke.GetStrokeOutlinePoints(points, Size, left, right, outline);
            return (left, right);
        }

        private static void AssertEdgesMatchBatch(FreehandStrokeBuilder builder, ReadOnlySpan<Sample> raw, string where)
        {
            var (bl, br) = BatchEdges(raw);
            var il = new List<Point>();
            var ir = new List<Point>();
            builder.GetEdges(il, ir);
            Assert.True(bl.SequenceEqual(il), $"{where}: left edge differs (batch {bl.Count}, incremental {il.Count})");
            Assert.True(br.SequenceEqual(ir), $"{where}: right edge differs (batch {br.Count}, incremental {ir.Count})");
        }

        [AvaloniaFact]
        public void OneSampleAtATime_IsTheBatchOutline_AtEveryLength()
        {
            var raw = Scribble(1200);
            var builder = new FreehandStrokeBuilder(Size);
            int sealedAt = -1;
            for (int n = 1; n <= raw.Length; n++)
            {
                builder.Update(raw.AsSpan(0, n));
                if (n >= 3)
                    AssertEdgesMatchBatch(builder, raw.AsSpan(0, n), $"n={n}");
                if (sealedAt < 0 && builder.ChunkCount > 0)
                    sealedAt = n;
            }

            // the stroke is long enough to have crossed chunk boundaries, so the check covered them
            Assert.True(builder.ChunkCount >= 3, $"only {builder.ChunkCount} chunks sealed");
            Assert.InRange(sealedAt, FreehandStrokeBuilder.ChunkPoints, FreehandStrokeBuilder.ChunkPoints + 40);
        }

        [AvaloniaFact]
        public void UnevenBursts_SealTheSameChunks_AsOneAtATime_AndAsCold()
        {
            var raw = Scribble(2000, seed: 7);
            var rnd = new Random(42);

            var bursts = new FreehandStrokeBuilder(Size);
            var singles = new FreehandStrokeBuilder(Size);
            int n = 0;
            while (n < raw.Length)
            {
                n = Math.Min(raw.Length, n + 1 + rnd.Next(12)); // 1–12 samples per "event"
                bursts.Update(raw.AsSpan(0, n));
                for (int k = singles.SampleCount + 1; k <= n; k++)
                    singles.Update(raw.AsSpan(0, k));
                Assert.Equal(singles.ChunkCount, bursts.ChunkCount);
                Assert.Equal(singles.OpenStart, bursts.OpenStart);
                Assert.Equal(singles.OutlineSettled, bursts.OutlineSettled);
            }

            var cold = new FreehandStrokeBuilder(Size);
            cold.Update(raw);
            Assert.True(cold.ChunkCount >= 5, $"only {cold.ChunkCount} chunks");
            Assert.Equal(singles.ChunkCount, cold.ChunkCount);
            Assert.Equal(singles.OpenStart, cold.OpenStart);
            Assert.Equal(singles.Bounds, cold.Bounds);
            Assert.Equal(singles.Settled.Bounds, cold.Settled.Bounds);
            Assert.Equal(singles.Tail.Bounds, cold.Tail.Bounds);
            AssertEdgesMatchBatch(cold, raw, "cold");
            AssertEdgesMatchBatch(singles, raw, "live");
        }

        [AvaloniaFact]
        public void ThePieces_FillWhatTheBatchFigureFills()
        {
            // probe a grid; a probe whose 1 px neighbourhood is all-in or all-out of the batch
            // figure (away from the anti-aliased edge) must agree with the union of the pieces
            var raw = Scribble(1500, seed: 3);
            var batch = FreehandStroke.BuildGeometry(raw, Size);
            var builder = new FreehandStrokeBuilder(Size);
            builder.Update(raw);
            Assert.True(builder.ChunkCount >= 3);

            var b = batch.Bounds;
            int probes = 0, inside = 0;
            for (double y = b.Top; y <= b.Bottom; y += 1.5)
            {
                for (double x = b.Left; x <= b.Right; x += 1.5)
                {
                    var p = new Point(x, y);
                    bool c = batch.FillContains(p);
                    bool clear = true;
                    for (int k = 0; k < 8 && clear; k++)
                    {
                        double a = Math.PI * k / 4;
                        clear = batch.FillContains(new Point(x + Math.Cos(a), y + Math.Sin(a))) == c;
                    }

                    if (!clear)
                        continue;
                    probes++;
                    if (c) inside++;
                    Assert.True(c == builder.FillContains(p), $"({x:F1},{y:F1}): batch {c}, pieces {!c}");
                }
            }

            Assert.True(inside > 500 && probes - inside > 500, $"{inside} in, {probes - inside} out");
        }

        [AvaloniaFact]
        public void SelfCrossings_AreFilled_AcrossChunks()
        {
            // a figure-eight dense enough to span several chunks: the crossing (a settled chunk
            // crossed by a later one, and finally by the tail) is ink, not a hole
            var raw = Lemniscate(80, 1400);
            var builder = new FreehandStrokeBuilder(Size);
            builder.Update(raw);
            Assert.True(builder.ChunkCount >= 3, $"{builder.ChunkCount} chunks");

            Assert.True(builder.FillContains(new Point(0, 0)));
            Assert.True(builder.FillContains(new Point(80, 0)));
            Assert.False(builder.FillContains(new Point(40, 16)));
        }

        [AvaloniaFact]
        public void TranslucentInk_PaintsOnce_AcrossTheChunkOverlaps()
        {
            // the overlaps of the sealed chunks and of the tail must not show darker: nothing in
            // the rendered stroke exceeds the ink's alpha, and the interior reaches it
            var raw = Scribble(1500, seed: 11);
            var g = new GraphicBrush(Color.FromArgb(100, 0, 0, 0), Size / 2, new Point(0, 0)) { Samples = raw };
            Assert.True(g.GetStroke().ChunkCount >= 3);

            var bounds = g.Bounds;
            int w = (int)Math.Ceiling(bounds.Width) + 4, h = (int)Math.Ceiling(bounds.Height) + 4;
            var alpha = new byte[w * h];
            using (var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96)))
            {
                ShadowRenderer.RasterAlpha(rtb, new PixelRect(0, 0, w, h), ctx =>
                {
                    using (ctx.PushTransform(Matrix.CreateTranslation(-bounds.Left + 2, -bounds.Top + 2)))
                        g.DrawObject(ctx);
                }, alpha);
            }

            Assert.Equal(100, alpha.Max());
            Assert.True(alpha.Count(a => a == 100) > 2000, "little interior at the ink's alpha");
        }

        [AvaloniaFact]
        public void ADotAndADash_AreBatched_AndAStrokeBackOnItsStart_IsADot()
        {
            var dot = new FreehandStrokeBuilder(Size);
            dot.Update(new[] { S(5, 5, 0) });
            Assert.Null(dot.Settled);
            Assert.Equal(FreehandStroke.BuildGeometry(new[] { S(5, 5, 0) }, Size).Bounds, dot.Tail.Bounds);

            var dash = new FreehandStrokeBuilder(Size);
            dash.Update(new[] { S(0, 0, 0) });
            dash.Update(new[] { S(0, 0, 0), S(40, 0, 40) });
            Assert.Null(dash.Settled);
            Assert.Equal(FreehandStroke.BuildGeometry(new[] { S(0, 0, 0), S(40, 0, 40) }, Size).Bounds, dash.Tail.Bounds);

            // three samples, the middle swallowed by the start rule, the cursor back on the origin
            var back = new[] { S(0, 0, 0), S(0.6, 0, 1), S(0, 0, 2) };
            var builder = new FreehandStrokeBuilder(Size);
            foreach (var n in new[] { 1, 2, 3 })
                builder.Update(back.AsSpan(0, n));
            Assert.Equal(FreehandStroke.BuildGeometry(back, Size).Bounds, builder.Tail.Bounds);
            Assert.True(builder.Tail.FillContains(new Point(0, 0)));
        }

        [AvaloniaFact]
        public void Compact_KeepsTheGeometries_AndRefusesMoreSamples()
        {
            var raw = Scribble(600);
            var builder = new FreehandStrokeBuilder(Size);
            builder.Update(raw);
            var settled = builder.Settled;
            var tail = builder.Tail;
            var bounds = builder.Bounds;

            builder.Compact();

            Assert.Same(settled, builder.Settled);
            Assert.Same(tail, builder.Tail);
            Assert.Equal(bounds, builder.Bounds);
            Assert.True(builder.CanAppend(raw.Length));
            Assert.False(builder.CanAppend(raw.Length + 1));
            Assert.False(builder.CanAppend(raw.Length - 1));
            builder.Update(raw); // a no-op
            Assert.Same(tail, builder.Tail);
        }

        [AvaloniaFact]
        public void AddSample_WithoutNotify_RaisesOnce_OnNotifySamplesChanged()
        {
            var g = new GraphicBrush(Colors.Red, 3, new Point(0, 0));
            int raises = 0;
            g.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(GraphicBrush.Samples)) raises++; };

            Assert.True(g.AddSample(new Point(10, 0), 16, 0.5, notify: false));
            Assert.True(g.AddSample(new Point(20, 0), 17, 0.5, notify: false));
            Assert.True(g.AddSample(new Point(30, 0), 18, 0.5, notify: false));
            Assert.Equal(0, raises);

            g.NotifySamplesChanged();
            Assert.Equal(1, raises);
            Assert.Equal(4, g.SampleCount);
        }
    }
}
