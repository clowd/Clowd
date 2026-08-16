using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Clowd.VideoSDK.Render;
using FFmpeg.AutoGen.Abstractions;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // End-to-end render loop: Project → RenderJob (CPU backend) → probe/decode the produced mp4.
    // Real-encode tests skip when the FFmpeg natives are absent (same resolver as EncoderTests);
    // validation-path tests run everywhere.
    public class RenderJobTests : IDisposable
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
            string path = Path.Combine(Path.GetTempPath(), $"clowd-renderjob-test-{Guid.NewGuid():N}.mp4");
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

        /// <summary>1s fixture with a 440 Hz sine audio stream (index 1), reused as an audio
        /// source for render projects.</summary>
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

        /// <summary>Encodes one second of solid colour per entry (BGRA, e.g. 0xFFFF0000 = blue),
        /// so a decoded frame identifies the source second it came from.</summary>
        private string EncodeColorFixture(params uint[] secondsBgra)
        {
            string path = TempMp4();
            using var writer = new Mp4Writer(path, new Mp4WriterOptions
            {
                Width = W,
                Height = H,
                FpsNum = Fps,
                FpsDen = 1,
            });

            var pixels = new uint[W * H];
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                for (int s = 0; s < secondsBgra.Length; s++)
                {
                    Array.Fill(pixels, secondsBgra[s]);
                    for (int n = 0; n < Fps; n++)
                        writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), W * 4, W, H, s * Fps + n);
                }
            }
            finally
            {
                pin.Free();
            }

            writer.Finish();
            return path;
        }

        private static void AddMedia(Project project, Track track, Guid sourceId, int streamIndex,
            long startTicks, long durationTicks, long sourceInTicks)
        {
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new MediaContent
                {
                    SourceId = sourceId,
                    StreamIndex = streamIndex,
                    SourceInTicks = sourceInTicks,
                },
            });
        }

        private static unsafe (AVPixelFormat PixelFormat, int AudioStreams, int AudioSampleRate) ProbeRaw(string path)
        {
            AVFormatContext* fmt = null;
            int err = ffmpeg.avformat_open_input(&fmt, path, null, null);
            if (err < 0)
                throw new InvalidOperationException($"open failed: {FFmpegLoader.ErrorToString(err)}");
            try
            {
                err = ffmpeg.avformat_find_stream_info(fmt, null);
                if (err < 0)
                    throw new InvalidOperationException($"stream info failed: {FFmpegLoader.ErrorToString(err)}");

                var pixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
                int audioStreams = 0, sampleRate = 0;
                for (int i = 0; i < fmt->nb_streams; i++)
                {
                    var par = fmt->streams[i]->codecpar;
                    if (par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        pixFmt = (AVPixelFormat)par->format;
                    }
                    else if (par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                    {
                        audioStreams++;
                        sampleRate = par->sample_rate;
                    }
                }
                return (pixFmt, audioStreams, sampleRate);
            }
            finally
            {
                ffmpeg.avformat_close_input(&fmt);
            }
        }

        /// <summary>Reads the centre pixel (BGRA) of output frame <paramref name="frame"/> by
        /// decoding the rendered file back through the SDK's own sequential source.</summary>
        private static byte[] CentrePixelOfFrame(string path, int frame)
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

        private sealed class InlineProgress : IProgress<double>
        {
            private readonly Action<double> _handler;

            public InlineProgress(Action<double> handler) => _handler = handler;

            public void Report(double value) => _handler(value);
        }

        // ------------------------------------------------------------------------------- tests

        [Fact]
        public void Renders_two_solid_items_back_to_back()
        {
            RequireFFmpeg();

            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddSolid(project, track, 0, Second, "#FFFF0000");      // red   [0, 1s)
            AddSolid(project, track, Second, Second, "#FF0000FF"); // blue  [1s, 2s)

            string path = TempMp4();
            var reports = new List<double>();
            var result = RenderJob.Run(project, path,
                new RenderJobOptions { PreferGpu = false },
                new InlineProgress(reports.Add));

            Assert.Equal(RenderOutcome.Completed, result.Outcome);
            Assert.Equal("CPU", result.SurfaceBackend);
            Assert.Equal(2L * Fps, result.VideoFrames);
            Assert.True(File.Exists(path));
            Assert.Equal(new FileInfo(path).Length, result.OutputBytes);
            Assert.True(result.OutputBytes > 0);

            // progress: starts at 0, ends at 100, never goes backwards
            Assert.True(reports.Count >= 2);
            Assert.Equal(0, reports[0]);
            Assert.Equal(100, reports[^1]);
            for (int i = 1; i < reports.Count; i++)
                Assert.True(reports[i] >= reports[i - 1], "progress went backwards");

            var probe = MediaProbe.ProbeDetailed(path);
            Assert.False(probe.HasAudio);
            var v = Assert.Single(probe.VideoStreams);
            Assert.Equal(W, v.Width);
            Assert.Equal(H, v.Height);
            Assert.Equal((long)Fps * v.AvgFrameRateDen, (long)v.AvgFrameRateNum); // exactly 30/1
            Assert.InRange(probe.DurationTicks, 2 * Second - Second / 20, 2 * Second + Second / 20);

            var raw = ProbeRaw(path);
            Assert.Equal(AVPixelFormat.AV_PIX_FMT_YUV420P, raw.PixelFormat);
            Assert.Equal(0, raw.AudioStreams);

            // frame 15 (0.5s) is red, frame 45 (1.5s) is blue — BGRA order
            var red = CentrePixelOfFrame(path, 15);
            Assert.True(red[2] > 200 && red[0] < 60, $"expected red, got B={red[0]} G={red[1]} R={red[2]}");
            var blue = CentrePixelOfFrame(path, 45);
            Assert.True(blue[0] > 200 && blue[2] < 60, $"expected blue, got B={blue[0]} G={blue[1]} R={blue[2]}");
        }

        [Fact]
        public void Renders_clips_that_read_one_stream_out_of_source_order()
        {
            RequireFFmpeg();

            // source seconds: 0 = red, 1 = green, 2 = blue
            string fixturePath = EncodeColorFixture(0xFFFF0000, 0xFF00FF00, 0xFF0000FF);

            var project = NewProject();
            var sourceId = Guid.NewGuid();
            project.Sources.Add(new Source
            {
                Id = sourceId,
                Path = fixturePath,
                Streams = { new SourceStream { Index = 0, Kind = StreamKind.Video, Width = W, Height = H, AvgFrameRateNum = Fps, AvgFrameRateDen = 1 } },
            });

            // the timeline plays the last source second first — what a clip dragged behind an
            // earlier one (or swapped split halves) produces.
            var track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, sourceId, 0, 0, Second, 2 * Second);
            AddMedia(project, track, sourceId, 0, Second, Second, 0);

            string path = TempMp4();
            var result = RenderJob.Run(project, path, new RenderJobOptions { PreferGpu = false });

            Assert.Equal(RenderOutcome.Completed, result.Outcome);
            Assert.Equal(2L * Fps, result.VideoFrames);

            var first = CentrePixelOfFrame(path, 15);  // 0.5s — source second 2
            Assert.True(first[0] > 200 && first[2] < 60,
                $"expected blue, got B={first[0]} G={first[1]} R={first[2]}");
            var second = CentrePixelOfFrame(path, 45); // 1.5s — source second 0, read after it
            Assert.True(second[2] > 200 && second[0] < 60,
                $"expected red, got B={second[0]} G={second[1]} R={second[2]}");
        }

        [Fact]
        public void Frame_source_repositions_only_when_a_stream_is_read_backwards()
        {
            RequireFFmpeg();

            string fixturePath = EncodeColorFixture(0xFFFF0000, 0xFF00FF00, 0xFF0000FF);
            var project = NewProject();
            var sourceId = Guid.NewGuid();
            project.Sources.Add(new Source
            {
                Id = sourceId,
                Path = fixturePath,
                Streams = { new SourceStream { Index = 0, Kind = StreamKind.Video, Width = W, Height = H, AvgFrameRateNum = Fps, AvgFrameRateDen = 1 } },
            });

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(project, cache);

            for (int n = 0; n < 3 * Fps; n++)
                Assert.True(source.TryGetFrame(sourceId, 0, TimeBase.FrameIndexToTicks(n, Fps, 1), out _));
            Assert.Equal(0, source.RepositionCount); // forward playback decodes the file once

            Assert.True(source.TryGetFrame(sourceId, 0, 0, out var rewound));
            Assert.Equal(1, source.RepositionCount);
            Assert.Equal(0, rewound.PtsTicks); // back at the first frame, not held at the end
        }

        [Fact]
        public void Renders_project_with_audio_interleaved()
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

            // 2s audio item over a 1s source: the tail zero-pads (EOF silence) rather than failing
            var audio = AddTrack(project, TrackKind.Audio);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audio.Id,
                TimelineStartTicks = 0,
                DurationTicks = 2 * Second,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 1, SourceInTicks = 0 },
            });

            string path = TempMp4();
            var result = RenderJob.Run(project, path, new RenderJobOptions { PreferGpu = false });

            Assert.Equal(RenderOutcome.Completed, result.Outcome);
            var probe = MediaProbe.ProbeDetailed(path);
            Assert.True(probe.HasAudio);
            Assert.InRange(probe.DurationTicks, 2 * Second - Second / 20, 2 * Second + Second / 8);

            var raw = ProbeRaw(path);
            Assert.Equal(1, raw.AudioStreams);
            Assert.Equal(Rate, raw.AudioSampleRate);
        }

        [Fact]
        public void Cancel_mid_render_deletes_partial_output()
        {
            RequireFFmpeg();

            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddSolid(project, track, 0, 10 * Second, "#FF4080C0"); // 300 frames — plenty to cancel into

            string path = TempMp4();
            using var cts = new CancellationTokenSource();
            var progress = new InlineProgress(p =>
            {
                if (p > 0)
                    cts.Cancel(); // cancel once the first frame has actually been encoded
            });

            var result = RenderJob.Run(project, path,
                new RenderJobOptions { PreferGpu = false }, progress, cts.Token);

            Assert.Equal(RenderOutcome.Cancelled, result.Outcome);
            Assert.Equal(0, result.OutputBytes);
            Assert.True(result.VideoFrames > 0, "cancel should land mid-render, not before it");
            Assert.True(result.VideoFrames < 300, "render ran to completion despite cancellation");
            Assert.False(File.Exists(path), "partial output must be deleted on cancellation");
        }

        [Fact]
        public void Invalid_project_is_rejected_before_any_output()
        {
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            var id = Guid.NewGuid();
            AddSolid(project, track, 0, Second, "#FF000000");
            AddSolid(project, track, Second, Second, "#FF000000");
            project.Items[0].Id = id;
            project.Items[1].Id = id; // duplicate ids fail validation

            string path = TempMp4();
            Assert.Throws<ArgumentException>(() => RenderJob.Run(project, path));
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void Empty_project_throws()
        {
            var project = NewProject();
            Assert.Throws<InvalidOperationException>(() => RenderJob.Run(project, TempMp4()));
        }
    }
}
