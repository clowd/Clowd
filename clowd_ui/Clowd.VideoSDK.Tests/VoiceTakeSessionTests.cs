using System;
using System.Linq;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="EditorSession"/>'s voice take surface: where a take's row comes from (a new
    /// "Voice" row, the last take's row, a fresh row when that one is now occupied), the ghost
    /// the timeline draws and how it grows under a warp, the single undo entry a take leaves,
    /// and what a cancel takes back. Pure model math over a sources-free fixture plus a fake
    /// probe standing in for the recorded wav.
    /// </summary>
    public class VoiceTakeSessionTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        private static Project BaseProject()
        {
            var video = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            return new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Tracks = { video },
                Items =
                {
                    new Item
                    {
                        Id = Guid.NewGuid(),
                        TrackId = video.Id,
                        TimelineStartTicks = 0,
                        DurationTicks = Ms(10_000),
                        Content = new SolidContent { Color = "#FF000000" },
                    },
                },
            };
        }

        private static EditorSession NewSession() => new EditorSession(BaseProject(), null, save => save());

        private static MediaProbeResult WavProbe(long durationTicks) => new MediaProbeResult
        {
            Path = TestPath.Native(@"C:\rec\voice.wav"),
            DurationTicks = durationTicks,
            VideoStreams = Array.Empty<VideoStreamProbe>(),
            AudioStreams = new[]
            {
                new AudioStreamProbe { StreamIndex = 0, SampleRate = 48_000, Channels = 1, DurationTicks = durationTicks },
            },
            HasAudio = true,
        };

        private static Item Record(EditorSession session, long startTicks, long lengthTicks, string wav = null)
        {
            var take = session.BeginVoiceTake(startTicks);
            session.UpdateVoiceTake(take, lengthTicks);
            return session.FinishVoiceTake(take, wav ?? TestPath.Native(@"C:\rec\voice.wav"), WavProbe(lengthTicks), lengthTicks);
        }

        private static Track TrackOf(EditorSession session, Item item) =>
            session.Project.Tracks.Single(t => t.Id == item.TrackId);

        // -------------------------------------------------------------------------------- begin

        [Fact]
        public void First_take_creates_a_voice_row_and_shows_the_ghost_on_it()
        {
            var session = NewSession();
            var ghostChanges = 0;
            session.VoiceTakeGhostChanged += (_, _) => ghostChanges++;
            ProjectChangeKind? kind = null;
            session.ProjectChanged += (_, e) => kind = e.Kind;

            Assert.Null(session.VoiceTakeGhost);
            var take = session.BeginVoiceTake(Ms(3_000));

            Assert.True(session.IsVoiceTakeActive);
            var voice = session.Project.Tracks.Single(t => t.Name == EditorSession.VoiceTrackName);
            Assert.Equal(TrackKind.Audio, voice.Kind);
            Assert.Equal(ProjectChangeKind.Structural, kind);
            Assert.Equal(1, ghostChanges);

            var ghost = session.VoiceTakeGhost;
            Assert.NotNull(ghost);
            Assert.Equal(voice.Id, ghost.TrackId);
            Assert.Equal(Ms(3_000), ghost.StartTicks);
            Assert.Equal(0, ghost.DurationTicks);
            Assert.Empty(session.Project.Validate());

            // no clip yet: the row is the only thing in the model so far
            Assert.DoesNotContain(session.Project.Items, i => i.TrackId == voice.Id);
            session.CancelVoiceTake(take);
        }

        [Fact]
        public void A_second_take_cannot_start_while_one_is_open()
        {
            var session = NewSession();
            session.BeginVoiceTake(0);
            Assert.Throws<InvalidOperationException>(() => session.BeginVoiceTake(Ms(1_000)));
        }

        // ------------------------------------------------------------------------------- update

        [Fact]
        public void Ghost_grows_by_project_time_the_elapsed_real_time_covers()
        {
            var session = NewSession();
            session.AddSpeedEffect(0, Ms(10_000)); // 2x over the whole project
            var take = session.BeginVoiceTake(Ms(2_000));

            // start is output 1s; one real second later is output 2s = project 4s
            session.UpdateVoiceTake(take, Ms(1_000));
            Assert.Equal(Ms(2_000), session.VoiceTakeGhost.DurationTicks);

            // past the 2x span the clock runs 1:1 again: output 7s = project 10s + 2s
            session.UpdateVoiceTake(take, Ms(6_000));
            Assert.Equal(Ms(10_000), session.VoiceTakeGhost.DurationTicks);
            Assert.Equal(Ms(12_000), session.VoiceTakeGhost.EndTicks);
            session.CancelVoiceTake(take);
        }

        [Fact]
        public void Ghost_raises_only_when_it_changes()
        {
            var session = NewSession();
            var take = session.BeginVoiceTake(0);
            var ghostChanges = 0;
            session.VoiceTakeGhostChanged += (_, _) => ghostChanges++;

            session.UpdateVoiceTake(take, Ms(500));
            session.UpdateVoiceTake(take, Ms(500));
            Assert.Equal(1, ghostChanges);
            session.CancelVoiceTake(take);
        }

        // ------------------------------------------------------------------------------- finish

        [Fact]
        public void Finish_adds_an_exempt_clip_on_the_voice_row_as_one_undo_entry()
        {
            var session = NewSession();
            var item = Record(session, Ms(3_000), Ms(2_000));

            Assert.NotNull(item);
            Assert.False(session.IsVoiceTakeActive);
            Assert.Null(session.VoiceTakeGhost);

            var media = Assert.IsType<MediaContent>(item.Content);
            Assert.True(media.SpeedWarpExempt);
            Assert.Equal(0, media.SourceInTicks);
            Assert.Equal(0, media.StreamIndex);
            Assert.Equal(Ms(3_000), item.TimelineStartTicks);
            Assert.Equal(Ms(2_000), item.DurationTicks);

            var source = session.Project.Sources.Single(s => s.Id == media.SourceId);
            Assert.Equal(TestPath.Native(@"C:\rec\voice.wav"), source.Path);
            Assert.Single(source.Streams);

            var track = TrackOf(session, item);
            Assert.Equal(EditorSession.VoiceTrackName, track.Name);
            Assert.Equal(TrackKind.Audio, track.Kind);
            Assert.Equal(item.Id, session.PrimarySelectedItem?.Id);
            Assert.Empty(session.Project.Validate());

            // one step back takes the row and the clip together, and there is nothing behind it
            Assert.True(session.CanUndo);
            session.Undo();
            Assert.DoesNotContain(session.Project.Tracks, t => t.Name == EditorSession.VoiceTrackName);
            Assert.DoesNotContain(session.Project.Items, i => i.Content is MediaContent);
            Assert.Empty(session.Project.Sources);
            Assert.False(session.CanUndo);

            session.Redo();
            Assert.Contains(session.Project.Items, i => i.Id == item.Id);
        }

        [Fact]
        public void Next_take_reuses_the_last_row()
        {
            var session = NewSession();
            var first = Record(session, 0, Ms(2_000));
            var second = Record(session, Ms(5_000), Ms(2_000));

            Assert.Equal(first.TrackId, second.TrackId);
            Assert.Single(session.Project.Tracks, t => t.Kind == TrackKind.Audio);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_take_over_an_existing_clip_gets_a_new_row()
        {
            var session = NewSession();
            var first = Record(session, 0, Ms(4_000));

            // starts inside the first clip: decided at begin, on a fresh row
            var second = Record(session, Ms(2_000), Ms(1_000));
            Assert.NotEqual(first.TrackId, second.TrackId);
            Assert.Equal(EditorSession.VoiceTrackName, TrackOf(session, second).Name);
            Assert.Equal(2, session.Project.Tracks.Count(t => t.Kind == TrackKind.Audio));
            Assert.Equal(Ms(4_000), session.Project.Items.Single(i => i.Id == first.Id).DurationTicks);

            // the second row is now the last-recorded-to row
            var third = Record(session, Ms(6_000), Ms(1_000));
            Assert.Equal(second.TrackId, third.TrackId);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_take_that_grows_into_a_later_clip_gets_a_new_row_at_finish()
        {
            var session = NewSession();
            var first = Record(session, Ms(5_000), Ms(2_000));

            // begins in free space on the row, but by the time it stops it reaches into the
            // first clip: it lands on a new row rather than over it
            var second = Record(session, Ms(2_000), Ms(4_000));
            Assert.NotEqual(first.TrackId, second.TrackId);
            Assert.Equal(Ms(2_000), second.TimelineStartTicks);
            Assert.Equal(Ms(4_000), second.DurationTicks);
            Assert.Equal(Ms(5_000), session.Project.Items.Single(i => i.Id == first.Id).TimelineStartTicks);
            Assert.Empty(session.Project.Validate());

            // and that new row is the one remembered
            var third = Record(session, Ms(8_000), Ms(1_000));
            Assert.Equal(second.TrackId, third.TrackId);
        }

        [Fact]
        public void Take_past_the_project_end_extends_it()
        {
            var session = NewSession();
            Assert.Equal(Ms(10_000), session.DurationTicks);

            var item = Record(session, Ms(8_000), Ms(5_000));
            Assert.Equal(Ms(13_000), item.TimelineEndTicks);
            Assert.Equal(Ms(13_000), session.DurationTicks);
        }

        [Fact]
        public void Take_length_is_laid_onto_the_project_through_the_warp()
        {
            var session = NewSession();
            session.AddSpeedEffect(0, Ms(10_000)); // 2x

            // 2s of real time from project 2s (output 1s) reaches output 3s = project 6s
            var item = Record(session, Ms(2_000), Ms(2_000));
            Assert.Equal(Ms(4_000), item.DurationTicks);
            Assert.True(((MediaContent)item.Content).SpeedWarpExempt);

            // and it re-fits like any exempt clip once the speed item goes
            session.DeleteItem(session.Project.Items.Single(i => i.Content is SpeedContent).Id);
            Assert.Equal(Ms(2_000), session.Project.Items.Single(i => i.Id == item.Id).DurationTicks);
        }

        [Fact]
        public void A_take_too_short_to_grab_is_padded_to_the_minimum_segment()
        {
            var session = NewSession();
            var item = Record(session, 0, Ms(10));
            Assert.Equal(TimelineOps.MinSegmentTicks, item.DurationTicks);
        }

        [Fact]
        public void Finish_without_an_audio_stream_ends_the_take_with_nothing_added()
        {
            var session = NewSession();
            var take = session.BeginVoiceTake(0);
            var probe = new MediaProbeResult
            {
                Path = TestPath.Native(@"C:\rec\voice.wav"),
                VideoStreams = Array.Empty<VideoStreamProbe>(),
                AudioStreams = Array.Empty<AudioStreamProbe>(),
            };

            Assert.Null(session.FinishVoiceTake(take, probe.Path, probe, Ms(1_000)));
            Assert.False(session.IsVoiceTakeActive);
            Assert.Null(session.VoiceTakeGhost);
            Assert.DoesNotContain(session.Project.Tracks, t => t.Name == EditorSession.VoiceTrackName);
            Assert.False(session.CanUndo);
        }

        // ------------------------------------------------------------------------------- cancel

        [Fact]
        public void Cancel_takes_the_new_row_and_its_undo_entry_back()
        {
            var session = NewSession();
            var before = session.Project.ToJson();
            var take = session.BeginVoiceTake(Ms(1_000));
            Assert.True(session.CanUndo);

            session.CancelVoiceTake(take);
            Assert.False(session.IsVoiceTakeActive);
            Assert.Null(session.VoiceTakeGhost);
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void Cancel_puts_back_the_redo_history_the_new_row_displaced()
        {
            var session = NewSession();
            var solid = session.Project.Items.Single();
            session.MoveItem(solid.Id, Ms(1_000));
            session.Undo();
            Assert.True(session.CanRedo);
            Assert.False(session.CanUndo);

            // making the row is a mutation, so redo is gone for as long as the take is open
            var take = session.BeginVoiceTake(Ms(1_000));
            Assert.False(session.CanRedo);

            session.CancelVoiceTake(take);
            Assert.False(session.CanUndo);
            Assert.True(session.CanRedo);
            session.Redo();
            Assert.Equal(Ms(1_000), session.Project.Items.Single(i => i.Id == solid.Id).TimelineStartTicks);
        }

        [Fact]
        public void A_finished_take_clears_redo_like_any_edit()
        {
            var session = NewSession();
            var solid = session.Project.Items.Single();
            session.MoveItem(solid.Id, Ms(1_000));
            session.Undo();

            Record(session, 0, Ms(1_000));
            Assert.False(session.CanRedo);
        }

        [Fact]
        public void Finish_is_refused_while_a_gesture_is_open_and_the_take_stays()
        {
            var session = NewSession();
            var take = session.BeginVoiceTake(Ms(1_000));
            session.UpdateVoiceTake(take, Ms(2_000));
            var solid = session.Project.Items.Single();

            using (var gesture = session.BeginGesture("Move"))
            {
                session.MoveItem(solid.Id, Ms(500));
                Assert.Throws<InvalidOperationException>(() =>
                    session.FinishVoiceTake(take, TestPath.Native(@"C:\rec\voice.wav"), WavProbe(Ms(2_000)), Ms(2_000)));
                Assert.True(session.IsVoiceTakeActive);
                Assert.NotNull(session.VoiceTakeGhost);
                Assert.DoesNotContain(session.Project.Items, i => i.Content is MediaContent);
                gesture.Commit();
            }

            // once the drag is over the same finish goes through as its own undo entry
            var item = session.FinishVoiceTake(take, TestPath.Native(@"C:\rec\voice.wav"), WavProbe(Ms(2_000)), Ms(2_000));
            Assert.NotNull(item);
            Assert.Equal(Ms(2_000), item.DurationTicks);
            Assert.False(session.IsVoiceTakeActive);

            session.Undo();
            Assert.DoesNotContain(session.Project.Items, i => i.Id == item.Id);
            Assert.Equal(Ms(500), session.Project.Items.Single(i => i.Id == solid.Id).TimelineStartTicks);
        }

        [Fact]
        public void Cancel_under_a_gesture_takes_the_row_out_when_the_gesture_ends()
        {
            var session = NewSession();
            var before = session.Project.ToJson();
            var take = session.BeginVoiceTake(Ms(1_000));
            var voiceTrackId = session.VoiceTakeGhost.TrackId;

            using (var gesture = session.BeginGesture("Move"))
            {
                session.CancelVoiceTake(take);
                Assert.False(session.IsVoiceTakeActive);
                Assert.Null(session.VoiceTakeGhost);
                // the row waits: taking it out inside the drag would fold into the drag
                Assert.Contains(session.Project.Tracks, t => t.Id == voiceTrackId);
                gesture.Cancel();
            }

            Assert.DoesNotContain(session.Project.Tracks, t => t.Id == voiceTrackId);
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void Cancel_under_a_committed_gesture_leaves_an_undoable_row_delete()
        {
            var session = NewSession();
            var take = session.BeginVoiceTake(Ms(1_000));
            var voiceTrackId = session.VoiceTakeGhost.TrackId;
            var solid = session.Project.Items.Single();

            using (var gesture = session.BeginGesture("Move"))
            {
                session.MoveItem(solid.Id, Ms(500));
                session.CancelVoiceTake(take);
                gesture.Commit();
            }

            // the drag's own entry sits between the row's creation and its removal, so the
            // removal is an ordinary delete: one undo brings the empty row back, not the drag
            Assert.DoesNotContain(session.Project.Tracks, t => t.Id == voiceTrackId);
            session.Undo();
            Assert.Contains(session.Project.Tracks, t => t.Id == voiceTrackId);
            Assert.Equal(Ms(500), session.Project.Items.Single(i => i.Id == solid.Id).TimelineStartTicks);
        }

        [Fact]
        public void Cancel_on_a_reused_row_leaves_it_alone()
        {
            var session = NewSession();
            var first = Record(session, 0, Ms(2_000));
            var before = session.Project.ToJson();

            var take = session.BeginVoiceTake(Ms(5_000));
            Assert.Equal(first.TrackId, session.VoiceTakeGhost.TrackId);
            session.CancelVoiceTake(take);

            Assert.Equal(before, session.Project.ToJson());
            Assert.Contains(session.Project.Tracks, t => t.Id == first.TrackId);
        }

        [Fact]
        public void Cancel_of_an_unknown_take_throws()
        {
            var session = NewSession();
            Assert.Throws<InvalidOperationException>(() => session.CancelVoiceTake(Guid.NewGuid()));
        }
    }
}
