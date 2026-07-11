using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// Pins the persistence boundary (final-design §B.6): discrete history actions (append / undo /
    /// redo) raise <see cref="DrawingCanvas.StateUpdated"/> immediately with the freshly-serialized
    /// document, merge-in-place rewrites (the scrub path) do NOT raise synchronously — they only arm
    /// the trailing-edge debounce — and <see cref="DrawingCanvas.FlushPendingState"/> delivers the
    /// final state exactly once. A restore-load never raises (contract #23) and cancels any armed
    /// tail. The debounce timer itself is time-driven and not pumped here; Flush is the deterministic
    /// delivery seam these tests exercise.
    /// </summary>
    public class AutosaveThrottleTests
    {
        static AutosaveThrottleTests()
        {
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
        }

        private static (DrawingCanvas Canvas, GraphicRectangle Rect) CanvasWithRect()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var rect = new GraphicRectangle(Colors.Red, 2, new Rect(10, 10, 50, 40));
            canvas.GraphicsList.Add(rect);
            canvas.AddCommandToHistory(false); // baseline (raised before any subscriber attaches)
            return (canvas, rect);
        }

        [AvaloniaFact]
        public void Append_RaisesStateUpdatedImmediately_WithDocumentPayload()
        {
            var (canvas, rect) = CanvasWithRect();
            int raises = 0;
            StateChangedEventArgs last = null;
            canvas.StateUpdated += (_, e) => { raises++; last = e; };

            rect.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(false);

            Assert.Equal(1, raises);
            Assert.NotNull(last.State);
            Assert.True(JsonNode.DeepEquals(UndoManager.SerializeDocument(canvas), last.State));
        }

        [AvaloniaFact]
        public void UndoAndRedo_EachRaiseStateUpdatedImmediately()
        {
            var (canvas, rect) = CanvasWithRect();
            int raises = 0;
            canvas.StateUpdated += (_, _) => raises++;

            rect.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(false);
            Assert.Equal(1, raises); // append

            canvas.Undo();
            Assert.Equal(2, raises); // undo

            canvas.Redo();
            Assert.Equal(3, raises); // redo
        }

        [AvaloniaFact]
        public void Merge_DoesNotRaiseSynchronously_ButFlushDeliversFinalState()
        {
            var (canvas, rect) = CanvasWithRect();
            int raises = 0;
            StateChangedEventArgs last = null;
            canvas.StateUpdated += (_, e) => { raises++; last = e; };

            // first mergable edit is an append (nothing to merge into) → immediate raise
            rect.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(true);
            Assert.Equal(1, raises);

            // second mergable edit of the SAME property folds in place → no synchronous raise
            rect.ObjectColor = Colors.Blue;
            canvas.AddCommandToHistory(true);
            Assert.Equal(1, raises);

            // the trailing edge delivers the LAST state once, on flush
            canvas.FlushPendingState();
            Assert.Equal(2, raises);
            Assert.True(JsonNode.DeepEquals(UndoManager.SerializeDocument(canvas), last.State));
        }

        [AvaloniaFact]
        public void RapidMergeScrub_CollapsesToOneFlushedRaise()
        {
            var (canvas, rect) = CanvasWithRect();
            int raises = 0;
            canvas.StateUpdated += (_, _) => raises++;

            rect.LineWidth = 3;
            canvas.AddCommandToHistory(true);   // append → immediate
            Assert.Equal(1, raises);

            for (int i = 4; i < 24; i++)         // 20 folds, all the same change set
            {
                rect.LineWidth = i;
                canvas.AddCommandToHistory(true);
            }
            Assert.Equal(1, raises);             // no O(document) raise per scrub step

            canvas.FlushPendingState();
            Assert.Equal(2, raises);             // exactly one trailing-edge delivery

            canvas.FlushPendingState();
            Assert.Equal(2, raises);             // nothing pending → no-op
        }

        [AvaloniaFact]
        public void Flush_DefersWhileTextEditActive_ThenDeliversAfterEditEnds()
        {
            // Scratch-deferral: the trailing edge must never serialize the live document while a
            // text/image edit (or tool drag) is in progress — the collection then contains
            // uncommitted, possibly about-to-be-aborted scratch the old system never persisted.
            var (canvas, rect) = CanvasWithRect();
            int raises = 0;
            StateChangedEventArgs last = null;
            canvas.StateUpdated += (_, e) => { raises++; last = e; };

            // two mergable color commits arm the merge tail
            rect.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(true);
            rect.ObjectColor = Colors.Blue;
            canvas.AddCommandToHistory(true);
            Assert.Equal(1, raises); // first append raised; the fold did not

            // live text edit in progress → the flush must defer (stay pending), not serialize
            var text = new GraphicText(Colors.Black, 2, new Point(40, 40), 0, "hi");
            canvas.GraphicsList.Add(text);
            text.Editing = true;

            canvas.FlushPendingState();
            Assert.Equal(1, raises); // deferred — no StateUpdated while scratch is live

            // edit over → the retried flush delivers exactly once, with a real payload
            text.Editing = false;
            canvas.FlushPendingState();
            Assert.Equal(2, raises);
            Assert.NotNull(last.State);
        }

        [AvaloniaFact]
        public void RestoreState_DoesNotRaise_AndCancelsPendingTail()
        {
            var (canvas, rect) = CanvasWithRect();

            // arm a pending merge tail
            rect.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(true);
            rect.ObjectColor = Colors.Blue;
            canvas.AddCommandToHistory(true);

            int raises = 0;
            canvas.StateUpdated += (_, _) => raises++;

            var snapshot = UndoManager.SerializeDocument(canvas);
            canvas.RestoreState(snapshot); // contract #23: no raise on restore-load
            Assert.Equal(0, raises);

            // the armed tail belonged to the replaced document — it must have been cancelled
            canvas.FlushPendingState();
            Assert.Equal(0, raises);
        }
    }
}
