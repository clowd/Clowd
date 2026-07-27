using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Clowd.Drawing.Graphics;
using Xunit;

namespace Clowd.Drawing.Tests
{
    /// <summary>
    /// Pins the persistent undo history (history.json — MIGRATION.md §8.8): the serialized chain
    /// round-trips through text and rehydrates into a fully working undo/redo stack (cursor
    /// position, redo branch, undo-of-delete via reconstruction from captured fields), the write
    /// path attaches the chain to every document-carrying StateUpdated raise, and every failure
    /// mode (corrupt file, unknown version, history inconsistent with graphics.json) falls back
    /// silently to today's behavior — document loaded, empty history.
    /// </summary>
    public class HistoryPersistenceTests
    {
        static HistoryPersistenceTests()
        {
            Clowd.Config.SettingsRoot.Current ??= new Clowd.Config.SettingsRoot();
        }

        private static bool CanUndo(DrawingCanvas c) => c.CommandUndo.CanExecute(null);
        private static bool CanRedo(DrawingCanvas c) => c.CommandRedo.CanExecute(null);

        private static JsonObject Doc(DrawingCanvas c) => UndoManager.SerializeDocument(c);

        /// <summary>Serialized history round-tripped through text, as it would come off disk.</summary>
        private static JsonObject HistoryFromDisk(DrawingCanvas c) =>
            (JsonObject)JsonNode.Parse(c.SerializeHistory().ToJsonString());

        private static void AssertDocsEqual(JsonObject expected, JsonObject actual)
        {
            var diff = UndoManager.GetChangedNodes(expected, actual);
            Assert.True(diff.Count == 0, "documents differ: " + string.Join(", ", diff));
        }

        private static (DrawingCanvas Canvas, GraphicRectangle Rect) CanvasWithRect()
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            var rect = new GraphicRectangle(Colors.Red, 2, new Rect(10, 10, 50, 40));
            canvas.GraphicsList.Add(rect);
            canvas.AddCommandToHistory(false);
            return (canvas, rect);
        }

        private static DrawingCanvas Reopen(JsonObject doc, JsonObject history)
        {
            var canvas = new DrawingCanvas { Tool = ToolType.None };
            canvas.RestoreState(doc);
            Assert.True(canvas.TryRestoreHistory(history, doc));
            return canvas;
        }

        private static void WritePng(string path, int width, int height)
        {
            using var wb = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                                               PixelFormats.Bgra8888, AlphaFormat.Premul);
            wb.Save(path, PngBitmapEncoderOptions.Default);
        }

        [AvaloniaFact]
        public void FullRoundTrip_UndoRedoLadder_MatchesFieldForField()
        {
            // a varied session: adds, field edits, a move, a background change, a delete, a reorder
            var (canvas, rect) = CanvasWithRect();

            var line = new GraphicLine(Colors.Black, 2, new Point(0, 0), new Point(30, 30));
            var text = new GraphicText(Colors.Purple, 2, new Point(100, 100), 0, "hello");
            // second Normalize settles CenterOfRotation against the text-measured bounds (the
            // ctor computes it from the pre-measure 1×1 rect — a pre-existing load-path quirk);
            // the ladder comparison below needs a normalize-stable document
            text.Normalize();
            canvas.GraphicsList.Add(line);
            canvas.GraphicsList.Add(text);
            canvas.AddCommandToHistory(false);

            rect.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(false);

            rect.Move(5, 3);
            canvas.AddCommandToHistory(false);

            canvas.SetBackgroundColor(Colors.Yellow);

            line.IsSelected = true;
            canvas.Delete();

            rect.IsSelected = true;
            canvas.MoveToFront();

            // reference ladder: ladder[i] = the document after i undos
            var ladder = new List<JsonObject> { Doc(canvas) };
            while (CanUndo(canvas))
            {
                canvas.Undo();
                ladder.Add(Doc(canvas));
            }

            Assert.Equal(8, ladder.Count); // 7 steps + the empty root
            while (CanRedo(canvas))
                canvas.Redo();

            var doc = Doc(canvas);
            var canvas2 = Reopen(doc, HistoryFromDisk(canvas));
            AssertDocsEqual(doc, Doc(canvas2));

            // all the way down...
            for (int i = 1; i < ladder.Count; i++)
            {
                Assert.True(CanUndo(canvas2));
                canvas2.Undo();
                AssertDocsEqual(ladder[i], Doc(canvas2));
            }

            Assert.False(CanUndo(canvas2));

            // ...and back up
            for (int i = ladder.Count - 2; i >= 0; i--)
            {
                Assert.True(CanRedo(canvas2));
                canvas2.Redo();
                AssertDocsEqual(ladder[i], Doc(canvas2));
            }

            Assert.False(CanRedo(canvas2));
        }

        [AvaloniaFact]
        public void RedoBranch_SurvivesReopen_WhenCursorSitsMidList()
        {
            var (canvas, rect) = CanvasWithRect();

            rect.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(false);
            rect.ObjectColor = Colors.Blue;
            canvas.AddCommandToHistory(false);
            rect.ObjectColor = Colors.Purple;
            canvas.AddCommandToHistory(false);

            canvas.Undo();
            canvas.Undo(); // cursor now mid-list: color == Green, redo depth 2

            var history = HistoryFromDisk(canvas);
            Assert.Equal(4, history["steps"].AsArray().Count);
            Assert.Equal(2, history["cursor"].GetValue<int>());

            var canvas2 = Reopen(Doc(canvas), history);
            Assert.True(CanUndo(canvas2));
            Assert.True(CanRedo(canvas2));

            GraphicRectangle Rect2() => Assert.IsType<GraphicRectangle>(canvas2.GraphicsList[0]);

            canvas2.Redo();
            Assert.Equal(Colors.Blue, Rect2().ObjectColor);
            canvas2.Redo();
            Assert.Equal(Colors.Purple, Rect2().ObjectColor);
            Assert.False(CanRedo(canvas2));

            canvas2.Undo();
            canvas2.Undo();
            canvas2.Undo();
            Assert.Equal(Colors.Red, Rect2().ObjectColor);
        }

        [AvaloniaFact]
        public void UndoOfDelete_AfterReopen_ReconstructsGraphicFromCapturedFields()
        {
            var (canvas, rect) = CanvasWithRect();

            var line = new GraphicLine(Colors.Black, 4, new Point(1, 2), new Point(30, 40));
            var lineId = line.Id;
            canvas.GraphicsList.Add(line);
            var top = new GraphicRectangle(Colors.Blue, 2, new Rect(60, 60, 10, 10));
            canvas.GraphicsList.Add(top);
            canvas.AddCommandToHistory(false);

            line.IsSelected = true;
            canvas.Delete();

            var canvas2 = Reopen(Doc(canvas), HistoryFromDisk(canvas));
            Assert.Equal(2, canvas2.GraphicsList.Count);

            // undo-of-delete after a restart cannot re-insert a retained live instance — it must
            // reconstruct from the captured fields, back at the original z-index
            canvas2.Undo();
            Assert.Equal(3, canvas2.GraphicsList.Count);
            var restored = Assert.IsType<GraphicLine>(canvas2.GraphicsList[1]);
            Assert.Equal(lineId, restored.Id);
            Assert.Equal(Colors.Black, restored.ObjectColor);
            Assert.Equal(4, restored.LineWidth);
            Assert.Equal(new Point(1, 2), restored.LineStart);
            Assert.Equal(new Point(30, 40), restored.LineEnd);

            canvas2.Redo();
            Assert.Equal(2, canvas2.GraphicsList.Count);
        }

        [AvaloniaFact]
        public void UndoOfImageDelete_AfterReopen_RestoresDecodableImage()
        {
            var dir = Directory.CreateTempSubdirectory();
            try
            {
                var path = Path.Combine(dir.FullName, "img.png");
                WritePng(path, 10, 8);

                var (canvas, _) = CanvasWithRect();
                var img = new GraphicImage(path, new Rect(0, 0, 10, 8), default);
                canvas.GraphicsList.Add(img);
                canvas.AddCommandToHistory(false);

                img.IsSelected = true;
                canvas.Delete();

                var canvas2 = Reopen(Doc(canvas), HistoryFromDisk(canvas));
                canvas2.Undo();

                var restored = Assert.IsType<GraphicImage>(canvas2.GraphicsList[1]);
                Assert.Equal(path, restored.BitmapFilePath);
                Assert.NotNull(restored.ImageSource); // decodes from disk via the shared LRU
                Assert.Equal(10, restored.BitmapPixelWidth);
                Assert.Equal(8, restored.BitmapPixelHeight);
            }
            finally
            {
                dir.Delete(true);
            }
        }

        [AvaloniaFact]
        public void CorruptHistory_FallsBackToEmptyHistory_CanvasStaysFunctional()
        {
            var (canvas, rect) = CanvasWithRect();
            rect.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(false);
            var doc = Doc(canvas);

            // each corruption is rejected without touching the freshly-loaded empty history
            var missingParts = new JsonObject { ["version"] = 1 };

            var wrongVersion = HistoryFromDisk(canvas);
            wrongVersion["version"] = 2;

            var badCursor = HistoryFromDisk(canvas);
            badCursor["cursor"] = 99;

            var badType = HistoryFromDisk(canvas);
            badType["steps"].AsArray()[0].AsObject()["graphics"].AsArray()[0].AsObject()["after"].AsObject()["$type"] = "NotAGraphic";

            var emptyDelta = HistoryFromDisk(canvas);
            var delta = emptyDelta["steps"].AsArray()[1].AsObject()["graphics"].AsArray()[0].AsObject();
            delta["before"] = null;
            delta["after"] = null;

            foreach (var corrupt in new[] { missingParts, wrongVersion, badCursor, badType, emptyDelta })
            {
                var canvas2 = new DrawingCanvas { Tool = ToolType.None };
                canvas2.RestoreState(doc);
                Assert.False(canvas2.TryRestoreHistory(corrupt, doc));
                Assert.False(CanUndo(canvas2));
                Assert.False(CanRedo(canvas2));

                // still a working editor: new commits build a fresh history
                var rect2 = Assert.IsType<GraphicRectangle>(canvas2.GraphicsList[0]);
                rect2.ObjectColor = Colors.Blue;
                canvas2.AddCommandToHistory(false);
                Assert.True(CanUndo(canvas2));
                canvas2.Undo();
                Assert.Equal(Colors.Green, Assert.IsType<GraphicRectangle>(canvas2.GraphicsList[0]).ObjectColor);
            }
        }

        [AvaloniaFact]
        public void HistoryInconsistentWithDocument_IsRejected()
        {
            // a history saved BEFORE the latest graphics.json write (e.g. a crash between the two
            // file writes) replays to a different document — graphics.json is the authority
            var (canvas, rect) = CanvasWithRect();
            rect.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(false);
            var staleHistory = HistoryFromDisk(canvas);

            rect.ObjectColor = Colors.Blue;
            canvas.AddCommandToHistory(false);
            var newerDoc = Doc(canvas);

            var canvas2 = new DrawingCanvas { Tool = ToolType.None };
            canvas2.RestoreState(newerDoc);
            Assert.False(canvas2.TryRestoreHistory(staleHistory, newerDoc));
            Assert.False(CanUndo(canvas2));
            AssertDocsEqual(newerDoc, Doc(canvas2));
        }

        [AvaloniaFact]
        public void OldSession_WithoutHistory_LoadsExactlyAsBefore()
        {
            var (canvas, rect) = CanvasWithRect();
            rect.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(false);
            var doc = Doc(canvas);

            var canvas2 = new DrawingCanvas { Tool = ToolType.None };
            canvas2.RestoreState(doc);

            Assert.False(CanUndo(canvas2));
            Assert.False(CanRedo(canvas2));
            AssertDocsEqual(doc, Doc(canvas2));

            // edits undo back to the loaded state, never past it
            var rect2 = Assert.IsType<GraphicRectangle>(canvas2.GraphicsList[0]);
            rect2.LineWidth = 9;
            canvas2.AddCommandToHistory(false);
            canvas2.Undo();
            AssertDocsEqual(doc, Doc(canvas2));
            Assert.False(CanUndo(canvas2));
        }

        [AvaloniaFact]
        public void LoadBoundary_IsNeverMergable()
        {
            var (canvas, rect) = CanvasWithRect();
            rect.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(true); // mergable step, change set {objectColor}

            var canvas2 = Reopen(Doc(canvas), HistoryFromDisk(canvas));
            var rect2 = Assert.IsType<GraphicRectangle>(canvas2.GraphicsList[0]);

            // an identical mergable change set must NOT fold into the rehydrated step...
            rect2.ObjectColor = Colors.Blue;
            canvas2.AddCommandToHistory(true);
            // ...while the in-session merge chain still works from the second commit on
            rect2.ObjectColor = Colors.Purple;
            canvas2.AddCommandToHistory(true);

            canvas2.Undo(); // reverts the merged Blue→Purple step in one go
            Assert.Equal(Colors.Green, rect2.ObjectColor);
            Assert.True(CanUndo(canvas2)); // the rehydrated Green step is still its own step
            canvas2.Undo();
            Assert.Equal(Colors.Red, Assert.IsType<GraphicRectangle>(canvas2.GraphicsList[0]).ObjectColor);
        }

        [AvaloniaFact]
        public void CappedHistory_RoundTripsAtTwoHundredSteps()
        {
            var (canvas, rect) = CanvasWithRect();

            // 250 distinct steps; the cap keeps the newest 200 (LineWidth floor becomes 52)
            for (int i = 3; i <= 252; i++)
            {
                rect.LineWidth = i;
                canvas.AddCommandToHistory(false);
            }

            var history = HistoryFromDisk(canvas);
            Assert.Equal(200, history["steps"].AsArray().Count);
            Assert.Equal(200, history["cursor"].GetValue<int>());

            var canvas2 = Reopen(Doc(canvas), history);
            int undos = 0;
            while (CanUndo(canvas2))
            {
                canvas2.Undo();
                undos++;
            }

            Assert.Equal(200, undos);
            Assert.Equal(52, Assert.IsType<GraphicRectangle>(canvas2.GraphicsList[0]).LineWidth);
        }

        [AvaloniaFact]
        public void UndoOfTextEdit_ThenReopen_SurvivesNormalizeDrift()
        {
            // GraphicText.Normalize recomputes CenterOfRotation from the PRE-measure bounds, so
            // committed records are chronically one Normalize behind; the undo re-Normalize then
            // moves the live document (and graphics.json) off the raw step records. The load
            // validation must still accept the file — both sides are the same document once
            // equally normalized.
            var (canvas, _) = CanvasWithRect();
            var text = new GraphicText(Colors.Black, 2, new Point(40, 40), 0, "aa");
            canvas.GraphicsList.Add(text); // deliberately NOT re-normalized (the real tool flow)
            canvas.AddCommandToHistory(false);

            text.Body = "a much longer body";
            canvas.AddCommandToHistory(false);

            canvas.Undo(); // Normalize drift: live CenterOfRotation leaves the records

            var doc = Doc(canvas);
            var canvas2 = new DrawingCanvas { Tool = ToolType.None };
            canvas2.RestoreState(doc);
            Assert.True(canvas2.TryRestoreHistory(HistoryFromDisk(canvas), doc));

            Assert.True(CanRedo(canvas2));
            canvas2.Redo();
            Assert.Equal("a much longer body", Assert.IsType<GraphicText>(canvas2.GraphicsList[1]).Body);

            canvas2.Undo();
            canvas2.Undo();
            Assert.Single(canvas2.GraphicsList); // back before the text was added
        }

        [AvaloniaFact]
        public void StateUpdated_CarriesHistoryPayload_OnDiscreteActionsAndFlush()
        {
            var (canvas, rect) = CanvasWithRect();
            StateChangedEventArgs last = null;
            canvas.StateUpdated += (_, e) => last = e;

            // discrete append: document and history land in the same raise
            rect.ObjectColor = Colors.Green;
            canvas.AddCommandToHistory(false);
            Assert.NotNull(last.State);
            Assert.NotNull(last.History);
            Assert.Equal(1, last.History["version"].GetValue<int>());
            Assert.Equal(2, last.History["cursor"].GetValue<int>()); // add-rect + color steps

            // undo raises with the moved cursor
            canvas.Undo();
            Assert.Equal(1, last.History["cursor"].GetValue<int>());
            canvas.Redo();

            // merge tail: no synchronous raise, but the flush delivers BOTH payloads
            rect.ObjectColor = Colors.Blue;
            canvas.AddCommandToHistory(true);
            rect.ObjectColor = Colors.Purple;
            canvas.AddCommandToHistory(true);
            last = null;
            canvas.FlushPendingState();
            Assert.NotNull(last);
            Assert.NotNull(last.State);
            Assert.NotNull(last.History);

            // the flushed pair is loadable and consistent with each other
            var reparsed = (JsonObject)JsonNode.Parse(last.History.ToJsonString());
            var reopened = Reopen((JsonObject)JsonNode.Parse(last.State.ToJsonString()), reparsed);
            Assert.True(CanUndo(reopened));
        }
    }
}
