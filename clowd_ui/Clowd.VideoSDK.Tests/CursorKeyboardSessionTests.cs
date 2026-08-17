using System;
using System.IO;
using System.Linq;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="EditorSession"/>'s input-overlay surface: the cursor/keyboard track factories
    /// (placement above the screen row, span/link-group mirroring), the Has* flags, the hard-sync
    /// refusals (unlink, reorder, duplicate), pruning and undo/redo — pure model math over a
    /// recording-shaped fixture; only the <see cref="EditorSession.HasInputCapture"/> tests touch
    /// the filesystem.
    /// </summary>
    public class CursorKeyboardSessionTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>A recording with two keep-segments: screen row (order 0) and webcam row
        /// (order 1) over one source, each segment its own link group — the shape the editor
        /// produces after one cut — plus an audio row.</summary>
        private static Project BaseProject(string capturePath, out Source source,
            out Track screenTrack, out Track webcamTrack, out Track audioTrack)
        {
            source = new Source
            {
                Id = Guid.NewGuid(),
                Path = @"C:\rec\in.mp4",
                InputCapturePath = capturePath,
                CursorStreamIndex = 2,
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080 },
                    new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 640, Height = 480 },
                    new SourceStream { Index = 2, Kind = StreamKind.Video, Width = 512, Height = 512 },
                },
            };

            screenTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            webcamTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Webcam", Order = 1 };
            audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 2 };

            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources = { source },
                Tracks = { screenTrack, webcamTrack, audioTrack },
            };

            var g1 = Guid.NewGuid();
            var g2 = Guid.NewGuid();
            AddSegment(project, screenTrack, webcamTrack, source.Id, 0, Ms(8_000), g1);
            AddSegment(project, screenTrack, webcamTrack, source.Id, Ms(8_000), Ms(12_000), g2);
            return project;
        }

        private static void AddSegment(Project project, Track screen, Track webcam, Guid sourceId,
            long startTicks, long durationTicks, Guid group)
        {
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = screen.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = startTicks },
                LinkGroupId = group,
            });
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = webcam.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 1, SourceInTicks = startTicks },
                LinkGroupId = group,
            });
        }

        private static EditorSession NewSession(out Track screenTrack, out Track webcamTrack,
            out Track audioTrack, string capturePath = @"C:\rec\input-capture.jsonl")
        {
            var project = BaseProject(capturePath, out _, out screenTrack, out webcamTrack, out audioTrack);
            return new EditorSession(project, null, save => save());
        }

        private static int OrderOf(EditorSession session, Guid trackId) =>
            session.Project.Tracks.Single(t => t.Id == trackId).Order;

        private static Track TrackOf(EditorSession session, Item item) =>
            session.Project.Tracks.Single(t => t.Id == item.TrackId);

        // ------------------------------------------------------------------------------ factories

        [Fact]
        public void AddCursorTrack_slots_directly_above_the_screen_row()
        {
            var session = NewSession(out var screenTrack, out var webcamTrack, out var audioTrack);
            ProjectChangeKind? kind = null;
            session.ProjectChanged += (_, e) => kind = e.Kind;

            var item = session.AddCursorTrack();

            Assert.NotNull(item);
            Assert.Equal(ProjectChangeKind.Structural, kind);
            Assert.True(session.HasCursorTrack);

            var track = TrackOf(session, item);
            Assert.Equal(TrackKind.Video, track.Kind);
            Assert.Equal("Cursor", track.Name);
            Assert.Equal(0, OrderOf(session, screenTrack.Id));
            Assert.Equal(1, track.Order);
            Assert.Equal(2, OrderOf(session, webcamTrack.Id));
            Assert.Equal(3, OrderOf(session, audioTrack.Id));
            Assert.Empty(session.Project.Validate());

            // the returned item is the live model instance, not a copy.
            Assert.Contains(session.Project.Items, i => ReferenceEquals(i, item));
        }

        [Fact]
        public void Cursor_items_mirror_the_screen_segments_and_their_groups()
        {
            var session = NewSession(out var screenTrack, out _, out _);
            var first = session.AddCursorTrack();

            var screenItems = session.Project.Items
                .Where(i => i.TrackId == screenTrack.Id).OrderBy(i => i.TimelineStartTicks).ToList();
            var cursorItems = session.Project.Items
                .Where(i => i.Content is CursorContent).OrderBy(i => i.TimelineStartTicks).ToList();

            Assert.Equal(2, cursorItems.Count);
            Assert.Same(first, cursorItems[0]);
            for (var i = 0; i < 2; i++)
            {
                Assert.Equal(screenItems[i].TimelineStartTicks, cursorItems[i].TimelineStartTicks);
                Assert.Equal(screenItems[i].DurationTicks, cursorItems[i].DurationTicks);
                Assert.Equal(screenItems[i].LinkGroupId, cursorItems[i].LinkGroupId);
                Assert.NotNull(cursorItems[i].LinkGroupId);

                var content = (CursorContent)cursorItems[i].Content;
                Assert.Equal(session.Project.Sources.Single().Id, content.SourceId);
                Assert.Equal(2, content.StreamIndex); // Source.CursorStreamIndex
            }
        }

        [Fact]
        public void AddKeyboardTrack_slots_between_screen_and_an_existing_cursor_row()
        {
            var session = NewSession(out var screenTrack, out var webcamTrack, out _);
            var cursor = session.AddCursorTrack();

            var keys = session.AddKeyboardTrack();

            Assert.NotNull(keys);
            Assert.True(session.HasKeyboardTrack);
            var keysTrack = TrackOf(session, keys);
            Assert.Equal("Keys", keysTrack.Name);
            Assert.Equal(TrackKind.Video, keysTrack.Kind);

            Assert.Equal(0, OrderOf(session, screenTrack.Id));
            Assert.Equal(1, keysTrack.Order);
            Assert.Equal(2, TrackOf(session, cursor).Order);
            Assert.Equal(3, OrderOf(session, webcamTrack.Id));
            Assert.Empty(session.Project.Validate());

            // keyboard placement is the item's transform: bottom-centre anchor, half width.
            Assert.Equal(0.5, keys.Transform.X);
            Assert.Equal(0.85, keys.Transform.Y);
            Assert.Equal(0.5, keys.Transform.Scale);
        }

        [Fact]
        public void AddCursorTrack_lands_above_an_existing_keyboard_row()
        {
            var session = NewSession(out var screenTrack, out _, out _);
            var keys = session.AddKeyboardTrack();

            var cursor = session.AddCursorTrack();

            Assert.Equal(0, OrderOf(session, screenTrack.Id));
            Assert.Equal(1, TrackOf(session, keys).Order);
            Assert.Equal(2, TrackOf(session, cursor).Order);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_second_add_is_refused_and_changes_nothing()
        {
            var session = NewSession(out _, out _, out _);
            session.AddCursorTrack();
            session.AddKeyboardTrack();
            var json = session.Project.ToJson();

            Assert.Null(session.AddCursorTrack());
            Assert.Null(session.AddKeyboardTrack());
            Assert.Equal(json, session.Project.ToJson());
        }

        [Fact]
        public void Adds_are_refused_without_an_input_capture_source()
        {
            var session = NewSession(out _, out _, out _, capturePath: null);
            var json = session.Project.ToJson();

            Assert.Null(session.AddCursorTrack());
            Assert.Null(session.AddKeyboardTrack());
            Assert.Equal(json, session.Project.ToJson());
        }

        [Fact]
        public void An_unlinked_screen_segment_gets_a_fresh_shared_group()
        {
            var session = NewSession(out var screenTrack, out _, out _);
            var project = session.Project;
            var loose = project.Items.First(i => i.TrackId == screenTrack.Id);
            loose.LinkGroupId = null;

            session.AddCursorTrack();

            var refreshed = session.Project.Items.Single(i => i.Id == loose.Id);
            Assert.NotNull(refreshed.LinkGroupId);
            var mirror = session.Project.Items.Single(i =>
                i.Content is CursorContent && i.TimelineStartTicks == refreshed.TimelineStartTicks);
            Assert.Equal(refreshed.LinkGroupId, mirror.LinkGroupId);
            Assert.Empty(session.Project.Validate());
        }

        // ------------------------------------------------------------------------------ hard sync

        [Fact]
        public void Moving_a_cursor_item_moves_its_whole_recording_segment()
        {
            var session = NewSession(out var screenTrack, out _, out _);
            session.AddCursorTrack();
            var cursorItem = session.Project.Items
                .Where(i => i.Content is CursorContent).OrderBy(i => i.TimelineStartTicks).Last();
            var screenItem = session.Project.Items
                .Where(i => i.TrackId == screenTrack.Id).OrderBy(i => i.TimelineStartTicks).Last();

            Assert.Equal(Ms(1_000), session.MoveItem(cursorItem.Id, Ms(1_000)));

            Assert.Equal(Ms(9_000), session.Project.Items.Single(i => i.Id == screenItem.Id).TimelineStartTicks);
            Assert.Equal(Ms(9_000), session.Project.Items.Single(i => i.Id == cursorItem.Id).TimelineStartTicks);
        }

        [Fact]
        public void SplitAt_cuts_the_overlay_with_its_segment()
        {
            var session = NewSession(out var screenTrack, out _, out _);
            session.AddCursorTrack();
            var cursorItem = session.Project.Items
                .Where(i => i.Content is CursorContent).OrderBy(i => i.TimelineStartTicks).First();

            Assert.True(session.SplitAt(cursorItem.Id, Ms(4_000)));

            Assert.Equal(3, session.Project.Items.Count(i => i.Content is CursorContent));
            Assert.Equal(3, session.Project.Items.Count(i => i.TrackId == screenTrack.Id));
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void RippleDeleting_a_screen_segment_takes_its_overlay_items_along()
        {
            var session = NewSession(out var screenTrack, out _, out _);
            session.AddCursorTrack();
            var firstScreen = session.Project.Items
                .Where(i => i.TrackId == screenTrack.Id).OrderBy(i => i.TimelineStartTicks).First();

            session.RippleDeleteItem(firstScreen.Id);

            var cursorItems = session.Project.Items.Where(i => i.Content is CursorContent).ToList();
            Assert.Single(cursorItems);
            Assert.Equal(0, cursorItems[0].TimelineStartTicks); // the gap closed
            Assert.True(session.HasCursorTrack);
            Assert.Empty(session.Project.Validate());
        }

        // ------------------------------------------------------------------------------- refusals

        [Fact]
        public void UnlinkTrack_refuses_overlay_rows()
        {
            var session = NewSession(out _, out var webcamTrack, out _);
            var cursor = session.AddCursorTrack();
            var keys = session.AddKeyboardTrack();
            var json = session.Project.ToJson();
            var undoCount = 0;
            session.HistoryChanged += (_, _) => undoCount++;

            session.UnlinkTrack(cursor.TrackId);
            session.UnlinkTrack(keys.TrackId);

            Assert.Equal(json, session.Project.ToJson());
            Assert.Equal(0, undoCount); // refused before the pipeline, not rolled back inside it

            // a normal row still unlinks.
            session.UnlinkTrack(webcamTrack.Id);
            Assert.All(session.Project.Items.Where(i => i.TrackId == webcamTrack.Id),
                i => Assert.Null(i.LinkGroupId));
        }

        [Fact]
        public void Overlay_rows_never_reorder_or_duplicate()
        {
            var session = NewSession(out _, out _, out _);
            var cursor = session.AddCursorTrack();
            var json = session.Project.ToJson();

            Assert.False(session.CanMoveTrackLayer(cursor.TrackId, towardsFront: true));
            Assert.False(session.CanMoveTrackLayer(cursor.TrackId, towardsFront: false));
            Assert.False(session.MoveTrackLayer(cursor.TrackId, towardsFront: false));
            Assert.False(session.MoveTrackToIndex(cursor.TrackId, 0));
            Assert.False(session.DuplicateTrack(cursor.TrackId));
            Assert.Equal(json, session.Project.ToJson());
        }

        [Fact]
        public void The_screen_row_never_rises_above_its_overlay_rows()
        {
            var session = NewSession(out var screenTrack, out var webcamTrack, out _);
            var cursor = session.AddCursorTrack();
            var json = session.Project.ToJson();

            // screen=0, cursor=1, webcam=2: every raise of the screen row would cross the
            // cursor row and bury it beneath the opaque screen frame.
            Assert.False(session.CanMoveTrackLayer(screenTrack.Id, towardsFront: true));
            Assert.False(session.MoveTrackLayer(screenTrack.Id, towardsFront: true));
            Assert.False(session.MoveTrackToIndex(screenTrack.Id, 1));
            Assert.False(session.MoveTrackToIndex(screenTrack.Id, 2));
            Assert.Equal(json, session.Project.ToJson());

            // with the webcam stepped down between them (screen=0, webcam=1, cursor=2) the
            // screen may still step over the webcam — the pin binds it only to its overlays.
            Assert.True(session.MoveTrackLayer(webcamTrack.Id, towardsFront: false));
            Assert.True(session.CanMoveTrackLayer(screenTrack.Id, towardsFront: true));
            Assert.True(session.MoveTrackLayer(screenTrack.Id, towardsFront: true));
            Assert.Equal(0, OrderOf(session, webcamTrack.Id));
            Assert.Equal(1, OrderOf(session, screenTrack.Id));
            Assert.Equal(2, TrackOf(session, cursor).Order);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void AddText_never_lands_on_an_overlay_row()
        {
            var session = NewSession(out _, out var webcamTrack, out _);
            session.SetTrackHidden(webcamTrack.Id, true); // the fresh-edit shape: webcam hidden
            session.AddCursorTrack();
            var firstCursor = session.Project.Items
                .Where(i => i.Content is CursorContent).OrderBy(i => i.TimelineStartTicks).First();
            session.DeleteItem(firstCursor.Id); // frees [0, 8s) on the cursor row

            var text = session.AddText(Ms(1_000), Ms(2_000));

            Assert.NotNull(text);
            var row = TrackOf(session, text);
            Assert.DoesNotContain(session.Project.Items,
                i => i.TrackId == row.Id && i.Content is CursorContent);
            Assert.Equal("Overlay", row.Name);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void Other_video_rows_still_reorder_around_an_overlay_row()
        {
            var session = NewSession(out var screenTrack, out var webcamTrack, out _);
            session.AddCursorTrack();

            // webcam (frontmost of the block) drops one layer, stepping past the cursor row.
            Assert.True(session.MoveTrackLayer(webcamTrack.Id, towardsFront: false));

            Assert.Equal(0, OrderOf(session, screenTrack.Id));
            Assert.Equal(1, OrderOf(session, webcamTrack.Id));
            Assert.Empty(session.Project.Validate());
        }

        // -------------------------------------------------------------------------- prune / flags

        [Fact]
        public void Deleting_the_last_overlay_item_prunes_the_row()
        {
            var session = NewSession(out _, out _, out _);
            session.AddCursorTrack();
            var cursorItems = session.Project.Items.Where(i => i.Content is CursorContent).ToList();

            session.DeleteItem(cursorItems[0].Id);
            Assert.True(session.HasCursorTrack); // one item left, row survives

            session.DeleteItem(cursorItems[1].Id);
            Assert.False(session.HasCursorTrack); // row pruned — the sidebar re-enables
            Assert.DoesNotContain(session.Project.Tracks, t => t.Name == "Cursor");
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_pruned_row_can_be_added_again()
        {
            var session = NewSession(out _, out _, out _);
            var first = session.AddCursorTrack();
            session.DeleteTrack(first.TrackId);
            Assert.False(session.HasCursorTrack);

            Assert.NotNull(session.AddCursorTrack());
            Assert.True(session.HasCursorTrack);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void Has_flags_classify_by_content_not_name()
        {
            var session = NewSession(out _, out _, out _);
            Assert.False(session.HasCursorTrack);
            Assert.False(session.HasKeyboardTrack);

            session.AddCursorTrack();
            Assert.True(session.HasCursorTrack);
            Assert.False(session.HasKeyboardTrack);

            // renaming the row changes nothing — classification is by item content.
            var track = session.Project.Tracks.Single(t => t.Name == "Cursor");
            session.RenameTrack(track.Id, "My pointer");
            Assert.True(session.HasCursorTrack);
        }

        // -------------------------------------------------------------------------- undo / redo

        [Fact]
        public void Undo_and_redo_replay_an_overlay_add()
        {
            var session = NewSession(out _, out _, out _);
            var before = session.Project.ToJson();
            session.AddCursorTrack();
            var after = session.Project.ToJson();

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.HasCursorTrack);

            session.Redo();
            Assert.Equal(after, session.Project.ToJson());
            Assert.True(session.HasCursorTrack);
        }

        // ---------------------------------------------------------------------- HasInputCapture

        [Fact]
        public void HasInputCapture_requires_the_sidecar_file_to_exist()
        {
            var missing = Path.Combine(Path.GetTempPath(), $"input-capture-{Guid.NewGuid():N}.jsonl");
            var session = NewSession(out _, out _, out _, capturePath: missing);

            Assert.False(session.HasInputCapture);
        }

        [Fact]
        public void HasInputCapture_is_true_for_an_existing_sidecar_and_caches_the_probe()
        {
            var path = Path.Combine(Path.GetTempPath(), $"input-capture-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(path, "");
            try
            {
                var session = NewSession(out _, out _, out _, capturePath: path);
                Assert.True(session.HasInputCapture);

                // the probe is cached: the file vanishing mid-session does not flip the flag
                // (and CanExecute polling never hits the disk again).
                File.Delete(path);
                Assert.True(session.HasInputCapture);
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Fact]
        public void HasInputCapture_is_false_without_a_capture_path()
        {
            var session = NewSession(out _, out _, out _, capturePath: null);
            Assert.False(session.HasInputCapture);
        }

        // --------------------------------------------------------------------------- source ops

        [Fact]
        public void Removing_the_source_takes_the_overlay_rows_with_it()
        {
            var session = NewSession(out _, out _, out _);
            session.AddCursorTrack();
            session.AddKeyboardTrack();
            var sourceId = session.Project.Sources.Single().Id;

            Assert.True(session.RemoveSource(sourceId));

            Assert.Empty(session.Project.Items);
            Assert.DoesNotContain(session.Project.Tracks, t => t.Name is "Cursor" or "Keys");
            Assert.False(session.HasCursorTrack);
            Assert.Empty(session.Project.Validate());
        }
    }
}
