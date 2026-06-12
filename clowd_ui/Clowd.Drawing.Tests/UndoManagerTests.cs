using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// Exercises the undo merge contract through DrawingCanvas (which owns the UndoManager):
    /// consecutive mergable edits to the same property collapse into one undo step; edits to
    /// different properties, or non-mergable edits, do not.
    /// </summary>
    public class UndoManagerTests
    {
        static UndoManagerTests()
        {
            // DrawingCanvas reads tool settings through SettingsRoot.Current, which the
            // application assigns at startup; give the tests a defaults instance.
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
        }

        private static bool CanExecute(System.Windows.Input.ICommand command) => command.CanExecute(null);

        private static (DrawingCanvas Canvas, GraphicRectangle Rect) CreateCanvasWithRect()
        {
            var canvas = new DrawingCanvas();
            var rect = new GraphicRectangle(Colors.Red, 2, new Rect(10, 10, 50, 40));
            canvas.GraphicsList.Add(rect);
            canvas.AddCommandToHistory(false);
            return (canvas, rect);
        }

        private static GraphicRectangle CurrentRect(DrawingCanvas canvas) =>
            Assert.IsType<GraphicRectangle>(canvas.GraphicsList[0]);

        [AvaloniaFact]
        public void ConsecutiveMergableEdits_ToSameProperty_MergeIntoOneUndoStep()
        {
            var (canvas, rect) = CreateCanvasWithRect();

            rect.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(true);
            rect.ObjectColor = Colors.Blue;
            canvas.AddCommandToHistory(true);

            // one undo steps over BOTH color edits
            canvas.Undo();
            Assert.Equal(Colors.Red, CurrentRect(canvas).ObjectColor);

            // and one redo lands on the final (merged) value with nothing further to redo
            canvas.Redo();
            Assert.Equal(Colors.Blue, CurrentRect(canvas).ObjectColor);
            Assert.False(CanExecute(canvas.CommandRedo));
        }

        [AvaloniaFact]
        public void MergableEdits_ToDifferentProperties_DoNotMerge()
        {
            var (canvas, rect) = CreateCanvasWithRect();

            rect.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(true);
            rect.LineWidth = 7;
            canvas.AddCommandToHistory(true);

            canvas.Undo();
            var afterFirstUndo = CurrentRect(canvas);
            Assert.Equal(Colors.Green, afterFirstUndo.ObjectColor);
            Assert.Equal(2, afterFirstUndo.LineWidth);

            canvas.Undo();
            var afterSecondUndo = CurrentRect(canvas);
            Assert.Equal(Colors.Red, afterSecondUndo.ObjectColor);
            Assert.Equal(2, afterSecondUndo.LineWidth);
        }

        [AvaloniaFact]
        public void MergableEdit_AfterNonMergableEdit_DoesNotMergeIntoIt()
        {
            var (canvas, rect) = CreateCanvasWithRect();

            rect.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(false);
            rect.ObjectColor = Colors.Blue;
            canvas.AddCommandToHistory(true);

            // the non-mergable step must survive as its own step
            canvas.Undo();
            Assert.Equal(Colors.Green, CurrentRect(canvas).ObjectColor);
            canvas.Undo();
            Assert.Equal(Colors.Red, CurrentRect(canvas).ObjectColor);
        }

        [AvaloniaFact]
        public void MoveToFront_IsItsOwnUndoStep()
        {
            var canvas = new DrawingCanvas();
            var red = new GraphicRectangle(Colors.Red, 2, new Rect(0, 0, 5, 5));
            var blue = new GraphicRectangle(Colors.Blue, 2, new Rect(10, 10, 5, 5));
            canvas.GraphicsList.Add(red);
            canvas.GraphicsList.Add(blue);
            canvas.AddCommandToHistory(false);

            red.IsSelected = true;
            canvas.MoveToFront();
            Assert.Equal(Colors.Blue, Assert.IsType<GraphicRectangle>(canvas.GraphicsList[0]).ObjectColor);
            Assert.Equal(Colors.Red, Assert.IsType<GraphicRectangle>(canvas.GraphicsList[1]).ObjectColor);

            // a pure z-order change must be undoable
            Assert.True(CanExecute(canvas.CommandUndo));
            canvas.Undo();
            Assert.Equal(Colors.Red, Assert.IsType<GraphicRectangle>(canvas.GraphicsList[0]).ObjectColor);
            Assert.Equal(Colors.Blue, Assert.IsType<GraphicRectangle>(canvas.GraphicsList[1]).ObjectColor);
        }

        [AvaloniaFact]
        public void NonMergableEdits_ToSameProperty_DoNotMerge()
        {
            var (canvas, rect) = CreateCanvasWithRect();

            rect.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(false);
            rect.ObjectColor = Colors.Blue;
            canvas.AddCommandToHistory(false);

            canvas.Undo();
            Assert.Equal(Colors.Green, CurrentRect(canvas).ObjectColor);
            canvas.Undo();
            Assert.Equal(Colors.Red, CurrentRect(canvas).ObjectColor);
        }

        [AvaloniaFact]
        public void NoChange_AddsNoUndoStep()
        {
            var canvas = new DrawingCanvas();
            Assert.False(CanExecute(canvas.CommandUndo));

            canvas.AddCommandToHistory(true);
            Assert.False(CanExecute(canvas.CommandUndo));

            canvas.GraphicsList.Add(new GraphicRectangle(Colors.Red, 2, new Rect(0, 0, 5, 5)));
            canvas.AddCommandToHistory(false);
            Assert.True(CanExecute(canvas.CommandUndo));

            // no further change → no new step (redo stays empty after an undo+redo cycle)
            canvas.AddCommandToHistory(false);
            canvas.Undo();
            Assert.Empty(canvas.GraphicsList);
            Assert.True(CanExecute(canvas.CommandRedo));
            canvas.Redo();
            Assert.Single(canvas.GraphicsList);
            Assert.False(CanExecute(canvas.CommandRedo));
        }

        [AvaloniaFact]
        public void BackgroundColorChange_IsItsOwnUndoStep()
        {
            var (canvas, _) = CreateCanvasWithRect();

            canvas.SetBackgroundColor(Colors.Cyan);
            Assert.Equal(Colors.Cyan, canvas.ArtworkBackground);

            canvas.Undo();
            Assert.NotEqual(Colors.Cyan, canvas.ArtworkBackground);
            Assert.Single(canvas.GraphicsList);
        }

        // ====================================================================
        // GetChangedNodes — the per-property diff feeding the merge decision
        // ====================================================================

        private static JsonObject State(string json) => (JsonObject)JsonNode.Parse(json);

        [AvaloniaFact]
        public void Diff_DetectsChangedLeaf_PerGraphicById()
        {
            var prev = State("""
                {"BackgroundColor":"#00000000","Graphics":[
                    {"$type":"GraphicRectangle","id":"a","objectColor":"#FFFF0000","lineWidth":2},
                    {"$type":"GraphicRectangle","id":"b","objectColor":"#FF00FF00","lineWidth":2}]}
                """);
            var next = State("""
                {"BackgroundColor":"#00000000","Graphics":[
                    {"$type":"GraphicRectangle","id":"a","objectColor":"#FFFF0000","lineWidth":2},
                    {"$type":"GraphicRectangle","id":"b","objectColor":"#FF0000FF","lineWidth":2}]}
                """);

            var changes = UndoManager.GetChangedNodes(prev, next);
            Assert.Equal(new[] { "root/Graphics/b/objectColor" }, changes);
        }

        [AvaloniaFact]
        public void Diff_SamePropertyOnSameGraphic_ProducesEqualChangeSets()
        {
            var s0 = State("""{"BackgroundColor":"#00000000","Graphics":[{"id":"a","objectColor":"#FFFF0000"}]}""");
            var s1 = State("""{"BackgroundColor":"#00000000","Graphics":[{"id":"a","objectColor":"#FF00FF00"}]}""");
            var s2 = State("""{"BackgroundColor":"#00000000","Graphics":[{"id":"a","objectColor":"#FF0000FF"}]}""");

            var first = UndoManager.GetChangedNodes(s0, s1);
            var second = UndoManager.GetChangedNodes(s1, s2);
            Assert.Equal(first, second); // equal sets → the steps merge
        }

        [AvaloniaFact]
        public void Diff_DifferentProperties_ProduceDifferentChangeSets()
        {
            var s0 = State("""{"Graphics":[{"id":"a","objectColor":"#FFFF0000","lineWidth":2}]}""");
            var s1 = State("""{"Graphics":[{"id":"a","objectColor":"#FF00FF00","lineWidth":2}]}""");
            var s2 = State("""{"Graphics":[{"id":"a","objectColor":"#FF00FF00","lineWidth":7}]}""");

            var first = UndoManager.GetChangedNodes(s0, s1);
            var second = UndoManager.GetChangedNodes(s1, s2);
            Assert.NotEqual(first, second); // different sets → no merge
        }

        [AvaloniaFact]
        public void Diff_AddedAndRemovedGraphics_AreReported()
        {
            var prev = State("""{"Graphics":[{"id":"a","objectColor":"#FFFF0000"}]}""");
            var next = State("""{"Graphics":[{"id":"b","objectColor":"#FFFF0000"}]}""");

            var changes = UndoManager.GetChangedNodes(prev, next);
            Assert.Contains("root/Graphics/a", changes);
            Assert.Contains("root/Graphics/b", changes);
        }

        [AvaloniaFact]
        public void Diff_PureReorder_IsReported()
        {
            var prev = State("""{"Graphics":[{"id":"a","objectColor":"#FFFF0000"},{"id":"b","objectColor":"#FF00FF00"}]}""");
            var next = State("""{"Graphics":[{"id":"b","objectColor":"#FF00FF00"},{"id":"a","objectColor":"#FFFF0000"}]}""");

            var changes = UndoManager.GetChangedNodes(prev, next);
            Assert.Equal(new[] { "root/Graphics/(order)" }, changes);
        }

        [AvaloniaFact]
        public void Diff_NoChanges_IsEmpty()
        {
            var prev = State("""{"BackgroundColor":"#11223344","Graphics":[{"id":"a","left":1.5,"points":["1,2","3,4"]}]}""");
            var next = State("""{"BackgroundColor":"#11223344","Graphics":[{"id":"a","left":1.5,"points":["1,2","3,4"]}]}""");

            Assert.Empty(UndoManager.GetChangedNodes(prev, next));
        }
    }
}
