using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // CompositionPlayer × TimeWarp: the clock runs linearly in warped output time and Position
    // (still the project-time contract for seeking/UI) is the warp's inverse of it, so speed
    // ramps glide continuously — no piecewise rate changes, no audio flushes at boundaries.
    // Most tests run on media-less (solid) projects — the clock alone drives the position, so
    // they run everywhere; the audio tests skip without the FFmpeg natives.
    public class CompositionPlayerWarpTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30;
        private const long Second = 10_000_000;

        private static bool FFmpegAvailable => TestFFmpeg.Available;


        private static void RequireFFmpeg() =>
            Assert.SkipUnless(FFmpegAvailable,
                TestFFmpeg.SkipReason);

        // ----------------------------------------------------------------------------- fixtures

        private readonly List<string> _tempFiles = new List<string>();

        public void Dispose()
        {
            foreach (var f in _tempFiles)
            {
                try { File.Delete(f); }
                catch { /* best effort */ }
            }
        }

        private string EncodeAvFixture(int seconds)
        {
            string path = Path.Combine(Path.GetTempPath(), $"clowd-player-warp-test-{Guid.NewGuid():N}.mp4");
            _tempFiles.Add(path);
            using var writer = new Mp4Writer(path, new Mp4WriterOptions
            {
                Width = W,
                Height = H,
                FpsNum = Fps,
                FpsDen = 1,
                Audio = new Mp4AudioOptions { SampleRate = 48000, Channels = 2 },
            });

            var bgra = new byte[W * H * 4];
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                for (int n = 0; n < seconds * Fps; n++)
                    writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), W * 4, W, H, n);
            }
            finally
            {
                pin.Free();
            }

            int total = 48000 * seconds;
            var buf = new float[total * 2];
            for (int i = 0; i < total; i++)
            {
                float s = 0.3f * (float)Math.Sin(2 * Math.PI * 440 * i / 48000);
                buf[i * 2] = s;
                buf[i * 2 + 1] = s;
            }
            writer.SubmitAudioSamples(buf, total);
            writer.Finish();
            return path;
        }

        private static VideoOpenOptions SilentOptions() => new VideoOpenOptions
        {
            EnableHardwareDecode = false,
            CreateAudioOutput = () => new SilentAudioOutput(),
        };

        private static Project SolidProject(long durationTicks)
        {
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = W, HeightPx = H, FpsNum = Fps, FpsDen = 1, SampleRate = 48000 },
            };
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0 };
            project.Tracks.Add(track);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = durationTicks,
                Content = new SolidContent { Color = "#FF336699" },
            });
            return project;
        }

        private static Item AddSpeedItem(Project project, long start, long duration, double factor,
            Transition entry = null, Transition exit = null)
        {
            var track = new Track
            {
                Id = Guid.NewGuid(),
                Kind = TrackKind.Effect,
                Name = "Speed",
                Order = project.Tracks.Count,
            };
            project.Tracks.Add(track);
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

        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                    return true;
                Thread.Sleep(15);
            }

            return condition();
        }

        // ---------------------------------------------------------------- duration + mapping API

        [Fact]
        public async Task Output_duration_and_mapping_reflect_the_warp()
        {
            var project = SolidProject(4 * Second);
            AddSpeedItem(project, 0, 4 * Second, 2.0);

            using var player = new CompositionPlayer();
            Assert.Equal(0, player.OutputDurationTicks); // nothing open: graceful zeros
            Assert.Equal(Second, player.ToOutputTicks(Second)); // pass-through before open

            await player.OpenAsync(project);

            // transport seeking/position stay project-time; only the readout domain warps
            Assert.Equal(new TimeSpan(4 * Second), player.Duration);
            Assert.Equal(2 * Second, player.OutputDurationTicks);
            Assert.Equal(0, player.ToOutputTicks(0));
            Assert.Equal(Second, player.ToOutputTicks(2 * Second));
        }

        [Fact]
        public async Task Identity_warp_passes_output_time_through()
        {
            var project = SolidProject(2 * Second);
            AddSpeedItem(project, 0, 2 * Second, 1.0); // a unity target warps nothing

            using var player = new CompositionPlayer();
            await player.OpenAsync(project);

            Assert.Equal(2 * Second, player.OutputDurationTicks);
            Assert.Equal(Second + 42, player.ToOutputTicks(Second + 42));
        }

        [Fact]
        public async Task Update_project_rebuilds_the_warp_on_the_cheap_path()
        {
            var project = SolidProject(4 * Second);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project);
            Assert.Equal(4 * Second, player.OutputDurationTicks);

            // adding a speed item references no new media stream: must stay synchronous
            var withSpeed = Project.FromJson(project.ToJson());
            var item = AddSpeedItem(withSpeed, 0, 4 * Second, 2.0);
            var applied = player.UpdateProject(withSpeed);
            Assert.True(applied.IsCompleted, "effect edit must take the synchronous cheap path");
            await applied;
            Assert.Equal(2 * Second, player.OutputDurationTicks);

            var faster = Project.FromJson(withSpeed.ToJson());
            ((SpeedContent)faster.Items[^1].Content).Factor = 4.0;
            await player.UpdateProject(faster);
            Assert.Equal(Second, player.OutputDurationTicks);

            // the eye toggle: a hidden effect track contributes nothing
            var hidden = Project.FromJson(faster.ToJson());
            foreach (var track in hidden.Tracks)
            {
                if (track.Kind == TrackKind.Effect)
                    track.Hidden = true;
            }
            await player.UpdateProject(hidden);
            Assert.Equal(4 * Second, player.OutputDurationTicks);
        }

        // --------------------------------------------------------------------- warp-driven rate

        [Fact]
        public async Task Speed_item_paces_the_clock_from_open()
        {
            var project = SolidProject(30 * Second);
            AddSpeedItem(project, 0, 20 * Second, 8.0);

            using var player = new CompositionPlayer();
            await player.OpenAsync(project);

            player.Play();
            var sw = Stopwatch.StartNew();
            Assert.True(WaitUntil(() => player.Position.Ticks >= 16 * Second, 8000),
                $"position only reached {player.Position} under an 8x speed item");
            player.Pause();

            // 16s of project time at 8x is 2s of wall time — realtime could not have
            Assert.True(sw.ElapsedMilliseconds < 6000,
                $"16s of timeline took {sw.ElapsedMilliseconds}ms under an 8x speed item");
        }

        [Fact]
        public async Task Crossing_a_segment_boundary_changes_the_pace()
        {
            var project = SolidProject(60 * Second);
            AddSpeedItem(project, 2 * Second, 58 * Second, 8.0);

            using var player = new CompositionPlayer();
            await player.OpenAsync(project);

            player.Play(); // 2s at 1x, then the mapping itself carries the position at 8x
            var sw = Stopwatch.StartNew();
            Assert.True(WaitUntil(() => player.Position.Ticks >= 18 * Second, 12000),
                $"position only reached {player.Position} after the 8x boundary");
            player.Pause();

            // 2s realtime + 16s at 8x ≈ 4s of wall time; 18s realtime would blow the bound
            Assert.True(sw.ElapsedMilliseconds < 9000,
                $"18s of timeline took {sw.ElapsedMilliseconds}ms across a 1x→8x boundary");
        }

        [Fact]
        public async Task Seeking_into_a_warped_region_paces_at_its_speed()
        {
            var project = SolidProject(60 * Second);
            AddSpeedItem(project, 30 * Second, 30 * Second, 8.0);

            using var player = new CompositionPlayer();
            await player.OpenAsync(project);

            await player.SeekAsync(new TimeSpan(32 * Second), SeekMode.Exact);
            player.Play();
            var sw = Stopwatch.StartNew();
            Assert.True(WaitUntil(() => player.Position.Ticks >= 46 * Second, 8000),
                $"position only reached {player.Position} inside the 8x region");
            player.Pause();

            // 14s of project time at 8x is 1.75s of wall time
            Assert.True(sw.ElapsedMilliseconds < 6000,
                $"14s of timeline took {sw.ElapsedMilliseconds}ms inside an 8x region");
        }

        [Fact]
        public async Task Warp_composes_with_the_user_playback_rate()
        {
            var project = SolidProject(60 * Second);
            AddSpeedItem(project, 0, 60 * Second, 2.0);

            using var player = new CompositionPlayer();
            await player.OpenAsync(project);

            player.PlaybackRate = 4.0; // combined 8x
            Assert.Equal(4.0, player.PlaybackRate); // the user rate never absorbs the warp

            player.Play();
            var sw = Stopwatch.StartNew();
            Assert.True(WaitUntil(() => player.Position.Ticks >= 16 * Second, 8000),
                $"position only reached {player.Position} at 4x user rate × 2x warp");
            player.Pause();

            Assert.True(sw.ElapsedMilliseconds < 6000,
                $"16s of timeline took {sw.ElapsedMilliseconds}ms at a combined 8x");
        }

        [Fact]
        public async Task Ramped_speed_item_plays_through_with_a_monotonic_clock()
        {
            // a linear 1→8→1 ramp pair rides entirely on the continuous mapping — the clock is
            // never rebased inside it, and the position may never move backwards
            var project = SolidProject(10 * Second);
            AddSpeedItem(project, 0, 10 * Second, 8.0,
                entry: new Transition { Kind = TransitionKind.Ramp, DurationTicks = 2 * Second, Easing = TransitionEasing.Linear },
                exit: new Transition { Kind = TransitionKind.Ramp, DurationTicks = 2 * Second, Easing = TransitionEasing.Linear });

            using var player = new CompositionPlayer();
            await player.OpenAsync(project);
            Assert.InRange(player.OutputDurationTicks, Second, 4 * Second); // warped well below 10s

            player.Play();
            var last = TimeSpan.Zero;
            var sw = Stopwatch.StartNew();
            while (player.State != PlayerState.Ended && sw.ElapsedMilliseconds < 20000)
            {
                var pos = player.Position;
                Assert.True(pos >= last, $"position went backwards across a ramp rebase: {last} -> {pos}");
                last = pos;
                Thread.Sleep(5);
            }

            Assert.Equal(PlayerState.Ended, player.State);
            Assert.Equal(player.Duration, player.Position);
        }

        // ----------------------------------------------------------------- smooth-warp mapping

        private sealed class FakeTime : IMonotonicTime
        {
            public TimeSpan Elapsed { get; set; }
        }

        [Fact]
        public void Position_follows_the_eased_curve_on_a_simulated_clock()
        {
            // the smooth-warp design in isolation: the clock advances linearly in output time
            // and the position is the warp's inverse of it. Sampled every 10ms of output time
            // through a linear-eased 1→8 entry ramp, the project position must ease with it —
            // strictly advancing (no plateaus) and never slowing down (a quantized piecewise
            // rate would sawtooth the deltas at every re-application).
            var project = SolidProject(10 * Second);
            AddSpeedItem(project, 0, 10 * Second, 8.0,
                entry: new Transition { Kind = TransitionKind.Ramp, DurationTicks = 4 * Second, Easing = TransitionEasing.Linear });
            var warp = TimeWarp.Build(project);

            var time = new FakeTime();
            var clock = new PlaybackClock(time);
            clock.Start();

            long step = Second / 100;
            long rampEndOutput = warp.ToOutput(4 * Second);
            long firstDelta = -1, lastDelta = -1, prev = 0, prevDelta = -1;
            for (long o = step; o <= rampEndOutput; o += step)
            {
                time.Elapsed = new TimeSpan(o);
                long p = warp.ToProject(clock.Position.Ticks);
                long delta = p - prev;
                Assert.True(delta > 0, $"position plateaued at output {o}: {prev} -> {p}");
                if (prevDelta >= 0)
                {
                    Assert.True(delta >= prevDelta - Second / 1000,
                        $"pacing fell back mid-ramp at output {o}: {prevDelta} -> {delta} ticks per step");
                }
                else
                {
                    firstDelta = delta;
                }

                prev = p;
                prevDelta = lastDelta = delta;
            }

            // the pacing swept the full range: ~1x at the ramp's foot, ~8x at its head
            Assert.InRange(firstDelta, step / 2, 2 * step);
            Assert.True(lastDelta > 5 * firstDelta,
                $"ramp never sped up: first delta {firstDelta}, last delta {lastDelta}");
        }

        [Fact]
        public async Task Warp_edit_preserves_the_project_position()
        {
            var project = SolidProject(8 * Second);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project);
            await player.SeekAsync(new TimeSpan(4 * Second), SeekMode.Exact);
            Assert.Equal(4 * Second, player.Position.Ticks);

            // the playhead's project instant is the stable anchor across the edit: the output
            // clock re-anchors underneath it (4s of project time = 2s of output time at 2x)
            var withSpeed = Project.FromJson(project.ToJson());
            AddSpeedItem(withSpeed, 0, 8 * Second, 2.0);
            await player.UpdateProject(withSpeed);
            Assert.InRange(player.Position.Ticks, 4 * Second - 1, 4 * Second + 1);
            Assert.Equal(2 * Second, player.ToOutputTicks(player.Position.Ticks));

            // and back off the warp: still anchored (round-trips are 1-tick consistent, so a
            // couple of edits may drift the anchor by single ticks, never more)
            var reverted = Project.FromJson(project.ToJson());
            await player.UpdateProject(reverted);
            Assert.InRange(player.Position.Ticks, 4 * Second - 2, 4 * Second + 2);
        }

        [Fact]
        public void Mapping_equality_ignores_unwarped_footage()
        {
            var a = SolidProject(10 * Second);
            AddSpeedItem(a, 2 * Second, 3 * Second, 2.0,
                entry: new Transition { Kind = TransitionKind.Ramp, DurationTicks = Second, Easing = TransitionEasing.CubicInOut });

            var same = Project.FromJson(a.ToJson());
            Assert.True(TimeWarp.Build(a).MappingEquals(TimeWarp.Build(same)));

            // edits that never move an output instant: unwarped footage growing/shrinking
            var longer = Project.FromJson(a.ToJson());
            foreach (var item in longer.Items)
            {
                if (item.Content is SolidContent)
                    item.DurationTicks = 20 * Second;
            }
            Assert.True(TimeWarp.Build(a).MappingEquals(TimeWarp.Build(longer)));

            // identity warps always agree, whatever the footage underneath
            Assert.True(TimeWarp.Build(SolidProject(Second)).MappingEquals(TimeWarp.Build(SolidProject(9 * Second))));

            // edits that bend time differently do not
            var faster = Project.FromJson(a.ToJson());
            ((SpeedContent)faster.Items[^1].Content).Factor = 3.0;
            Assert.False(TimeWarp.Build(a).MappingEquals(TimeWarp.Build(faster)));

            var eased = Project.FromJson(a.ToJson());
            eased.Items[^1].Entry.Easing = TransitionEasing.Linear;
            Assert.False(TimeWarp.Build(a).MappingEquals(TimeWarp.Build(eased)));

            var moved = Project.FromJson(a.ToJson());
            moved.Items[^1].TimelineStartTicks = 3 * Second;
            Assert.False(TimeWarp.Build(a).MappingEquals(TimeWarp.Build(moved)));
        }

        // ------------------------------------------------------------------- audio-master warp

        [Fact]
        public async Task Audio_master_glides_through_warp_boundaries()
        {
            RequireFFmpeg();

            // audio masters the clock, and the mix worker resamples straight through the 2x
            // span's boundaries — no flush, no timing rebase, no container seek anywhere after
            // playback starts. The clock must stay ~continuous through both boundaries and the
            // project must still play out to its (project-time) end.
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = W, HeightPx = H, FpsNum = Fps, FpsDen = 1, SampleRate = 48000 },
            };
            var sourceId = Guid.NewGuid();
            project.Sources.Add(new Source
            {
                Id = sourceId,
                Path = EncodeAvFixture(3),
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video, Width = W, Height = H, AvgFrameRateNum = Fps, AvgFrameRateDen = 1 },
                    new SourceStream { Index = 1, Kind = StreamKind.Audio },
                },
            });
            var video = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0 };
            var audio = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Order = 1 };
            project.Tracks.Add(video);
            project.Tracks.Add(audio);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = video.Id,
                TimelineStartTicks = 0,
                DurationTicks = 3 * Second,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0 },
            });
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audio.Id,
                TimelineStartTicks = 0,
                DurationTicks = 3 * Second,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 1 },
            });
            AddSpeedItem(project, Second, Second, 2.0);

            using var player = new CompositionPlayer();
            await player.OpenAsync(project, SilentOptions());
            Assert.True(player.GetStatistics().HasAudio);
            Assert.Equal(new TimeSpan(3 * Second), player.Duration);
            Assert.Equal(5 * Second / 2, player.OutputDurationTicks); // 1s + 1s@2x + 1s

            player.Play();
            Assert.True(WaitUntil(() => player.Position.Ticks >= 12 * Second / 10, 15000),
                $"position only reached {player.Position} inside the 2x span");
            int repositions = player.AudioRepositionCount;

            var tolerance = new TimeSpan(Second / 4); // audio-master attach jitter, never a real rewind
            var last = TimeSpan.Zero;
            var sw = Stopwatch.StartNew();
            while (player.State == PlayerState.Playing && sw.ElapsedMilliseconds < 25000)
            {
                var pos = player.Position;
                Assert.True(pos >= last - tolerance,
                    $"clock rewound across a warp boundary: {last} -> {pos}");
                if (pos > last)
                    last = pos;
                Thread.Sleep(10);
            }

            Assert.True(WaitUntil(() => player.State == PlayerState.Ended, 25000),
                $"did not end (state={player.State}, pos={player.Position})");
            Assert.Equal(player.Duration, player.Position);

            // the continuous design's core promise: leaving the span at 2s repositioned nothing —
            // the mix decoded straight through both boundaries.
            Assert.Equal(repositions, player.AudioRepositionCount);
        }
    }
}
