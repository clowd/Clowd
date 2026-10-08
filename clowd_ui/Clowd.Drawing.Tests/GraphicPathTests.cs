using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Rendering;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// The pen path: handle numbering in three runs of n, hit-test priority (live handles beat
    /// anchors, absent ones are skipped), the stroke corridor, the smooth/corner handle rules with
    /// Shift/Alt, translation (anchors and cached bounds together), bounds that include the stroke
    /// and round caps, straight segments drawn as lines, the closing segment, anchor removal, and
    /// the double-click type toggle as one history step.
    /// </summary>
    public class GraphicPathTests
    {
        static GraphicPathTests()
        {
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
        }

        private static readonly DpiScale Dpi = new DpiScale(1, 1);

        private static void AssertPointClose(Point expected, Point actual, double tol = 1e-9)
        {
            Assert.True(Math.Abs(expected.X - actual.X) < tol && Math.Abs(expected.Y - actual.Y) < tol,
                        $"expected {expected} actual {actual}");
        }

        // (0,0) corner → (50,0) smooth with 10-unit handles → (100,50) corner
        private static GraphicPath MakeThree(double lineWidth = 4)
        {
            var g = new GraphicPath(Colors.Red, lineWidth, new Point(0, 0));
            g.AppendAnchor(new PathAnchor(new Point(50, 0), new Point(-10, 0), new Point(10, 0), true));
            g.AppendAnchor(PathAnchor.Corner(new Point(100, 50)));
            return g;
        }

        [AvaloniaFact]
        public void HandleNumbering_RoundTrips_ThroughAnchorOfAndKindOf()
        {
            var g = MakeThree();
            Assert.Equal(9, g.HandleCount);

            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(i, GraphicPath.AnchorOf(g.AnchorHandle(i), 3));
                Assert.Equal(GraphicPath.HandleKind.Anchor, GraphicPath.KindOf(g.AnchorHandle(i), 3));
                Assert.Equal(i, GraphicPath.AnchorOf(g.InHandle(i), 3));
                Assert.Equal(GraphicPath.HandleKind.In, GraphicPath.KindOf(g.InHandle(i), 3));
                Assert.Equal(i, GraphicPath.AnchorOf(g.OutHandle(i), 3));
                Assert.Equal(GraphicPath.HandleKind.Out, GraphicPath.KindOf(g.OutHandle(i), 3));
            }

            Assert.Equal(new[] { 1, 2, 3 }, new[] { g.AnchorHandle(0), g.AnchorHandle(1), g.AnchorHandle(2) });
            Assert.Equal(new[] { 4, 5, 6 }, new[] { g.InHandle(0), g.InHandle(1), g.InHandle(2) });
            Assert.Equal(new[] { 7, 8, 9 }, new[] { g.OutHandle(0), g.OutHandle(1), g.OutHandle(2) });

            Assert.True(g.IsEndpointHandle(1));
            Assert.True(g.IsEndpointHandle(3));
            Assert.False(g.IsEndpointHandle(2));
            Assert.False(g.IsEndpointHandle(g.OutHandle(2)));
            g.Closed = true;
            Assert.False(g.IsEndpointHandle(1));
        }

        [AvaloniaFact]
        public void GetHandle_AnchorsAreThePoint_HandlesAreTheOffsetTips()
        {
            var g = MakeThree();
            AssertPointClose(new Point(50, 0), g.GetHandle(g.AnchorHandle(1), Dpi));
            AssertPointClose(new Point(40, 0), g.GetHandle(g.InHandle(1), Dpi));
            AssertPointClose(new Point(60, 0), g.GetHandle(g.OutHandle(1), Dpi));
            AssertPointClose(new Point(100, 50), g.GetHandle(g.AnchorHandle(2), Dpi));
        }

        [AvaloniaFact]
        public void MakeHitTest_LiveHandlesWinOverTheAnchorTheyOverlap_AbsentHandlesAreSkipped()
        {
            var g = MakeThree();
            g.IsSelected = true;

            // squarely on anchor 1: the handle dots are 10 away, outside their 12-unit squares
            Assert.Equal(g.AnchorHandle(1), g.MakeHitTest(new Point(50, 0), Dpi));

            // inside both the In handle's square (34..46) and the anchor's (44..56): the handle wins
            Assert.Equal(g.InHandle(1), g.MakeHitTest(new Point(45, 0), Dpi));
            Assert.Equal(g.OutHandle(1), g.MakeHitTest(new Point(55, 0), Dpi));

            // a handle too short to count sits on the anchor and is never offered
            g.MoveHandleTo(new Point(100.3, 50), g.OutHandle(2), KeyModifiers.Alt);
            Assert.False(g.Anchors[2].HasOut);
            Assert.Equal(g.AnchorHandle(2), g.MakeHitTest(new Point(100, 50), Dpi));

            // unselected: no handles at all, only the body
            g.IsSelected = false;
            Assert.Equal(0, g.MakeHitTest(new Point(50, 0), Dpi));
        }

        [AvaloniaFact]
        public void BodyCorridor_IsTheStrokePlusEightUnits()
        {
            var g = new GraphicPath(Colors.Red, 4, new Point(0, 0));
            g.AppendAnchor(PathAnchor.Corner(new Point(100, 0)));

            // (4 + 8) / 2 = 6 either side of the line
            Assert.Equal(0, g.MakeHitTest(new Point(50, 5), Dpi));
            Assert.Equal(-1, g.MakeHitTest(new Point(50, 8), Dpi));
            Assert.True(g.Contains(new Point(50, 3)));
            Assert.False(g.Contains(new Point(50, 7)));
        }

        [AvaloniaFact]
        public void MoveHandleTo_OnASmoothAnchor_KeepsTheOppositeCollinear_WithItsOwnLength()
        {
            var g = MakeThree();
            g.MoveHandleTo(new Point(50, 20), g.OutHandle(1));

            var a = g.Anchors[1];
            AssertPointClose(new Point(0, 20), a.Out);
            AssertPointClose(new Point(0, -10), a.In); // turned, still 10 long
            Assert.True(a.Smooth);
            Assert.Equal(1, g.ActiveAnchor);
        }

        [AvaloniaFact]
        public void MoveHandleTo_WithAlt_BreaksTheSymmetry_AndMakesACorner()
        {
            var g = MakeThree();
            g.MoveHandleTo(new Point(50, 20), g.OutHandle(1), KeyModifiers.Alt);

            var a = g.Anchors[1];
            AssertPointClose(new Point(0, 20), a.Out);
            AssertPointClose(new Point(-10, 0), a.In);
            Assert.False(a.Smooth);
        }

        [AvaloniaFact]
        public void MoveHandleTo_WithShift_SnapsTheHandleTo15DegreeSteps()
        {
            var g = MakeThree();
            g.MoveHandleTo(new Point(60, 1), g.OutHandle(1), KeyModifiers.Shift);

            var a = g.Anchors[1];
            Assert.Equal(0, a.Out.Y, 9);
            Assert.Equal(Math.Sqrt(101), a.Out.X, 9);
        }

        [AvaloniaFact]
        public void MovingAnAnchor_CarriesBothHandleTips()
        {
            var g = MakeThree();
            g.MoveHandleTo(new Point(60, 10), g.AnchorHandle(1));

            AssertPointClose(new Point(50, 10), g.GetHandle(g.InHandle(1), Dpi));
            AssertPointClose(new Point(70, 10), g.GetHandle(g.OutHandle(1), Dpi));
            AssertPointClose(new Point(-10, 0), g.Anchors[1].In);
        }

        [AvaloniaFact]
        public void ResolveGrab_AltOnAnAnchor_PullsFreshMirroredHandlesOutOfIt()
        {
            var g = MakeThree();
            var grab = g.ResolveGrab(g.AnchorHandle(2), KeyModifiers.Alt);
            Assert.Equal(g.OutHandle(2), grab);

            // Alt is still held during the drag; the mirror gesture wins over the cusp rule
            g.MoveHandleTo(new Point(100, 80), grab, KeyModifiers.Alt);
            var a = g.Anchors[2];
            AssertPointClose(new Point(0, 30), a.Out);
            AssertPointClose(new Point(0, -30), a.In);
            Assert.True(a.Smooth);

            // Normalize ends the gesture: the next Alt drag is an ordinary cusp edit again
            g.Normalize();
            g.MoveHandleTo(new Point(100, 90), g.OutHandle(2), KeyModifiers.Alt);
            AssertPointClose(new Point(0, -30), g.Anchors[2].In);
            Assert.False(g.Anchors[2].Smooth);

            Assert.Equal(g.AnchorHandle(2), g.ResolveGrab(g.AnchorHandle(2), KeyModifiers.None));
        }

        [AvaloniaFact]
        public void Move_TranslatesEveryAnchor_AndTheCachedBounds()
        {
            var g = MakeThree();
            var before = g.Bounds; // warm the cache so Move takes the TranslateCachedBounds path

            g.Move(5, 7);

            AssertPointClose(new Point(5, 7), g.Anchors[0].P);
            AssertPointClose(new Point(55, 7), g.Anchors[1].P);
            AssertPointClose(new Point(-10, 0), g.Anchors[1].In); // offsets are untouched
            Assert.NotNull(g.RenderCache.CachedBounds);

            var warm = g.Bounds;
            g.RenderCache.Clear(InvalidationAspects.All);
            var cold = g.Bounds;
            Assert.Equal(cold.Left, warm.Left, 6);
            Assert.Equal(cold.Top, warm.Top, 6);
            Assert.Equal(cold.Width, warm.Width, 6);
            Assert.Equal(cold.Height, warm.Height, 6);
            Assert.Equal(before.Left + 5, cold.Left, 6);
            Assert.Equal(before.Top + 7, cold.Top, 6);
        }

        [AvaloniaFact]
        public void Bounds_IncludeTheStrokeHalfWidth_AndRoundCaps()
        {
            // an L: (0,0) → (100,0) → (100,100), 10 wide
            var g = new GraphicPath(Colors.Red, 10, new Point(0, 0));
            g.AppendAnchor(PathAnchor.Corner(new Point(100, 0)));
            g.AppendAnchor(PathAnchor.Corner(new Point(100, 100)));

            var b = g.Bounds;
            Assert.Equal(-5, b.Left, 0.5);
            Assert.Equal(-5, b.Top, 0.5);
            Assert.Equal(105, b.Right, 0.5);
            Assert.Equal(105, b.Bottom, 0.5);
        }

        [AvaloniaFact]
        public void Build_EmitsAStraightLine_ForASegmentWithoutHandles()
        {
            var g = new GraphicPath(Colors.Red, 4, new Point(0, 0));
            g.AppendAnchor(PathAnchor.Corner(new Point(100, 40)));

            var pen = RenderResources.GetPen(default, 4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var expected = new LineGeometry(new Point(0, 0), new Point(100, 40)).GetRenderBounds(pen);
            var actual = g.GetGeometry().GetRenderBounds(pen);
            Assert.Equal(expected.Left, actual.Left, 2);
            Assert.Equal(expected.Top, actual.Top, 2);
            Assert.Equal(expected.Right, actual.Right, 2);
            Assert.Equal(expected.Bottom, actual.Bottom, 2);
        }

        [AvaloniaFact]
        public void ClosedPath_BoundsIncludeTheClosingSegment()
        {
            // the closing segment (100,100) → (0,0) bows down past y = 100 through its handles
            var g = new GraphicPath(Colors.Red, 2, new Point(0, 0));
            g.Anchors[0].In = new Point(0, 60);
            g.AppendAnchor(PathAnchor.Corner(new Point(100, 0)));
            g.AppendAnchor(new PathAnchor(new Point(100, 100), default, new Point(0, 60), false));

            var open = g.Bounds;
            g.Closed = true;
            var closed = g.Bounds;

            Assert.True(closed.Bottom > open.Bottom + 10, $"open {open} closed {closed}");
            Assert.Equal(open.Right, closed.Right, 1);
        }

        [AvaloniaFact]
        public void TryRemoveActiveAnchor_RefusesBelowThreeAnchors_RemovesOtherwise()
        {
            var g = MakeThree();
            Assert.Equal(2, g.ActiveAnchor); // appending marks the placed anchor (pen feedback)
            g.ClearActiveAnchor();
            Assert.False(g.TryRemoveActiveAnchor()); // nothing active

            g.SetActiveAnchor(g.AnchorHandle(1));
            Assert.True(g.TryRemoveActiveAnchor());
            Assert.Equal(2, g.AnchorCount);
            AssertPointClose(new Point(100, 50), g.Anchors[1].P);
            Assert.Equal(-1, g.ActiveAnchor);

            g.SetActiveAnchor(g.AnchorHandle(0));
            Assert.False(g.TryRemoveActiveAnchor()); // two must remain
            Assert.Equal(2, g.AnchorCount);
        }

        [AvaloniaFact]
        public void Activate_OnAnAnchor_TogglesSmoothAndCorner_AsOneHistoryStep()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var g = MakeThree();
            canvas.GraphicsList.Add(g);
            canvas.AddCommandToHistory(false);
            g.IsSelected = true;

            // smooth → corner drops both handles
            g.Activate(canvas, new Point(50, 0));
            Assert.False(g.Anchors[1].Smooth);
            Assert.Equal(default, g.Anchors[1].In);
            Assert.Equal(default, g.Anchors[1].Out);

            // corner → smooth gets auto handles along the neighbours' chord
            g.Activate(canvas, new Point(50, 0));
            Assert.True(g.Anchors[1].Smooth);
            Assert.True(g.Anchors[1].HasIn && g.Anchors[1].HasOut);
            var cross = g.Anchors[1].In.X * g.Anchors[1].Out.Y - g.Anchors[1].In.Y * g.Anchors[1].Out.X;
            Assert.Equal(0, cross, 9);

            // two steps: undo one at a time
            canvas.Undo();
            Assert.False(g.Anchors[1].Smooth);
            canvas.Undo();
            Assert.True(g.Anchors[1].Smooth);
            AssertPointClose(new Point(-10, 0), g.Anchors[1].In);
            Assert.True(canvas.CommandUndo.CanExecute(null)); // only the add itself is left
        }

        // ====================================================================
        // History grammar (the PathAnchorArrayCodec's positional paths; the DEBUG parity assert
        // in UndoManager cross-checks every commit below against the JSON oracle)
        // ====================================================================

        [AvaloniaFact]
        public void HandleEdit_EmitsExactlyThatMembersPath_AndUndoRestoresTheArray()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var g = MakeThree();
            g.MoveHandleTo(new Point(100, 80), g.OutHandle(2), KeyModifiers.Alt); // anchor 2: a corner with an Out handle
            canvas.GraphicsList.Add(g);
            canvas.AddCommandToHistory(false);
            var original = (PathAnchor[])g.Anchors.Clone();

            SortedSet<string> built = null;
            Action<UndoManager, SortedSet<string>> hook = (_, changes) => built = changes;
            UndoManager.DiagnosticCommitBuilt += hook;
            try
            {
                g.MoveHandleTo(new Point(120, 70), g.OutHandle(2)); // a corner: only Out moves
                canvas.AddCommandToHistory(false);
                Assert.Equal(new[] { $"root/Graphics/{g.Id}/anchors/item.2/Out" }, built);

                g.AppendAnchor(PathAnchor.Corner(new Point(10, 90)));
                canvas.AddCommandToHistory(false);
                Assert.Equal(new[] { $"root/Graphics/{g.Id}/anchors/item.3" }, built);

                g.ToggleAnchorType(0); // corner → smooth: Smooth flips and the end anchor gets an Out auto handle
                canvas.AddCommandToHistory(false);
                Assert.Contains($"root/Graphics/{g.Id}/anchors/item.0/Smooth", built);
                Assert.Contains($"root/Graphics/{g.Id}/anchors/item.0/Out", built);
                Assert.DoesNotContain($"root/Graphics/{g.Id}/anchors/item.0/In", built);

                g.ToggleAnchorType(0); // ...and back, dropping the auto handle it just got
                canvas.AddCommandToHistory(false);
                Assert.Contains($"root/Graphics/{g.Id}/anchors/item.0/Smooth", built);
                Assert.Contains($"root/Graphics/{g.Id}/anchors/item.0/Out", built);
            }
            finally
            {
                UndoManager.DiagnosticCommitBuilt -= hook;
            }

            _ = g.GetGeometry();
            canvas.Undo();
            canvas.Undo();
            canvas.Undo();
            canvas.Undo();
            Assert.Same(g, canvas.GraphicsList[0]);
            Assert.Equal(original, g.Anchors);
            Assert.Null(g.RenderCache.Geometry);
            Assert.Equal(-1, g.ActiveAnchor);
        }

        [AvaloniaFact]
        public void SmoothToCorner_OnAnAnchorWithoutHandles_EmitsOnlyTheSmoothPath()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var g = new GraphicPath(Colors.Red, 2, new Point(0, 0));
            g.AppendAnchor(new PathAnchor(new Point(40, 0), default, default, true));
            canvas.GraphicsList.Add(g);
            canvas.AddCommandToHistory(false);

            SortedSet<string> built = null;
            Action<UndoManager, SortedSet<string>> hook = (_, changes) => built = changes;
            UndoManager.DiagnosticCommitBuilt += hook;
            try
            {
                g.ToggleAnchorType(1);
                canvas.AddCommandToHistory(false);
            }
            finally
            {
                UndoManager.DiagnosticCommitBuilt -= hook;
            }

            Assert.Equal(new[] { $"root/Graphics/{g.Id}/anchors/item.1/Smooth" }, built);
        }
    }
}
