using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Clowd.Drawing.Tools;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// Drives the eraser with synthetic pointer states, the way DrawingCanvas would: a click
    /// deletes what it lands on, a drag deletes what its marquee fully contains, each as one
    /// step; hidden and locked graphics are never touched; nothing is ever selected; the tool
    /// stays active; and cancelling mid-drag leaves no marquee behind.
    /// </summary>
    public class ToolEraserTests
    {
        static ToolEraserTests()
        {
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
        }

        // the eraser keys off its own marquee rather than the capture, so a null pointer is fine
        private static PointerState Down(Point p) => new PointerState(p, KeyModifiers.None, true, false, false, null, 0, null);
        private static PointerState Move(Point p) => new PointerState(p, KeyModifiers.None, true, false, false, null, 0, null);
        private static PointerState Up(Point p) => new PointerState(p, KeyModifiers.None, false, false, false, null, 0, null);
        private static PointerState Hover(Point p) => new PointerState(p, KeyModifiers.None, false, false, false, null, 0, null);

        private static GraphicRectangle Rect(double x, double y, double w, double h, Color? color = null) =>
            new GraphicRectangle(color ?? Colors.Red, 2, new Rect(x, y, w, h));

        /// <summary>A canvas with the eraser active and a committed baseline of <paramref name="graphics"/>.</summary>
        private static DrawingCanvas MakeCanvas(params GraphicBase[] graphics)
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            foreach (var g in graphics)
                canvas.GraphicsList.Add(g);
            canvas.AddCommandToHistory(false); // baseline, so a deletion is undoable
            canvas.Tool = ToolType.Eraser;
            return canvas;
        }

        private static void Click(DrawingCanvas canvas, Point p)
        {
            canvas.ToolEraser.OnMouseDown(canvas, Down(p), 1);
            canvas.ToolEraser.OnMouseUp(canvas, Up(p));
        }

        private static void Drag(DrawingCanvas canvas, Point from, Point to)
        {
            canvas.ToolEraser.OnMouseDown(canvas, Down(from), 1);
            canvas.ToolEraser.OnMouseMove(canvas, Move(to));
            canvas.ToolEraser.OnMouseUp(canvas, Up(to));
        }

        /// <summary>Counts the history steps (non-empty commits) made while <paramref name="action"/> runs.</summary>
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
        public void ClickOnAGraphic_DeletesIt_InOneStep()
        {
            var keep = Rect(200, 200, 40, 40);
            var target = Rect(10, 10, 80, 60);
            var canvas = MakeCanvas(keep, target);

            var steps = CountSteps(() => Click(canvas, new Point(50, 40)));

            Assert.Equal(1, steps);
            Assert.Same(keep, Assert.Single(canvas.GraphicsList));
            Assert.Equal(ToolType.Eraser, canvas.Tool);
        }

        [AvaloniaFact]
        public void ClickOnEmptySpace_ChangesNothing_InNoSteps()
        {
            var a = Rect(10, 10, 30, 30);
            var b = Rect(100, 100, 30, 30);
            var canvas = MakeCanvas(a, b);

            var steps = CountSteps(() => Click(canvas, new Point(500, 500)));

            Assert.Equal(0, steps);
            Assert.Equal(new GraphicBase[] { a, b }, canvas.GraphicsList);
        }

        [AvaloniaFact]
        public void Marquee_DeletesOnlyFullyContainedGraphics_InOneStep_AndUndoRestoresTheOrder()
        {
            var a = Rect(10, 10, 30, 30);
            var b = Rect(50, 50, 20, 20);
            var c = Rect(90, 90, 50, 50); // straddles the marquee edge
            var d = Rect(300, 300, 10, 10);
            var canvas = MakeCanvas(a, b, c, d);

            var steps = CountSteps(() => Drag(canvas, new Point(0, 0), new Point(100, 100)));

            Assert.Equal(1, steps);
            Assert.Equal(new GraphicBase[] { c, d }, canvas.GraphicsList);
            Assert.Equal(ToolType.Eraser, canvas.Tool);

            canvas.Undo();
            Assert.Equal(new GraphicBase[] { a, b, c, d }, canvas.GraphicsList);
        }

        [AvaloniaFact]
        public void MarqueeDraggedUpAndLeft_StillDeletesWhatItContains()
        {
            var a = Rect(10, 10, 30, 30);
            var canvas = MakeCanvas(a);

            Drag(canvas, new Point(100, 100), new Point(0, 0));

            Assert.Empty(canvas.GraphicsList);
        }

        [AvaloniaFact]
        public void HiddenAndLocked_AreSkipped_ByClickAndMarquee()
        {
            var hidden = Rect(10, 10, 30, 30);
            var locked = Rect(50, 10, 30, 30);
            var plain = Rect(10, 50, 30, 30);
            hidden.Hidden = true;
            locked.Locked = true;
            var canvas = MakeCanvas(hidden, locked, plain);

            // a click on the locked graphic hits nothing
            var clickSteps = CountSteps(() => Click(canvas, new Point(65, 25)));
            Assert.Equal(0, clickSteps);
            Assert.Equal(3, canvas.Count);

            // the marquee covers all three and takes only the plain one
            var dragSteps = CountSteps(() => Drag(canvas, new Point(0, 0), new Point(200, 200)));
            Assert.Equal(1, dragSteps);
            Assert.Equal(new GraphicBase[] { hidden, locked }, canvas.GraphicsList);
        }

        [AvaloniaFact]
        public void NothingIsEverSelected_AndTheSurvivorsDoNotMove()
        {
            var a = Rect(10, 10, 30, 30);
            var b = Rect(100, 100, 30, 30);
            var c = Rect(200, 200, 30, 30);
            var canvas = MakeCanvas(a, b, c);
            var boundsB = b.Bounds;
            var boundsC = c.Bounds;

            // mid-gesture and after a click, a drag over nothing, and a drag over a
            canvas.ToolEraser.OnMouseDown(canvas, Down(new Point(115, 115)), 1);
            Assert.Equal(0, canvas.SelectedCount);
            canvas.ToolEraser.OnMouseMove(canvas, Move(new Point(160, 160)));
            Assert.Equal(0, canvas.SelectedCount);
            canvas.ToolEraser.OnMouseUp(canvas, Up(new Point(160, 160)));
            Assert.Equal(0, canvas.SelectedCount);

            Drag(canvas, new Point(400, 400), new Point(500, 500));
            Click(canvas, new Point(25, 25));
            Assert.Equal(0, canvas.SelectedCount);

            Assert.Equal(new GraphicBase[] { b, c }, canvas.GraphicsList);
            Assert.Equal(boundsB, b.Bounds);
            Assert.Equal(boundsC, c.Bounds);
        }

        [AvaloniaFact]
        public void ToolStaysEraser_AfterEveryKindOfRelease()
        {
            var canvas = MakeCanvas(Rect(10, 10, 30, 30), Rect(100, 100, 30, 30));

            Click(canvas, new Point(25, 25));
            Assert.Equal(ToolType.Eraser, canvas.Tool);

            Click(canvas, new Point(500, 500));
            Assert.Equal(ToolType.Eraser, canvas.Tool);

            Drag(canvas, new Point(90, 90), new Point(140, 140));
            Assert.Equal(ToolType.Eraser, canvas.Tool);

            // a release with no press (the press went elsewhere)
            canvas.ToolEraser.OnMouseUp(canvas, Up(new Point(0, 0)));
            Assert.Equal(ToolType.Eraser, canvas.Tool);
            Assert.Empty(canvas.GraphicsList);
        }

        [AvaloniaFact]
        public void AbortMidDrag_LeavesNoMarqueeBehind_AndDeletesNothing()
        {
            var a = Rect(10, 10, 30, 30);
            var canvas = MakeCanvas(a);

            canvas.ToolEraser.OnMouseDown(canvas, Down(new Point(0, 0)), 1);
            canvas.ToolEraser.OnMouseMove(canvas, Move(new Point(100, 100)));
            Assert.Contains(canvas.GraphicsList, g => g is GraphicSelectionRectangle);
            Assert.NotNull(canvas.HoveredGraphics);

            canvas.ToolEraser.AbortOperation(canvas);

            Assert.Same(a, Assert.Single(canvas.GraphicsList));
            Assert.Null(canvas.HoveredGraphics);

            // and a release after the abort is inert
            canvas.ToolEraser.OnMouseUp(canvas, Up(new Point(100, 100)));
            Assert.Same(a, Assert.Single(canvas.GraphicsList));
        }

        [AvaloniaFact]
        public void CancelCurrentOperationMidDrag_LeavesNoMarqueeBehind()
        {
            var a = Rect(10, 10, 30, 30);
            var canvas = MakeCanvas(a);

            canvas.ToolEraser.OnMouseDown(canvas, Down(new Point(0, 0)), 1);
            canvas.ToolEraser.OnMouseMove(canvas, Move(new Point(100, 100)));

            var steps = CountSteps(() => canvas.CancelCurrentOperation());

            Assert.Equal(0, steps);
            Assert.Same(a, Assert.Single(canvas.GraphicsList));
            Assert.DoesNotContain(canvas.GraphicsList, g => g is GraphicSelectionRectangle);
        }

        [AvaloniaFact]
        public void Hover_SetsTheHoveredGraphic_AndClearsItOverEmptySpace()
        {
            var a = Rect(10, 10, 80, 60);
            var canvas = MakeCanvas(a);

            canvas.ToolEraser.OnMouseMove(canvas, Hover(new Point(50, 40)));
            Assert.Same(a, canvas.HoveredGraphic);

            canvas.ToolEraser.OnMouseMove(canvas, Hover(new Point(500, 500)));
            Assert.Null(canvas.HoveredGraphic);

            // the eraser never swaps in the pointer's Move cursor
            canvas.ToolEraser.OnMouseMove(canvas, Hover(new Point(50, 40)));
            Assert.Same(CursorResources.Eraser, canvas.Cursor);
        }

        [AvaloniaFact]
        public void MarqueeDrag_HoversEveryContainedGraphic_UntilRelease()
        {
            var a = Rect(10, 10, 30, 30);
            var b = Rect(50, 50, 20, 20);
            var c = Rect(300, 300, 10, 10);
            var canvas = MakeCanvas(a, b, c);

            canvas.ToolEraser.OnMouseDown(canvas, Down(new Point(0, 0)), 1);
            canvas.ToolEraser.OnMouseMove(canvas, Move(new Point(100, 100)));

            Assert.NotNull(canvas.HoveredGraphics);
            Assert.True(canvas.HoveredGraphics.SetEquals(new GraphicBase[] { a, b }));

            canvas.ToolEraser.OnMouseUp(canvas, Up(new Point(100, 100)));
            Assert.Null(canvas.HoveredGraphics);
        }

        [AvaloniaFact]
        public void SelectingTheEraser_WritesNoEraserKeyIntoTheEditorSettings()
        {
            var tools = Clowd.Config.SettingsRoot.Current.Editor.Tools;
            tools.Remove(ToolType.Eraser);

            var canvas = new DrawingCanvas { Tool = ToolType.Eraser };
            canvas.GraphicsList.Add(Rect(10, 10, 30, 30));
            Click(canvas, new Point(25, 25));
            canvas.ResyncToolSettings();

            Assert.False(tools.ContainsKey(ToolType.Eraser));
            Assert.Equal("Eraser", canvas.SubjectName);
            Assert.Equal(Skill.None, canvas.SubjectSkill);
        }

        [AvaloniaFact]
        public void ResolveTargets_PrefersTheMarquee_ThenThePressHit_ThenNothing()
        {
            var a = Rect(10, 10, 30, 30);
            var b = Rect(100, 100, 30, 30);
            var canvas = MakeCanvas(a, b);

            Assert.Equal(new GraphicBase[] { a }, ToolEraser.ResolveTargets(canvas, b, new Rect(0, 0, 50, 50)));
            Assert.Equal(new GraphicBase[] { b }, ToolEraser.ResolveTargets(canvas, b, null));
            Assert.Empty(ToolEraser.ResolveTargets(canvas, null, null));

            // a press hit that has since left the list is not deleted twice
            canvas.GraphicsList.Remove(b);
            Assert.Empty(ToolEraser.ResolveTargets(canvas, b, null));
        }
    }
}
