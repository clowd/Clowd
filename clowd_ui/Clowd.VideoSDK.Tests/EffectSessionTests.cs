using System;
using System.Linq;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="EditorSession"/>'s effect-item surface: the speed row's find-or-create,
    /// pinning and uniqueness, zoom rows stacking above the video block, effect-row pruning,
    /// and the effect setters — all pure model math over a sources-free fixture.
    /// </summary>
    public class EffectSessionTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>Two video rows carrying solid clips and an empty audio row — the order shape
        /// of a recording (screen 0, webcam 1, audio 2) without any sources to resolve.</summary>
        private static Project BaseProject(out Item screenItem, out Track screenTrack,
            out Track webcamTrack, out Track audioTrack)
        {
            screenTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            webcamTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Webcam", Order = 1 };
            audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 2 };

            screenItem = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = screenTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(20_000),
                Content = new SolidContent { Color = "#FF000000" },
            };

            return new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Tracks = { screenTrack, webcamTrack, audioTrack },
                Items =
                {
                    screenItem,
                    new Item
                    {
                        Id = Guid.NewGuid(),
                        TrackId = webcamTrack.Id,
                        TimelineStartTicks = 0,
                        DurationTicks = Ms(10_000),
                        Content = new SolidContent { Color = "#FFFFFFFF" },
                    },
                },
            };
        }

        private static EditorSession NewSession(out Track screenTrack, out Track webcamTrack,
            out Track audioTrack)
        {
            var project = BaseProject(out _, out screenTrack, out webcamTrack, out audioTrack);
            return new EditorSession(project, null, save => save());
        }

        private static Track TrackOf(EditorSession session, Item item) =>
            session.Project.Tracks.Single(t => t.Id == item.TrackId);

        // ------------------------------------------------------------------------------ add speed

        [Fact]
        public void AddSpeedEffect_creates_the_speed_row_above_everything()
        {
            var session = NewSession(out _, out _, out var audioTrack);
            ProjectChangeKind? kind = null;
            session.ProjectChanged += (_, e) => kind = e.Kind;

            Assert.False(session.HasSpeedTrack);
            var item = session.AddSpeedEffect(Ms(1_000), Ms(5_000));

            Assert.NotNull(item);
            Assert.Equal(2.0, ((SpeedContent)item.Content).Factor);
            Assert.Equal(Ms(1_000), item.TimelineStartTicks);
            Assert.Equal(Ms(5_000), item.DurationTicks);
            Assert.Equal(ProjectChangeKind.Structural, kind);
            Assert.True(session.HasSpeedTrack);

            var track = TrackOf(session, item);
            Assert.Equal(TrackKind.Effect, track.Kind);
            Assert.Equal("Speed", track.Name);
            Assert.Equal(session.Project.Tracks.Max(t => t.Order), track.Order);
            Assert.True(track.Order > session.Project.Tracks.Single(t => t.Id == audioTrack.Id).Order);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void AddSpeedEffect_reuses_the_existing_row()
        {
            var session = NewSession(out _, out _, out _);
            var first = session.AddSpeedEffect(Ms(1_000), Ms(3_000));
            var trackCount = session.Project.Tracks.Count;

            var second = session.AddSpeedEffect(Ms(8_000), Ms(3_000));

            Assert.Equal(first.TrackId, second.TrackId);
            Assert.Equal(trackCount, session.Project.Tracks.Count);
        }

        [Fact]
        public void AddSpeedEffect_refuses_a_start_inside_an_existing_item()
        {
            var session = NewSession(out _, out _, out _);
            session.AddSpeedEffect(Ms(1_000), Ms(5_000));
            var json = session.Project.ToJson();

            Assert.Null(session.AddSpeedEffect(Ms(2_000), Ms(5_000)));
            Assert.Equal(json, session.Project.ToJson());
        }

        [Fact]
        public void AddSpeedEffect_clamps_the_duration_into_the_gap()
        {
            var session = NewSession(out _, out _, out _);
            session.AddSpeedEffect(Ms(6_000), Ms(5_000));

            var item = session.AddSpeedEffect(Ms(2_000), Ms(5_000));

            Assert.Equal(Ms(4_000), item.DurationTicks);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void AddSpeedEffect_refuses_a_gap_below_the_minimum_segment()
        {
            var session = NewSession(out _, out _, out _);
            session.AddSpeedEffect(0, Ms(5_000));
            session.AddSpeedEffect(Ms(5_050), Ms(5_000));

            // 50ms of free row between the two items — nothing can live there
            Assert.False(session.CanAddSpeedEffect(Ms(5_000)));
            Assert.Null(session.AddSpeedEffect(Ms(5_000), Ms(5_000)));
        }

        [Fact]
        public void AddSpeedEffect_backs_off_the_end_of_a_gap_too_short_to_grow_forward()
        {
            var session = NewSession(out _, out _, out _);
            session.AddSpeedEffect(Ms(1_050), Ms(5_000));

            // only 50ms in front of the playhead, but the gap behind it holds a grabbable item
            var item = session.AddSpeedEffect(Ms(1_000), Ms(5_000));

            Assert.NotNull(item);
            Assert.Equal(0, item.TimelineStartTicks);
            Assert.Equal(Ms(1_050), item.TimelineEndTicks);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void CanAddSpeedEffect_follows_the_playhead_not_the_row()
        {
            var session = NewSession(out _, out _, out _);
            Assert.True(session.CanAddSpeedEffect(Ms(1_000)));

            session.AddSpeedEffect(Ms(1_000), Ms(5_000));

            // the row exists now, but a second item is welcome anywhere it is not covered
            Assert.True(session.HasSpeedTrack);
            Assert.False(session.CanAddSpeedEffect(Ms(3_000)));
            Assert.True(session.CanAddSpeedEffect(Ms(8_000)));
            Assert.NotNull(session.AddSpeedEffect(Ms(8_000), Ms(5_000)));
        }

        [Fact]
        public void CanAddSpeedEffect_is_false_on_an_empty_project()
        {
            var session = new EditorSession(new Project(), null, save => save());

            Assert.False(session.CanAddSpeedEffect(0));
        }

        // ------------------------------------------------------------------------------- add zoom

        [Fact]
        public void AddZoomEffect_stacks_new_rows_above_the_video_block()
        {
            var session = NewSession(out var screenTrack, out var webcamTrack, out var audioTrack);

            var first = session.AddZoomEffect(Ms(1_000), Ms(5_000));
            var second = session.AddZoomEffect(Ms(2_000), Ms(5_000));

            Assert.NotEqual(first.TrackId, second.TrackId);
            var content = (ZoomContent)first.Content;
            Assert.Equal(1.5, content.Zoom);
            Assert.Equal(0.5, content.FocusX);
            Assert.Equal(0.5, content.FocusY);

            var p = session.Project;
            var firstTrack = TrackOf(session, first);
            var secondTrack = TrackOf(session, second);
            Assert.Equal(TrackKind.Effect, firstTrack.Kind);
            Assert.Equal("Zoom", firstTrack.Name);

            // each new zoom lands on top of the video block, audio stays above the block's orders.
            Assert.True(firstTrack.Order > p.Tracks.Single(t => t.Id == webcamTrack.Id).Order);
            Assert.True(secondTrack.Order > firstTrack.Order);
            Assert.True(p.Tracks.Single(t => t.Id == audioTrack.Id).Order > secondTrack.Order);
            Assert.Empty(p.Validate());
        }

        [Fact]
        public void Speed_row_stays_on_top_of_a_later_zoom_add()
        {
            var session = NewSession(out _, out _, out _);
            var speed = session.AddSpeedEffect(Ms(1_000), Ms(3_000));

            var zoom = session.AddZoomEffect(Ms(1_000), Ms(5_000));

            var speedOrder = TrackOf(session, speed).Order;
            Assert.Equal(session.Project.Tracks.Max(t => t.Order), speedOrder);
            Assert.True(speedOrder > TrackOf(session, zoom).Order);
        }

        // ---------------------------------------------------------------------------- undo / redo

        [Fact]
        public void Undo_and_redo_replay_an_effect_add()
        {
            var session = NewSession(out _, out _, out _);
            var before = session.Project.ToJson();
            session.AddSpeedEffect(Ms(1_000), Ms(5_000));
            var after = session.Project.ToJson();

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.HasSpeedTrack);

            session.Redo();
            Assert.Equal(after, session.Project.ToJson());
            Assert.True(session.HasSpeedTrack);
        }

        // -------------------------------------------------------------------------------- pruning

        [Fact]
        public void Deleting_the_last_speed_item_prunes_the_speed_row()
        {
            var session = NewSession(out _, out _, out _);
            var item = session.AddSpeedEffect(Ms(1_000), Ms(5_000));

            session.DeleteItem(item.Id);

            Assert.False(session.HasSpeedTrack);
            Assert.DoesNotContain(session.Project.Tracks, t => t.Kind == TrackKind.Effect);
        }

        [Fact]
        public void A_split_speed_row_survives_until_its_last_item_goes()
        {
            var session = NewSession(out _, out _, out _);
            var item = session.AddSpeedEffect(Ms(1_000), Ms(5_000));
            Assert.True(session.SplitAt(item.Id, Ms(3_000)));

            var halves = session.Project.Items.Where(i => i.Content is SpeedContent).ToList();
            Assert.Equal(2, halves.Count);

            session.DeleteItem(halves[0].Id);
            Assert.True(session.HasSpeedTrack);

            session.DeleteItem(halves[1].Id);
            Assert.False(session.HasSpeedTrack);
        }

        [Fact]
        public void Ripple_deleting_a_zoom_item_prunes_its_row()
        {
            var session = NewSession(out _, out _, out _);
            var item = session.AddZoomEffect(Ms(1_000), Ms(5_000));

            session.RippleDeleteItem(item.Id);

            Assert.DoesNotContain(session.Project.Tracks, t => t.Kind == TrackKind.Effect);
            Assert.Empty(session.Project.Validate());
        }

        // ------------------------------------------------------------------------------- pinning

        [Fact]
        public void The_speed_row_never_moves_or_duplicates()
        {
            var session = NewSession(out _, out _, out _);
            var item = session.AddSpeedEffect(Ms(1_000), Ms(5_000));
            var trackId = item.TrackId;
            var json = session.Project.ToJson();

            Assert.False(session.CanMoveTrackLayer(trackId, towardsFront: true));
            Assert.False(session.CanMoveTrackLayer(trackId, towardsFront: false));
            Assert.False(session.MoveTrackLayer(trackId, towardsFront: false));
            Assert.False(session.MoveTrackToIndex(trackId, 0));
            Assert.False(session.DuplicateTrack(trackId));
            Assert.Equal(json, session.Project.ToJson());
        }

        [Fact]
        public void A_zoom_row_reorders_within_the_video_block()
        {
            var session = NewSession(out var screenTrack, out var webcamTrack, out var audioTrack);
            session.AddSpeedEffect(Ms(1_000), Ms(3_000));
            var zoom = session.AddZoomEffect(Ms(1_000), Ms(5_000));
            var zoomTrackId = zoom.TrackId;

            // top of the block: can drop behind, cannot rise (the speed row is not passable).
            Assert.False(session.CanMoveTrackLayer(zoomTrackId, towardsFront: true));
            Assert.True(session.CanMoveTrackLayer(zoomTrackId, towardsFront: false));
            Assert.True(session.MoveTrackLayer(zoomTrackId, towardsFront: false));

            var p = session.Project;
            int OrderOf(Guid id) => p.Tracks.Single(t => t.Id == id).Order;
            Assert.Equal(0, OrderOf(screenTrack.Id));
            Assert.Equal(1, OrderOf(zoomTrackId));
            Assert.Equal(2, OrderOf(webcamTrack.Id));
            Assert.Equal(3, OrderOf(audioTrack.Id));
            Assert.Equal(4, p.Tracks.Single(t => t.Kind == TrackKind.Effect && t.Id != zoomTrackId).Order);
            Assert.Empty(p.Validate());
        }

        [Fact]
        public void MoveTrackToIndex_counts_the_block_without_the_speed_row()
        {
            var session = NewSession(out var screenTrack, out var webcamTrack, out _);
            session.AddSpeedEffect(Ms(1_000), Ms(3_000));
            var zoom = session.AddZoomEffect(Ms(1_000), Ms(5_000));

            // block is [screen, webcam, zoom] — index 3 would only exist if the speed row counted.
            Assert.False(session.MoveTrackToIndex(zoom.TrackId, 3));
            Assert.True(session.MoveTrackToIndex(zoom.TrackId, 0));

            var p = session.Project;
            int OrderOf(Guid id) => p.Tracks.Single(t => t.Id == id).Order;
            Assert.Equal(0, OrderOf(zoom.TrackId));
            Assert.Equal(1, OrderOf(screenTrack.Id));
            Assert.Equal(2, OrderOf(webcamTrack.Id));
        }

        [Fact]
        public void A_zoom_row_duplicates_like_any_other()
        {
            var session = NewSession(out _, out _, out _);
            var zoom = session.AddZoomEffect(Ms(1_000), Ms(5_000));

            Assert.True(session.DuplicateTrack(zoom.TrackId));

            var effectTracks = session.Project.Tracks.Where(t => t.Kind == TrackKind.Effect).ToList();
            Assert.Equal(2, effectTracks.Count);
            Assert.Equal(2, session.Project.Items.Count(i => i.Content is ZoomContent));
            Assert.Empty(session.Project.Validate());
        }

        // ------------------------------------------------------------------------------- setters

        [Fact]
        public void SetSpeedFactor_writes_clamps_and_coalesces()
        {
            var session = NewSession(out _, out _, out _);
            session.Clock = () => 0; // everything lands inside the coalesce window
            var item = session.AddSpeedEffect(Ms(1_000), Ms(5_000));

            Assert.Equal(3.0, session.SetSpeedFactor(item.Id, 3.0));
            Assert.Equal(10.0, session.SetSpeedFactor(item.Id, 50));
            Assert.Equal(10.0, ((SpeedContent)session.Project.Items.Single(i => i.Id == item.Id).Content).Factor);

            // both writes coalesced into one entry: a single undo lands back at the default.
            session.Undo();
            Assert.Equal(2.0, ((SpeedContent)session.Project.Items.Single(i => i.Id == item.Id).Content).Factor);
        }

        [Fact]
        public void SetSpeedFactor_ignores_non_speed_content()
        {
            var session = NewSession(out _, out _, out _);
            var clip = session.Project.Items.First(i => i.Content is SolidContent);
            var json = session.Project.ToJson();

            Assert.Equal(1.0, session.SetSpeedFactor(clip.Id, 4.0));
            Assert.Equal(json, session.Project.ToJson());
        }

        [Fact]
        public void Zoom_edits_ride_EditItem_and_coalesce()
        {
            var session = NewSession(out _, out _, out _);
            session.Clock = () => 0;
            var item = session.AddZoomEffect(Ms(1_000), Ms(5_000));
            var key = $"sel:zoom:{item.Id}";

            session.EditItem(item.Id, i => ((ZoomContent)i.Content).Zoom = 2.5, key);
            session.EditItem(item.Id, i => ((ZoomContent)i.Content).FocusX = 0.2, key);

            var content = (ZoomContent)session.Project.Items.Single(i => i.Id == item.Id).Content;
            Assert.Equal(2.5, content.Zoom);
            Assert.Equal(0.2, content.FocusX);

            session.Undo();
            content = (ZoomContent)session.Project.Items.Single(i => i.Id == item.Id).Content;
            Assert.Equal(1.5, content.Zoom);
            Assert.Equal(0.5, content.FocusX);
        }

        [Fact]
        public void An_out_of_range_zoom_edit_rolls_back()
        {
            var session = NewSession(out _, out _, out _);
            var item = session.AddZoomEffect(Ms(1_000), Ms(5_000));
            var failures = 0;
            session.ValidationFailed += (_, _) => failures++;

            session.EditItem(item.Id, i => ((ZoomContent)i.Content).Zoom = 9.0);

            Assert.Equal(1, failures);
            Assert.Equal(1.5, ((ZoomContent)session.Project.Items.Single(i => i.Id == item.Id).Content).Zoom);
        }

        [Fact]
        public void A_move_onto_another_speed_item_rolls_back()
        {
            var session = NewSession(out _, out _, out _);
            var first = session.AddSpeedEffect(Ms(1_000), Ms(3_000));
            var second = session.AddSpeedEffect(Ms(6_000), Ms(3_000));
            var failures = 0;
            session.ValidationFailed += (_, _) => failures++;

            Assert.Equal(0, session.MoveItem(second.Id, -Ms(3_000)));

            Assert.Equal(1, failures);
            Assert.Equal(Ms(6_000), session.Project.Items.Single(i => i.Id == second.Id).TimelineStartTicks);
        }

        // ------------------------------------------------------------------------------ snapshot

        [Fact]
        public void SnapshotForPlayer_round_trips_effect_content()
        {
            var session = NewSession(out _, out _, out _);
            var speed = session.AddSpeedEffect(Ms(1_000), Ms(3_000));
            var zoom = session.AddZoomEffect(Ms(2_000), Ms(4_000));
            session.SetSpeedFactor(speed.Id, 4.0);
            session.EditItem(zoom.Id, i => ((ZoomContent)i.Content).FocusX = 0.3, $"sel:zoom:{zoom.Id}");

            var snapshot = session.SnapshotForPlayer();

            var snapSpeed = (SpeedContent)snapshot.Items.Single(i => i.Id == speed.Id).Content;
            Assert.Equal(4.0, snapSpeed.Factor);
            var snapZoom = (ZoomContent)snapshot.Items.Single(i => i.Id == zoom.Id).Content;
            Assert.Equal(0.3, snapZoom.FocusX);

            // the snapshot is detached: writing it never touches the live model.
            snapSpeed.Factor = 0.5;
            Assert.Equal(4.0, ((SpeedContent)session.Project.Items.Single(i => i.Id == speed.Id).Content).Factor);
        }

        [Fact]
        public void Effect_items_do_not_extend_the_session_duration()
        {
            var session = NewSession(out _, out _, out _);
            var duration = session.DurationTicks;

            session.AddZoomEffect(duration + Ms(5_000), Ms(5_000));

            Assert.Equal(duration, session.DurationTicks);
        }
    }
}
