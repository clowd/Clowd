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
    [Collection("HardwareFrames")] // HardwareFrame.LiveCount is process-wide: no parallel producers
    public class RenderJobTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30;
        private const int Rate = 48000;
        private const long Second = 10_000_000;

        private static bool FFmpegAvailable => TestFFmpeg.Available;


        private static void RequireFFmpeg() =>
            Assert.SkipUnless(FFmpegAvailable,
                TestFFmpeg.SkipReason);

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

        private static Project NewProject(int width = W, int height = H) => new Project
        {
            Output = new OutputSettings
            {
                WidthPx = width,
                HeightPx = height,
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

        /// <summary>Encodes one second of solid color per entry (BGRA, e.g. 0xFFFF0000 = blue),
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

        /// <summary>Reads the center pixel (BGRA) of output frame <paramref name="frame"/> by
        /// decoding the rendered file back through the SDK's own sequential source.</summary>
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
            var red = CenterPixelOfFrame(path, 15);
            Assert.True(red[2] > 200 && red[0] < 60, $"expected red, got B={red[0]} G={red[1]} R={red[2]}");
            var blue = CenterPixelOfFrame(path, 45);
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

            var first = CenterPixelOfFrame(path, 15);  // 0.5s — source second 2
            Assert.True(first[0] > 200 && first[2] < 60,
                $"expected blue, got B={first[0]} G={first[1]} R={first[2]}");
            var second = CenterPixelOfFrame(path, 45); // 1.5s — source second 0, read after it
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

            Assert.Equal(RenderOutcome.Canceled, result.Outcome);
            Assert.Equal(0, result.OutputBytes);
            Assert.True(result.VideoFrames > 0, "cancel should land mid-render, not before it");
            Assert.True(result.VideoFrames < 300, "render ran to completion despite cancellation");
            Assert.False(File.Exists(path), "partial output must be deleted on cancellation");
        }

        // ------------------------------------------------------------- staged loop equivalence

        /// <summary>A project whose frames change every few frames and whose items sit off the
        /// pixel grid, so composition, readback and conversion all have something to get wrong:
        /// a full-canvas colour that switches at 0.5 s, a smaller off-centre item over it from
        /// 0.25 s, and a third one on a higher track for the last third.</summary>
        private static Project SyntheticProject(int width = W, int height = H)
        {
            var project = NewProject(width, height);
            var back = AddTrack(project, TrackKind.Video);
            var front = AddTrack(project, TrackKind.Video);
            AddSolid(project, back, 0, Second / 2, "#FFC03020");
            AddSolid(project, back, Second / 2, Second / 2, "#FF2040C0");
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = front.Id,
                TimelineStartTicks = Second / 4,
                DurationTicks = Second / 2,
                Content = new SolidContent { Color = "#FF30C060" },
                Transform = new Transform { X = 0.37, Y = 0.58, Scale = 0.41 },
            });
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = front.Id,
                TimelineStartTicks = 3 * Second / 4,
                DurationTicks = Second / 4,
                Content = new SolidContent { Color = "#80FFFFFF" },
                Transform = new Transform { X = 0.61, Y = 0.33, Scale = 0.53 },
            });
            return project;
        }

        /// <summary>Every frame of a rendered file as tightly packed BGRA, decoded through the
        /// SDK's own sequential source.</summary>
        private static List<byte[]> DecodeAllFrames(string path, int count, int width = W, int height = H)
        {
            var project = NewProject(width, height);
            var sourceId = Guid.NewGuid();
            project.Sources.Add(new Source
            {
                Id = sourceId,
                Path = path,
                Streams = { new SourceStream { Index = 0, Kind = StreamKind.Video, Width = width, Height = height, AvgFrameRateNum = Fps, AvgFrameRateDen = 1 } },
            });

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(project, cache);
            var frames = new List<byte[]>();
            var native = Marshal.AllocHGlobal(width * 4 * height);
            try
            {
                var info = new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                for (int n = 0; n < count; n++)
                {
                    Assert.True(source.TryGetFrame(sourceId, 0, TimeBase.FrameIndexToTicks(n, Fps, 1), out var frameRef));
                    Assert.True(frameRef.Image.ReadPixels(info, native, width * 4, 0, 0));
                    var pixels = new byte[width * 4 * height];
                    Marshal.Copy(native, pixels, 0, pixels.Length);
                    frames.Add(pixels);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
            return frames;
        }

        private static (int Worst, long Differing, double Mean, long BeyondFour, long Total) Compare(List<byte[]> a, List<byte[]> b)
        {
            Assert.Equal(a.Count, b.Count);
            int worst = 0;
            long differing = 0, beyondFour = 0, total = 0, sum = 0;
            for (int n = 0; n < a.Count; n++)
            {
                Assert.Equal(a[n].Length, b[n].Length);
                for (int i = 0; i < a[n].Length; i++)
                {
                    int d = Math.Abs(a[n][i] - b[n][i]);
                    worst = Math.Max(worst, d);
                    sum += d;
                    total++;
                    if (d != 0)
                        differing++;
                    if (d > 4)
                        beyondFour++;
                }
            }
            return (worst, differing, sum / (double)Math.Max(1, total), beyondFour, total);
        }

        [Fact]
        public void Staged_loop_matches_the_direct_compose_and_encode_path()
        {
            RequireFFmpeg();
            var project = SyntheticProject();
            int frames = Fps; // 1 s

            // the pipeline: compose stage -> readback ring -> convert stage -> encode stage
            string staged = TempMp4();
            var result = RenderJob.Run(project, staged, new RenderJobOptions { PreferGpu = false, Encoder = VideoEncoder.Software });
            Assert.Equal(RenderOutcome.Completed, result.Outcome);
            Assert.Equal(frames, result.VideoFrames);
            Assert.Equal(VideoEncoder.Software, result.Encoder);
            Assert.Equal("libx264", result.EncoderName);

            // the loop as it was: compose, TryReadPixels, and the writer's own BGRA conversion,
            // one frame at a time on one thread
            string direct = TempMp4();
            using (var factory = new CpuSurfaceFactory())
            using (var cache = new FrameTextureCache(factory))
            using (var source = new SequentialFrameSource(project, cache))
            using (var surface = factory.CreateSurface(W, H))
            using (var writer = new Mp4Writer(direct, new Mp4WriterOptions { Width = W, Height = H, FpsNum = Fps, FpsDen = 1 }))
            {
                var native = Marshal.AllocHGlobal(W * 4 * H);
                try
                {
                    for (int n = 0; n < frames; n++)
                    {
                        FrameComposer.Compose(project, TimeBase.FrameIndexToTicks(n, Fps, 1), source, surface.Canvas, W, H);
                        Assert.True(factory.TryReadPixels(surface, W, H, native, W * 4));
                        writer.SubmitVideoFrame(native, W * 4, W, H, n);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(native);
                }
                writer.Finish();
            }

            // identical encoder input and a deterministic encoder: identical files
            Assert.Equal(File.ReadAllBytes(direct), File.ReadAllBytes(staged));
            var (worst, differing, _, _, _) = Compare(DecodeAllFrames(direct, frames), DecodeAllFrames(staged, frames));
            Assert.True(worst == 0, $"decoded frames differ: worst {worst}, {differing} bytes");
        }

        [Fact]
        public void Gpu_render_matches_cpu_render()
        {
            RequireFFmpeg();
            var gpu = GpuSurfaceFactory.TryCreate(out var reason);
            Assert.SkipWhen(gpu == null, "GPU backend unavailable: " + reason);
            gpu.Dispose(); // only probing; RenderJob creates its own on the composer thread

            var project = SyntheticProject();
            string cpuPath = TempMp4(), gpuPath = TempMp4();
            var log = new List<string>();
            var cpu = RenderJob.Run(project, cpuPath, new RenderJobOptions { PreferGpu = false, Encoder = VideoEncoder.Software });
            var gpuResult = RenderJob.Run(project, gpuPath, new RenderJobOptions { PreferGpu = true, Encoder = VideoEncoder.Software, DiagnosticLog = log.Add });
            Assert.Equal(RenderOutcome.Completed, cpu.Outcome);
            Assert.Equal(RenderOutcome.Completed, gpuResult.Outcome);
            Assert.NotEqual("CPU", gpuResult.SurfaceBackend);
            if (OperatingSystem.IsWindows())
                Assert.Contains(log, line => line.Contains("Direct3D 12 copy-queue readback", StringComparison.Ordinal));

            // Flat colours whose edges sit off the pixel grid: the fills agree exactly, the
            // anti-aliased edge pixels differ between the GPU's and the CPU's coverage
            // computation, and x264 spreads those edge differences into neighbouring pixels.
            // The gate is the one BackgroundComposeTests.Gpu_matches_cpu_for_backgrounds uses:
            // a mean well under one level and few bytes beyond four — a misplaced, mistimed or
            // mis-converted frame moves whole regions and trips it by a wide margin.
            var (worst, differing, mean, beyondFour, total) = Compare(DecodeAllFrames(cpuPath, Fps), DecodeAllFrames(gpuPath, Fps));
            Assert.True(mean < 1.0 && beyondFour <= total * 3 / 100,
                $"GPU and CPU renders differ: worst {worst}, mean {mean:F3}, {differing} of {total} bytes, {beyondFour} beyond 4");
        }

        [Fact]
        public void Compose_stage_failure_propagates_and_deletes_partial_output()
        {
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = Guid.NewGuid();
            project.Sources.Add(new Source
            {
                Id = sourceId,
                Path = Path.Combine(Path.GetTempPath(), $"clowd-missing-{Guid.NewGuid():N}.mp4"),
                Streams = { new SourceStream { Index = 0, Kind = StreamKind.Video, Width = W, Height = H, AvgFrameRateNum = Fps, AvgFrameRateDen = 1 } },
            });
            var track = AddTrack(project, TrackKind.Video);
            AddSolid(project, track, 0, Second, "#FF102030");
            AddMedia(project, track, sourceId, 0, Second, Second, 0); // the second second opens a file that is not there

            string path = TempMp4();
            var ex = Assert.Throws<InvalidOperationException>(() =>
                RenderJob.Run(project, path, new RenderJobOptions { PreferGpu = false }));
            Assert.Contains("open", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path), "partial output must be deleted when a stage fails");
        }

        [Fact]
        public void Diagnostic_log_carries_the_timing_summary()
        {
            RequireFFmpeg();
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddSolid(project, track, 0, Second, "#FF335577");

            var log = new List<string>();
            var result = RenderJob.Run(project, TempMp4(), new RenderJobOptions { PreferGpu = false, DiagnosticLog = log.Add });
            Assert.Equal(RenderOutcome.Completed, result.Outcome);

            var summary = Assert.Single(log, line => line.StartsWith($"RenderJob: {Fps} frames in ", StringComparison.Ordinal));
            foreach (var stage in new[] { " fps) on CPU;", "composer: compose ", "readback submit ", "convert: convert ", "readback wait ", "encoder: encode ", "audio ", "finish " })
                Assert.Contains(stage, summary, StringComparison.Ordinal);
            Assert.EndsWith($"({result.EncoderName})", summary, StringComparison.Ordinal);
            Assert.Contains(log, line => line.Contains("readback: synchronous readback", StringComparison.Ordinal));
            Assert.Contains(log, line => line.StartsWith($"Mp4Writer: video encoder {result.EncoderName} (", StringComparison.Ordinal));
        }

        /// <summary>The job's encoder choice reaches the writer and comes back in the result:
        /// Software is x264 by contract; Auto is whatever the process-wide probe picked, and at
        /// this test's 64x64 (below NVENC's minimum) a hardware pick falls back to x264 with a
        /// logged reason rather than failing the render.</summary>
        [Fact]
        public void Encoder_choice_is_honoured_and_reported()
        {
            RequireFFmpeg();
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddSolid(project, track, 0, Second, "#FF335577");

            var software = RenderJob.Run(project, TempMp4(), new RenderJobOptions { PreferGpu = false, Encoder = VideoEncoder.Software });
            Assert.Equal(VideoEncoder.Software, software.Encoder);
            Assert.Equal("libx264", software.EncoderName);

            var log = new List<string>();
            var auto = RenderJob.Run(project, TempMp4(), new RenderJobOptions { PreferGpu = false, Encoder = VideoEncoder.Auto, DiagnosticLog = log.Add });
            Assert.Equal(RenderOutcome.Completed, auto.Outcome);
            Assert.NotEqual(VideoEncoder.Auto, auto.Encoder);
            Assert.Equal(H264EncoderSettings.CodecNameOf(auto.Encoder), auto.EncoderName);
            var line = Assert.Single(log, l => l.StartsWith("Mp4Writer: video encoder ", StringComparison.Ordinal));
            Assert.Contains("requested auto", line, StringComparison.Ordinal);
            if (auto.Encoder == VideoEncoder.Software && H264EncoderProbe.Resolve(VideoEncoder.Auto, null) != VideoEncoder.Software)
                Assert.Contains(log, l => l.Contains("falling back to libx264", StringComparison.Ordinal));
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

        // ------------------------------------------------------------------ encode-time size cap

        /// <summary>The size the encoder is opened at: the cap's height (rounded down to even)
        /// with the width that preserves the aspect ratio (likewise even), and the canvas itself
        /// whenever the cap is absent or would not shrink it — a cap never upscales.</summary>
        [Theory]
        [InlineData(1920, 1080, 0, 1920, 1080)]     // no cap
        [InlineData(1920, 1080, 1080, 1920, 1080)]  // exactly the output height: nothing to do
        [InlineData(1920, 1080, 2160, 1920, 1080)]  // above it: never upscale
        [InlineData(1920, 1080, 720, 1280, 720)]
        [InlineData(1240, 1166, 720, 766, 720)]     // the recorder's window size, capped to 720p
        [InlineData(64, 64, 32, 32, 32)]
        [InlineData(64, 64, 33, 32, 32)]            // odd cap rounds down to even
        [InlineData(66, 44, 22, 32, 22)]            // 66*22/44 = 33: the odd width rounds too
        [InlineData(1920, 1080, 1, 4, 2)]           // a silly cap still produces an encodable size
        public void Cap_size_preserves_the_aspect_ratio_on_even_dimensions(
            int width, int height, int maxHeight, int expectedWidth, int expectedHeight)
        {
            Assert.Equal((expectedWidth, expectedHeight), RenderJob.CapSize(width, height, maxHeight));
        }

        [Fact]
        public void Cap_size_rejects_a_negative_cap()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => RenderJob.CapSize(64, 64, -1));
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddSolid(project, track, 0, Second, "#FF335577");
            string path = TempMp4();
            Assert.Throws<ArgumentException>(() =>
                RenderJob.Run(project, path, new RenderJobOptions { MaxHeight = -1 }));
            Assert.False(File.Exists(path));
        }

        /// <summary>The cap is an encode-time resample: the project composes at its canvas size
        /// (a 96x64 canvas here, so a wrong aspect would show up as a wrong width) and the mp4
        /// comes out at the capped size, with the convert stage — not the project — scaling.</summary>
        [Fact]
        public void Max_height_caps_the_encoded_mp4()
        {
            RequireFFmpeg();

            var project = SyntheticProject(96, 64);
            string path = TempMp4();
            var log = new List<string>();
            var result = RenderJob.Run(project, path,
                new RenderJobOptions { PreferGpu = false, MaxHeight = 32, DiagnosticLog = log.Add });

            Assert.Equal(RenderOutcome.Completed, result.Outcome);
            Assert.Equal(Fps, result.VideoFrames); // the cap changes pixels, never frames
            Assert.False(result.ZeroCopy);
            var video = Assert.Single(MediaProbe.ProbeDetailed(path).VideoStreams);
            Assert.Equal(32, video.Height);
            Assert.Equal(48, video.Width); // 96x64 at 32 rows
            Assert.Contains(log, l => l.Contains("encoded at 48x32 (max height 32)", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains("scaling to 48x32", StringComparison.Ordinal));
        }

        /// <summary>An odd cap is rounded down to an even height (4:2:0 chroma) rather than
        /// refused, and the whole render still runs.</summary>
        [Fact]
        public void An_odd_cap_renders_at_the_even_size_below_it()
        {
            RequireFFmpeg();

            var project = SyntheticProject();
            string path = TempMp4();
            var result = RenderJob.Run(project, path,
                new RenderJobOptions { PreferGpu = false, MaxHeight = 33 });

            Assert.Equal(RenderOutcome.Completed, result.Outcome);
            var video = Assert.Single(MediaProbe.ProbeDetailed(path).VideoStreams);
            Assert.Equal(32, video.Height);
            Assert.Equal(32, video.Width);
        }

        /// <summary>A cap at or above the project's own height changes nothing at all — not the
        /// size, and not the encoded bytes (x264 is deterministic for identical input), so an
        /// uncapped render is never paying for a no-op rescale.</summary>
        [Fact]
        public void A_cap_that_would_not_shrink_the_output_is_a_no_op()
        {
            RequireFFmpeg();

            var project = SyntheticProject();
            string uncapped = TempMp4(), capped = TempMp4();
            var options = new RenderJobOptions { PreferGpu = false, Encoder = VideoEncoder.Software };
            RenderJob.Run(project, uncapped, options);
            RenderJob.Run(project, capped, new RenderJobOptions
            {
                PreferGpu = false,
                Encoder = VideoEncoder.Software,
                MaxHeight = H * 2,
            });

            var video = Assert.Single(MediaProbe.ProbeDetailed(capped).VideoStreams);
            Assert.Equal(W, video.Width);
            Assert.Equal(H, video.Height);
            Assert.Equal(File.ReadAllBytes(uncapped), File.ReadAllBytes(capped));
        }

        // -------------------------------------------------------------------- zero-copy encode

        /// <summary>The zero-copy path end to end on this machine (a shareable Direct3D 12 ring
        /// and NVENC; skips without either) against the readback path with the same encoder:
        /// both must decode to the same pictures. The two convert BGRA→NV12 with different
        /// implementations (the GPU video processor, swscale) under the same BT.601 matrix, and
        /// NVENC then encodes each, so the gate is the statistical one the GPU/CPU test uses —
        /// a misplaced, mistimed, flipped or mis-converted frame trips it by a wide margin.</summary>
        [Fact]
        public void Zero_copy_render_matches_readback_render()
        {
            RequireFFmpeg();
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the zero-copy path is Direct3D-only");
            var gpu = GpuSurfaceFactory.TryCreate(out var reason);
            Assert.SkipWhen(gpu == null, "GPU backend unavailable: " + reason);
            gpu.Dispose();
            Assert.SkipUnless(H264EncoderProbe.CanOpen(VideoEncoder.Nvenc, out reason), "h264_nvenc does not open here: " + reason);

            // above NVENC's minimum size; the 64x64 fixtures would fall back to x264
            const int w = H264EncoderProbe.ProbeWidth, h = H264EncoderProbe.ProbeHeight;
            var project = SyntheticProject(w, h);
            string zeroCopyPath = TempMp4(), readbackPath = TempMp4();
            var zeroCopyLog = new List<string>();
            var readbackLog = new List<string>();

            var zeroCopy = RenderJob.Run(project, zeroCopyPath, new RenderJobOptions
            {
                Encoder = VideoEncoder.Nvenc, Crf = 16, ZeroCopyEncode = true, DiagnosticLog = zeroCopyLog.Add,
            });
            var readback = RenderJob.Run(project, readbackPath, new RenderJobOptions
            {
                Encoder = VideoEncoder.Nvenc, Crf = 16, ZeroCopyEncode = false, DiagnosticLog = readbackLog.Add,
            });

            Assert.Equal(RenderOutcome.Completed, zeroCopy.Outcome);
            Assert.Equal(RenderOutcome.Completed, readback.Outcome);
            Assert.Equal("h264_nvenc", zeroCopy.EncoderName);
            Assert.Equal("h264_nvenc", readback.EncoderName);
            Assert.True(zeroCopy.ZeroCopy, "the zero-copy path did not engage: " + string.Join(" | ", zeroCopyLog));
            Assert.False(readback.ZeroCopy);
            Assert.Contains(zeroCopyLog, line => line.StartsWith("RenderJob: zero-copy: Direct3D 11 zero-copy", StringComparison.Ordinal));
            Assert.Contains(zeroCopyLog, line => line.Contains("input: Direct3D 11 NV12 textures", StringComparison.Ordinal));
            Assert.Contains(zeroCopyLog, line => line.EndsWith("(h264_nvenc, zero-copy)", StringComparison.Ordinal));
            Assert.DoesNotContain(zeroCopyLog, line => line.Contains("using readback", StringComparison.Ordinal));
            Assert.Contains(readbackLog, line => line.Contains("zero-copy encode disabled; using readback", StringComparison.Ordinal));
            Assert.Contains(readbackLog, line => line.StartsWith("RenderJob: readback: Direct3D 12 copy-queue readback", StringComparison.Ordinal));
            Assert.Contains(readbackLog, line => line.EndsWith("(h264_nvenc)", StringComparison.Ordinal));

            var (worst, differing, mean, beyondFour, total) = Compare(
                DecodeAllFrames(zeroCopyPath, Fps, w, h), DecodeAllFrames(readbackPath, Fps, w, h));
            Assert.True(mean < 1.0 && beyondFour <= total * 3 / 100,
                $"zero-copy and readback renders differ: worst {worst}, mean {mean:F3}, {differing} of {total} bytes, {beyondFour} beyond 4");
        }

        /// <summary>A hardware encoder that will not open over the bridge's frames (AMF on a
        /// machine without an AMD driver: the bridge comes up, the encoder declines) drops the
        /// bridge and takes the readback stages with whatever encoder the writer settled on —
        /// the render still completes. On a machine where AMF does open the same render runs
        /// zero-copy, and the test checks that instead.</summary>
        [Fact]
        public void Zero_copy_bridge_is_dropped_when_the_encoder_declines_gpu_frames()
        {
            RequireFFmpeg();
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the zero-copy path is Direct3D-only");
            var gpu = GpuSurfaceFactory.TryCreate(out var reason);
            Assert.SkipWhen(gpu == null, "GPU backend unavailable: " + reason);
            gpu.Dispose();
            bool amfOpens = H264EncoderProbe.CanOpen(VideoEncoder.Amf, out _);

            const int w = H264EncoderProbe.ProbeWidth, h = H264EncoderProbe.ProbeHeight;
            var log = new List<string>();
            var result = RenderJob.Run(SyntheticProject(w, h), TempMp4(), new RenderJobOptions
            {
                Encoder = VideoEncoder.Amf, ZeroCopyEncode = true, DiagnosticLog = log.Add,
            });
            Assert.Equal(RenderOutcome.Completed, result.Outcome);
            Assert.Equal(Fps, result.VideoFrames);
            if (amfOpens)
            {
                Assert.Equal(VideoEncoder.Amf, result.Encoder);
                Assert.True(result.ZeroCopy || log.Exists(line => line.Contains("using readback", StringComparison.Ordinal)));
                return;
            }

            Assert.False(result.ZeroCopy);
            Assert.Equal(VideoEncoder.Software, result.Encoder);
            Assert.Contains(log, line => line.Contains("falling back to libx264", StringComparison.Ordinal));
            var unavailable = Assert.Single(log, line => line.StartsWith("RenderJob: zero-copy encode unavailable", StringComparison.Ordinal));
            Assert.EndsWith("using readback", unavailable, StringComparison.Ordinal);
            // a bridge that did come up (a shareable ring) was dropped for the writer's sake
            if (log.Exists(line => line.Contains("would not open over Direct3D 11 frames", StringComparison.Ordinal)))
                Assert.Contains("no encoder opened over Direct3D 11 frames", unavailable, StringComparison.Ordinal);
            Assert.Contains(log, line => line.StartsWith("RenderJob: readback: ", StringComparison.Ordinal));
            Assert.Contains(log, line => line.EndsWith("(libx264)", StringComparison.Ordinal));
        }

        /// <summary>The bridge is only ever built for an encoder with a GPU input: a software
        /// render on the Direct3D 12 composer says nothing about zero-copy and takes the
        /// readback stages.</summary>
        [Fact]
        public void Zero_copy_is_not_attempted_for_the_software_encoder()
        {
            RequireFFmpeg();
            var gpu = GpuSurfaceFactory.TryCreate(out var reason);
            Assert.SkipWhen(gpu == null, "GPU backend unavailable: " + reason);
            gpu.Dispose();

            var log = new List<string>();
            var result = RenderJob.Run(SyntheticProject(), TempMp4(), new RenderJobOptions
            {
                Encoder = VideoEncoder.Software, ZeroCopyEncode = true, DiagnosticLog = log.Add,
            });
            Assert.Equal(RenderOutcome.Completed, result.Outcome);
            Assert.False(result.ZeroCopy);
            Assert.DoesNotContain(log, line => line.Contains("zero-copy", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(log, line => line.StartsWith("RenderJob: readback: ", StringComparison.Ordinal));
            Assert.Contains(log, line => line.EndsWith("(libx264)", StringComparison.Ordinal));
        }

        /// <summary>A cancelled zero-copy render (NVENC; skips without it) must leave no GPU
        /// frame behind. The teardown drains only what is still queued for the encoder; the
        /// frame the convert thread holds while a full queue blocks its hand-off and the one the
        /// encode thread has taken out are each stage's own to drop — and a HardwareFrame has no
        /// finalizer, so a dropped one would keep its texture, the frames context and the D3D11
        /// device alive for the rest of the process.</summary>
        [Fact]
        public void Zero_copy_cancel_leaves_no_hardware_frame_alive()
        {
            RequireFFmpeg();
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the zero-copy path is Direct3D-only");
            var gpu = GpuSurfaceFactory.TryCreate(out var reason);
            Assert.SkipWhen(gpu == null, "GPU backend unavailable: " + reason);
            gpu.Dispose();
            Assert.SkipUnless(H264EncoderProbe.CanOpen(VideoEncoder.Nvenc, out reason), "h264_nvenc does not open here: " + reason);

            const int w = H264EncoderProbe.ProbeWidth, h = H264EncoderProbe.ProbeHeight;
            var project = NewProject(w, h);
            var track = AddTrack(project, TrackKind.Video);
            AddSolid(project, track, 0, 10 * Second, "#FF4080C0"); // 300 frames — plenty to cancel into

            string path = TempMp4();
            using var cts = new CancellationTokenSource();
            var progress = new InlineProgress(p =>
            {
                if (p > 0)
                    cts.Cancel(); // cancel once the first frame has actually been encoded
            });
            var log = new List<string>();
            int live = HardwareFrame.LiveCount;

            var result = RenderJob.Run(project, path, new RenderJobOptions
            {
                Encoder = VideoEncoder.Nvenc, ZeroCopyEncode = true, DiagnosticLog = log.Add,
            }, progress, cts.Token);

            Assert.Equal(RenderOutcome.Canceled, result.Outcome);
            Assert.True(result.ZeroCopy, "the zero-copy path did not engage: " + string.Join(" | ", log));
            Assert.True(result.VideoFrames > 0, "cancel should land mid-render, not before it");
            Assert.True(result.VideoFrames < 300, "render ran to completion despite cancellation");
            Assert.False(File.Exists(path), "partial output must be deleted on cancellation");
            Assert.Equal(live, HardwareFrame.LiveCount);
        }
    }
}
