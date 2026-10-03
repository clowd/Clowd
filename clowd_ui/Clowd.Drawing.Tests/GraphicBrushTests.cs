using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Ink;
using Clowd.Drawing.Rendering;
using Clowd.Drawing.Tools;
using Xunit;
using Sample = Clowd.Drawing.Graphics.GraphicBrush.Sample;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// The brush stroke as a graphic (NonZero fill through self-crossings, bounds and hit
    /// testing in local coordinates, a translation that keeps the outline, the stroke-width
    /// rebuild, sample capture rules, the history grammar) and the brush tool driven with
    /// synthetic pointer states: one stroke → one selected graphic, one step, back to the
    /// pointer, and the live outline is the final outline.
    /// </summary>
    public class GraphicBrushTests
    {
        static GraphicBrushTests()
        {
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
        }

        private static readonly DpiScale Dpi = new DpiScale(1, 1);
        private const double Frame = FreehandStroke.RefFrameMs;

        private static Sample S(double x, double y, double t) => new Sample(new Point(x, y), t);

        /// <summary>A stroke at <paramref name="origin"/> running 100 units along +x at 2 per frame.</summary>
        private static GraphicBrush MakeLine(Point origin, double lineWidth = 3)
        {
            var g = new GraphicBrush(Colors.Red, lineWidth, origin);
            for (int i = 1; i <= 50; i++)
                g.AddSample(new Point(origin.X + i * 2, origin.Y), i * Frame, 0.5);
            g.EndStroke();
            return g;
        }

        private static Sample[] Lemniscate(double a, int n)
        {
            var raw = new Sample[n + 1];
            for (int i = 0; i <= n; i++)
            {
                double th = 2 * Math.PI * i / n;
                double d = 1 + Math.Sin(th) * Math.Sin(th);
                raw[i] = S(a * Math.Cos(th) / d, a * Math.Sin(th) * Math.Cos(th) / d, i * Frame);
            }

            return raw;
        }

        [AvaloniaFact]
        public void BuildGeometry_FillsTheCrossing_OfAFigureEight()
        {
            // the two passes through the centre overlap; EvenOdd would punch a hole there
            var geometry = FreehandStroke.BuildGeometry(Lemniscate(60, 120), 6);

            Assert.True(geometry.FillContains(new Point(0, 0)));
            Assert.True(geometry.FillContains(new Point(60, 0)));    // the right lobe's apex, on the ink
            Assert.False(geometry.FillContains(new Point(30, 12)));  // inside the lobe, off the ink
        }

        [AvaloniaFact]
        public void Bounds_AreTheOutlineBounds_TranslatedToTheOrigin()
        {
            var g = MakeLine(new Point(100, 200));

            var local = g.GetStroke().Bounds;
            var expected = local.Translate(new Vector(100, 200));
            Assert.Equal(expected, g.Bounds);

            // sanity: the stroke runs from the origin 100 along +x (plus the round caps), a few units wide
            Assert.InRange(g.Bounds.Left, 96, 100);
            Assert.InRange(g.Bounds.Right, 200, 205);
            Assert.True(g.Bounds.Height > 3 && g.Bounds.Height < 12, $"height {g.Bounds.Height}");
            Assert.Equal(200, (g.Bounds.Top + g.Bounds.Bottom) / 2, 0.5);
        }

        [AvaloniaFact]
        public void Move_OffsetsTheOriginAndCachedBounds_AndKeepsTheGeometry()
        {
            var g = MakeLine(new Point(0, 0));
            var before = g.Bounds;
            var geometry = g.GetStroke().Tail;

            g.Move(5, 7);

            Assert.Equal(new Point(5, 7), g.Origin);
            Assert.Same(geometry, g.RenderCache.Geometry);
            Assert.NotNull(g.RenderCache.CachedBounds);
            Assert.Equal(before.Translate(new Vector(5, 7)), g.Bounds);

            // the warm bounds agree with a cold recompute
            g.RenderCache.Clear(InvalidationAspects.All);
            Assert.Equal(before.Translate(new Vector(5, 7)), g.Bounds);
        }

        [AvaloniaFact]
        public void MakeHitTest_HitsTheInk_AndTheMargin_MissesFarther()
        {
            var g = MakeLine(new Point(0, 0));

            Assert.Equal(0, g.MakeHitTest(new Point(50, 0), Dpi));
            Assert.True(g.Contains(new Point(50, 0)));

            // the ink is ~3 wide at speed; the 8-unit pen reaches 4 past its edge
            Assert.Equal(0, g.MakeHitTest(new Point(50, 5), Dpi));
            Assert.False(g.Contains(new Point(50, 5)));
            Assert.Equal(-1, g.MakeHitTest(new Point(50, 12), Dpi));
            Assert.Equal(-1, g.MakeHitTest(new Point(130, 0), Dpi));

            Assert.Equal(0, g.HandleCount);
        }

        [AvaloniaFact]
        public void LineWidth_RebuildsTheOutline_AndWidensTheBounds()
        {
            var g = MakeLine(new Point(0, 0));
            var thin = g.Bounds;
            var geometry = g.GetStroke().Tail;

            g.LineWidth = 9;

            Assert.Null(g.RenderCache.Geometry);
            Assert.Equal(18, g.Size);
            Assert.NotSame(geometry, g.GetStroke().Tail);
            Assert.True(g.Bounds.Height > thin.Height * 2, $"thin {thin.Height} thick {g.Bounds.Height}");
        }

        [AvaloniaFact]
        public void AddSample_StoresLocalPoints_DedupesCloseOnes_AndClampsTime()
        {
            var g = new GraphicBrush(Colors.Red, 3, new Point(10, 10));
            Assert.Single(g.Samples);
            Assert.Equal(S(0, 0, 0), g.Samples[0]);

            Assert.True(g.AddSample(new Point(20, 10), 16, 0.5));
            Assert.False(g.AddSample(new Point(20.3, 10), 32, 0.5)); // within the threshold
            Assert.True(g.AddSample(new Point(30, 10), 10, 0.5));    // time runs backwards: clamped
            Assert.Equal(3, g.SampleCount);

            // the persisted array is always the one Samples returns; the live ones are GetSamples()
            Assert.Single(g.Samples);
            Assert.Equal(3, g.GetSamples().Length);

            g.EndStroke();
            Assert.Equal(new[] { S(0, 0, 0), S(10, 0, 16), S(20, 0, 16) }, g.Samples);
        }

        [AvaloniaFact]
        public void SampleEdit_EmitsExactlyThatMembersPath_AndUndoRestoresTheArray()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var g = MakeLine(new Point(0, 0));
            canvas.GraphicsList.Add(g);
            canvas.AddCommandToHistory(false);
            var original = (Sample[])g.Samples.Clone();

            SortedSet<string> built = null;
            Action<UndoManager, SortedSet<string>> hook = (_, changes) => built = changes;
            UndoManager.DiagnosticCommitBuilt += hook;
            try
            {
                var edited = (Sample[])g.Samples.Clone();
                edited[2] = edited[2] with { T = 99 };
                g.Samples = edited;
                canvas.AddCommandToHistory(false);
                Assert.Equal(new[] { $"root/Graphics/{g.Id}/samples/item.2/T" }, built);

                g.AddSample(new Point(120, 0), 2000, 0.5);
                g.EndStroke();
                canvas.AddCommandToHistory(false);
                Assert.Equal(new[] { $"root/Graphics/{g.Id}/samples/item.{original.Length}" }, built);

                g.Move(3, 4);
                canvas.AddCommandToHistory(false);
                Assert.Equal(new[] { $"root/Graphics/{g.Id}/origin" }, built);
            }
            finally
            {
                UndoManager.DiagnosticCommitBuilt -= hook;
            }

            _ = g.GetStroke();
            canvas.Undo();
            canvas.Undo();
            canvas.Undo();
            Assert.Same(g, canvas.GraphicsList[0]);
            Assert.Equal(original, g.Samples);
            Assert.Equal(new Point(0, 0), g.Origin);
            Assert.Null(g.RenderCache.Geometry);
        }

        // ====================================================================
        // The tool
        // ====================================================================

        private static IPointer MakePointer() => new Pointer(1, PointerType.Mouse, true);

        private static PointerState Down(Point p, IPointer pointer, ulong t) =>
            new PointerState(p, KeyModifiers.None, true, false, false, pointer, t, null);

        private static PointerState Move(Point p, IPointer pointer, ulong t) =>
            new PointerState(p, KeyModifiers.None, true, false, false, pointer, t, null);

        private static PointerState Up(Point p, IPointer pointer, ulong t) =>
            new PointerState(p, KeyModifiers.None, false, false, false, pointer, t, null);

        private static int CountSteps(Action action)
        {
            int steps = 0;
            Action<UndoManager, SortedSet<string>> hook = (_, changes) => { if (changes.Count > 0) steps++; };
            UndoManager.DiagnosticCommitBuilt += hook;
            try
            {
                action();
            }
            finally
            {
                UndoManager.DiagnosticCommitBuilt -= hook;
            }

            return steps;
        }

        [AvaloniaFact]
        public void OneStroke_IsOneSelectedGraphic_OneStep_AndRevertsToThePointer()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.Brush };
            var pointer = MakePointer();
            var tool = canvas.ToolBrush;

            GraphicBrush stroke = null;
            var steps = CountSteps(() =>
            {
                tool.OnMouseDown(canvas, Down(new Point(10, 10), pointer, 1000), 1);
                stroke = Assert.IsType<GraphicBrush>(Assert.Single(canvas.GraphicsList));
                tool.OnMouseMove(canvas, Move(new Point(20, 12), pointer, 1016));
                tool.OnMouseMove(canvas, Move(new Point(30, 15), pointer, 1032));
                tool.OnMouseMove(canvas, Move(new Point(40, 19), pointer, 1048));
                tool.OnMouseUp(canvas, Up(new Point(40, 19), pointer, 1060));
            });

            Assert.Equal(1, steps);
            Assert.Same(stroke, Assert.Single(canvas.GraphicsList));
            Assert.True(stroke.IsSelected);
            Assert.Equal(ToolType.Pointer, canvas.Tool);
            Assert.False(canvas.IsMouseCaptured);

            // the press, three moves; the release sat on the last move and was deduped
            Assert.Equal(new[] { S(0, 0, 0), S(10, 2, 16), S(20, 5, 32), S(30, 9, 48) }, stroke.Samples);
            Assert.Equal(new Point(10, 10), stroke.Origin);
            Assert.Equal(canvas.LineWidth, stroke.LineWidth);
        }

        [AvaloniaFact]
        public void SyntheticReplay_AddsNoSample_AndAStalledEventAddsAtMostTheGapCap()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.Brush };
            var pointer = MakePointer();
            var tool = canvas.ToolBrush;

            tool.OnMouseDown(canvas, Down(new Point(0, 0), pointer, 500), 1);
            var stroke = Assert.IsType<GraphicBrush>(Assert.Single(canvas.GraphicsList));

            tool.OnMouseMove(canvas, new PointerState(new Point(10, 0), KeyModifiers.Shift, true, false, false, null, 516, null));
            Assert.Equal(1, stroke.SampleCount);

            // a two-second hitch is one 50 ms step on the stroke's clock
            tool.OnMouseMove(canvas, Move(new Point(10, 0), pointer, 2500));
            Assert.Equal(2, stroke.SampleCount);
            Assert.Equal(ToolBrush.MaxEventGapMs, stroke.GetSamples()[1].T); // still being collected: not in Samples yet

            tool.OnMouseUp(canvas, Up(new Point(20, 0), pointer, 2516));
            Assert.Equal(3, stroke.Samples.Length);
            Assert.Equal(ToolBrush.MaxEventGapMs + 16, stroke.Samples[2].T);
        }

        [AvaloniaFact]
        public void TheLiveOutline_IsTheFinalOutline()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.Brush };
            var pointer = MakePointer();
            var tool = canvas.ToolBrush;

            tool.OnMouseDown(canvas, Down(new Point(0, 0), pointer, 0), 1);
            var stroke = Assert.IsType<GraphicBrush>(Assert.Single(canvas.GraphicsList));
            for (int i = 1; i <= 40; i++)
                tool.OnMouseMove(canvas, Move(new Point(i * 2.5, 8 * Math.Sin(i / 5.0)), pointer, (ulong)(i * 16)));

            // what the last frame before the release drew
            var live = stroke.GetStroke().Tail;
            var liveBounds = stroke.Bounds;

            tool.OnMouseUp(canvas, Up(new Point(100, 8 * Math.Sin(8.0)), pointer, 41 * 16));

            // the release publishes the samples without touching the cached outline...
            Assert.Same(live, stroke.RenderCache.Geometry);

            // ...and a cold rebuild from the persisted samples is the same shape
            stroke.TrimTransientCaches(); // drops the builder too: a cold rebuild, as after a load
            var final = stroke.GetStroke().Tail;
            Assert.NotSame(live, final);
            Assert.Equal(liveBounds, stroke.Bounds);
            Assert.Equal(live.Bounds, final.Bounds);
            foreach (var probe in new[] { new Point(20, 8 * Math.Sin(8 / 5.0)), new Point(50, 8 * Math.Sin(4.0)), new Point(50, 50) })
                Assert.Equal(live.FillContains(probe), final.FillContains(probe));
        }

        [AvaloniaFact]
        public void Escape_MidStroke_DropsIt()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.Brush };
            var pointer = MakePointer();
            var tool = canvas.ToolBrush;

            tool.OnMouseDown(canvas, Down(new Point(0, 0), pointer, 0), 1);
            tool.OnMouseMove(canvas, Move(new Point(10, 0), pointer, 16));
            Assert.Single(canvas.GraphicsList);

            canvas.CancelCurrentOperation();
            Assert.Empty(canvas.GraphicsList);
            Assert.False(canvas.CommandUndo.CanExecute(null));
        }
    }
}
