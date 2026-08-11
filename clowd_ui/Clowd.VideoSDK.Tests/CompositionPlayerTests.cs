using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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

            // live transform edit (drag the PiP): mapping unchanged, decoders must keep running
            item.Transform = new Transform { X = 0.25, Y = 0.25, Scale = 0.4 };
            player.UpdateProject(project);

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
            player.UpdateProject(project);
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
            player.UpdateProject(project);

            Assert.Equal(2, player.DecoderOpenCount);
            Assert.True(WaitUntil(() => LatestPts(player, cache, sourceId) is not null, 4000),
                "no frame surfaced after relinking the source");
        }
    }
}
