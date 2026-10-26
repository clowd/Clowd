using System;
using System.Linq;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The project-time ↔ output-time warp built from speed effect items: exact pass-through on
    /// unwarped spans, rational math on constant-factor spans, eased ramp LUTs, the monotone
    /// inverse pair, and the segment list the clock/audio consumers walk.
    /// </summary>
    public class TimeWarpTests
    {
        private const long Sec = 10_000_000; // 100ns ticks

        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>A project whose only content is one solid clip from 0 — sourceless, so the
        /// warp is isolated from media resolution; the clip length is the project duration.</summary>
        private static Project ProjectWithClip(long clipTicks)
        {
            var video = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Video", Order = 0 };
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
                        DurationTicks = clipTicks,
                        Content = new SolidContent { Color = "#FF102030" },
                    },
                },
            };
        }

        private static Item AddSpeedItem(Project project, long start, long duration, double factor,
            Transition entry = null, Transition exit = null)
        {
            var track = project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Effect);
            if (track == null)
            {
                track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Name = "Speed", Order = project.Tracks.Count };
                project.Tracks.Add(track);
            }

            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = start,
                DurationTicks = duration,
                Content = new SpeedContent { Factor = factor },
                Entry = entry,
                Exit = exit,
            };
            project.Items.Add(item);
            return item;
        }

        private static Transition Ramp(long ticks, TransitionEasing easing = TransitionEasing.Linear)
            => new Transition { Kind = TransitionKind.Ramp, DurationTicks = ticks, Easing = easing };

        // ---------------------------------------------------------------------- pitch flag

        [Fact]
        public void Segments_carry_the_items_pitch_correction_flag()
        {
            var project = ProjectWithClip(10 * Sec);
            var item = AddSpeedItem(project, 2 * Sec, 4 * Sec, 2.0, Ramp(Sec), Ramp(Sec));
            ((SpeedContent)item.Content).PitchCorrect = false;
            AddSpeedItem(project, 7 * Sec, 2 * Sec, 3.0); // default: pitch-corrected

            var warp = TimeWarp.Build(project);

            foreach (var seg in warp.Segments)
            {
                bool bent = seg.IsRamp || seg.Speed != 1.0;
                bool insideFirst = seg.ProjectStartTicks >= 2 * Sec && seg.ProjectEndTicks <= 6 * Sec;
                Assert.Equal(bent && !insideFirst, seg.PitchCorrect);
            }
        }

        // -------------------------------------------------------------------------- identity

        [Fact]
        public void Empty_project_is_identity()
        {
            var warp = TimeWarp.Build(new Project());

            Assert.True(warp.IsIdentity);
            Assert.Equal(0, warp.OutputDurationTicks);
            Assert.Empty(warp.Segments);
            Assert.Equal(0, warp.ToOutput(0));
            Assert.Equal(5 * Sec, warp.ToOutput(5 * Sec));
            Assert.Equal(5 * Sec, warp.ToProject(5 * Sec));
            Assert.Equal(1, warp.SpeedAt(3 * Sec));
        }

        [Fact]
        public void Project_without_speed_items_is_exact_pass_through()
        {
            var warp = TimeWarp.Build(ProjectWithClip(10 * Sec));

            Assert.True(warp.IsIdentity);
            Assert.Equal(10 * Sec, warp.OutputDurationTicks);

            var seg = Assert.Single(warp.Segments);
            Assert.Equal(0, seg.ProjectStartTicks);
            Assert.Equal(10 * Sec, seg.ProjectEndTicks);
            Assert.Equal(0, seg.OutputStartTicks);
            Assert.Equal(10 * Sec, seg.OutputEndTicks);
            Assert.False(seg.IsRamp);
            Assert.Equal(1.0, seg.Speed);

            for (long p = 0; p <= 12 * Sec; p += 333_333)
            {
                Assert.Equal(p, warp.ToOutput(p));
                Assert.Equal(p, warp.ToProject(p));
            }
        }

        [Fact]
        public void Factor_one_item_is_identity_even_with_ramps()
        {
            var project = ProjectWithClip(10 * Sec);
            AddSpeedItem(project, 2 * Sec, 3 * Sec, 1.0, Ramp(Sec), Ramp(Sec));

            var warp = TimeWarp.Build(project);
            Assert.True(warp.IsIdentity);
            Assert.Equal(10 * Sec, warp.OutputDurationTicks);
            Assert.Equal(7 * Sec, warp.ToOutput(7 * Sec));
        }

        [Fact]
        public void Hidden_speed_track_is_ignored()
        {
            var project = ProjectWithClip(10 * Sec);
            AddSpeedItem(project, 2 * Sec, 2 * Sec, 2.0);
            project.Tracks.Single(t => t.Kind == TrackKind.Effect).Hidden = true;

            var warp = TimeWarp.Build(project);
            Assert.True(warp.IsIdentity);
            Assert.Equal(10 * Sec, warp.OutputDurationTicks);
            Assert.Equal(3 * Sec, warp.ToOutput(3 * Sec));
            Assert.Equal(1, warp.SpeedAt(3 * Sec));
        }

        [Fact]
        public void Build_null_throws()
        {
            Assert.Throws<ArgumentNullException>(() => TimeWarp.Build(null));
        }

        // -------------------------------------------------------------------- constant factor

        [Fact]
        public void Constant_factor_two_compresses_the_item_span()
        {
            var project = ProjectWithClip(10 * Sec);
            AddSpeedItem(project, 2 * Sec, 2 * Sec, 2.0); // [2s,4s) at 2x -> 1s of output

            var warp = TimeWarp.Build(project);
            Assert.False(warp.IsIdentity);

            Assert.Equal(0, warp.ToOutput(0));
            Assert.Equal(2 * Sec, warp.ToOutput(2 * Sec));
            Assert.Equal(2 * Sec + Sec / 2, warp.ToOutput(3 * Sec));
            Assert.Equal(3 * Sec, warp.ToOutput(4 * Sec));
            Assert.Equal(9 * Sec, warp.ToOutput(10 * Sec));
            Assert.Equal(9 * Sec, warp.OutputDurationTicks);
        }

        [Fact]
        public void Constant_factor_half_stretches_the_item_span()
        {
            var project = ProjectWithClip(10 * Sec);
            AddSpeedItem(project, 2 * Sec, 2 * Sec, 0.5); // [2s,4s) at 0.5x -> 4s of output

            var warp = TimeWarp.Build(project);
            Assert.Equal(4 * Sec, warp.ToOutput(3 * Sec));
            Assert.Equal(6 * Sec, warp.ToOutput(4 * Sec));
            Assert.Equal(12 * Sec, warp.OutputDurationTicks);
            Assert.Equal(3 * Sec, warp.ToProject(4 * Sec));
        }

        [Fact]
        public void SpeedAt_is_factor_inside_and_unity_outside()
        {
            var project = ProjectWithClip(10 * Sec);
            AddSpeedItem(project, 2 * Sec, 2 * Sec, 3.0);

            var warp = TimeWarp.Build(project);
            Assert.Equal(1, warp.SpeedAt(0));
            Assert.Equal(1, warp.SpeedAt(2 * Sec - 1));
            Assert.Equal(3.0, warp.SpeedAt(2 * Sec)); // half-open span includes the start
            Assert.Equal(3.0, warp.SpeedAt(4 * Sec - 1));
            Assert.Equal(1, warp.SpeedAt(4 * Sec)); // ...and excludes the end
            Assert.Equal(1, warp.SpeedAt(-Sec));
            Assert.Equal(1, warp.SpeedAt(20 * Sec));
        }

        [Fact]
        public void ToProject_inverts_the_constant_span()
        {
            var project = ProjectWithClip(10 * Sec);
            AddSpeedItem(project, 2 * Sec, 2 * Sec, 2.0);

            var warp = TimeWarp.Build(project);
            Assert.Equal(2 * Sec, warp.ToProject(2 * Sec));
            Assert.Equal(3 * Sec, warp.ToProject(2 * Sec + Sec / 2));
            Assert.Equal(4 * Sec, warp.ToProject(3 * Sec));
            Assert.Equal(10 * Sec, warp.ToProject(9 * Sec));
        }

        [Fact]
        public void Unwarped_spans_are_bit_exact_integer_offsets()
        {
            var project = ProjectWithClip(10 * Sec);
            AddSpeedItem(project, 2 * Sec, 2 * Sec, 2.0);

            var warp = TimeWarp.Build(project);

            // before the item: exact identity
            for (long p = 0; p < 2 * Sec; p += 77_777)
            {
                Assert.Equal(p, warp.ToOutput(p));
                Assert.Equal(p, warp.ToProject(p));
            }

            // after the item: exact constant offset (the 1s the item removed)
            long tail = warp.ToOutput(4 * Sec) - 4 * Sec;
            for (long p = 4 * Sec; p < 10 * Sec; p += 77_777)
                Assert.Equal(p + tail, warp.ToOutput(p));
        }

        [Fact]
        public void Beyond_the_last_segment_continues_at_unity()
        {
            var project = ProjectWithClip(10 * Sec);
            AddSpeedItem(project, 2 * Sec, 2 * Sec, 2.0);

            var warp = TimeWarp.Build(project);
            Assert.Equal(11 * Sec, warp.ToOutput(12 * Sec));
            Assert.Equal(12 * Sec, warp.ToProject(11 * Sec));
        }

        [Fact]
        public void Negative_inputs_clamp_to_zero()
        {
            var project = ProjectWithClip(10 * Sec);
            AddSpeedItem(project, 2 * Sec, 2 * Sec, 2.0);

            var warp = TimeWarp.Build(project);
            Assert.Equal(0, warp.ToOutput(-Sec));
            Assert.Equal(0, warp.ToProject(-Sec));
        }

        // ----------------------------------------------------------------------------- ramps

        [Fact]
        public void Linear_entry_ramp_integrates_to_ln_two()
        {
            // s(t) = 1 + t over the 1s entry, so the ramp's output length is ln(2) seconds
            var project = ProjectWithClip(10 * Sec);
            AddSpeedItem(project, Sec, 4 * Sec, 2.0, Ramp(Sec));

            var warp = TimeWarp.Build(project);
            long rampOut = warp.ToOutput(2 * Sec) - warp.ToOutput(Sec);
            long ln2 = (long)Math.Round(Math.Log(2) * Sec);
            Assert.InRange(Math.Abs(rampOut - ln2), 0, 60);

            // the rest of the item is 3s at 2x
            Assert.Equal(warp.ToOutput(2 * Sec) + 3 * Sec / 2, warp.ToOutput(5 * Sec));
            Assert.InRange(Math.Abs(warp.OutputDurationTicks - (Sec + ln2 + 3 * Sec / 2 + 5 * Sec)), 0, 60);
        }

        [Fact]
        public void Entry_ramp_speed_eases_from_unity_to_factor()
        {
            var project = ProjectWithClip(10 * Sec);
            AddSpeedItem(project, Sec, 4 * Sec, 2.0, Ramp(Sec));

            var warp = TimeWarp.Build(project);
            Assert.Equal(1.0, warp.SpeedAt(Sec));
            Assert.Equal(1.5, warp.SpeedAt(Sec + Sec / 2), 10);
            Assert.Equal(2.0, warp.SpeedAt(2 * Sec));
            Assert.Equal(2.0, warp.SpeedAt(3 * Sec));
        }

        [Fact]
        public void Exit_ramp_speed_eases_from_factor_to_unity()
        {
            var project = ProjectWithClip(10 * Sec);
            AddSpeedItem(project, Sec, 4 * Sec, 2.0, exit: Ramp(Sec)); // exit window [4s,5s)

            var warp = TimeWarp.Build(project);
            Assert.Equal(2.0, warp.SpeedAt(4 * Sec - 1), 6);
            Assert.Equal(2.0, warp.SpeedAt(4 * Sec));
            Assert.Equal(1.5, warp.SpeedAt(4 * Sec + Sec / 2), 10);
            Assert.Equal(1.25, warp.SpeedAt(4 * Sec + 3 * Sec / 4), 10);
            Assert.Equal(1, warp.SpeedAt(5 * Sec));
        }

        [Fact]
        public void Ramp_easing_curves_flow_through_speed()
        {
            var project = ProjectWithClip(10 * Sec);
            AddSpeedItem(project, Sec, 4 * Sec, 3.0, Ramp(Sec, TransitionEasing.CubicIn));

            var warp = TimeWarp.Build(project);
            // Easing.Apply(CubicIn, 0.5) = 0.125 -> 1 + 2 * 0.125
            Assert.Equal(1.25, warp.SpeedAt(Sec + Sec / 2), 10);
            Assert.Equal(1 + 2 * Easing.Apply(TransitionEasing.CubicIn, 0.25), warp.SpeedAt(Sec + Sec / 4), 10);
        }

        [Fact]
        public void Entry_and_exit_shrink_proportionally_when_they_overlap()
        {
            // 2s item with 3s ramps on both ends: each clamps to the 2s item first, then the
            // pair shrinks proportionally to 1s + 1s — no constant middle survives
            var project = ProjectWithClip(10 * Sec);
            AddSpeedItem(project, 0, 2 * Sec, 2.0, Ramp(3 * Sec), Ramp(3 * Sec));

            var warp = TimeWarp.Build(project);
            var ramps = warp.Segments.Where(s => s.IsRamp).ToList();
            Assert.Equal(2, ramps.Count);
            Assert.Equal(0, ramps[0].ProjectStartTicks);
            Assert.Equal(Sec, ramps[0].ProjectEndTicks);
            Assert.Equal(Sec, ramps[1].ProjectStartTicks);
            Assert.Equal(2 * Sec, ramps[1].ProjectEndTicks);
            Assert.DoesNotContain(warp.Segments, s => !s.IsRamp && s.Speed != 1.0);

            Assert.Equal(1.5, warp.SpeedAt(Sec / 2), 10);           // entry midpoint
            Assert.Equal(2.0, warp.SpeedAt(Sec));                   // seam: entry done, exit not started
            Assert.Equal(1.5, warp.SpeedAt(Sec + Sec / 2), 10);     // exit midpoint
        }

        [Fact]
        public void Ramp_longer_than_the_item_clamps_to_its_duration()
        {
            var project = ProjectWithClip(10 * Sec);
            AddSpeedItem(project, 0, 2 * Sec, 2.0, Ramp(10 * Sec));

            var warp = TimeWarp.Build(project);
            Assert.Equal(1.5, warp.SpeedAt(Sec), 10); // halfway through the 2s effective ramp
            Assert.Equal(1, warp.SpeedAt(2 * Sec));
        }

        [Fact]
        public void Non_ramp_or_zero_duration_transitions_are_not_ramps()
        {
            var project = ProjectWithClip(10 * Sec);
            AddSpeedItem(project, Sec, 2 * Sec, 2.0,
                new Transition { Kind = TransitionKind.Ramp, DurationTicks = 0 },
                new Transition { Kind = TransitionKind.Fade, DurationTicks = Sec });

            var warp = TimeWarp.Build(project);
            Assert.DoesNotContain(warp.Segments, s => s.IsRamp);
            Assert.Equal(2.0, warp.SpeedAt(Sec));
            Assert.Equal(2.0, warp.SpeedAt(3 * Sec - 1));
        }

        // -------------------------------------------------------------- multiple items, segments

        [Fact]
        public void Multiple_items_accumulate_across_exact_gaps()
        {
            var project = ProjectWithClip(6 * Sec);
            AddSpeedItem(project, Sec, Sec, 2.0);      // [1s,2s) -> 0.5s
            AddSpeedItem(project, 3 * Sec, 2 * Sec, 0.5); // [3s,5s) -> 4s

            var warp = TimeWarp.Build(project);
            Assert.Equal(Sec, warp.ToOutput(Sec));
            Assert.Equal(Sec + Sec / 4, warp.ToOutput(Sec + Sec / 2));
            Assert.Equal(Ms(1500), warp.ToOutput(2 * Sec));
            Assert.Equal(Ms(2500), warp.ToOutput(3 * Sec));
            Assert.Equal(Ms(4500), warp.ToOutput(4 * Sec));
            Assert.Equal(Ms(6500), warp.ToOutput(5 * Sec));
            Assert.Equal(Ms(7500), warp.ToOutput(6 * Sec));
            Assert.Equal(Ms(7500), warp.OutputDurationTicks);

            Assert.Equal(4 * Sec, warp.ToProject(Ms(4500)));
            Assert.Equal(6 * Sec, warp.ToProject(Ms(7500)));

            // the gap between the items is a bit-exact offset span
            long offset = warp.ToOutput(2 * Sec) - 2 * Sec;
            for (long p = 2 * Sec; p < 3 * Sec; p += 33_333)
                Assert.Equal(p + offset, warp.ToOutput(p));
        }

        [Fact]
        public void Segments_tile_both_domains_contiguously()
        {
            var project = ProjectWithClip(12 * Sec);
            AddSpeedItem(project, Sec, 3 * Sec, 2.0, Ramp(Sec), Ramp(Sec, TransitionEasing.CubicInOut));
            AddSpeedItem(project, 6 * Sec, 2 * Sec, 0.25);

            var warp = TimeWarp.Build(project);
            var segments = warp.Segments;
            Assert.True(segments.Count >= 6);

            Assert.Equal(0, segments[0].ProjectStartTicks);
            Assert.Equal(0, segments[0].OutputStartTicks);
            for (int i = 1; i < segments.Count; i++)
            {
                Assert.Equal(segments[i - 1].ProjectEndTicks, segments[i].ProjectStartTicks);
                Assert.Equal(segments[i - 1].OutputEndTicks, segments[i].OutputStartTicks);
            }
            Assert.Equal(12 * Sec, segments[^1].ProjectEndTicks);
            Assert.Equal(warp.ToOutput(12 * Sec), segments[^1].OutputEndTicks);

            foreach (var seg in segments)
            {
                Assert.Equal(seg.OutputStartTicks, warp.ToOutput(seg.ProjectStartTicks));
                Assert.True(seg.ProjectEndTicks > seg.ProjectStartTicks);
            }

            // item 1: ramp, constant 2x, ramp; item 2: constant 0.25x; identity in between
            Assert.True(segments[1].IsRamp);
            Assert.Equal(2.0, segments[1].Speed);
            Assert.False(segments[2].IsRamp);
            Assert.Equal(2.0, segments[2].Speed);
            Assert.True(segments[3].IsRamp);
            var slow = segments.Single(s => s.Speed == 0.25);
            Assert.False(slow.IsRamp);
            Assert.Equal(8 * Sec, slow.OutputEndTicks - slow.OutputStartTicks);
        }

        [Fact]
        public void Speed_item_past_the_content_end_does_not_extend_output_duration()
        {
            var project = ProjectWithClip(5 * Sec);
            AddSpeedItem(project, 4 * Sec, 4 * Sec, 2.0); // [4s,8s), content ends at 5s

            var warp = TimeWarp.Build(project);
            Assert.Equal(4 * Sec + Sec / 2, warp.OutputDurationTicks);

            // the warp is still defined and monotone across and past the item
            long prev = long.MinValue;
            for (long p = 0; p <= 10 * Sec; p += 99_999)
            {
                long o = warp.ToOutput(p);
                Assert.True(o >= prev);
                prev = o;
            }
        }

        [Fact]
        public void Overlapping_items_resolve_in_favor_of_the_earlier_one()
        {
            // invalid per Validate, but Build must stay sane: the second item is clamped to [3s,4s)
            var project = ProjectWithClip(6 * Sec);
            AddSpeedItem(project, Sec, 2 * Sec, 2.0);
            AddSpeedItem(project, 2 * Sec, 2 * Sec, 4.0);

            var warp = TimeWarp.Build(project);
            Assert.Equal(2.0, warp.SpeedAt(2 * Sec + Sec / 2));
            Assert.Equal(4.0, warp.SpeedAt(3 * Sec + Sec / 2));
            Assert.Equal(2 * Sec + Sec / 4, warp.ToOutput(4 * Sec));
            Assert.DoesNotContain(warp.Segments, s => s.ProjectEndTicks <= s.ProjectStartTicks);
        }

        // ------------------------------------------------------------------ round-trip properties

        [Fact]
        public void Fast_only_projects_round_trip_output_within_one_tick()
        {
            var project = ProjectWithClip(20 * Sec);
            AddSpeedItem(project, Sec, 3 * Sec, 2.0, Ramp(Sec), Ramp(Sec));
            AddSpeedItem(project, 6 * Sec, 2 * Sec, 10.0);
            AddSpeedItem(project, 10 * Sec, 4 * Sec, 1.5, Ramp(2 * Sec, TransitionEasing.CubicOut));

            var warp = TimeWarp.Build(project);
            for (long o = 0; o <= warp.OutputDurationTicks; o += 1_237)
                Assert.InRange(Math.Abs(warp.ToOutput(warp.ToProject(o)) - o), 0, 1);
        }

        [Fact]
        public void Slow_only_projects_round_trip_project_within_one_tick()
        {
            var project = ProjectWithClip(20 * Sec);
            AddSpeedItem(project, Sec, 3 * Sec, 0.5, Ramp(Sec), Ramp(Sec));
            AddSpeedItem(project, 6 * Sec, 2 * Sec, 0.1);
            AddSpeedItem(project, 10 * Sec, 4 * Sec, 0.25, exit: Ramp(2 * Sec, TransitionEasing.CubicIn));

            var warp = TimeWarp.Build(project);
            for (long p = 0; p <= 20 * Sec; p += 1_237)
                Assert.InRange(Math.Abs(warp.ToProject(warp.ToOutput(p)) - p), 0, 1);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(42)]
        public void Randomized_projects_are_monotone_and_round_trip_consistent(int seed)
        {
            var rng = new Random(seed);
            var project = ProjectWithClip(60 * Sec);
            double[] factors = { 0.1, 0.25, 0.5, 1.0, 1.5, 2.0, 3.0, 10.0 };

            long cursor = Ms(rng.Next(0, 3000));
            while (cursor < 50 * Sec)
            {
                long duration = Ms(rng.Next(500, 8000));
                var entry = rng.Next(2) == 0 ? Ramp(Ms(rng.Next(0, 4000)), (TransitionEasing)rng.Next(4)) : null;
                var exit = rng.Next(2) == 0 ? Ramp(Ms(rng.Next(0, 4000)), (TransitionEasing)rng.Next(4)) : null;
                AddSpeedItem(project, cursor, duration, factors[rng.Next(factors.Length)], entry, exit);
                cursor += duration + Ms(rng.Next(0, 3000));
            }

            var warp = TimeWarp.Build(project);

            // both maps are monotone non-decreasing over a coarse sweep...
            long prevOut = long.MinValue, prevProj = long.MinValue;
            for (long t = 0; t <= 62 * Sec; t += 7_919)
            {
                long o = warp.ToOutput(t);
                Assert.True(o >= prevOut, $"ToOutput not monotone at {t}");
                prevOut = o;

                long p = warp.ToProject(t);
                Assert.True(p >= prevProj, $"ToProject not monotone at {t}");
                prevProj = p;

                Assert.InRange(warp.SpeedAt(t), 0.1, 10.0);
            }

            // ...and tick-by-tick across every segment boundary in both domains
            foreach (var seg in warp.Segments)
            {
                long o1 = long.MinValue, p1 = long.MinValue;
                for (long d = -3; d <= 3; d++)
                {
                    long o = warp.ToOutput(seg.ProjectStartTicks + d);
                    Assert.True(o >= o1);
                    o1 = o;

                    long p = warp.ToProject(seg.OutputStartTicks + d);
                    Assert.True(p >= p1);
                    p1 = p;
                }
            }

            // round trips land within the local quantization bound: exact-ish where the map is
            // expanding, half the speed ratio where it is compressing (speed spans 0.1..10 -> 6).
            for (long o = 0; o <= warp.OutputDurationTicks; o += 5_003)
                Assert.InRange(Math.Abs(warp.ToOutput(warp.ToProject(o)) - o), 0, 6);
            for (long p = 0; p <= 60 * Sec; p += 5_003)
                Assert.InRange(Math.Abs(warp.ToProject(warp.ToOutput(p)) - p), 0, 6);
        }

        [Fact]
        public void Segment_end_anchors_agree_with_the_maps()
        {
            var project = ProjectWithClip(12 * Sec);
            AddSpeedItem(project, Sec, 3 * Sec, 3.0, Ramp(Ms(700), TransitionEasing.CubicInOut), Ramp(Ms(1300)));
            AddSpeedItem(project, 7 * Sec, 2 * Sec, 0.5, Ramp(Ms(500)));

            var warp = TimeWarp.Build(project);
            foreach (var seg in warp.Segments)
            {
                Assert.Equal(seg.OutputStartTicks, warp.ToOutput(seg.ProjectStartTicks));
                Assert.Equal(seg.OutputEndTicks, warp.ToOutput(seg.ProjectEndTicks));
                Assert.Equal(seg.ProjectEndTicks, warp.ToProject(seg.OutputEndTicks));
            }
        }
    }
}
