using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Clowd.VideoSDK.Render;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // RenderJob under a speed warp: the output frame grid runs in output time (frame count from
    // TimeWarp.OutputDurationTicks, each frame composed at ToProject of its instant), explicit
    // schedules are output-time instants, and the audio stream is warped to match. Skips when
    // the FFmpeg natives are absent, like RenderJobTests.
    public class RenderWarpTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30;
        private const int Rate = 48000;
        private const long Second = 10_000_000;

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

        // ----------------------------------------------------------------------------- helpers

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
            string path = Path.Combine(Path.GetTempPath(), $"clowd-renderwarp-test-{Guid.NewGuid():N}.mp4");
            _tempFiles.Add(path);
            return path;
        }

        private static Project NewProject() => new Project
        {
            Output = new OutputSettings
            {
                WidthPx = W,
                HeightPx = H,
                FpsNum = Fps,
                FpsDen = 1,
                SampleRate = Rate,
            },
        };

        private static Track AddTrack(Project project, TrackKind kind)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = kind, Order = project.Tracks.Count };
            project.Tracks.Add(track);
            return track;
        }

        private static void AddSolid(Project project, Track track, long startTicks, long durationTicks, string color)
        {
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new SolidContent { Color = color },
            });
        }

        private static void AddSpeedItem(Project project, long startTicks, long durationTicks, double factor)
        {
            var track = AddTrack(project, TrackKind.Effect);
            track.Name = "Speed";
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new SpeedContent { Factor = factor },
            });
        }

        /// <summary>Red for the first project second, blue for the second — a decoded frame's
        /// color identifies the project instant it was composed at.</summary>
        private static Project RedThenBlueProject()
        {
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddSolid(project, track, 0, Second, "#FFFF0000");
            AddSolid(project, track, Second, Second, "#FF0000FF");
            return project;
        }

        /// <summary>1s fixture with a 440 Hz sine audio stream (index 1).</summary>
        private string EncodeAudioFixture()
        {
            string path = TempMp4();
            using var writer = new Mp4Writer(path, new Mp4WriterOptions
            {
                Width = W,
                Height = H,
                FpsNum = Fps,
                FpsDen = 1,
                Audio = new Mp4AudioOptions { SampleRate = Rate, Channels = 2 },
            });

            var bgra = new byte[W * H * 4];
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                for (int n = 0; n < Fps; n++)
                    writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), W * 4, W, H, n);
            }
            finally
            {
                pin.Free();
            }

            var buf = new float[Rate * 2];
            for (int i = 0; i < Rate; i++)
            {
                float s = 0.25f * MathF.Sin(2f * MathF.PI * 440f * i / Rate);
                buf[i * 2] = s;
                buf[i * 2 + 1] = s;
            }
            writer.SubmitAudioSamples(buf, Rate);
            writer.Finish();
            return path;
        }

        /// <summary>Reads the center pixel (BGRA) of the output frame covering
        /// <paramref name="frame"/>'s grid instant, through the SDK's own sequential source.</summary>
        private static byte[] CenterPixelOfFrame(string path, int frame)
        {
            var project = NewProject();
            var sourceId = Guid.NewGuid();
            project.Sources.Add(new Source
            {
                Id = sourceId,
                Path = path,
                Streams = { new SourceStream { Index = 0, Kind = StreamKind.Video, Width = W, Height = H, AvgFrameRateNum = Fps, AvgFrameRateDen = 1 } },
            });

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(project, cache);

            Assert.True(source.TryGetFrame(sourceId, 0, TimeBase.FrameIndexToTicks(frame, Fps, 1), out var frameRef));
            var native = Marshal.AllocHGlobal(4);
            try
            {
                var info = new SkiaSharp.SKImageInfo(1, 1, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                Assert.True(frameRef.Image.ReadPixels(info, native, 4, W / 2, H / 2));
                var px = new byte[4];
                Marshal.Copy(native, px, 0, 4);
                return px;
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        private static void AssertRed(byte[] px) =>
            Assert.True(px[2] > 200 && px[0] < 60, $"expected red, got B={px[0]} G={px[1]} R={px[2]}");

        private static void AssertBlue(byte[] px) =>
            Assert.True(px[0] > 200 && px[2] < 60, $"expected blue, got B={px[0]} G={px[1]} R={px[2]}");

        // ------------------------------------------------------------------------------- tests

        [Fact]
        public void Speed_two_halves_the_video_and_maps_frames_to_project_time()
        {
            RequireFFmpeg();

            var project = RedThenBlueProject();
            AddSpeedItem(project, 0, 2 * Second, 2.0);
            Assert.Empty(project.Validate());

            string path = TempMp4();
            var result = RenderJob.Run(project, path, new RenderJobOptions { PreferGpu = false });

            // 2s of project at 2x = 1s of output — frame count comes from the warped duration
            Assert.Equal(RenderOutcome.Completed, result.Outcome);
            Assert.Equal((long)Fps, result.VideoFrames);

            var probe = MediaProbe.ProbeDetailed(path);
            Assert.InRange(probe.DurationTicks, Second - Second / 20, Second + Second / 20);

            // output 0.25s shows project 0.5s (red); output 0.75s shows project 1.5s (blue)
            AssertRed(CenterPixelOfFrame(path, Fps / 4));
            AssertBlue(CenterPixelOfFrame(path, Fps * 3 / 4));
        }

        [Fact]
        public void Explicit_schedule_instants_are_output_time()
        {
            RequireFFmpeg();

            var project = RedThenBlueProject();
            AddSpeedItem(project, 0, 2 * Second, 2.0);

            // the warped CFR grid, handed in explicitly: same instants, same mapping
            var schedule = new long[Fps];
            for (int n = 0; n < Fps; n++)
                schedule[n] = TimeBase.FrameIndexToTicks(n, Fps, 1);

            string path = TempMp4();
            var result = RenderJob.Run(project, path, new RenderJobOptions
            {
                PreferGpu = false,
                FrameTimestampsTicks = schedule,
            });

            Assert.Equal(RenderOutcome.Completed, result.Outcome);
            Assert.Equal((long)Fps, result.VideoFrames);
            AssertRed(CenterPixelOfFrame(path, Fps / 4));
            AssertBlue(CenterPixelOfFrame(path, Fps * 3 / 4));
        }

        [Fact]
        public void Speed_two_halves_the_audio_stream_with_the_video()
        {
            RequireFFmpeg();

            string fixturePath = EncodeAudioFixture();
            var project = NewProject();
            var sourceId = Guid.NewGuid();
            project.Sources.Add(new Source
            {
                Id = sourceId,
                Path = fixturePath,
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video, Width = W, Height = H, AvgFrameRateNum = Fps, AvgFrameRateDen = 1 },
                    new SourceStream { Index = 1, Kind = StreamKind.Audio },
                },
            });

            var video = AddTrack(project, TrackKind.Video);
            AddSolid(project, video, 0, 2 * Second, "#FF208040");

            var audio = AddTrack(project, TrackKind.Audio);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audio.Id,
                TimelineStartTicks = 0,
                DurationTicks = 2 * Second,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 1, SourceInTicks = 0 },
            });

            AddSpeedItem(project, 0, 2 * Second, 2.0);
            Assert.Empty(project.Validate());

            string path = TempMp4();
            var result = RenderJob.Run(project, path, new RenderJobOptions { PreferGpu = false });

            Assert.Equal(RenderOutcome.Completed, result.Outcome);
            Assert.Equal((long)Fps, result.VideoFrames);

            var probe = MediaProbe.ProbeDetailed(path);
            Assert.True(probe.HasAudio);
            Assert.InRange(probe.DurationTicks, Second - Second / 20, Second + Second / 8);
        }

        [Fact]
        public void Hidden_speed_track_renders_unwarped()
        {
            RequireFFmpeg();

            var project = RedThenBlueProject();
            AddSpeedItem(project, 0, 2 * Second, 2.0);
            project.Tracks[^1].Hidden = true; // eye toggle disables the effect

            string path = TempMp4();
            var result = RenderJob.Run(project, path, new RenderJobOptions { PreferGpu = false });

            Assert.Equal(RenderOutcome.Completed, result.Outcome);
            Assert.Equal(2L * Fps, result.VideoFrames);
            AssertRed(CenterPixelOfFrame(path, Fps / 2));
            AssertBlue(CenterPixelOfFrame(path, Fps * 3 / 2));
        }
    }
}
