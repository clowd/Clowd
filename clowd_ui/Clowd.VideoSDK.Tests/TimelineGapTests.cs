using System;
using System.Linq;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // The timeline gap operations (TimelineOps.FindGap and friends), the freeze-frame media flag
    // they introduce, and the session wrappers the timeline's gap menu calls.
    public class TimelineGapTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>One recording — screen, webcam and audio rows grouped over [0, 10s) with a 2s
        /// source in-point — split at 4s and the right halves moved 3s later: every row has a gap
        /// over [4s, 7s), the left halves in the original group and the right halves in a new
        /// one.</summary>
        private static Project GappedProject(out Track screenTrack, out Track audioTrack,
            out Item screenLeft, out Item screenRight)
        {
            var sourceId = Guid.NewGuid();
            var group = Guid.NewGuid();
            screenTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            var webcamTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Webcam", Order = 1 };
            audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 2 };

            Item NewItem(Track track, int streamIndex) => new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(10_000),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = streamIndex, SourceInTicks = Ms(2_000) },
                GroupId = group,
            };

            var screen = NewItem(screenTrack, 0);
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources =
                {
                    new Source
                    {
                        Id = sourceId,
                        Path = @"C:\rec\input.mp4",
                        Streams =
                        {
                            new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                            new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 640, Height = 480, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                            new SourceStream { Index = 2, Kind = StreamKind.Audio, DurationTicks = Ms(60_000) },
                        },
                    },
                },
                Tracks = { screenTrack, webcamTrack, audioTrack },
                Items = { screen, NewItem(webcamTrack, 1), NewItem(audioTrack, 2) },
            };

            Assert.True(TimelineOps.Split(project, screen.Id, Ms(4_000)));
            var screenTrackId = screenTrack.Id;
            screenLeft = screen;
            screenRight = project.Items.Single(i => i.TrackId == screenTrackId && i.TimelineStartTicks == Ms(4_000));
            TimelineOps.Move(project, screenRight.Id, Ms(3_000));
            Assert.Empty(project.Validate());
            return project;
        }

        private static MediaContent Media(Item item) => (MediaContent)item.Content;

        private static Item AddItem(Project project, Track track, long start, long duration, ItemContent content)
        {
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = start,
                DurationTicks = duration,
                Content = content,
            };
            project.Items.Add(item);
            return item;
        }

        private static Track AddTrack(Project project, TrackKind kind)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = kind, Order = project.Tracks.Count };
            project.Tracks.Add(track);
            return track;
        }

        // ---- find ----

        [Fact]
        public void FindGap_spans_from_the_left_item_end_to_the_right_item_start()
        {
            var project = GappedProject(out var screenTrack, out _, out var left, out var right);

            var gap = TimelineOps.FindGap(project, screenTrack.Id, Ms(5_000));

            Assert.Equal(new TimelineGap(screenTrack.Id, Ms(4_000), Ms(7_000), left.Id, right.Id), gap);
        }

        [Fact]
        public void FindGap_is_null_on_a_clip_and_past_the_last_clip()
        {
            var project = GappedProject(out var screenTrack, out _, out _, out _);

            Assert.Null(TimelineOps.FindGap(project, screenTrack.Id, Ms(1_000)));
            Assert.Null(TimelineOps.FindGap(project, screenTrack.Id, Ms(20_000)));
        }

        [Fact]
        public void FindGap_finds_the_leading_gap_before_a_rows_first_clip()
        {
            var project = GappedProject(out _, out _, out _, out _);
            var text = AddTrack(project, TrackKind.Video);
            var card = AddItem(project, text, Ms(2_000), Ms(1_000), new TextContent { Text = "hi" });

            var gap = TimelineOps.FindGap(project, text.Id, Ms(500));

            Assert.Equal(new TimelineGap(text.Id, 0, Ms(2_000), null, card.Id), gap);
        }

        // ---- close ----

        [Fact]
        public void CloseGap_slides_the_right_clip_and_its_group_onto_the_gap_start()
        {
            var project = GappedProject(out var screenTrack, out _, out var left, out var right);
            var gap = TimelineOps.FindGap(project, screenTrack.Id, Ms(5_000)).Value;

            Assert.True(TimelineOps.CloseGap(project, gap));

            Assert.Equal(Ms(4_000), right.TimelineStartTicks);
            Assert.Equal(0, left.TimelineStartTicks);
            // the right halves on the other rows are its group, so they closed too
            Assert.All(project.Items.Where(i => i.GroupId == right.GroupId),
                i => Assert.Equal(Ms(4_000), i.TimelineStartTicks));
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void CloseGap_refuses_a_gap_the_project_no_longer_has()
        {
            var project = GappedProject(out var screenTrack, out _, out _, out var right);
            var gap = TimelineOps.FindGap(project, screenTrack.Id, Ms(5_000)).Value;
            TimelineOps.Move(project, right.Id, Ms(1_000));

            Assert.False(TimelineOps.CloseGap(project, gap));
            Assert.Equal(Ms(8_000), right.TimelineStartTicks);
        }

        [Fact]
        public void RippleCloseGap_cuts_the_span_out_of_every_row_and_shifts_what_follows()
        {
            var project = GappedProject(out var screenTrack, out _, out _, out var right);
            var textTrack = AddTrack(project, TrackKind.Video);
            var card = AddItem(project, textTrack, Ms(3_000), Ms(6_000), new TextContent { Text = "caption" });
            var gap = TimelineOps.FindGap(project, screenTrack.Id, Ms(5_000)).Value;

            Assert.True(TimelineOps.RippleCloseGap(project, gap));

            Assert.Equal(Ms(4_000), right.TimelineStartTicks);
            // the caption lost the gap's 3s: [3s, 4s) stays, [7s, 9s) became [4s, 6s)
            var pieces = project.Items.Where(i => i.TrackId == textTrack.Id).OrderBy(i => i.TimelineStartTicks).ToList();
            Assert.Equal(2, pieces.Count);
            Assert.Equal((Ms(3_000), Ms(1_000)), (pieces[0].TimelineStartTicks, pieces[0].DurationTicks));
            Assert.Equal((Ms(4_000), Ms(2_000)), (pieces[1].TimelineStartTicks, pieces[1].DurationTicks));
            Assert.Same(card, pieces[0]);
            Assert.Empty(project.Validate());
        }

        // ---- freeze ----

        [Fact]
        public void FillGapWithFreeze_from_the_left_holds_the_last_frame_the_left_clip_shows()
        {
            var project = GappedProject(out var screenTrack, out _, out var left, out _);
            left.Transform.Scale = 0.5;
            var gap = TimelineOps.FindGap(project, screenTrack.Id, Ms(5_000)).Value;

            var id = TimelineOps.FillGapWithFreeze(project, gap, fromLeft: true);

            var freeze = project.Items.Single(i => i.Id == id);
            Assert.Equal((Ms(4_000), Ms(3_000)), (freeze.TimelineStartTicks, freeze.DurationTicks));
            Assert.True(Media(freeze).Freeze);
            // the left half plays source [2s, 6s): its last frame is just short of 6s
            Assert.Equal(Ms(6_000) - 1, Media(freeze).SourceInTicks);
            Assert.Equal(0.5, freeze.Transform.Scale);
            Assert.Null(freeze.GroupId);
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void FillGapWithFreeze_from_the_right_holds_its_first_frame()
        {
            var project = GappedProject(out var screenTrack, out _, out _, out var right);
            var gap = TimelineOps.FindGap(project, screenTrack.Id, Ms(5_000)).Value;

            var id = TimelineOps.FillGapWithFreeze(project, gap, fromLeft: false);

            Assert.Equal(Media(right).SourceInTicks, Media(project.Items.Single(i => i.Id == id)).SourceInTicks);
        }

        [Fact]
        public void FillGapWithFreeze_is_refused_on_audio_rows_and_without_a_left_clip()
        {
            var project = GappedProject(out _, out var audioTrack, out _, out _);
            var audioGap = TimelineOps.FindGap(project, audioTrack.Id, Ms(5_000)).Value;
            var textTrack = AddTrack(project, TrackKind.Video);
            AddItem(project, textTrack, Ms(2_000), Ms(1_000), new TextContent { Text = "hi" });
            var leading = TimelineOps.FindGap(project, textTrack.Id, 0).Value;

            Assert.False(TimelineOps.CanFillGapWithFreeze(project, audioGap, fromLeft: true));
            Assert.False(TimelineOps.CanFillGapWithFreeze(project, leading, fromLeft: true));
            // the right neighbor is a text card, not media
            Assert.False(TimelineOps.CanFillGapWithFreeze(project, leading, fromLeft: false));
        }

        [Fact]
        public void A_freeze_frame_trims_and_splits_without_moving_its_frame()
        {
            var project = GappedProject(out var screenTrack, out _, out _, out var right);
            var gap = TimelineOps.FindGap(project, screenTrack.Id, Ms(5_000)).Value;
            var freezeId = TimelineOps.FillGapWithFreeze(project, gap, fromLeft: false);
            var freeze = project.Items.Single(i => i.Id == freezeId);
            var frame = Media(freeze).SourceInTicks;
            TimelineOps.Move(project, right.Id, Ms(100_000)); // room to grow past the source's end

            Assert.Equal(Ms(90_000), TimelineOps.TrimEnd(project, freeze.Id, Ms(90_000)));
            Assert.True(TimelineOps.SplitItem(project, freeze.Id, Ms(50_000)));
            Assert.Equal(1.0, TimelineOps.SetSpeed(project, freeze.Id, 2.0));

            Assert.All(project.Items.Where(i => i.TrackId == screenTrack.Id && Media(i).Freeze),
                i => Assert.Equal(frame, Media(i).SourceInTicks));
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void A_freeze_frame_composes_one_source_instant()
        {
            var item = new Item { TimelineStartTicks = Ms(1_000), DurationTicks = Ms(5_000) };
            var media = new MediaContent { SourceInTicks = Ms(3_000), Freeze = true, Speed = 2 };

            Assert.Equal(Ms(3_000), FrameComposer.SourceTimeTicks(media, item, Ms(1_000)));
            Assert.Equal(Ms(3_000), FrameComposer.SourceTimeTicks(media, item, Ms(4_500)));
        }

        [Fact]
        public void Validate_rejects_a_freeze_frame_on_an_audio_row()
        {
            var project = GappedProject(out _, out var audioTrack, out _, out _);
            var audio = project.Items.First(i => i.TrackId == audioTrack.Id);

            Media(audio).Freeze = true;

            Assert.Contains(project.Validate(), e => e.Contains("Freeze frame"));
        }

        [Fact]
        public void The_preview_map_holds_a_freeze_frame_and_hops_into_and_out_of_it()
        {
            var project = GappedProject(out var screenTrack, out _, out var left, out var right);
            var gap = TimelineOps.FindGap(project, screenTrack.Id, Ms(5_000)).Value;
            TimelineOps.FillGapWithFreeze(project, gap, fromLeft: true);

            var map = ProjectTimelineMap.Build(project);
            Assert.True(map.TryGetVideo((Media(left).SourceId, 0), out var stream));

            Assert.Equal(Ms(6_000) - 1, stream.TimelineToSource(Ms(4_000)));
            Assert.Equal(Ms(6_000) - 1, stream.TimelineToSource(Ms(6_500)));
            Assert.True(stream.IsFrozenAt(Ms(5_000)));
            Assert.False(stream.IsFrozenAt(Ms(3_000)));

            // entering and leaving the hold are both seams the player hops at
            Assert.NotEqual(stream.OffsetAtTimeline(Ms(3_000)), stream.OffsetAtTimeline(Ms(5_000)));
            Assert.NotEqual(stream.OffsetAtTimeline(Ms(5_000)), stream.OffsetAtTimeline(Ms(8_000)));
            Assert.NotEqual(long.MinValue, stream.OffsetAtTimeline(Ms(5_000)));

            // the halves still play contiguous source [2s, 6s) + [6s, ...), so the hold cuts nothing
            Assert.Empty(stream.SourceCuts.Ranges);
            // a source frame after the hold paces to the right clip, never into the hold
            Assert.Equal(Ms(7_000), stream.SourceToTimeline(Media(right).SourceInTicks));
        }

        // ---- rejoin ----

        [Fact]
        public void RejoinGap_heals_a_trimmed_cut_that_was_never_moved()
        {
            // cut at 4s, then 1s trimmed off each side of the cut: a [3s, 5s) gap on the row
            var project = GappedProject(out var screenTrack, out _, out var left, out var right);
            TimelineOps.Move(project, right.Id, -Ms(3_000));
            TimelineOps.TrimEnd(project, left.Id, -Ms(1_000));
            TimelineOps.TrimStart(project, right.Id, Ms(1_000));
            var gap = TimelineOps.FindGap(project, screenTrack.Id, Ms(4_000)).Value;

            Assert.True(TimelineOps.RejoinGap(project, gap));

            Assert.DoesNotContain(project.Items, i => i.Id == right.Id);
            Assert.Equal((0L, Ms(10_000)), (left.TimelineStartTicks, left.DurationTicks));
            Assert.Equal(Ms(2_000), Media(left).SourceInTicks);
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void RejoinGap_shifts_the_rest_of_its_row_to_close_up_a_moved_cut()
        {
            var project = GappedProject(out var screenTrack, out var audioTrack, out var left, out var right);
            var later = AddItem(project, screenTrack, Ms(15_000), Ms(1_000), new TextContent { Text = "end" });
            var audioRight = project.Items.Single(i => i.TrackId == audioTrack.Id && i.TimelineStartTicks == Ms(7_000));
            var gap = TimelineOps.FindGap(project, screenTrack.Id, Ms(5_000)).Value;

            Assert.True(TimelineOps.RejoinGap(project, gap));

            // source [2s, 12s) played straight through from 0, so the row's tail moves 3s earlier
            Assert.Equal(Ms(10_000), left.TimelineEndTicks);
            Assert.Equal(Ms(12_000), later.TimelineStartTicks);
            // other rows are not this row's business
            Assert.Equal(Ms(7_000), audioRight.TimelineStartTicks);
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void RejoinGap_shifts_the_row_later_when_the_gap_is_narrower_than_the_removed_material()
        {
            var project = GappedProject(out var screenTrack, out _, out var left, out var right);
            var later = AddItem(project, screenTrack, Ms(15_000), Ms(1_000), new TextContent { Text = "end" });
            // 2s of source cut out of the middle, but the clips only 1s apart (moved on their own:
            // the other rows' halves are not trimmed and would collide)
            TimelineOps.Ungroup(project, new[] { right.Id });
            TimelineOps.TrimStart(project, right.Id, Ms(2_000));
            TimelineOps.Move(project, right.Id, -Ms(4_000)); // right: [5s, 9s) playing source [8s, 12s)
            var gap = TimelineOps.FindGap(project, screenTrack.Id, Ms(4_500)).Value;

            Assert.True(TimelineOps.RejoinGap(project, gap));

            // source [2s, 12s) from 0 ends at 10s, 1s past where the right clip ended
            Assert.Equal(Ms(10_000), left.TimelineEndTicks);
            Assert.Equal(Ms(16_000), later.TimelineStartTicks);
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void RejoinGap_is_refused_for_clips_from_different_places()
        {
            var project = GappedProject(out var screenTrack, out _, out _, out var right);
            // the right clip now starts earlier in the source than the left one ends
            Media(right).SourceInTicks = Ms(3_000);
            var gap = TimelineOps.FindGap(project, screenTrack.Id, Ms(5_000)).Value;

            Assert.False(TimelineOps.CanRejoinGap(project, gap));
            Assert.False(TimelineOps.RejoinGap(project, gap));
        }

        // ---- extend ----

        [Fact]
        public void ExtendIntoGap_reveals_trimmed_source_on_either_side()
        {
            var project = GappedProject(out var screenTrack, out _, out var left, out var right);
            var gap = TimelineOps.FindGap(project, screenTrack.Id, Ms(5_000)).Value;
            var copy = Project.FromJson(project.ToJson());

            Assert.True(TimelineOps.ExtendIntoGap(project, gap, fromLeft: true));
            Assert.Equal(Ms(7_000), left.TimelineEndTicks);

            var copyRight = copy.Items.Single(i => i.Id == right.Id);
            Assert.True(TimelineOps.ExtendIntoGap(copy, gap, fromLeft: false));
            Assert.Equal(Ms(4_000), copyRight.TimelineStartTicks);
            Assert.Equal(Ms(3_000), Media(copyRight).SourceInTicks);
        }

        [Fact]
        public void ExtendIntoGap_is_all_or_nothing_when_the_source_runs_out()
        {
            var project = GappedProject(out var screenTrack, out _, out _, out var right);
            // the right half starts 1s into its source but the gap is 3s wide
            Media(right).SourceInTicks = Ms(1_000);
            var gap = TimelineOps.FindGap(project, screenTrack.Id, Ms(5_000)).Value;

            Assert.False(TimelineOps.CanExtendIntoGap(project, gap, fromLeft: false));
            Assert.False(TimelineOps.ExtendIntoGap(project, gap, fromLeft: false));
            Assert.Equal(Ms(7_000), right.TimelineStartTicks);
            Assert.Equal(Ms(1_000), Media(right).SourceInTicks);
        }

        // ---- session: copy/paste, dry runs, undo ----

        [Fact]
        public void Pasting_a_copied_clip_fills_the_gap_and_is_trimmed_to_fit()
        {
            var project = GappedProject(out var screenTrack, out var audioTrack, out var left, out _);
            var session = new EditorSession(project, null, null);
            var gap = session.FindGap(screenTrack.Id, Ms(5_000)).Value;
            var audioGap = session.FindGap(audioTrack.Id, Ms(5_000)).Value;

            Assert.True(session.CopyItem(left.Id));
            Assert.False(session.CanPasteIntoGap(audioGap)); // a picture never lands on an audio row
            var id = session.PasteIntoGap(gap);

            var pasted = session.Project.Items.Single(i => i.Id == id);
            Assert.Equal((Ms(4_000), Ms(3_000)), (pasted.TimelineStartTicks, pasted.DurationTicks));
            Assert.Equal(Ms(2_000), Media(pasted).SourceInTicks);
            Assert.Null(pasted.GroupId);

            session.Undo();
            Assert.DoesNotContain(session.Project.Items, i => i.Id == id);
        }

        [Fact]
        public void Pasting_media_into_another_project_brings_its_source_along()
        {
            var project = GappedProject(out _, out _, out var left, out _);
            var session = new EditorSession(project, null, null);
            Assert.True(session.CopyItem(left.Id));

            var other = new Project
            {
                Output = new OutputSettings { WidthPx = 1280, HeightPx = 720, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
            };
            var video = AddTrack(other, TrackKind.Video);
            AddItem(other, video, Ms(10_000), Ms(1_000), new TextContent { Text = "end" });
            var otherSession = new EditorSession(other, null, null);
            var gap = otherSession.FindGap(video.Id, 0).Value;

            Assert.NotNull(otherSession.PasteIntoGap(gap));
            Assert.Contains(otherSession.Project.Sources, s => s.Id == Media(left).SourceId);
            Assert.Empty(otherSession.Project.Validate());
        }

        [Fact]
        public void CanCloseGap_is_false_when_a_group_member_would_collide()
        {
            var project = GappedProject(out var screenTrack, out var audioTrack, out _, out _);
            // something else now sits in the audio row's gap: sliding the right half's audio
            // back would run into it
            AddItem(project, audioTrack, Ms(5_000), Ms(1_000), new MediaContent
            {
                SourceId = project.Sources[0].Id,
                StreamIndex = 2,
            });
            var session = new EditorSession(project, null, null);
            var gap = session.FindGap(screenTrack.Id, Ms(5_000)).Value;

            Assert.False(session.CanCloseGap(gap));
            Assert.False(session.CloseGap(gap));
            Assert.True(session.RippleCloseGap(gap));
            Assert.Empty(session.Project.Validate());
        }
    }
}
