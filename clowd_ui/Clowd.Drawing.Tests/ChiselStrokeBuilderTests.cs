using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Ink;
using Clowd.Drawing.Tools;
using Xunit;
using Sample = Clowd.Drawing.Graphics.GraphicBrush.Sample;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// The highlighter's outline is the tip swept along the centreline: flat-ended, its full
    /// height across a horizontal pass and only its width across a vertical one, with no holes
    /// where the stroke loops, and the same pieces whether built live or cold. The geometry tests
    /// hold the tip upright (no seed); the slant has its own.
    /// </summary>
    public class ChiselStrokeBuilderTests
    {
        private const double Size = 20;
        private static readonly double HalfW = ChiselStrokeBuilder.TipSize(Size).Width / 2;

        private static Sample S(double x, double y, double t) => new Sample(new Point(x, y), t);

        private static Sample[] Line(Point from, Point to, int n)
        {
            var raw = new Sample[n + 1];
            for (int i = 0; i <= n; i++)
                raw[i] = new Sample(GraphicLine.Lerp(from, to, (double)i / n), i * 8);
            return raw;
        }

        private static ChiselStrokeBuilder Build(ReadOnlySpan<Sample> samples)
        {
            var b = new ChiselStrokeBuilder(Size, null);
            b.Update(samples);
            return b;
        }

        [AvaloniaFact]
        public void Click_IsTheTip()
        {
            var b = Build(new[] { S(10, 10, 0) });
            Assert.Equal(new Rect(10 - HalfW, 0, 2 * HalfW, Size), b.Bounds);
        }

        [AvaloniaFact]
        public void HorizontalPass_IsTipHigh_WithFlatEnds()
        {
            var b = Build(Line(new Point(0, 0), new Point(200, 0), 50));
            var bounds = b.Bounds;
            Assert.Equal(-Size / 2, bounds.Top, 6);
            Assert.Equal(Size / 2, bounds.Bottom, 6);
            Assert.Equal(-HalfW, bounds.Left, 6);
            Assert.Equal(200 + HalfW, bounds.Right, 6);

            // flat end: the corners of the end tip are inked, which a round cap would leave out
            Assert.True(b.FillContains(new Point(200 + HalfW - 0.5, Size / 2 - 0.5)));
            Assert.True(b.FillContains(new Point(-HalfW + 0.5, -Size / 2 + 0.5)));
        }

        [AvaloniaFact]
        public void VerticalPass_IsTipWide()
        {
            var b = Build(Line(new Point(0, 0), new Point(0, 200), 50));
            Assert.Equal(-HalfW, b.Bounds.Left, 6);
            Assert.Equal(HalfW, b.Bounds.Right, 6);
            Assert.False(b.FillContains(new Point(HalfW + 0.5, 100)));
        }

        [AvaloniaFact]
        public void Loop_LeavesNoHoleInTheInk_AndAllDirectionsFill()
        {
            // a square traced clockwise and back the other way: every sweep direction appears,
            // and each figure must add winding (a reversed one would punch a hole)
            var corners = new[] { new Point(0, 0), new Point(100, 0), new Point(100, 100), new Point(0, 100), new Point(0, 0), new Point(100, 100), new Point(100, 0), new Point(0, 100) };
            var raw = new Sample[(corners.Length - 1) * 20 + 1];
            int k = 0;
            for (int c = 0; c < corners.Length - 1; c++)
                for (int i = 0; i < 20; i++)
                    raw[k] = new Sample(GraphicLine.Lerp(corners[c], corners[c + 1], i / 20.0), 8 * k++);
            raw[k] = new Sample(corners[^1], 8 * k);

            var b = Build(raw);
            Assert.True(b.FillContains(new Point(50, 50)));   // where the diagonals cross
            Assert.True(b.FillContains(new Point(25, 25)));   // on a diagonal
            Assert.True(b.FillContains(new Point(50, 0)));    // on the top edge
            Assert.False(b.FillContains(new Point(50, 20)));  // between the top edge and the diagonals
        }

        [AvaloniaFact]
        public void Incremental_MatchesCold_AcrossChunks()
        {
            var raw = new Sample[1500];
            for (int i = 0; i < raw.Length; i++)
                raw[i] = S(i * 2.0, 40 * Math.Sin(i / 30.0), i * 4);

            var live = new ChiselStrokeBuilder(Size, null);
            for (int n = 1; n <= raw.Length; n += 7)
                live.Update(raw.AsSpan(0, n));
            live.Update(raw);

            var cold = Build(raw);
            Assert.True(cold.ChunkCount >= 2);
            Assert.Equal(cold.ChunkCount, live.ChunkCount);
            Assert.Equal(cold.Bounds, live.Bounds);

            for (int x = 0; x < 3000; x += 13)
                for (int y = -50; y <= 50; y += 7)
                    Assert.Equal(cold.FillContains(new Point(x, y)), live.FillContains(new Point(x, y)));
        }

        [AvaloniaFact]
        public void Highlighter_UsesTheChiselOutline_AndCastsNoShadow()
        {
            var g = new GraphicHighlighter(Colors.Yellow, Size, new Point(10, 10), 42);
            g.AddSample(new Point(60, 10), 16, 0.5);
            g.EndStroke();

            Assert.IsType<ChiselStrokeBuilder>(g.GetOutline());
            Assert.False(g.DropShadowEffect);
            g.DropShadowEffect = true;
            Assert.False(g.DropShadowEffect);
        }

        [AvaloniaFact]
        public void Seed_SlantsTheEnds_TheSameWayEveryBuild()
        {
            var raw = Line(new Point(0, 0), new Point(200, 0), 50);
            var a = new ChiselStrokeBuilder(Size, 7);
            a.Update(raw);
            var b = new ChiselStrokeBuilder(Size, 7);
            b.Update(raw);
            Assert.Equal(a.Bounds, b.Bounds);

            // a slanted flat end: the start's leftmost ink is at the top or the bottom of the
            // tip, not along a vertical edge
            double angle = ChiselStrokeBuilder.TipAngle(7, Size, 0);
            Assert.InRange(Math.Abs(angle) * 180 / Math.PI, ChiselStrokeBuilder.MinSlantDeg - ChiselStrokeBuilder.DriftDeg,
                           ChiselStrokeBuilder.MaxSlantDeg + ChiselStrokeBuilder.DriftDeg);
            double left = a.Bounds.Left;
            bool topInked = a.FillContains(new Point(left + 1, -Size / 2 * Math.Cos(angle) + 1.5));
            bool bottomInked = a.FillContains(new Point(left + 1, Size / 2 * Math.Cos(angle) - 1.5));
            Assert.NotEqual(topInked, bottomInked);

            // and different seeds lean differently
            Assert.NotEqual(ChiselStrokeBuilder.TipAngle(7, Size, 0), ChiselStrokeBuilder.TipAngle(8, Size, 0));
        }

        [AvaloniaFact]
        public void Ink_IsMultipliedIntoTheArtwork()
        {
            // white paper turns the ink's colour; black only lifts to a dim band of it, from the
            // screen pass (a plain alpha blend would wash it most of the way to yellow)
            var g = new GraphicHighlighter(Color.FromRgb(250, 255, 20), Size, new Point(0, 10), 3);
            g.AddSample(new Point(40, 10), 16, 0.5);
            g.EndStroke();

            using var rtb = new RenderTargetBitmap(new PixelSize(40, 20));
            using (var ctx = rtb.CreateDrawingContext())
            {
                ctx.FillRectangle(Brushes.White, new Rect(0, 0, 20, 20));
                ctx.FillRectangle(Brushes.Black, new Rect(20, 0, 20, 20));
                g.DrawObject(ctx);
            }

            var px = new byte[40 * 20 * 4];
            using (var wb = new WriteableBitmap(new PixelSize(40, 20), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul))
            using (var fb = wb.Lock())
            {
                rtb.CopyPixels(new PixelRect(0, 0, 40, 20), fb.Address, fb.RowBytes * 20, fb.RowBytes);
                for (int y = 0; y < 20; y++)
                    Marshal.Copy(fb.Address + y * fb.RowBytes, px, y * 40 * 4, 40 * 4);
            }

            (byte r, byte g, byte b) At(int x, int y) { int i = (y * 40 + x) * 4; return (px[i + 2], px[i + 1], px[i]); }
            var white = At(10, 10);
            Assert.InRange(white.r, 249, 255);
            Assert.Equal(255, white.g);
            Assert.InRange(white.b, 18, 30);
            var black = At(30, 10);
            Assert.InRange(black.r, 45, 65);   // 0.22 of the ink
            Assert.InRange(black.g, 45, 65);
            Assert.InRange(black.b, 0, 10);
        }

        [Fact]
        public void RectCursor_IsWhiteInsideBlack()
        {
            var px = BrushCursor.RasterizeRect(5, 9, out int w, out int h);
            Assert.Equal(9, w);
            Assert.Equal(13, h);

            byte A(int x, int y) => px[(y * w + x) * 4 + 3];
            byte G(int x, int y) => px[(y * w + x) * 4];
            Assert.Equal(0, A(0, 0));                               // slack
            Assert.Equal((255, 0), (A(1, 6), G(1, 6)));             // black band, left
            Assert.Equal((255, 255), (A(2, 6), G(2, 6)));           // white band, left
            Assert.Equal(0, A(4, 6));                               // clear core
            Assert.Equal((255, 255), (A(6, 6), G(6, 6)));           // white band, right
            Assert.Equal((255, 0), (A(7, 6), G(7, 6)));             // black band, right
        }
    }
}
