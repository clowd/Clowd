using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // CompositionPlayer + PlaybackFrameSource: the preview-side engine playing a Project through
    // the real demux/decode pipelines. Media tests skip when the FFmpeg natives are absent (same
    // resolver as EncoderTests/RenderJobTests); transport tests on media-less projects run
    // everywhere. All timing assertions are polled with generous deadlines, never sleep-and-hope.
    public class CompositionPlayerTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30;
        private const long Second = 10_000_000;
        private const long FrameTicks = Second / Fps;

        private static bool FFmpegAvailable => FFmpegLoader.TryInitialize(FindFFmpegDirectory);

        private static string FindFFmpegDirectory()
        {
            string probeFile = OperatingSystem.IsWindows() ? "avcodec-61.dll" : "libavcodec.so.61";
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                foreach (var cfg in new[] { "release", "debug" })
                {
                    var candidate = Path.Combine(dir.FullName, "obs-express-rs", "target", cfg);
                    if (File.Exists(Path.Combine(candidate, probeFile)))
                        return candidate;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private static void RequireFFmpeg() =>
            Assert.SkipUnless(FFmpegAvailable,
                $"FFmpeg natives not found (set {FFmpegLoader.EnvVarName} or build obs-express-rs): {FFmpegLoader.FailureReason}");

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

        private string TempMp4()
        {
            string path = Path.Combine(Path.GetTempPath(), $"clowd-composition-player-test-{Guid.NewGuid():N}.mp4");
            _tempFiles.Add(path);
            return path;
        }

        /// <summary>Video-only fixture (no audio stream, so tests never touch an audio device).</summary>
        private string EncodeVideoFixture(int seconds)
        {
            string path = TempMp4();
            using var writer = new Mp4Writer(path, new Mp4WriterOptions
            {
                Width = W,
                Height = H,
                FpsNum = Fps,
                FpsDen = 1,
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

            writer.Finish();
            return path;
        }

        /// <summary>A/V fixture: video plus a steady stereo sine (audio stream 1). Audio playback
        /// tests pair it with <see cref="SilentOptions"/> so no real device is ever opened.</summary>
        private string EncodeAvFixture(int seconds, double freq = 440, float amplitude = 0.3f)
        {
            string path = TempMp4();
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
                float s = amplitude * (float)Math.Sin(2 * Math.PI * freq * i / 48000);
                buf[i * 2] = s;
                buf[i * 2 + 1] = s;
            }
            writer.SubmitAudioSamples(buf, total);
            writer.Finish();
            return path;
        }

        /// <summary>Software decode + silent audio output: playback runs on real timing (the
        /// silent output pulls the ring like a device) without touching audio hardware.</summary>
        private static VideoOpenOptions SilentOptions() => new VideoOpenOptions
        {
            EnableHardwareDecode = false,
            CreateAudioOutput = () => new SilentAudioOutput(),
        };

        private static Project NewProject() => new Project
        {
            Output = new OutputSettings { WidthPx = W, HeightPx = H, FpsNum = Fps, FpsDen = 1, SampleRate = 48000 },
        };

        private static Track AddVideoTrack(Project project)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = project.Tracks.Count };
            project.Tracks.Add(track);
            return track;
        }

        private static Guid AddVideoSource(Project project, string path)
        {
            var id = Guid.NewGuid();
            project.Sources.Add(new Source
            {
                Id = id,
                Path = path,
                Streams = { new SourceStream { Index = 0, Kind = StreamKind.Video, Width = W, Height = H, AvgFrameRateNum = Fps, AvgFrameRateDen = 1 } },
            });
            return id;
        }

        private static Guid AddAvSource(Project project, string path)
        {
            var id = AddVideoSource(project, path);
            project.Sources[^1].Streams.Add(new SourceStream { Index = 1, Kind = StreamKind.Audio });
            return id;
        }

        private static Item AddAudioItem(Project project, Guid sourceId, long tlStart, long duration,
            long srcIn = 0)
        {
            var track = new Track
            {
                Id = Guid.NewGuid(),
                Kind = TrackKind.Audio,
                Order = project.Tracks.Count,
            };
            project.Tracks.Add(track);
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = tlStart,
                DurationTicks = duration,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 1, SourceInTicks = srcIn },
            };
            project.Items.Add(item);
            return item;
        }

        private static Item AddMediaItem(Project project, Track track, Guid sourceId,
            long tlStart, long duration, long srcIn)
        {
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = tlStart,
                DurationTicks = duration,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = srcIn },
            };
            project.Items.Add(item);
            return item;
        }

        private static Project SolidProject(long durationTicks)
        {
            var project = NewProject();
            var track = AddVideoTrack(project);
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

        // ------------------------------------------------------------------------------ helpers

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

        /// <summary>Pumps the frame source and returns the latest surfaced pts for the stream,
        /// exactly as the preview draw-op would (Pump then TryGetFrame at the player position).</summary>
        private static long? LatestPts(CompositionPlayer player, FrameTextureCache cache, Guid sourceId)
        {
            if (!player.TryGetFrameSource(out var source, out long tl))
                return null;
            source.Pump(cache);
            return source.TryGetFrame(sourceId, 0, tl, out var frame) ? frame.PtsTicks : null;
        }

        // -------------------------------------------------------------------- transport (no ffmpeg)

        [Fact]
        public async Task Media_less_project_plays_to_the_end_and_restarts()
        {
            var project = SolidProject(Second); // 1s of solid color — no decode pipelines at all
            using var player = new CompositionPlayer();
            await player.OpenAsync(project);

            Assert.Equal(PlayerState.Paused, player.State);
            Assert.Equal(new TimeSpan(Second), player.Duration);
            Assert.True(player.TryGetFrameSource(out var source, out _));
            Assert.NotNull(source);

            player.Play();
            Assert.True(WaitUntil(() => player.State == PlayerState.Ended, 5000),
                $"did not end (state={player.State}, pos={player.Position})");
            Assert.Equal(player.Duration, player.Position);

            // Play from Ended rewinds and restarts
            player.Play();
            Assert.True(WaitUntil(
                () => player.State == PlayerState.Playing && player.Position < player.Duration, 5000));
        }

        [Fact]
        public async Task Pause_freezes_the_position()
        {
            var project = SolidProject(10 * Second);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project);

            player.Play();
            Assert.True(WaitUntil(() => player.Position > TimeSpan.Zero, 3000));
            player.Pause();

            var frozen = player.Position;
            Thread.Sleep(300);
            Assert.Equal(frozen, player.Position);
            Assert.Equal(PlayerState.Paused, player.State);
        }

        // ------------------------------------------------------------------------ media playback

        [Fact]
        public async Task Frames_advance_monotonically_while_playing_and_freeze_on_pause()
        {
            RequireFFmpeg();

            string fixture = EncodeVideoFixture(3);
            var project = NewProject();
            var sourceId = AddVideoSource(project, fixture);
            AddMediaItem(project, AddVideoTrack(project), sourceId, 0, 3 * Second, 0);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, new VideoOpenOptions { EnableHardwareDecode = false });

            player.Play();

            var samples = new List<long>();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1200)
            {
                var pts = LatestPts(player, cache, sourceId);
                if (pts is { } p && (samples.Count == 0 || samples[^1] != p))
                    samples.Add(p);
                Thread.Sleep(20);
            }

            Assert.True(samples.Count >= 3, $"expected several distinct frames, got {samples.Count}");
            for (int i = 1; i < samples.Count; i++)
                Assert.True(samples[i] > samples[i - 1],
                    $"pts went backwards while playing: {samples[i - 1]} -> {samples[i]}");

            player.Pause();
            Thread.Sleep(150); // let any in-flight present drain
            var frozenPts = LatestPts(player, cache, sourceId);
            var frozenPos = player.Position;
            Thread.Sleep(300);
            Assert.Equal(frozenPts, LatestPts(player, cache, sourceId));
            Assert.Equal(frozenPos, player.Position);
        }

        [Fact]
        public async Task Seek_lands_within_one_frame()
        {
            RequireFFmpeg();

            string fixture = EncodeVideoFixture(3);
            var project = NewProject();
            var sourceId = AddVideoSource(project, fixture);
            AddMediaItem(project, AddVideoTrack(project), sourceId, 0, 3 * Second, 0);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, new VideoOpenOptions { EnableHardwareDecode = false });

            long target = Second + Second / 2; // 1.5s, timeline == source for this project
            await player.SeekAsync(new TimeSpan(target), SeekMode.Exact);

            Assert.True(WaitUntil(() =>
            {
                var pts = LatestPts(player, cache, sourceId);
                return pts is { } p && Math.Abs(p - target) <= FrameTicks + Second / 1000;
            }, 4000), $"seek did not land: pts={LatestPts(player, cache, sourceId)}, target={target}");

            // the paused position rebases onto the presented frame — within one frame of target
            Assert.True(Math.Abs(player.Position.Ticks - target) <= FrameTicks + Second / 1000,
                $"position {player.Position.Ticks} not within a frame of {target}");
        }

        [Fact]
        public async Task Cut_never_surfaces_source_frames_inside_the_cut()
        {
            RequireFFmpeg();

            string fixture = EncodeVideoFixture(3);
            var project = NewProject();
            var sourceId = AddVideoSource(project, fixture);
            var track = AddVideoTrack(project);
            // A: timeline [0, 0.5s) ← source [0, 0.5s); B: timeline [0.5s, 2s) ← source [1s, 2.5s)
            // The source span [0.5s, 1s) is cut out.
            AddMediaItem(project, track, sourceId, 0, Second / 2, 0);
            AddMediaItem(project, track, sourceId, Second / 2, Second + Second / 2, Second);
            long cutStart = Second / 2, cutEnd = Second;

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, new VideoOpenOptions { EnableHardwareDecode = false });

            await player.SeekAsync(new TimeSpan(3 * Second / 10), SeekMode.Exact); // 0.3s, before the seam
            player.Play();

            // sample continuously across the seam and beyond; collect every surfaced pts
            var seen = new List<long>();
            var sw = Stopwatch.StartNew();
            bool crossed = false;
            while (sw.ElapsedMilliseconds < 8000)
            {
                var pts = LatestPts(player, cache, sourceId);
                if (pts is { } p)
                {
                    if (seen.Count == 0 || seen[^1] != p)
                        seen.Add(p);
                    if (p >= cutEnd + Second / 4)
                    {
                        crossed = true; // sampled well past the seam
                        break;
                    }
                }

                if (player.State == PlayerState.Ended)
                    break;
                Thread.Sleep(10);
            }

            Assert.True(crossed, $"never crossed the seam; last pts={(seen.Count > 0 ? seen[^1] : -1)}, pos={player.Position}");
            foreach (var pts in seen)
                Assert.False(pts >= cutStart && pts < cutEnd,
                    $"surfaced a frame from inside the cut: pts={pts} in [{cutStart}, {cutEnd})");
        }

        // --------------------------------------------------------------------------- live edits

        [Fact]
        public async Task UpdateProject_with_transform_change_does_not_reopen_decoders()
        {
            RequireFFmpeg();

            string fixture = EncodeVideoFixture(3);
            var project = NewProject();
            var sourceId = AddVideoSource(project, fixture);
            var item = AddMediaItem(project, AddVideoTrack(project), sourceId, 0, 3 * Second, 0);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, new VideoOpenOptions { EnableHardwareDecode = false });

            int opens = player.DecoderOpenCount;
            Assert.Equal(1, opens); // one video pipeline, no audio

            player.Play();
            Assert.True(WaitUntil(() => LatestPts(player, cache, sourceId) is not null, 4000));

            // live transform edit (drag the PiP): mapping unchanged, decoders must keep running.
            // Same stream set ⇒ the swap is synchronous and the returned task already completed.
            item.Transform = new Transform { X = 0.25, Y = 0.25, Scale = 0.4 };
            var applied = player.UpdateProject(project);
            Assert.True(applied.IsCompleted, "same-signature update must apply synchronously");
            await applied;

            Assert.Equal(opens, player.DecoderOpenCount);
            Assert.Equal(PlayerState.Playing, player.State);

            // frames still advance after the update
            var before = LatestPts(player, cache, sourceId);
            Assert.True(WaitUntil(() =>
            {
                var now = LatestPts(player, cache, sourceId);
                return now is { } n && before is { } b && n > b;
            }, 4000), "frames stopped advancing after a transform-only UpdateProject");

            // a timeline-structure edit (trim) also keeps the pipelines: only the mapping swaps
            player.Pause();
            item.DurationTicks = Second;
            await player.UpdateProject(project);
            Assert.Equal(opens, player.DecoderOpenCount);
            Assert.Equal(new TimeSpan(Second), player.Duration);
        }

        [Fact]
        public async Task UpdateProject_with_changed_source_file_rebuilds_pipelines()
        {
            RequireFFmpeg();

            string fixtureA = EncodeVideoFixture(2);
            string fixtureB = EncodeVideoFixture(2);
            var project = NewProject();
            var sourceId = AddVideoSource(project, fixtureA);
            AddMediaItem(project, AddVideoTrack(project), sourceId, 0, 2 * Second, 0);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, new VideoOpenOptions { EnableHardwareDecode = false });
            Assert.Equal(1, player.DecoderOpenCount);

            project.Sources[0].Path = fixtureB; // relink → the pipeline set must rebuild
            await player.UpdateProject(project); // rebuild runs on a background task; await it

            Assert.Equal(2, player.DecoderOpenCount);
            Assert.True(WaitUntil(() => LatestPts(player, cache, sourceId) is not null, 4000),
                "no frame surfaced after relinking the source");
        }

        // ------------------------------------------------------------------------ failure paths

        [Fact]
        public async Task Failed_open_releases_every_native_resource_including_file_handles()
        {
            RequireFFmpeg();

            // Two sources: the first opens fine (its demuxer holds an OS handle on the mp4),
            // the second does not exist, so BuildPipelines throws part-way through. The failed
            // build must dispose everything already constructed — a leaked AVFormatContext
            // keeps the first file locked for the process lifetime on Windows.
            string fixture = EncodeVideoFixture(2);
            var project = NewProject();
            var goodId = AddVideoSource(project, fixture);
            var missingId = AddVideoSource(project,
                Path.Combine(Path.GetTempPath(), $"clowd-missing-{Guid.NewGuid():N}.mp4"));
            var track = AddVideoTrack(project);
            AddMediaItem(project, track, goodId, 0, Second, 0);
            AddMediaItem(project, track, missingId, Second, Second, 0);

            using var player = new CompositionPlayer();
            await Assert.ThrowsAnyAsync<Exception>(
                () => player.OpenAsync(project, new VideoOpenOptions { EnableHardwareDecode = false }));
            Assert.Equal(PlayerState.Failed, player.State);
            Assert.NotNull(player.LastError);

            // proves the handle is released: File.Delete throws IOException on a locked file
            File.Delete(fixture);
            Assert.False(File.Exists(fixture));
        }

        [Fact]
        public async Task Failed_reopen_lands_in_failed_state_without_throwing_and_releases_the_old_pipelines()
        {
            RequireFFmpeg();

            string fixture = EncodeVideoFixture(2);
            var project = NewProject();
            var sourceId = AddVideoSource(project, fixture);
            AddMediaItem(project, AddVideoTrack(project), sourceId, 0, 2 * Second, 0);

            using var player = new CompositionPlayer();
            await player.OpenAsync(project, new VideoOpenOptions { EnableHardwareDecode = false });
            Assert.Equal(PlayerState.Paused, player.State);

            // relink to a missing file: signature changes → background rebuild → the rebuild
            // fails. The task must complete WITHOUT faulting (the error channel is the Failed
            // state + LastError, never an exception escaping into the caller's thread).
            project.Sources[0].Path = Path.Combine(Path.GetTempPath(), $"clowd-missing-{Guid.NewGuid():N}.mp4");
            await player.UpdateProject(project);

            Assert.True(WaitUntil(() => player.State == PlayerState.Failed, 4000),
                $"expected Failed after a rebuild against a missing file, got {player.State}");
            Assert.NotNull(player.LastError);

            // the old pipeline set was torn down before the rebuild and the failed rebuild
            // must not leave anything holding the original fixture open.
            File.Delete(fixture);
            Assert.False(File.Exists(fixture));
        }

        [Fact]
        public async Task Seek_to_timeline_end_presents_the_last_kept_frame_not_a_trimmed_one()
        {
            RequireFFmpeg();

            // 3s recording trimmed to keep only source [0, 1.5s): seeking to the end of the
            // timeline must show the last kept frame, never the first frame past the out-point
            // (material a render does not contain).
            string fixture = EncodeVideoFixture(3);
            long keptTicks = Second + Second / 2;
            var project = NewProject();
            var sourceId = AddVideoSource(project, fixture);
            AddMediaItem(project, AddVideoTrack(project), sourceId, 0, keptTicks, 0);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, new VideoOpenOptions { EnableHardwareDecode = false });
            Assert.Equal(new TimeSpan(keptTicks), player.Duration);

            await player.SeekAsync(player.Duration, SeekMode.Exact);

            Assert.True(WaitUntil(() =>
            {
                var pts = LatestPts(player, cache, sourceId);
                return pts is { } p && p >= keptTicks - 2 * FrameTicks;
            }, 4000), $"end seek did not land: pts={LatestPts(player, cache, sourceId)}");

            var final = LatestPts(player, cache, sourceId);
            Assert.NotNull(final);
            Assert.True(final < keptTicks,
                $"presented a trimmed-away frame: pts={final} >= out-point {keptTicks}");
            Assert.True(final >= keptTicks - 2 * FrameTicks,
                $"end seek landed too early: pts={final}, out-point {keptTicks}");
        }

        // ------------------------------------------------------------------ mixed audio playback

        [Fact]
        public async Task Two_audio_track_project_plays_through_the_state_machine()
        {
            RequireFFmpeg();

            var project = NewProject();
            var a = AddAvSource(project, EncodeAvFixture(2, 440, 0.3f));
            var b = AddAvSource(project, EncodeAvFixture(2, 1000, 0.2f));
            AddMediaItem(project, AddVideoTrack(project), a, 0, 2 * Second, 0);
            AddAudioItem(project, a, 0, 2 * Second);
            AddAudioItem(project, b, 0, 2 * Second);

            using var player = new CompositionPlayer();
            await player.OpenAsync(project, SilentOptions());

            Assert.Equal(PlayerState.Paused, player.State);
            Assert.Equal(new TimeSpan(2 * Second), player.Duration);
            Assert.True(player.GetStatistics().HasAudio);

            // the mixed audio masters the clock: position advances against the (silent) device
            player.Play();
            Assert.True(WaitUntil(() => player.Position.Ticks > Second / 2, 5000),
                $"position did not advance under the audio master (pos={player.Position})");

            Assert.True(WaitUntil(() => player.State == PlayerState.Ended, 15000),
                $"did not end (state={player.State}, pos={player.Position})");
            Assert.Equal(player.Duration, player.Position);

            // Play from Ended rewinds — the mix worker seeks back to zero and produces again
            player.Play();
            Assert.True(WaitUntil(
                () => player.State == PlayerState.Playing && player.Position < player.Duration, 5000));
        }

        [Fact]
        public async Task Volume_and_transition_edits_ride_the_cheap_update_path()
        {
            RequireFFmpeg();

            var project = NewProject();
            var a = AddAvSource(project, EncodeAvFixture(2, 440, 0.3f));
            var b = AddAvSource(project, EncodeAvFixture(2, 1000, 0.2f));
            AddMediaItem(project, AddVideoTrack(project), a, 0, 2 * Second, 0);
            AddAudioItem(project, a, 0, 2 * Second);
            AddAudioItem(project, b, 0, 2 * Second);

            using var player = new CompositionPlayer();
            await player.OpenAsync(project, SilentOptions());

            int opens = player.DecoderOpenCount;
            Assert.Equal(2, opens); // one video pipeline + the mixed-audio pipeline (as one)

            // a volume edit swaps the mixer snapshot at a chunk boundary: same stream set, so the
            // update is synchronous and no decoder is reopened.
            var volumeEdit = Project.FromJson(project.ToJson());
            volumeEdit.Items[1].Volume = 0.25;
            var applied = player.UpdateProject(volumeEdit);
            Assert.True(applied.IsCompleted, "volume edit must take the synchronous cheap path");
            await applied;
            Assert.Equal(opens, player.DecoderOpenCount);

            // a transition (audio ramp) edit is just as cheap
            var fadeEdit = Project.FromJson(volumeEdit.ToJson());
            fadeEdit.Items[1].Entry = new Transition
            {
                Kind = TransitionKind.Fade,
                DurationTicks = Second / 2,
                Easing = TransitionEasing.Linear,
            };
            applied = player.UpdateProject(fadeEdit);
            Assert.True(applied.IsCompleted, "transition edit must take the synchronous cheap path");
            await applied;
            Assert.Equal(opens, player.DecoderOpenCount);
            Assert.True(player.GetStatistics().HasAudio);

            // muting a track changes the referenced stream set: that IS a rebuild
            var muteEdit = Project.FromJson(fadeEdit.ToJson());
            foreach (var track in muteEdit.Tracks)
            {
                if (track.Kind == TrackKind.Audio)
                    track.Muted = true;
            }
            await player.UpdateProject(muteEdit);
            Assert.True(player.DecoderOpenCount > opens);
            Assert.False(player.GetStatistics().HasAudio);
        }

        [Fact]
        public async Task Ended_fires_when_audio_ends_before_video()
        {
            RequireFFmpeg();

            // audio item [0, 1s), video runs to 3s: the mix worker hits EOF at 1s, the clock
            // detaches to the stopwatch, and video plays out to the whole-timeline end.
            var project = NewProject();
            var a = AddAvSource(project, EncodeAvFixture(3));
            AddMediaItem(project, AddVideoTrack(project), a, 0, 3 * Second, 0);
            AddAudioItem(project, a, 0, Second);

            using var player = new CompositionPlayer();
            await player.OpenAsync(project, SilentOptions());
            Assert.Equal(new TimeSpan(3 * Second), player.Duration);

            player.Play();
            Assert.True(WaitUntil(() => player.State == PlayerState.Ended, 20000),
                $"did not end (state={player.State}, pos={player.Position})");
            Assert.Equal(player.Duration, player.Position);
        }

        [Fact]
        public async Task Extending_audio_after_eof_while_playing_never_rewinds_the_clock()
        {
            RequireFFmpeg();

            // audio item [0, 1s), video to 3s: past 1s the mix worker EOFs and the clock detaches
            // to the stopwatch. An edit that extends the audio item (cheap path — stream set
            // unchanged) revives the worker; the player must re-base production on the playhead
            // instead of resuming at the old audio end and re-attaching the sink's frozen timing
            // (which snapped Position back to ~0.9s before the fix).
            var project = NewProject();
            var a = AddAvSource(project, EncodeAvFixture(3));
            AddMediaItem(project, AddVideoTrack(project), a, 0, 3 * Second, 0);
            AddAudioItem(project, a, 0, Second);

            using var player = new CompositionPlayer();
            await player.OpenAsync(project, SilentOptions());

            player.Play();
            Assert.True(WaitUntil(() => player.Position.Ticks > 2 * Second, 15000),
                $"did not play past the audio end (pos={player.Position})");

            int opens = player.DecoderOpenCount;
            var edited = Project.FromJson(project.ToJson());
            foreach (var item in edited.Items)
            {
                if (item.Content is MediaContent m && m.StreamIndex == 1)
                    item.DurationTicks = 3 * Second;
            }

            // generous slack under the floor: a legitimate re-attach may pull the clock back by
            // the sink latency + reattach tolerance, never by seconds toward the old audio end.
            var floor = player.Position - new TimeSpan(Second / 2);
            var applied = player.UpdateProject(edited);
            Assert.True(applied.IsCompleted, "audio-extend edit must take the synchronous cheap path");
            await applied;
            Assert.Equal(opens, player.DecoderOpenCount);

            // sample the clock across the revive + re-attach window: it must never snap back
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1200 && player.State == PlayerState.Playing)
            {
                Assert.True(player.Position >= floor,
                    $"clock rewound to {player.Position} (floor {floor})");
                Thread.Sleep(15);
            }

            Assert.True(WaitUntil(() => player.State == PlayerState.Ended, 20000),
                $"did not end (state={player.State}, pos={player.Position})");
            Assert.Equal(player.Duration, player.Position);
        }

        [Fact]
        public async Task Cut_seam_hop_does_not_flush_the_audio_mix()
        {
            RequireFFmpeg();

            // the seam hop is a video-pipeline correction: the timeline position is unchanged
            // across a video cut, so the timeline-domain mix is already producing the right
            // samples. Hopping the video must not container-seek the audio — that would dump
            // the ring's buffered lead (audible dropout at every cut) and leave the
            // decode-discard path that keeps preview seams sample-exact with render.
            var project = NewProject();
            var a = AddAvSource(project, EncodeAvFixture(3));
            var track = AddVideoTrack(project);
            // video: [0, 1s) ← src [0, 1s), then [1s, 2.5s) ← src [1.5s, 3s) — seam at 1s
            AddMediaItem(project, track, a, 0, Second, 0);
            AddMediaItem(project, track, a, Second, 3 * Second / 2, 3 * Second / 2);
            AddAudioItem(project, a, 0, 5 * Second / 2);

            using var player = new CompositionPlayer();
            await player.OpenAsync(project, SilentOptions());

            player.Play();
            Assert.True(WaitUntil(() => player.Position.Ticks > Second / 2, 5000),
                $"did not start playing (pos={player.Position})");

            int repositions = player.AudioRepositionCount;
            Assert.True(WaitUntil(() => player.Position.Ticks > 3 * Second / 2, 10000),
                $"did not cross the seam (pos={player.Position})");

            Assert.Equal(repositions, player.AudioRepositionCount);
            Assert.Equal(PlayerState.Playing, player.State);
        }

        [Fact]
        public async Task Ended_fires_when_audio_outlasts_video()
        {
            RequireFFmpeg();

            // video item [0, 1s), audio item [0, 2s): the timeline (and the audio master) run to
            // 2s — audio does not get cut short by video finishing first.
            var project = NewProject();
            var a = AddAvSource(project, EncodeAvFixture(3));
            AddMediaItem(project, AddVideoTrack(project), a, 0, Second, 0);
            AddAudioItem(project, a, 0, 2 * Second);

            using var player = new CompositionPlayer();
            await player.OpenAsync(project, SilentOptions());
            Assert.Equal(new TimeSpan(2 * Second), player.Duration);

            player.Play();
            Assert.True(WaitUntil(() => player.Position.Ticks > 3 * Second / 2, 15000),
                $"clock stopped with the video instead of following the audio (pos={player.Position})");
            Assert.True(WaitUntil(() => player.State == PlayerState.Ended, 15000),
                $"did not end (state={player.State}, pos={player.Position})");
            Assert.Equal(player.Duration, player.Position);
        }
    }
}
