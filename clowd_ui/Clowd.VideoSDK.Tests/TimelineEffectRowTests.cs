using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.UI.VideoEditor.Timeline;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // The timeline's effect rows: how the layout classifies and stacks them (speed pinned first,
    // zoom inside the video block) and how the reorder math keeps the speed row pinned while zoom
    // rows move with the video rows — against the same EditorSession index space the drop lands
    // in. Pure model math, no Avalonia runtime — Clowd.Ui exposes its internals to this project
    // via InternalsVisibleTo.
    public class TimelineEffectRowTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        private static Track NewTrack(TrackKind kind, int order, string name = null) => new Track
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Order = order,
            Name = name ?? kind.ToString(),
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
        public void KindOf_classifies_effect_rows_by_their_earliest_item()
        {
            var speed = NewTrack(TrackKind.Effect, 5, "Speed");
            var zoom = NewTrack(TrackKind.Effect, 2, "Zoom");

            Assert.Equal(TimelineRowKind.Speed,
                TimelineRowLayout.KindOf(speed, new[] { NewItem(speed, new SpeedContent()) }));
            Assert.Equal(TimelineRowKind.Zoom,
                TimelineRowLayout.KindOf(zoom, new[] { NewItem(zoom, new ZoomContent()) }));
        }

        /// <summary>An empty effect track only exists in a hand-edited file (the session prunes
        /// them), but the layout must classify it without items to ask — and a row without a
        /// SpeedContent item cannot be the speed row.</summary>
        [Fact]
        public void KindOf_reads_an_empty_effect_track_as_zoom()
        {
            var track = NewTrack(TrackKind.Effect, 0, "Zoom");

            Assert.Equal(TimelineRowKind.Zoom, TimelineRowLayout.KindOf(track, null));
            Assert.Equal(TimelineRowKind.Zoom, TimelineRowLayout.KindOf(track, Array.Empty<Item>()));
        }

        [Fact]
        public void Effect_rows_are_card_height()
        {
            Assert.Equal(26, TimelineRowLayout.HeightOf(TimelineRowKind.Speed));
            Assert.Equal(26, TimelineRowLayout.HeightOf(TimelineRowKind.Zoom));
        }

        // -------------------------------------------------------------------------------- layout

        /// <summary>The three blocks: the speed row pinned first, zoom rows interleaved with the
        /// video rows by descending Order, audio underneath.</summary>
        [Fact]
        public void Build_pins_the_speed_row_first_and_stacks_zoom_in_the_video_block()
        {
            var screen = NewTrack(TrackKind.Video, 0, "Screen");
            var zoom = NewTrack(TrackKind.Effect, 1, "Zoom");
            var webcam = NewTrack(TrackKind.Video, 2, "Webcam");
            var audio = NewTrack(TrackKind.Audio, 3, "Audio");
            var speed = NewTrack(TrackKind.Effect, 4, "Speed");

            var project = new Project
            {
                Tracks = new List<Track> { screen, zoom, webcam, audio, speed },
                Items = new List<Item>
                {
                    NewItem(screen, new SolidContent { Color = "#FF000000" }),
                    NewItem(zoom, new ZoomContent()),
                    NewItem(webcam, new SolidContent { Color = "#FFFFFFFF" }),
                    NewItem(speed, new SpeedContent()),
                },
            };

            var rows = TimelineRowLayout.Build(project);

            Assert.Equal(new[] { speed.Id, webcam.Id, zoom.Id, screen.Id, audio.Id },
                rows.Select(r => r.TrackId).ToArray());
            Assert.Equal(new[]
            {
                TimelineRowKind.Speed, TimelineRowKind.Video, TimelineRowKind.Zoom,
                TimelineRowKind.Video, TimelineRowKind.Audio,
            }, rows.Select(r => r.Kind).ToArray());
            Assert.Equal(new[] { 26d, 56d, 26d, 56d, 36d }, rows.Select(r => r.Height).ToArray());
            // a BlockGap (8) after the speed row and another before the audio row
            Assert.Equal(new[] { 0d, 34d, 90d, 116d, 180d }, rows.Select(r => r.Top).ToArray());
            Assert.Equal(216d, TimelineRowLayout.TotalHeight(rows));
        }

        /// <summary>The session keeps the speed row's Order above everything, but the pin is a
        /// property of the row's meaning, not of a number a hand-edited file can carry — it leads
        /// the layout whatever its Order says.</summary>
        [Fact]
        public void Build_pins_the_speed_row_even_with_a_low_order()
        {
            var speed = NewTrack(TrackKind.Effect, 0, "Speed");
            var screen = NewTrack(TrackKind.Video, 1, "Screen");

            var project = new Project
            {
                Tracks = new List<Track> { speed, screen },
                Items = new List<Item> { NewItem(speed, new SpeedContent()) },
            };

            var rows = TimelineRowLayout.Build(project);

            Assert.Equal(new[] { speed.Id, screen.Id }, rows.Select(r => r.TrackId).ToArray());
        }

        // ------------------------------------------------------------------------------- reorder

        /// <summary>Rows as the header panel sees them, stacked from y = 0 with each kind's real
        /// height. The display here is the exact model reverse, so video-block layer indexes run
        /// count-1 down to 0, audio indexes 0 upward, and the speed row sits outside both
        /// spaces — what <c>TimelineRowLayout.Build</c> would stamp on the same shape.</summary>
        private static IReadOnlyList<TimelineRow> Rows(params TimelineRowKind[] kinds)
        {
            var videoLayer = kinds.Count(k => k != TimelineRowKind.Speed && k != TimelineRowKind.Audio);
            var audioLayer = 0;
            var rows = new List<TimelineRow>();
            double top = 0;
            foreach (var kind in kinds)
            {
                var height = TimelineRowLayout.HeightOf(kind);
                var layer = kind == TimelineRowKind.Speed ? -1
                    : kind == TimelineRowKind.Audio ? audioLayer++
                    : --videoLayer;
                rows.Add(new TimelineRow(Guid.NewGuid(), kind, top, height, layer));
                top += height;
            }

            return rows;
        }

        /// <summary>The full stack: the pinned speed row, a video block with zoom rows in it, and
        /// the audio block.</summary>
        private static IReadOnlyList<TimelineRow> Stack() => Rows(
            TimelineRowKind.Speed, TimelineRowKind.Zoom, TimelineRowKind.Video,
            TimelineRowKind.Zoom, TimelineRowKind.Video, TimelineRowKind.Audio,
            TimelineRowKind.Audio);

        [Fact]
        public void GroupRange_makes_the_speed_row_a_block_of_one()
        {
            var rows = Stack();

            Assert.Equal((0, 0), TimelineReorder.GroupRange(rows, 0));

            // …which is exactly what the header panel asks before giving a row a grip
            Assert.Equal((1, 4), TimelineReorder.GroupRange(rows, 1));
            Assert.Equal((1, 4), TimelineReorder.GroupRange(rows, 4));
            Assert.Equal((5, 6), TimelineReorder.GroupRange(rows, 5));
        }

        [Fact]
        public void The_speed_row_never_produces_a_move()
        {
            var rows = Stack();

            for (var drop = 0; drop <= rows.Count; drop++)
                Assert.Null(TimelineReorder.TargetLayerIndex(rows, 0, drop));
        }

        /// <summary>The video block's flip counts its zoom rows — display top is the highest layer
        /// index of the non-audio non-speed tracks, which is the space
        /// <c>EditorSession.MoveTrackToIndex</c> counts in.</summary>
        [Fact]
        public void TargetLayerIndex_flips_zoom_rows_with_the_video_block()
        {
            var rows = Stack(); // video block is rows 1..4, so layer indexes run 3..0 down it

            Assert.Equal(3, TimelineReorder.TargetLayerIndex(rows, 4, 1)); // bottom row to the top
            Assert.Equal(0, TimelineReorder.TargetLayerIndex(rows, 1, 5)); // top row to the bottom
            Assert.Equal(2, TimelineReorder.TargetLayerIndex(rows, 3, 2));
            Assert.Null(TimelineReorder.TargetLayerIndex(rows, 2, 2));     // a drag that came home
        }

        // --------------------------------------------------------- agreement with EditorSession

        /// <summary>A session carrying every block: two video rows, two zoom rows, the speed row
        /// and an audio row.</summary>
        private static EditorSession EffectSession()
        {
            var screen = NewTrack(TrackKind.Video, 0, "Screen");
            var webcam = NewTrack(TrackKind.Video, 1, "Webcam");
            var audio = NewTrack(TrackKind.Audio, 2, "Audio");

            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Tracks = { screen, webcam, audio },
                Items =
                {
                    NewItem(screen, new SolidContent { Color = "#FF000000" }, 0, Ms(20_000)),
                    NewItem(webcam, new SolidContent { Color = "#FFFFFFFF" }, 0, Ms(20_000)),
                },
            };

            var session = new EditorSession(project, null, save => save());
            Assert.NotNull(session.AddZoomEffect(Ms(1_000), Ms(5_000)));
            Assert.NotNull(session.AddZoomEffect(Ms(8_000), Ms(5_000)));
            Assert.NotNull(session.AddSpeedEffect(Ms(2_000), Ms(5_000)));
            return session;
        }

        /// <summary>Every drop of every row, driven all the way through the session: the layer
        /// index the panel would send must make <c>MoveTrackToIndex</c> land the row exactly where
        /// the drag showed it — the two index spaces (display flip here, ascending (Order, Id)
        /// minus the speed row there) must agree.</summary>
        [Fact]
        public void Every_drop_lands_where_the_drag_showed_it()
        {
            var shape = TimelineRowLayout.Build(EffectSession().Project);
            Assert.Equal(new[]
            {
                TimelineRowKind.Speed, TimelineRowKind.Zoom, TimelineRowKind.Zoom,
                TimelineRowKind.Video, TimelineRowKind.Video, TimelineRowKind.Audio,
            }, shape.Select(r => r.Kind).ToArray());

            for (var from = 0; from < shape.Count; from++)
            {
                var (start, end) = TimelineReorder.GroupRange(shape, from);

                for (var drop = start; drop <= end + 1; drop++)
                {
                    // fresh session per drop — MoveTrackToIndex mutates the project
                    var session = EffectSession();
                    var rows = TimelineRowLayout.Build(session.Project);

                    var layerIndex = TimelineReorder.TargetLayerIndex(rows, from, drop);
                    if (layerIndex == null)
                        continue;

                    // what the drag meant, spelled out: the block's rows with the dragged one moved
                    var display = rows.Skip(start).Take(end - start + 1).Select(r => r.TrackId).ToList();
                    var moved = rows[from].TrackId;
                    display.Remove(moved);
                    display.Insert((drop > from ? drop - 1 : drop) - start, moved);

                    Assert.True(session.MoveTrackToIndex(moved, layerIndex.Value));
                    var after = TimelineRowLayout.Build(session.Project);
                    Assert.Equal(display, after.Skip(start).Take(end - start + 1).Select(r => r.TrackId).ToList());

                    // …and the blocks either side never move
                    Assert.Equal(rows.Take(start).Select(r => r.TrackId),
                        after.Take(start).Select(r => r.TrackId));
                    Assert.Equal(rows.Skip(end + 1).Select(r => r.TrackId),
                        after.Skip(end + 1).Select(r => r.TrackId));
                }
            }
        }

        [Fact]
        public void The_session_refuses_to_move_the_speed_row_at_all()
        {
            var session = EffectSession();
            var rows = TimelineRowLayout.Build(session.Project);
            var speedId = rows[0].TrackId;
            var json = session.Project.ToJson();

            for (var index = 0; index < session.Project.Tracks.Count; index++)
                Assert.False(session.MoveTrackToIndex(speedId, index));

            Assert.False(session.CanMoveTrackLayer(speedId, towardsFront: true));
            Assert.False(session.CanMoveTrackLayer(speedId, towardsFront: false));
            Assert.Equal(json, session.Project.ToJson());
        }
    }
}
