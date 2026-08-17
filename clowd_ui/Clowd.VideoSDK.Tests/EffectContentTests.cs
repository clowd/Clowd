using System;
using System.Linq;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The effect item model: <see cref="SpeedContent"/>/<see cref="ZoomContent"/> serialization,
    /// the <see cref="Project.Validate"/> rules that keep effect rows well-formed, the duration
    /// exclusion, and <see cref="TimelineOps"/> behaviour on sourceless effect items.
    /// </summary>
    public class EffectContentTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>A 20s solid clip on a video row, one zoom row above it, and the speed row on
        /// top — no sources, so every rule under test is isolated from media resolution.</summary>
        private static Project EffectProject(out Item clip, out Item zoom, out Item speed)
        {
            var videoTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Video", Order = 0 };
            var zoomTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Name = "Zoom", Order = 1 };
            var speedTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Name = "Speed", Order = 2 };

            clip = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = videoTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(20_000),
                Content = new SolidContent { Color = "#FF102030" },
            };
            zoom = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = zoomTrack.Id,
                TimelineStartTicks = Ms(2_000),
                DurationTicks = Ms(6_000),
                Content = new ZoomContent { Zoom = 2.0, FocusX = 0.25, FocusY = 0.75 },
                Entry = new Transition { Kind = TransitionKind.Ramp, DurationTicks = Ms(400), Easing = TransitionEasing.CubicOut },
                Exit = new Transition { Kind = TransitionKind.Ramp, DurationTicks = Ms(600), Easing = TransitionEasing.CubicInOut },
            };
            speed = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = speedTrack.Id,
                TimelineStartTicks = Ms(4_000),
                DurationTicks = Ms(5_000),
                Content = new SpeedContent { Factor = 3.0 },
                Entry = new Transition { Kind = TransitionKind.Ramp, DurationTicks = Ms(250), Easing = TransitionEasing.Linear },
            };

            return new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Tracks = { videoTrack, zoomTrack, speedTrack },
                Items = { clip, zoom, speed },
            };
        }

        // ---------------------------------------------------------------------------- round trip

        [Fact]
        public void Effect_project_round_trips_byte_identical_and_valid()
        {
            var project = EffectProject(out _, out _, out _);
            project.Normalize();
            Assert.Empty(project.Validate());

            var json = project.ToJson();
            var restored = Project.FromJson(json);

            Assert.Equal(json, restored.ToJson());
            Assert.Empty(restored.Validate());
        }

        [Fact]
        public void Round_trip_preserves_effect_content_fields()
        {
            var project = EffectProject(out _, out var zoom, out var speed);
            var restored = Project.FromJson(project.ToJson());

            var restoredZoom = (ZoomContent)restored.Items.Single(i => i.Id == zoom.Id).Content;
            Assert.Equal(2.0, restoredZoom.Zoom);
            Assert.Equal(0.25, restoredZoom.FocusX);
            Assert.Equal(0.75, restoredZoom.FocusY);

            var restoredSpeed = (SpeedContent)restored.Items.Single(i => i.Id == speed.Id).Content;
            Assert.Equal(3.0, restoredSpeed.Factor);

            var entry = restored.Items.Single(i => i.Id == speed.Id).Entry;
            Assert.Equal(TransitionKind.Ramp, entry.Kind);
            Assert.Equal(Ms(250), entry.DurationTicks);
        }

        [Fact]
        public void Effect_content_clones_are_independent()
        {
            var speed = new SpeedContent { Factor = 4.0 };
            var speedCopy = (SpeedContent)speed.Clone();
            speedCopy.Factor = 0.5;
            Assert.Equal(4.0, speed.Factor);

            var zoom = new ZoomContent { Zoom = 3.0, FocusX = 0.1, FocusY = 0.9 };
            var zoomCopy = (ZoomContent)zoom.Clone();
            zoomCopy.Zoom = 1.5;
            zoomCopy.FocusX = 0.5;
            Assert.Equal(3.0, zoom.Zoom);
            Assert.Equal(0.1, zoom.FocusX);
            Assert.Equal(0.9, zoomCopy.FocusY);
        }

        // ------------------------------------------------------------------------------ duration

        [Fact]
        public void Duration_excludes_effect_track_items()
        {
            var project = EffectProject(out var clip, out var zoom, out _);

            // a zoom hanging far past the last clip extends nothing.
            zoom.TimelineStartTicks = Ms(25_000);
            zoom.DurationTicks = Ms(30_000);

            Assert.Equal(clip.TimelineEndTicks, project.GetDurationTicks());
        }

        [Fact]
        public void Duration_of_effect_only_project_is_zero()
        {
            var project = EffectProject(out var clip, out _, out _);
            project.Items.Remove(clip);

            Assert.Equal(0, project.GetDurationTicks());
        }

        // ---------------------------------------------------------------------------- validation

        [Fact]
        public void Validate_rejects_effect_content_off_effect_tracks()
        {
            var project = EffectProject(out var clip, out var zoom, out _);
            zoom.TrackId = clip.TrackId;
            zoom.TimelineStartTicks = clip.TimelineEndTicks; // dodge the overlap check

            Assert.Contains(project.Validate(), e => e.Contains("non-effect track"));
        }

        [Fact]
        public void Validate_rejects_non_effect_content_on_effect_tracks()
        {
            var project = EffectProject(out var clip, out var zoom, out _);
            clip.TrackId = zoom.TrackId;
            clip.TimelineStartTicks = zoom.TimelineEndTicks;

            Assert.Contains(project.Validate(), e => e.Contains("on effect track"));
        }

        [Fact]
        public void Validate_rejects_a_mixed_effect_track()
        {
            var project = EffectProject(out _, out var zoom, out var speed);
            speed.TrackId = zoom.TrackId;
            speed.TimelineStartTicks = zoom.TimelineEndTicks;

            Assert.Contains(project.Validate(), e => e.Contains("mixes speed and zoom"));
        }

        [Fact]
        public void Validate_rejects_a_second_speed_track()
        {
            var project = EffectProject(out _, out _, out var speed);
            var second = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Name = "Speed 2", Order = 3 };
            project.Tracks.Add(second);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = second.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(1_000),
                Content = new SpeedContent(),
            });

            Assert.Contains(project.Validate(), e => e.Contains("at most one speed row"));
            Assert.DoesNotContain(project.Validate(), e => e.Contains("mixes speed and zoom"));
        }

        [Fact]
        public void Validate_rejects_a_linked_effect_item()
        {
            var project = EffectProject(out _, out var zoom, out _);
            zoom.LinkGroupId = Guid.NewGuid();

            Assert.Contains(project.Validate(), e => e.Contains("carries a link group"));
        }

        [Theory]
        [InlineData(0.05)]
        [InlineData(11)]
        [InlineData(0)]
        [InlineData(Double.NaN)]
        public void Validate_rejects_out_of_range_speed_factors(double factor)
        {
            var project = EffectProject(out _, out _, out var speed);
            ((SpeedContent)speed.Content).Factor = factor;

            Assert.Contains(project.Validate(), e => e.Contains("speed factor"));
        }

        [Theory]
        [InlineData(0.9, 0.5, 0.5)]
        [InlineData(5.1, 0.5, 0.5)]
        [InlineData(Double.NaN, 0.5, 0.5)]
        [InlineData(2.0, -0.1, 0.5)]
        [InlineData(2.0, 0.5, 1.1)]
        [InlineData(2.0, Double.NaN, 0.5)]
        public void Validate_rejects_out_of_range_zoom_values(double factor, double focusX, double focusY)
        {
            var project = EffectProject(out _, out var zoom, out _);
            var content = (ZoomContent)zoom.Content;
            content.Zoom = factor;
            content.FocusX = focusX;
            content.FocusY = focusY;

            Assert.Contains(project.Validate(), e => e.Contains("zoom factor") || e.Contains("zoom focus"));
        }

        [Fact]
        public void Validate_rejects_non_ramp_transitions_on_effect_items()
        {
            var project = EffectProject(out _, out var zoom, out var speed);
            zoom.Entry = new Transition { Kind = TransitionKind.Fade, DurationTicks = Ms(300) };
            speed.Exit = new Transition { Kind = TransitionKind.None, DurationTicks = Ms(300) };

            var errors = project.Validate();

            Assert.Contains(errors, e => e.Contains("non-ramp entry"));
            Assert.Contains(errors, e => e.Contains("non-ramp exit"));
        }

        [Fact]
        public void Validate_still_applies_the_overlap_check_to_effect_rows()
        {
            var project = EffectProject(out _, out var zoom, out _);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = zoom.TrackId,
                TimelineStartTicks = zoom.TimelineStartTicks + Ms(1_000),
                DurationTicks = Ms(2_000),
                Content = new ZoomContent(),
            });

            Assert.Contains(project.Validate(), e => e.Contains("overlap"));
        }

        // -------------------------------------------------------------------------- timeline ops

        [Fact]
        public void Move_shifts_an_effect_item_and_clamps_at_the_origin()
        {
            var project = EffectProject(out _, out var zoom, out _);

            Assert.Equal(-Ms(2_000), TimelineOps.Move(project, zoom.Id, -Ms(5_000)));
            Assert.Equal(0, zoom.TimelineStartTicks);
        }

        [Fact]
        public void Trims_on_an_effect_item_take_the_sourceless_branch()
        {
            var project = EffectProject(out _, out var zoom, out _);

            // no source to rewind before: extending the start is clamped by the origin only.
            Assert.Equal(-Ms(2_000), TimelineOps.TrimStart(project, zoom.Id, -Ms(10_000)));
            Assert.Equal(0, zoom.TimelineStartTicks);

            // no stream duration: the end extends freely.
            Assert.Equal(Ms(4_000), TimelineOps.TrimEnd(project, zoom.Id, Ms(4_000)));

            // and shrinking clamps at MinSegmentTicks like any item.
            TimelineOps.TrimEnd(project, zoom.Id, -Ms(60_000));
            Assert.Equal(TimelineOps.MinSegmentTicks, zoom.DurationTicks);
        }

        [Fact]
        public void Split_cuts_an_effect_item_keeping_entry_left_and_exit_right()
        {
            var project = EffectProject(out _, out var zoom, out _);
            var cut = zoom.TimelineStartTicks + Ms(2_500);

            Assert.True(TimelineOps.Split(project, zoom.Id, cut));

            var halves = project.Items.Where(i => i.Content is ZoomContent)
                                      .OrderBy(i => i.TimelineStartTicks).ToList();
            Assert.Equal(2, halves.Count);
            var (left, right) = (halves[0], halves[1]);

            Assert.Equal(cut, left.TimelineEndTicks);
            Assert.Equal(cut, right.TimelineStartTicks);
            Assert.Equal(TransitionKind.Ramp, left.Entry.Kind);
            Assert.Null(left.Exit);
            Assert.Null(right.Entry);
            Assert.Equal(TransitionKind.Ramp, right.Exit.Kind);
            Assert.Null(right.LinkGroupId);

            // the halves' content is a clone, not shared state.
            var rightContent = (ZoomContent)right.Content;
            Assert.Equal(2.0, rightContent.Zoom);
            rightContent.Zoom = 4.0;
            Assert.Equal(2.0, ((ZoomContent)left.Content).Zoom);

            Assert.Empty(project.Validate());
        }

        [Fact]
        public void RippleDelete_of_an_effect_item_closes_the_gap_under_everything()
        {
            var project = EffectProject(out _, out var zoom, out var speed);
            var start = zoom.TimelineStartTicks;

            TimelineOps.RippleDelete(project, zoom.Id);

            // the speed item began inside the deleted span, so its shift clamps at the span start.
            Assert.DoesNotContain(project.Items, i => i.Id == zoom.Id);
            Assert.Equal(start, project.Items.Single(i => i.Id == speed.Id).TimelineStartTicks);
        }

        [Fact]
        public void SetSpeed_refuses_effect_content()
        {
            var project = EffectProject(out _, out _, out var speed);
            var duration = speed.DurationTicks;

            Assert.Equal(1.0, TimelineOps.SetSpeed(project, speed.Id, 2.0));
            Assert.Equal(3.0, ((SpeedContent)speed.Content).Factor);
            Assert.Equal(duration, speed.DurationTicks);
        }

        [Fact]
        public void TryRelinkTrack_refuses_effect_rows()
        {
            var project = EffectProject(out var clip, out var zoom, out _);
            clip.LinkGroupId = Guid.NewGuid();

            Assert.False(TimelineOps.TryRelinkTrack(project, zoom.TrackId));
            Assert.Null(zoom.LinkGroupId);
        }
    }
}
