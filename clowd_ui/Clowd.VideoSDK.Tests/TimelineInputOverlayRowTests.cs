using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.UI.VideoEditor.Timeline;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // The timeline's input-capture rows: how the layout classifies them, how it pins each one
    // directly above its own screen row whatever the track Order says, and how the reorder math
    // keeps them (and the boundaries either side of them) out of every drag — against the same
    // EditorSession refusals the drop would hit. Pure model math, no Avalonia runtime — Clowd.Ui
    // exposes its internals to this project via InternalsVisibleTo.
    public class TimelineInputOverlayRowTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        private static Track NewTrack(TrackKind kind, int order, string name) => new Track
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Order = order,
            Name = name,
        };

        private static Item NewItem(Track track, ItemContent content, long startTicks = 0,
            long durationTicks = TimeSpan.TicksPerSecond) => new Item
        {
            Id = Guid.NewGuid(),
            TrackId = track.Id,
            TimelineStartTicks = startTicks,
            DurationTicks = durationTicks,
            Content = content,
        };

        // ------------------------------------------------------------------------ classification

        [Fact]
        public void KindOf_classifies_the_input_overlay_rows_by_their_earliest_item()
        {
            var cursor = NewTrack(TrackKind.Video, 3, "Cursor");
            var keys = NewTrack(TrackKind.Video, 2, "Keys");

            Assert.Equal(TimelineRowKind.Cursor,
                TimelineRowLayout.KindOf(cursor, new[] { NewItem(cursor, new CursorContent()) }));
            Assert.Equal(TimelineRowKind.Keyboard,
                TimelineRowLayout.KindOf(keys, new[] { NewItem(keys, new KeyboardContent()) }));
        }

        [Fact]
        public void Input_overlay_rows_are_card_height()
        {
            Assert.Equal(26, TimelineRowLayout.HeightOf(TimelineRowKind.Cursor));
            Assert.Equal(26, TimelineRowLayout.HeightOf(TimelineRowKind.Keyboard));
            Assert.True(TimelineRowLayout.IsInputOverlay(TimelineRowKind.Cursor));
            Assert.True(TimelineRowLayout.IsInputOverlay(TimelineRowKind.Keyboard));
            Assert.False(TimelineRowLayout.IsInputOverlay(TimelineRowKind.Video));
        }

        // -------------------------------------------------------------------------------- layout

        /// <summary>The pin is a relation, not a number: a webcam row whose Order sits between the
        /// screen row and its overlays still draws above them, because the overlays are laid out
        /// against their own screen row rather than against the layer run they came out of.</summary>
        [Fact]
        public void Build_pins_the_overlay_rows_directly_above_their_screen_row()
        {
            var sourceId = Guid.NewGuid();
            var screen = NewTrack(TrackKind.Video, 0, "Screen");
            var keys = NewTrack(TrackKind.Video, 1, "Keys");
            var webcam = NewTrack(TrackKind.Video, 2, "Webcam");
            var cursor = NewTrack(TrackKind.Video, 3, "Cursor");
            var audio = NewTrack(TrackKind.Audio, 4, "Audio");

            var project = new Project
            {
                Tracks = { screen, keys, webcam, cursor, audio },
                Items =
                {
                    NewItem(screen, new MediaContent { SourceId = sourceId, StreamIndex = 0 }),
                    NewItem(webcam, new MediaContent { SourceId = sourceId, StreamIndex = 1 }),
                    NewItem(keys, new KeyboardContent { SourceId = sourceId }),
                    NewItem(cursor, new CursorContent { SourceId = sourceId }),
                },
                Sources = { new Source { Id = sourceId, Path = @"C:\rec\in.mp4" } },
            };

            var rows = TimelineRowLayout.Build(project);

            Assert.Equal(new[] { webcam.Id, cursor.Id, keys.Id, screen.Id, audio.Id },
                rows.Select(r => r.TrackId).ToArray());
            Assert.Equal(new[]
            {
                TimelineRowKind.Video, TimelineRowKind.Cursor, TimelineRowKind.Keyboard,
                TimelineRowKind.Video, TimelineRowKind.Audio,
            }, rows.Select(r => r.Kind).ToArray());
            Assert.Equal(new[] { 56d, 26d, 26d, 56d, 36d }, rows.Select(r => r.Height).ToArray());
        }

        /// <summary>The overlays glue to the row playing the source's <b>screen stream</b>, not to
        /// whatever row of the source is backmost: a webcam row dragged behind the screen row
        /// plays the same source, and pinning to it would draw the overlays under the screen row
        /// while they composite above it.</summary>
        [Fact]
        public void Build_pins_to_the_screen_row_even_when_the_webcam_row_is_behind_it()
        {
            var sourceId = Guid.NewGuid();
            var webcam = NewTrack(TrackKind.Video, 0, "Webcam"); // dropped behind the screen row
            var screen = NewTrack(TrackKind.Video, 1, "Screen");
            var keys = NewTrack(TrackKind.Video, 2, "Keys");
            var cursor = NewTrack(TrackKind.Video, 3, "Cursor");

            var project = new Project
            {
                Tracks = { webcam, screen, keys, cursor },
                Items =
                {
                    NewItem(screen, new MediaContent { SourceId = sourceId, StreamIndex = 0 }),
                    NewItem(webcam, new MediaContent { SourceId = sourceId, StreamIndex = 1 }),
                    NewItem(keys, new KeyboardContent { SourceId = sourceId }),
                    NewItem(cursor, new CursorContent { SourceId = sourceId }),
                },
                Sources = { new Source { Id = sourceId, Path = @"C:\rec\in.mp4" } },
            };

            var rows = TimelineRowLayout.Build(project);

            Assert.Equal(new[] { cursor.Id, keys.Id, screen.Id, webcam.Id },
                rows.Select(r => r.TrackId).ToArray());
        }

        /// <summary>An overlay whose screen row is gone draws nothing and pins to nothing — it
        /// keeps its natural place in the layer run rather than dropping out of the list.</summary>
        [Fact]
        public void Build_keeps_an_orphaned_overlay_row_in_the_video_block()
        {
            var cursor = NewTrack(TrackKind.Video, 1, "Cursor");
            var overlay = NewTrack(TrackKind.Video, 0, "Overlay");

            var project = new Project
            {
                Tracks = { cursor, overlay },
                Items =
                {
                    NewItem(cursor, new CursorContent { SourceId = Guid.NewGuid() }),
                    NewItem(overlay, new TextContent { Text = "hi" }),
                },
            };

            var rows = TimelineRowLayout.Build(project);

            Assert.Equal(new[] { cursor.Id, overlay.Id }, rows.Select(r => r.TrackId).ToArray());
        }

        // ------------------------------------------------------------------------------- reorder

        /// <summary>The stack a recording with both overlays draws: webcam, cursor, keys, screen,
        /// then the audio row — in the canonical model order, where the display is the exact
        /// reverse and the video-block layer indexes run 3 down to 0.</summary>
        private static IReadOnlyList<TimelineRow> Stack()
        {
            var kinds = new[]
            {
                TimelineRowKind.Video, TimelineRowKind.Cursor, TimelineRowKind.Keyboard,
                TimelineRowKind.Video, TimelineRowKind.Audio,
            };
            var videoLayer = kinds.Count(k => k != TimelineRowKind.Audio);
            var audioLayer = 0;
            var rows = new List<TimelineRow>();
            double top = 0;
            foreach (var kind in kinds)
            {
                var height = TimelineRowLayout.HeightOf(kind);
                var layer = kind == TimelineRowKind.Audio ? audioLayer++ : --videoLayer;
                rows.Add(new TimelineRow(Guid.NewGuid(), kind, top, height, layer));
                top += height;
            }

            return rows;
        }

        [Fact]
        public void GroupRange_makes_each_overlay_row_a_block_of_one()
        {
            var rows = Stack();

            // …which is what denies them a grip in the header panel
            Assert.Equal((1, 1), TimelineReorder.GroupRange(rows, 1));
            Assert.Equal((2, 2), TimelineReorder.GroupRange(rows, 2));

            // the video block still runs across them: the rows either side of an overlay are one
            // block, or a webcam could never be dragged past the screen row
            Assert.Equal((0, 3), TimelineReorder.GroupRange(rows, 0));
            Assert.Equal((0, 3), TimelineReorder.GroupRange(rows, 3));
            Assert.Equal((4, 4), TimelineReorder.GroupRange(rows, 4));
        }

        [Fact]
        public void Overlay_rows_never_produce_a_move()
        {
            var rows = Stack();

            for (var drop = 0; drop <= rows.Count; drop++)
            {
                Assert.Null(TimelineReorder.TargetLayerIndex(rows, 1, drop));
                Assert.Null(TimelineReorder.TargetLayerIndex(rows, 2, drop));
            }
        }

        /// <summary>No drop may land between a screen row and the overlays pinned above it: the
        /// layout would put the row somewhere else entirely, so the indicator is pushed up past
        /// the whole overlay run instead of promising a landing that cannot happen.</summary>
        [Fact]
        public void No_drop_lands_between_a_screen_row_and_its_overlays()
        {
            var rows = Stack();

            for (var slot = 0; slot <= 4; slot++)
            {
                var coerced = TimelineReorder.CoerceDropIndex(rows, 0, slot);
                Assert.False(coerced > 0 && TimelineRowLayout.IsInputOverlay(rows[coerced - 1].Kind),
                    $"slot {slot} coerced to {coerced}, which is inside the overlay run");
                Assert.Equal(coerced, TimelineReorder.CoerceDropIndex(rows, 0, coerced)); // idempotent
            }

            // slots 2 and 3 are the two illegal ones; both collapse to the boundary above the run
            Assert.Equal(1, TimelineReorder.CoerceDropIndex(rows, 0, 2));
            Assert.Equal(1, TimelineReorder.CoerceDropIndex(rows, 0, 3));
            Assert.Equal(4, TimelineReorder.CoerceDropIndex(rows, 0, 4));

            // …and the pointer-driven entry point agrees, wherever the pointer is
            for (var y = -20d; y <= TimelineRowLayout.TotalHeight(rows) + 20; y += 1)
            {
                var slot = TimelineReorder.DropIndexAt(rows, 0, y);
                Assert.False(slot > 0 && TimelineRowLayout.IsInputOverlay(rows[slot - 1].Kind));
            }
        }

        /// <summary>The other half of the pin: the screen row cannot be dragged above its own
        /// overlays — the session refuses that move outright, so the drag never offers it.</summary>
        [Fact]
        public void The_screen_row_cannot_be_dragged_above_its_overlays()
        {
            var rows = Stack();

            for (var slot = 0; slot <= 4; slot++)
                Assert.Null(TimelineReorder.TargetLayerIndex(rows, 3, slot));

            for (var y = -20d; y <= TimelineRowLayout.TotalHeight(rows) + 20; y += 1)
                Assert.True(TimelineReorder.DropIndexAt(rows, 3, y) >= 3);
        }

        // --------------------------------------------------------- agreement with EditorSession

        /// <summary>A recording of one source over a screen and a webcam row, with both overlay
        /// rows added by the session's own factories.</summary>
        private static EditorSession OverlaySession() => OverlaySession(out _, out _);

        private static EditorSession OverlaySession(out Track screenTrack, out Track webcamTrack)
        {
            var session = BareOverlaySession(out screenTrack, out webcamTrack);
            Assert.NotNull(session.AddKeyboardTrack());
            Assert.NotNull(session.AddCursorTrack());
            return session;
        }

        /// <summary>The same recording before any overlay row exists.</summary>
        private static EditorSession BareOverlaySession(out Track screenTrack, out Track webcamTrack)
        {
            var sourceId = Guid.NewGuid();
            var screen = NewTrack(TrackKind.Video, 0, "Screen");
            var webcam = NewTrack(TrackKind.Video, 1, "Webcam");
            var audio = NewTrack(TrackKind.Audio, 2, "Audio");
            screenTrack = screen;
            webcamTrack = webcam;

            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources =
                {
                    new Source
                    {
                        Id = sourceId,
                        Path = @"C:\rec\in.mp4",
                        InputCapturePath = @"C:\rec\input-capture.jsonl",
                        Streams =
                        {
                            new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080 },
                            new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 640, Height = 480 },
                        },
                    },
                },
                Tracks = { screen, webcam, audio },
                Items =
                {
                    NewItem(screen, new MediaContent { SourceId = sourceId, StreamIndex = 0 }, 0, Ms(20_000)),
                    NewItem(webcam, new MediaContent { SourceId = sourceId, StreamIndex = 1 }, 0, Ms(20_000)),
                },
            };

            return new EditorSession(project, null, save => save());
        }

        /// <summary>The factories glue the new row to the screen row even when the webcam row —
        /// which plays the same source — has been dragged behind it: measuring from the webcam
        /// row would insert the overlay beneath the screen frame, where it composes invisibly
        /// (and the default cursor overlay stands down because a cursor item exists).</summary>
        [Fact]
        public void The_factories_measure_from_the_screen_row_even_when_the_webcam_row_is_behind_it()
        {
            var session = BareOverlaySession(out var screen, out var webcam);
            Assert.True(session.MoveTrackToIndex(webcam.Id, 0)); // webcam to the backmost layer

            var keys = session.AddKeyboardTrack();
            var cursor = session.AddCursorTrack();
            Assert.NotNull(keys);
            Assert.NotNull(cursor);

            int OrderOf(Guid trackId) => session.Project.Tracks.First(t => t.Id == trackId).Order;
            Assert.True(OrderOf(webcam.Id) < OrderOf(screen.Id));
            Assert.Equal(OrderOf(screen.Id) + 1, OrderOf(keys.TrackId));
            Assert.Equal(OrderOf(screen.Id) + 2, OrderOf(cursor.TrackId));

            var rows = TimelineRowLayout.Build(session.Project);
            Assert.Equal(new[]
            {
                TimelineRowKind.Cursor, TimelineRowKind.Keyboard, TimelineRowKind.Video,
                TimelineRowKind.Video, TimelineRowKind.Audio,
            }, rows.Select(r => r.Kind).ToArray());
            Assert.Equal(screen.Id, rows[2].TrackId);
            Assert.Equal(webcam.Id, rows[3].TrackId);
            Assert.Empty(session.Project.Validate());
        }

        /// <summary>Every legal drop, driven all the way through the session: the layer index the
        /// panel would send must land the dragged row where the indicator showed it (measured
        /// against the rows that can move — the overlays travel with their screen row), and the
        /// pin must survive the move.</summary>
        [Fact]
        public void Every_legal_drop_lands_where_the_drag_showed_it()
        {
            var shape = TimelineRowLayout.Build(OverlaySession().Project);
            Assert.Equal(new[]
            {
                TimelineRowKind.Video, TimelineRowKind.Cursor, TimelineRowKind.Keyboard,
                TimelineRowKind.Video, TimelineRowKind.Audio,
            }, shape.Select(r => r.Kind).ToArray());

            AssertEveryLegalDropLands(OverlaySession);
        }

        /// <summary>The same sweep over the state whose display is <b>not</b> the exact reverse of
        /// the model: a text row on top and the webcam row stepped down inside the overlay run
        /// (screen, keys, webcam, cursor, text back to front) — the session permits that step, the
        /// glued display hides it, and a display-position flip would land drops a layer away from
        /// the indicator. The drop math counts in layer space precisely for this state.</summary>
        [Fact]
        public void Every_legal_drop_lands_even_when_a_row_sits_inside_the_overlay_run()
        {
            var shape = TimelineRowLayout.Build(AnomalousSession().Project);
            Assert.Equal(new[]
            {
                TimelineRowKind.Text, TimelineRowKind.Video, TimelineRowKind.Cursor,
                TimelineRowKind.Keyboard, TimelineRowKind.Video, TimelineRowKind.Audio,
            }, shape.Select(r => r.Kind).ToArray());

            AssertEveryLegalDropLands(AnomalousSession);
        }

        private static EditorSession AnomalousSession()
        {
            var session = OverlaySession(out _, out var webcam);
            Assert.NotNull(session.AddText(0, Ms(2_000)));
            Assert.True(session.MoveTrackLayer(webcam.Id, towardsFront: false));
            return session;
        }

        private static void AssertEveryLegalDropLands(Func<EditorSession> factory)
        {
            var shape = TimelineRowLayout.Build(factory().Project);

            for (var from = 0; from < shape.Count; from++)
            {
                var (start, end) = TimelineReorder.GroupRange(shape, from);

                for (var drop = start; drop <= end + 1; drop++)
                {
                    // fresh session per drop — MoveTrackToIndex mutates the project
                    var session = factory();
                    var rows = TimelineRowLayout.Build(session.Project);

                    var layerIndex = TimelineReorder.TargetLayerIndex(rows, from, drop);
                    if (layerIndex == null)
                        continue;

                    // what the drag meant, in the rows that can actually move
                    var slot = TimelineReorder.CoerceDropIndex(rows, from, drop);
                    var movable = rows.Skip(start).Take(end - start + 1)
                        .Where(r => !TimelineRowLayout.IsInputOverlay(r.Kind))
                        .Select(r => r.TrackId).ToList();
                    var insertAt = rows.Skip(start).Take(slot - start)
                        .Count(r => !TimelineRowLayout.IsInputOverlay(r.Kind)) - (slot > from ? 1 : 0);
                    var moved = rows[from].TrackId;
                    movable.Remove(moved);
                    movable.Insert(insertAt, moved);

                    Assert.True(session.MoveTrackToIndex(moved, layerIndex.Value));
                    var after = TimelineRowLayout.Build(session.Project);

                    Assert.Equal(movable, after.Skip(start).Take(end - start + 1)
                        .Where(r => !TimelineRowLayout.IsInputOverlay(r.Kind))
                        .Select(r => r.TrackId).ToList());
                    AssertOverlaysArePinned(after);
                }
            }
        }

        /// <summary>Cursor, keys and screen stay in that order with nothing between them.</summary>
        private static void AssertOverlaysArePinned(IReadOnlyList<TimelineRow> rows)
        {
            var cursor = rows.Select((r, i) => (r, i)).Single(x => x.r.Kind == TimelineRowKind.Cursor).i;
            var keys = rows.Select((r, i) => (r, i)).Single(x => x.r.Kind == TimelineRowKind.Keyboard).i;

            Assert.Equal(cursor + 1, keys);
            Assert.Equal(TimelineRowKind.Video, rows[keys + 1].Kind);
        }

        [Fact]
        public void The_session_refuses_to_move_an_overlay_row_at_all()
        {
            var session = OverlaySession();
            var rows = TimelineRowLayout.Build(session.Project);
            var json = session.Project.ToJson();

            foreach (var row in rows.Where(r => TimelineRowLayout.IsInputOverlay(r.Kind)))
            {
                for (var index = 0; index < session.Project.Tracks.Count; index++)
                    Assert.False(session.MoveTrackToIndex(row.TrackId, index));

                Assert.False(session.CanMoveTrackLayer(row.TrackId, towardsFront: true));
                Assert.False(session.CanMoveTrackLayer(row.TrackId, towardsFront: false));
            }

            Assert.Equal(json, session.Project.ToJson());
        }
    }
}
