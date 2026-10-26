using System;
using System.Linq;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Clowd.VideoSDK.Render;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="MediaContent.SpeedWarpExempt"/>: a clip on the output clock. The mixing half
    /// runs against a fake <see cref="IAudioSource"/> whose samples encode their source position
    /// (no FFmpeg): an exempt clip under a 2x speed item must read its source at one source
    /// second per OUTPUT second, stop where its output span ends, and change nothing at all
    /// under an identity warp. The editing half drives <see cref="EditorSession"/>: the re-fit
    /// of an exempt clip's project span when a speed item arrives, leaves, or is dragged, when
    /// the flag flips, and the in-point math of a trim under a warp.
    /// </summary>
    public class SpeedWarpExemptTests
    {
        private const int Rate = 48000;
        private const long Second = 10_000_000;

        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        // ------------------------------------------------------------------------------ fixture

        /// <summary>Sample value is a pure function of the source position (ch0 = f(pos),
        /// ch1 = -f(pos)), so what a mixed sample should be is computable without a mixer.</summary>
        private sealed class PositionAudioSource : IAudioSource
        {
            public bool ReadSamples(Guid sourceId, int streamIndex, long sourcePosFrames,
                float[] dst, int frames, out int framesRead)
            {
                for (int i = 0; i < frames; i++)
                {
                    float v = HashValue(sourcePosFrames + i);
                    dst[i * 2] = v;
                    dst[i * 2 + 1] = -v;
                }
                framesRead = frames;
                return true;
            }
        }

        private static float HashValue(long pos) => ((pos * 31) % 997) / 1000f;

        private static Project NewProject() => new Project
        {
            Output = new OutputSettings { WidthPx = 64, HeightPx = 64, FpsNum = 30, FpsDen = 1, SampleRate = Rate },
        };

        private static Item AddAudioItem(Project project, long startTicks, long durationTicks, bool exempt)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Order = project.Tracks.Count };
            project.Tracks.Add(track);
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new MediaContent { SourceId = Guid.NewGuid(), StreamIndex = 0, SourceInTicks = 0, SpeedWarpExempt = exempt },
            };
            project.Items.Add(item);
            return item;
        }

        private static void AddSpeedItem(Project project, long startTicks, long durationTicks, double factor)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Order = project.Tracks.Count, Name = "Speed" };
            project.Tracks.Add(track);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new SpeedContent { Factor = factor, PitchCorrect = false },
            });
        }

        /// <summary>Reads [0, totalFrames) through the render's warp stage in forward chunks.</summary>
        private static float[] RenderMix(Project project, long totalFrames, int chunkFrames = 1601)
        {
            var warp = TimeWarp.Build(project);
            var resampler = new WarpAudioResampler(new AudioMixer(project, new PositionAudioSource(), warp), warp, Rate);
            var result = new float[totalFrames * AudioMixer.Channels];
            var chunk = new float[chunkFrames * AudioMixer.Channels];
            long pos = 0;
            while (pos < totalFrames)
            {
                int n = (int)Math.Min(chunkFrames, totalFrames - pos);
                resampler.ReadChunk(pos, n, chunk);
                Array.Copy(chunk, 0, result, pos * AudioMixer.Channels, n * AudioMixer.Channels);
                pos += n;
            }
            return result;
        }

        // ------------------------------------------------------------------------------- mixing

        [Fact]
        public void Exempt_clip_reads_one_source_second_per_output_second_under_2x()
        {
            // 2s of project at 2x is 1s of output; the exempt clip covers that second and reads
            // source frame o at output frame o, verbatim: no resampling touches it.
            var project = NewProject();
            AddAudioItem(project, 0, 2 * Second, exempt: true);
            AddSpeedItem(project, 0, 2 * Second, 2.0);

            var warp = TimeWarp.Build(project);
            Assert.Equal(Second, warp.ToOutput(2 * Second));

            var dst = RenderMix(project, Rate);
            for (long o = 0; o < Rate; o++)
            {
                Assert.Equal(HashValue(o), dst[o * 2]);
                Assert.Equal(-HashValue(o), dst[o * 2 + 1]);
            }
        }

        [Fact]
        public void Exempt_clip_falls_silent_where_its_output_span_ends()
        {
            var project = NewProject();
            AddAudioItem(project, 0, 2 * Second, exempt: true);
            AddSpeedItem(project, 0, 2 * Second, 2.0);

            // past the 2x span the warp continues at speed 1, but the clip's output span was
            // [0, 1s): nothing of it plays after that
            var dst = RenderMix(project, 2 * Rate);
            Assert.NotEqual(0f, dst[(Rate - 1) * 2]);
            for (long o = Rate; o < 2 * Rate; o++)
            {
                Assert.Equal(0f, dst[o * 2]);
                Assert.Equal(0f, dst[o * 2 + 1]);
            }
        }

        [Fact]
        public void Mixer_keeps_exempt_clips_off_the_project_clock()
        {
            var project = NewProject();
            AddAudioItem(project, 0, 2 * Second, exempt: true);
            AddSpeedItem(project, 0, 2 * Second, 2.0);

            var mixer = new AudioMixer(project, new PositionAudioSource(), TimeWarp.Build(project));
            Assert.True(mixer.HasOutputItems);
            Assert.Equal(1, mixer.OutputItemCount);
            Assert.Equal(1, mixer.AudibleItemCount);

            // the project-clock mix (what the warp stages bend) has nothing in it
            var chunk = new float[100 * AudioMixer.Channels];
            mixer.MixChunk(0, 100, chunk);
            Assert.All(chunk, v => Assert.Equal(0f, v));

            // the output-clock mix carries the clip at output positions
            mixer.MixOutputChunk(1000, 100, chunk);
            for (int i = 0; i < 100; i++)
                Assert.Equal(HashValue(1000 + i), chunk[i * 2]);
        }

        [Fact]
        public void Flag_changes_nothing_under_an_identity_warp()
        {
            var flagged = NewProject();
            AddAudioItem(flagged, Second / 2, 2 * Second, exempt: true);
            var plain = NewProject();
            AddAudioItem(plain, Second / 2, 2 * Second, exempt: false);

            var mixer = new AudioMixer(flagged, new PositionAudioSource(), TimeWarp.Build(flagged));
            Assert.False(mixer.HasOutputItems);
            Assert.Equal(1, mixer.AudibleItemCount);

            // element-wise exact: the flag is invisible until a speed item bends time
            Assert.Equal(RenderMix(plain, 3 * Rate), RenderMix(flagged, 3 * Rate));
        }

        [Fact]
        public void Non_exempt_clip_beside_an_exempt_one_is_still_warped()
        {
            // both clips cover 2s of project under 2x. The exempt one plays 1s of source over
            // the output second; the plain one is resampled and plays all 2s of its source over
            // the same second. Both are audible in the sum, so the mix is neither alone.
            var project = NewProject();
            AddAudioItem(project, 0, 2 * Second, exempt: true);
            AddAudioItem(project, 0, 2 * Second, exempt: false);
            AddSpeedItem(project, 0, 2 * Second, 2.0);

            var dst = RenderMix(project, Rate);
            var exemptOnly = NewProject();
            AddAudioItem(exemptOnly, 0, 2 * Second, exempt: true);
            AddSpeedItem(exemptOnly, 0, 2 * Second, 2.0);
            var expectedExempt = RenderMix(exemptOnly, Rate);

            // subtracting the exempt part leaves the plain clip's resampled samples, which
            // read source ~2o (tick quantization keeps it within a few frames)
            int checkedFrames = 0;
            for (long o = 100; o < Rate; o += 997)
            {
                float plain = dst[o * 2] - expectedExempt[o * 2];
                bool near = false;
                for (long s = 2 * o - 3; s <= 2 * o + 3 && !near; s++)
                    near = Math.Abs(plain - HashValue(s)) < 1e-3f || Math.Abs(plain) < 1e-6f;
                Assert.True(near, $"frame {o}: plain part {plain} is not near source ~{2 * o}");
                checkedFrames++;
            }
            Assert.True(checkedFrames > 10);
        }

        [Fact]
        public void Clone_carries_the_flag()
        {
            var media = new MediaContent { SourceId = Guid.NewGuid(), SpeedWarpExempt = true };
            Assert.True(((MediaContent)media.Clone()).SpeedWarpExempt);
        }

        [Fact]
        public void Flag_round_trips_through_json()
        {
            var project = NewProject();
            AddAudioItem(project, 0, Second, exempt: true);

            var loaded = Project.FromJson(project.ToJson());
            Assert.True(((MediaContent)loaded.Items.Single().Content).SpeedWarpExempt);
        }

        // ------------------------------------------------------------------------------ editing

        /// <summary>A 10s solid on a video row plus one audio row playing a 60s source; the audio
        /// clip's span and flag are the test's to set.</summary>
        private static EditorSession NewSession(long clipStart, long clipDuration, bool exempt,
            out Item clip, out Track audioTrack, long streamDurationTicks = 0)
        {
            var sourceId = Guid.NewGuid();
            var video = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 1 };
            clip = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audioTrack.Id,
                TimelineStartTicks = clipStart,
                DurationTicks = clipDuration,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = 0, SpeedWarpExempt = exempt },
            };
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = Rate },
                Sources =
                {
                    new Source
                    {
                        Id = sourceId,
                        Path = TestPath.Native(@"C:\rec\voice.wav"),
                        Streams = { new SourceStream { Index = 0, Kind = StreamKind.Audio, DurationTicks = streamDurationTicks > 0 ? streamDurationTicks : Ms(60_000) } },
                    },
                },
                Tracks = { video, audioTrack },
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
                    clip,
                },
            };
            return new EditorSession(project, null, save => save());
        }

        private static Item Live(EditorSession session, Item item) => session.Project.Items.Single(i => i.Id == item.Id);

        [Fact]
        public void Adding_and_removing_a_speed_item_refits_an_exempt_clip()
        {
            // clip [2s, 4s) = 2s of real time. Under 2x over the whole project its start lands at
            // output 1s and 2s of real time later is output 3s = project 6s: the clip now spans
            // [2s, 6s) of project time and plays exactly the same 2s of source.
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: true, out var clip, out _);

            var speed = session.AddSpeedEffect(0, Ms(10_000));
            Assert.NotNull(speed);
            Assert.Equal(2.0, ((SpeedContent)speed.Content).Factor);
            Assert.Equal(Ms(2_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(4_000), Live(session, clip).DurationTicks);
            Assert.Empty(session.Project.Validate());

            session.DeleteItem(speed.Id);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);

            // the snapshots restore the same states in reverse
            session.Undo();
            Assert.Equal(Ms(4_000), Live(session, clip).DurationTicks);
            session.Undo();
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
        }

        [Fact]
        public void Changing_the_speed_factor_refits_an_exempt_clip()
        {
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: true, out var clip, out _);
            var speed = session.AddSpeedEffect(0, Ms(10_000));
            Assert.Equal(Ms(4_000), Live(session, clip).DurationTicks);

            // 4x: output 0.5s at the start, 2s of real time later is output 2.5s = project 10s
            session.SetSpeedFactor(speed.Id, 4.0);
            Assert.Equal(Ms(8_000), Live(session, clip).DurationTicks);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_plain_clip_is_not_refit()
        {
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: false, out var clip, out _);
            session.AddSpeedEffect(0, Ms(10_000));
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
        }

        [Fact]
        public void Flipping_the_flag_refits_in_both_directions()
        {
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: false, out var clip, out _);
            session.AddSpeedEffect(0, Ms(10_000));
            ProjectChangeKind? kind = null;
            session.ProjectChanged += (_, e) => kind = e.Kind;

            // on: the 2s of project it covered become 2s of real time, which is 4s of project
            session.SetSpeedWarpExempt(clip.Id, true);
            Assert.True(((MediaContent)Live(session, clip).Content).SpeedWarpExempt);
            Assert.Equal(Ms(4_000), Live(session, clip).DurationTicks);
            Assert.Equal(ProjectChangeKind.Mapping, kind);

            // off: back to the 2s of project its 2s of real time span
            session.SetSpeedWarpExempt(clip.Id, false);
            Assert.False(((MediaContent)Live(session, clip).Content).SpeedWarpExempt);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);

            session.Undo();
            Assert.True(((MediaContent)Live(session, clip).Content).SpeedWarpExempt);
            Assert.Equal(Ms(4_000), Live(session, clip).DurationTicks);
        }

        [Fact]
        public void Flipping_the_flag_without_a_warp_keeps_the_span()
        {
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: false, out var clip, out _);

            session.SetSpeedWarpExempt(clip.Id, true);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
            Assert.True(session.CanUndo);
        }

        [Fact]
        public void Refit_stops_short_of_the_next_clip_on_the_row()
        {
            var seed = NewSession(Ms(2_000), Ms(2_000), exempt: true, out var clip, out var audioTrack);
            // a second clip at 5s on the same row, in the model the session then opens
            var project = seed.Project;
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audioTrack.Id,
                TimelineStartTicks = Ms(5_000),
                DurationTicks = Ms(1_000),
                Content = new MediaContent { SourceId = project.Sources[0].Id, StreamIndex = 0 },
            });
            var session = new EditorSession(project, null, save => save());

            // the re-fit would run the clip out to 6s, over the neighbour at 5s: it stops there
            session.AddSpeedEffect(0, Ms(10_000));
            Assert.Equal(Ms(3_000), Live(session, clip).DurationTicks);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_drag_that_comes_back_restores_the_original_span()
        {
            // the speed item starts past the clip, so the clip is unwarped. Dragged over it the
            // clip re-fits; dragged back the original 2s comes back exactly and the gesture is a
            // no-op (no undo entry) rather than a one-tick rounding of the clip.
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: true, out var clip, out _);
            var speed = session.AddSpeedEffect(Ms(5_000), Ms(5_000));
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
            var before = session.Project.ToJson();

            using (var gesture = session.BeginGesture("Move"))
            {
                // 2x over [0, 5s): the clip's start is output 1s, and its 2s of real time reach
                // output 3s, half a second past the span's output end of 2.5s (= project 5s)
                session.MoveItem(speed.Id, -Ms(5_000));
                Assert.Equal(Ms(3_500), Live(session, clip).DurationTicks);

                session.MoveItem(speed.Id, Ms(5_000));
                Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
                gesture.Commit();
            }

            Assert.Equal(before, session.Project.ToJson());
        }

        [Fact]
        public void Moving_an_exempt_clip_across_a_speed_boundary_keeps_its_real_time_span()
        {
            // 2x over [0, 5s): the clip at 2s starts at output 1s and its 2s of real time reach
            // output 3s, half a second past the span's output end of 2.5s (= project 5s), so it
            // spans 3.5s of project. Moved out past the speed item it is 2s of project again;
            // moved back, 3.5s. The mapping never changed, so this is the move's own re-fit.
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: true, out var clip, out _);
            session.AddSpeedEffect(0, Ms(5_000));
            Assert.Equal(Ms(3_500), Live(session, clip).DurationTicks);

            Assert.Equal(Ms(4_000), session.MoveItem(clip.Id, Ms(4_000)));
            Assert.Equal(Ms(6_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
            Assert.Empty(session.Project.Validate());

            Assert.Equal(-Ms(4_000), session.MoveItem(clip.Id, -Ms(4_000)));
            Assert.Equal(Ms(2_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(3_500), Live(session, clip).DurationTicks);

            // the same inside a drag, step by step
            using (var gesture = session.BeginGesture("Move"))
            {
                session.MoveItem(clip.Id, Ms(2_000)); // start 4s: output 2s + 2s = 4s = project 6.5s
                Assert.Equal(Ms(2_500), Live(session, clip).DurationTicks);
                session.MoveItem(clip.Id, Ms(2_000));
                Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
                gesture.Commit();
            }
            Assert.Equal(Ms(6_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);

            // a plain clip's project span is untouched by the same move
            session.SetSpeedWarpExempt(clip.Id, false);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
            session.MoveItem(clip.Id, -Ms(4_000));
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
        }

        [Fact]
        public void A_moved_exempt_clip_stops_short_of_the_next_clip_on_the_row()
        {
            var seed = NewSession(Ms(6_000), Ms(2_000), exempt: true, out var clip, out var audioTrack);
            var project = seed.Project;
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audioTrack.Id,
                TimelineStartTicks = Ms(3_500),
                DurationTicks = Ms(1_000),
                Content = new MediaContent { SourceId = project.Sources[0].Id, StreamIndex = 0 },
            });
            var session = new EditorSession(project, null, save => save());
            session.AddSpeedEffect(0, Ms(3_000)); // 2x over [0, 3s); the clip at 6s is unwarped

            // moved to 1s its 2s of real time reach output 2.5s = project 4s, over the
            // neighbour at 3.5s: it is end-trimmed to 2.5s of project instead
            Assert.Equal(-Ms(5_000), session.MoveItem(clip.Id, -Ms(5_000)));
            Assert.Equal(Ms(1_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(2_500), Live(session, clip).DurationTicks);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void Trimming_the_start_of_an_exempt_clip_moves_its_in_point_by_output_time()
        {
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: true, out var clip, out _);
            session.AddSpeedEffect(0, Ms(10_000)); // clip is [2s, 6s) now, 2s of real time

            // 2s of project off the front is 1s of output: the in-point advances one second
            var applied = session.TrimItemStart(clip.Id, Ms(2_000));
            Assert.Equal(Ms(2_000), applied);
            var live = Live(session, clip);
            Assert.Equal(Ms(4_000), live.TimelineStartTicks);
            Assert.Equal(Ms(2_000), live.DurationTicks);
            Assert.Equal(Ms(1_000), ((MediaContent)live.Content).SourceInTicks);
        }

        [Fact]
        public void Trimming_the_end_of_an_exempt_clip_is_bounded_by_its_source_in_output_time()
        {
            // 3s of source; the clip plays 2s of it over project [2s, 6s) under 2x. The
            // remaining second of source is one output second, which under 2x is 2s of project.
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: true, out var clip, out _, streamDurationTicks: Ms(3_000));
            session.AddSpeedEffect(0, Ms(10_000));
            Assert.Equal(Ms(4_000), Live(session, clip).DurationTicks);

            var applied = session.TrimItemEnd(clip.Id, Ms(10_000));
            Assert.Equal(Ms(2_000), applied);
            Assert.Equal(Ms(6_000), Live(session, clip).DurationTicks);
        }

        [Fact]
        public void Splitting_an_exempt_clip_advances_the_right_half_by_output_time()
        {
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: true, out var clip, out _);
            session.AddSpeedEffect(0, Ms(10_000)); // [2s, 6s)

            Assert.True(session.SplitItemAt(clip.Id, Ms(4_000)));
            var halves = session.Project.Items.Where(i => i.Content is MediaContent).OrderBy(i => i.TimelineStartTicks).ToList();
            Assert.Equal(2, halves.Count);
            Assert.Equal(Ms(4_000), halves[1].TimelineStartTicks);
            // 2s of project under 2x is 1s of output, so the right half starts 1s into the source
            Assert.Equal(Ms(1_000), ((MediaContent)halves[1].Content).SourceInTicks);
            Assert.True(((MediaContent)halves[1].Content).SpeedWarpExempt);
        }

        [Fact]
        public void CurrentWarp_follows_the_project()
        {
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: false, out _, out _);
            Assert.True(session.CurrentWarp.IsIdentity);
            Assert.Same(session.CurrentWarp, session.CurrentWarp);

            session.AddSpeedEffect(0, Ms(10_000));
            Assert.False(session.CurrentWarp.IsIdentity);
            Assert.Equal(Ms(5_000), session.CurrentWarp.ToOutput(Ms(10_000)));
        }
    }
}
