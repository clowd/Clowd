using System;
using System.Linq;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    public class TimelineOpsTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>The Phase-1 import shape: one recording, three streams, three tracks, three
        /// linked items covering [0, 10s) with a 2s source in-point on each.</summary>
        private static Project RecordingProject(out Item screen, out Item webcam, out Item audio)
        {
            var sourceId = Guid.NewGuid();
            var linkGroup = Guid.NewGuid();
            var screenTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            var webcamTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Webcam", Order = 1 };
            var audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 2 };

            Item NewItem(Track track, int streamIndex) => new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(10_000),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = streamIndex, SourceInTicks = Ms(2_000) },
                LinkGroupId = linkGroup,
            };

            screen = NewItem(screenTrack, 0);
            webcam = NewItem(webcamTrack, 1);
            audio = NewItem(audioTrack, 2);

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
                Items = { screen, webcam, audio },
            };

            return project;
        }

        private static long SourceIn(Item item) => ((MediaContent)item.Content).SourceInTicks;

        // ---- move ----

        [Fact]
        public void Move_applies_to_every_link_member()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);

            var applied = TimelineOps.Move(project, screen.Id, Ms(3_000));

            Assert.Equal(Ms(3_000), applied);
            Assert.All(new[] { screen, webcam, audio }, i => Assert.Equal(Ms(3_000), i.TimelineStartTicks));
            // moving never touches duration or the media in-point.
            Assert.All(new[] { screen, webcam, audio }, i => Assert.Equal(Ms(10_000), i.DurationTicks));
            Assert.All(new[] { screen, webcam, audio }, i => Assert.Equal(Ms(2_000), SourceIn(i)));
        }

        [Fact]
        public void Move_clamps_at_the_timeline_origin()
        {
            var project = RecordingProject(out var screen, out var webcam, out _);
            TimelineOps.Move(project, screen.Id, Ms(1_000));

            var applied = TimelineOps.Move(project, webcam.Id, -Ms(5_000));

            Assert.Equal(-Ms(1_000), applied);
            Assert.Equal(0, screen.TimelineStartTicks);
            Assert.Equal(0, webcam.TimelineStartTicks);
        }

        [Fact]
        public void Move_on_an_unlinked_item_moves_only_that_item()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);
            TimelineOps.Unlink(project, new[] { webcam.Id });

            TimelineOps.Move(project, webcam.Id, Ms(1_500));

            Assert.Equal(Ms(1_500), webcam.TimelineStartTicks);
            Assert.Equal(0, screen.TimelineStartTicks);
            Assert.Equal(0, audio.TimelineStartTicks);
        }

        [Fact]
        public void Ops_throw_on_an_unknown_item()
        {
            var project = RecordingProject(out _, out _, out _);
            Assert.Throws<ArgumentException>(() => TimelineOps.Move(project, Guid.NewGuid(), Ms(1)));
            Assert.Throws<ArgumentException>(() => TimelineOps.TrimStart(project, Guid.NewGuid(), Ms(1)));
            Assert.Throws<ArgumentException>(() => TimelineOps.TrimEnd(project, Guid.NewGuid(), Ms(1)));
        }

        // ---- trim ----

        [Fact]
        public void TrimStart_shrinks_only_the_target_and_advances_its_media_in_point()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);

            var applied = TimelineOps.TrimStart(project, webcam.Id, Ms(1_000));

            Assert.Equal(Ms(1_000), applied);
            Assert.Equal(Ms(1_000), webcam.TimelineStartTicks);
            Assert.Equal(Ms(9_000), webcam.DurationTicks);
            // the same source frame stays under the new in-point — which is exactly why the rest
            // of the link group needs no adjustment to stay in sync.
            Assert.Equal(Ms(3_000), SourceIn(webcam));

            foreach (var item in new[] { screen, audio })
            {
                Assert.Equal(0, item.TimelineStartTicks);
                Assert.Equal(Ms(10_000), item.DurationTicks);
                Assert.Equal(Ms(2_000), SourceIn(item));
            }
        }

        [Fact]
        public void TrimStart_clamps_to_the_minimum_segment_of_the_target_alone()
        {
            var project = RecordingProject(out var screen, out var webcam, out _);
            // a shorter linked member no longer governs the clamp — trim is per item.
            webcam.DurationTicks = Ms(2_000);

            var applied = TimelineOps.TrimStart(project, screen.Id, Ms(15_000));

            Assert.Equal(Ms(10_000) - TimelineOps.MinSegmentTicks, applied);
            Assert.Equal(TimelineOps.MinSegmentTicks, screen.DurationTicks);
            Assert.Equal(Ms(2_000), webcam.DurationTicks);
        }

        [Fact]
        public void TrimStart_extension_is_clamped_by_the_source_in_point()
        {
            var project = RecordingProject(out var screen, out _, out _);
            TimelineOps.Move(project, screen.Id, Ms(5_000));

            // 5s of timeline room, but only 2s of source before the in-point.
            var applied = TimelineOps.TrimStart(project, screen.Id, -Ms(4_000));

            Assert.Equal(-Ms(2_000), applied);
            Assert.Equal(Ms(3_000), screen.TimelineStartTicks);
            Assert.Equal(Ms(12_000), screen.DurationTicks);
            Assert.Equal(0, SourceIn(screen));
        }

        [Fact]
        public void TrimStart_extension_is_clamped_by_the_timeline_origin()
        {
            var project = RecordingProject(out var screen, out _, out _);
            TimelineOps.Move(project, screen.Id, Ms(1_000));

            // 2s of source available, but only 1s of timeline before the item.
            var applied = TimelineOps.TrimStart(project, screen.Id, -Ms(4_000));

            Assert.Equal(-Ms(1_000), applied);
            Assert.Equal(0, screen.TimelineStartTicks);
            Assert.Equal(Ms(1_000), SourceIn(screen));
        }

        [Fact]
        public void TrimEnd_shortens_only_the_target_and_clamps_to_the_minimum_segment()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);

            var applied = TimelineOps.TrimEnd(project, screen.Id, -Ms(20_000));

            Assert.Equal(TimelineOps.MinSegmentTicks - Ms(10_000), applied);
            Assert.Equal(TimelineOps.MinSegmentTicks, screen.DurationTicks);
            // trimming the end never moves the start or the in-point.
            Assert.Equal(0, screen.TimelineStartTicks);
            Assert.Equal(Ms(2_000), SourceIn(screen));
            Assert.All(new[] { webcam, audio }, i => Assert.Equal(Ms(10_000), i.DurationTicks));
        }

        [Fact]
        public void TrimEnd_extends_only_the_target()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);

            var applied = TimelineOps.TrimEnd(project, audio.Id, Ms(2_000));

            Assert.Equal(Ms(2_000), applied);
            Assert.Equal(Ms(12_000), audio.DurationTicks);
            Assert.All(new[] { screen, webcam }, i => Assert.Equal(Ms(10_000), i.DurationTicks));
        }

        // ---- split ----

        [Fact]
        public void Split_produces_back_to_back_items_with_correct_source_in()
        {
            var project = RecordingProject(out var screen, out _, out _);
            TimelineOps.Unlink(project, project.Items.Select(i => i.Id).ToArray());

            Assert.True(TimelineOps.Split(project, screen.Id, Ms(4_000)));

            var halves = project.Items.Where(i => i.TrackId == screen.TrackId)
                                      .OrderBy(i => i.TimelineStartTicks).ToList();
            Assert.Equal(2, halves.Count);

            Assert.Same(screen, halves[0]);
            Assert.Equal(0, halves[0].TimelineStartTicks);
            Assert.Equal(Ms(4_000), halves[0].DurationTicks);
            Assert.Equal(Ms(2_000), SourceIn(halves[0]));

            Assert.Equal(Ms(4_000), halves[1].TimelineStartTicks);
            Assert.Equal(Ms(6_000), halves[1].DurationTicks);
            // right half resumes exactly where the left half's source span ends.
            Assert.Equal(Ms(6_000), SourceIn(halves[1]));
            Assert.Equal(halves[0].TimelineEndTicks, halves[1].TimelineStartTicks);
        }

        [Fact]
        public void Split_applies_to_every_covered_member_and_relinks_the_right_halves()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);
            var originalGroup = screen.LinkGroupId;

            Assert.True(TimelineOps.Split(project, screen.Id, Ms(4_000)));

            Assert.Equal(6, project.Items.Count);

            var lefts = new[] { screen, webcam, audio };
            var rights = project.Items.Except(lefts).ToList();
            Assert.Equal(3, rights.Count);

            // left halves keep their group; right halves form a new one of their own.
            Assert.All(lefts, i => Assert.Equal(originalGroup, i.LinkGroupId));
            var rightGroup = rights[0].LinkGroupId;
            Assert.NotNull(rightGroup);
            Assert.NotEqual(originalGroup, rightGroup);
            Assert.All(rights, i => Assert.Equal(rightGroup, i.LinkGroupId));

            Assert.All(rights, i => Assert.Equal(Ms(4_000), i.TimelineStartTicks));
            Assert.All(rights, i => Assert.Equal(Ms(6_000), i.DurationTicks));
            Assert.All(rights, i => Assert.Equal(Ms(6_000), SourceIn(i)));

            // ...and the two new pairs edit independently of each other.
            TimelineOps.Move(project, rights[0].Id, Ms(1_000));
            Assert.All(rights, i => Assert.Equal(Ms(5_000), i.TimelineStartTicks));
            Assert.All(lefts, i => Assert.Equal(0, i.TimelineStartTicks));
        }

        /// <summary>The right-click split: cutting the screen row must leave the webcam and audio
        /// rows of the same recording untouched — the exact complaint that motivated it.</summary>
        [Fact]
        public void SplitItem_cuts_only_the_item_it_is_given()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);
            var originalGroup = screen.LinkGroupId;

            Assert.True(TimelineOps.SplitItem(project, screen.Id, Ms(4_000)));

            Assert.Equal(4, project.Items.Count); // one new segment, not three
            Assert.Equal(Ms(10_000), webcam.DurationTicks);
            Assert.Equal(Ms(10_000), audio.DurationTicks);

            var right = project.Items.Single(i => i.Id != screen.Id && i.TrackId == screen.TrackId);
            Assert.Equal(Ms(4_000), screen.DurationTicks);
            Assert.Equal(Ms(4_000), right.TimelineStartTicks);
            Assert.Equal(Ms(6_000), right.DurationTicks);
            Assert.Equal(Ms(6_000), SourceIn(right));

            // both halves stay in the recording: the clip was cut, not unlinked.
            Assert.Equal(originalGroup, right.LinkGroupId);
            Assert.Equal(originalGroup, screen.LinkGroupId);
        }

        /// <summary>A later group split still behaves, because it only ever acts on the members
        /// that cover the instant — the half that does not is simply left alone.</summary>
        [Fact]
        public void SplitItem_leaves_the_group_splittable_afterwards()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);
            Assert.True(TimelineOps.SplitItem(project, screen.Id, Ms(4_000)));

            Assert.True(TimelineOps.Split(project, webcam.Id, Ms(6_000)));

            Assert.Equal(Ms(4_000), screen.DurationTicks);   // the untouched left half stays put
            Assert.Equal(Ms(6_000), webcam.DurationTicks);
            Assert.Equal(Ms(6_000), audio.DurationTicks);

            // the screen's right half covered 6s too, so it was cut as a group member
            var screenSegments = project.Items.Where(i => i.TrackId == screen.TrackId)
                                              .OrderBy(i => i.TimelineStartTicks).ToList();
            Assert.Equal(3, screenSegments.Count);
            Assert.Equal(new[] { 0, Ms(4_000), Ms(6_000) },
                screenSegments.Select(i => i.TimelineStartTicks).ToArray());
        }

        [Fact]
        public void SplitItem_refuses_a_cut_that_is_off_the_item_or_too_short()
        {
            var project = RecordingProject(out var screen, out _, out _);

            Assert.False(TimelineOps.SplitItem(project, screen.Id, 0));
            Assert.False(TimelineOps.SplitItem(project, screen.Id, Ms(10_000)));
            Assert.False(TimelineOps.SplitItem(project, screen.Id, TimelineOps.MinSegmentTicks - 1));
            Assert.False(TimelineOps.SplitItem(project, screen.Id, Ms(10_000) - 1));
            Assert.Equal(3, project.Items.Count);
        }

        [Fact]
        public void Split_keeps_entry_on_the_left_and_moves_exit_to_the_right()
        {
            var project = RecordingProject(out var screen, out _, out _);
            var entry = new Transition { Kind = TransitionKind.Fade, DurationTicks = Ms(300) };
            var exit = new Transition { Kind = TransitionKind.SlideRight, DurationTicks = Ms(300) };
            screen.Entry = entry;
            screen.Exit = exit;

            Assert.True(TimelineOps.Split(project, screen.Id, Ms(4_000)));

            var right = project.Items.Single(i => i.TrackId == screen.TrackId && i.Id != screen.Id);
            Assert.Same(entry, screen.Entry);
            Assert.Null(screen.Exit);
            Assert.Null(right.Entry);
            Assert.Same(exit, right.Exit);
        }

        [Fact]
        public void Split_clones_the_transform_instead_of_sharing_it()
        {
            var project = RecordingProject(out _, out var webcam, out _);
            webcam.Transform = new Transform
            {
                X = 0.82, Y = 0.78, Scale = 0.2,
                Mask = new Mask { Shape = MaskShape.Circle },
            };

            Assert.True(TimelineOps.Split(project, webcam.Id, Ms(4_000)));

            var right = project.Items.Single(i => i.TrackId == webcam.TrackId && i.Id != webcam.Id);
            Assert.NotSame(webcam.Transform, right.Transform);
            Assert.NotSame(webcam.Transform.Mask, right.Transform.Mask);
            Assert.Equal(0.82, right.Transform.X);
            Assert.Equal(MaskShape.Circle, right.Transform.Mask.Shape);

            webcam.Transform.X = 0.1;
            Assert.Equal(0.82, right.Transform.X);
        }

        [Fact]
        public void Split_rejects_when_any_member_half_would_be_too_short()
        {
            var project = RecordingProject(out var screen, out var webcam, out _);
            // the webcam member starts later, so a split that is fine for the screen item lands
            // within MinSegment of the webcam item's start.
            TimelineOps.Unlink(project, new[] { webcam.Id });
            webcam.TimelineStartTicks = Ms(4_000) - TimelineOps.MinSegmentTicks / 2;
            webcam.DurationTicks = Ms(6_000);
            TimelineOps.Link(project, project.Items.Select(i => i.Id).ToArray());

            var before = project.ToJson();
            Assert.False(TimelineOps.Split(project, screen.Id, Ms(4_000)));
            Assert.Equal(before, project.ToJson()); // all-or-nothing: untouched on rejection

            // near the very edges of the target itself it also rejects.
            Assert.False(TimelineOps.Split(project, screen.Id, TimelineOps.MinSegmentTicks - 1));
            Assert.False(TimelineOps.Split(project, screen.Id, Ms(10_000) - 1));
            // and outside the target entirely.
            Assert.False(TimelineOps.Split(project, screen.Id, Ms(10_000)));
        }

        [Fact]
        public void Split_leaves_link_members_that_do_not_cover_the_instant_alone()
        {
            var project = RecordingProject(out var screen, out var webcam, out _);
            // shorten the webcam member so the split point falls past its end.
            TimelineOps.Unlink(project, new[] { webcam.Id });
            webcam.DurationTicks = Ms(3_000);
            TimelineOps.Link(project, project.Items.Select(i => i.Id).ToArray());

            Assert.True(TimelineOps.Split(project, screen.Id, Ms(5_000)));

            Assert.Equal(5, project.Items.Count); // screen + audio split, webcam untouched
            Assert.Equal(Ms(3_000), webcam.DurationTicks);
            Assert.Single(project.Items, i => i.TrackId == webcam.TrackId);
        }

        // ---- ripple delete ----

        [Fact]
        public void RippleDelete_removes_the_group_and_closes_the_gap_on_all_tracks()
        {
            var project = RecordingProject(out var screen, out _, out _);

            // split at 4s, then ripple-delete the left pairs: the right pairs slide back to 0.
            Assert.True(TimelineOps.Split(project, screen.Id, Ms(4_000)));
            TimelineOps.RippleDelete(project, screen.Id);

            Assert.Equal(3, project.Items.Count);
            Assert.All(project.Items, i => Assert.Equal(0, i.TimelineStartTicks));
            Assert.All(project.Items, i => Assert.Equal(Ms(6_000), i.DurationTicks));
            // the survivors still play the later part of the source — a ripple delete of the
            // first half is exactly the old "cut [0,4s)".
            Assert.All(project.Items, i => Assert.Equal(Ms(6_000), SourceIn(i)));
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void RippleDelete_leaves_items_before_the_gap_alone()
        {
            var project = RecordingProject(out var screen, out _, out _);
            Assert.True(TimelineOps.Split(project, screen.Id, Ms(4_000)));
            var right = project.Items.First(i => i.TrackId == screen.TrackId && i.Id != screen.Id);

            TimelineOps.RippleDelete(project, right.Id);

            Assert.Equal(3, project.Items.Count);
            Assert.All(project.Items, i => Assert.Equal(0, i.TimelineStartTicks));
            Assert.All(project.Items, i => Assert.Equal(Ms(4_000), i.DurationTicks));
            Assert.All(project.Items, i => Assert.Equal(Ms(2_000), SourceIn(i)));
        }

        // ---- delete ----

        [Fact]
        public void Delete_removes_only_that_item_and_leaves_a_gap()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);
            Assert.True(TimelineOps.Split(project, screen.Id, Ms(4_000)));
            var webcamRight = project.Items.Single(i => i.TrackId == webcam.TrackId && i.Id != webcam.Id);

            Assert.True(TimelineOps.Delete(project, webcam.Id));

            Assert.Equal(5, project.Items.Count);
            Assert.DoesNotContain(webcam, project.Items);
            // no ripple: the rest of the group and the row's other segment stay where they were,
            // leaving [0, 4s) empty on the webcam row.
            Assert.Equal(Ms(4_000), webcamRight.TimelineStartTicks);
            Assert.All(new[] { screen, audio }, i => Assert.Equal(0, i.TimelineStartTicks));
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void Delete_returns_false_for_an_unknown_item()
        {
            var project = RecordingProject(out _, out _, out _);

            Assert.False(TimelineOps.Delete(project, Guid.NewGuid()));
            Assert.Equal(3, project.Items.Count);
        }

        // ---- link / unlink ----

        [Fact]
        public void Unlink_detaches_only_the_given_items()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);

            TimelineOps.Unlink(project, new[] { audio.Id });
            TimelineOps.Move(project, screen.Id, Ms(2_000));

            Assert.Equal(Ms(2_000), screen.TimelineStartTicks);
            Assert.Equal(Ms(2_000), webcam.TimelineStartTicks);
            Assert.Equal(0, audio.TimelineStartTicks);
            Assert.Null(audio.LinkGroupId);
        }

        [Fact]
        public void Link_creates_a_fresh_shared_group()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);
            var originalGroup = screen.LinkGroupId;
            TimelineOps.Unlink(project, project.Items.Select(i => i.Id).ToArray());

            var group = TimelineOps.Link(project, new[] { screen.Id, audio.Id });

            Assert.NotEqual(originalGroup, group);
            Assert.Equal(group, screen.LinkGroupId);
            Assert.Equal(group, audio.LinkGroupId);
            Assert.Null(webcam.LinkGroupId);

            TimelineOps.Move(project, screen.Id, Ms(1_000));
            Assert.Equal(Ms(1_000), audio.TimelineStartTicks);
            Assert.Equal(0, webcam.TimelineStartTicks);
        }

        // ---- row sync toggle ----

        [Fact]
        public void UnlinkTrack_then_TryRelinkTrack_round_trips()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);
            var group = screen.LinkGroupId;

            TimelineOps.UnlinkTrack(project, webcam.TrackId);

            Assert.Null(webcam.LinkGroupId);
            Assert.Equal(group, screen.LinkGroupId);
            Assert.Equal(group, audio.LinkGroupId);

            Assert.True(TimelineOps.TryRelinkTrack(project, webcam.TrackId));

            Assert.Equal(group, webcam.LinkGroupId);
            TimelineOps.Move(project, screen.Id, Ms(1_000));
            Assert.Equal(Ms(1_000), webcam.TimelineStartTicks);
        }

        [Fact]
        public void TryRelinkTrack_rejoins_each_segment_to_its_own_group()
        {
            var project = RecordingProject(out var screen, out var webcam, out _);
            Assert.True(TimelineOps.Split(project, screen.Id, Ms(4_000)));

            var leftGroup = screen.LinkGroupId;
            var rightGroup = project.Items.Single(i => i.TrackId == screen.TrackId && i.Id != screen.Id).LinkGroupId;
            var webcamRight = project.Items.Single(i => i.TrackId == webcam.TrackId && i.Id != webcam.Id);

            TimelineOps.UnlinkTrack(project, webcam.TrackId);

            Assert.True(TimelineOps.TryRelinkTrack(project, webcam.TrackId));

            Assert.NotEqual(leftGroup, rightGroup);
            Assert.Equal(leftGroup, webcam.LinkGroupId);
            Assert.Equal(rightGroup, webcamRight.LinkGroupId);
        }

        [Fact]
        public void TryRelinkTrack_accepts_a_row_that_was_only_trimmed()
        {
            var project = RecordingProject(out var screen, out var webcam, out _);
            var group = screen.LinkGroupId;
            TimelineOps.UnlinkTrack(project, webcam.TrackId);

            // trim moves the in-point with the start, so the source<->timeline mapping — and with
            // it the row's claim of sync — survives.
            TimelineOps.TrimStart(project, webcam.Id, Ms(1_000));
            TimelineOps.TrimEnd(project, webcam.Id, -Ms(1_000));

            Assert.True(TimelineOps.TryRelinkTrack(project, webcam.TrackId));
            Assert.Equal(group, webcam.LinkGroupId);
        }

        [Fact]
        public void TryRelinkTrack_refuses_after_the_rest_of_the_group_moved()
        {
            var project = RecordingProject(out var screen, out var webcam, out _);
            TimelineOps.UnlinkTrack(project, webcam.TrackId);
            TimelineOps.Move(project, screen.Id, Ms(2_000));

            var before = project.ToJson();
            Assert.False(TimelineOps.TryRelinkTrack(project, webcam.TrackId));
            Assert.Equal(before, project.ToJson()); // refusal leaves the project untouched
            Assert.Null(webcam.LinkGroupId);

            // and once the group has moved clear of the row there is nothing to join at all.
            TimelineOps.Move(project, screen.Id, Ms(20_000));
            Assert.False(TimelineOps.TryRelinkTrack(project, webcam.TrackId));
            Assert.Null(webcam.LinkGroupId);
        }

        [Fact]
        public void TryRelinkTrack_refuses_when_an_item_overlaps_two_groups()
        {
            var project = RecordingProject(out var screen, out var webcam, out _);
            TimelineOps.UnlinkTrack(project, webcam.TrackId);
            // the other rows are cut in two while the webcam row is one long item, so it has no
            // single group to belong to.
            Assert.True(TimelineOps.Split(project, screen.Id, Ms(4_000)));

            var before = project.ToJson();
            Assert.False(TimelineOps.TryRelinkTrack(project, webcam.TrackId));
            Assert.Equal(before, project.ToJson());
        }
    }
}
