using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Clowd.VideoSDK.Render;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The v1 render-args → v2 project mapping (<see cref="RenderArgsCompat"/>), plus a protocol
    /// smoke test that runs the real Clowd.VideoRender executable end to end.
    ///
    /// The mapping tests hand <see cref="RenderArgsCompat.Build"/> a synthetic
    /// <see cref="MediaProbeResult"/>, so every rule is checked without the FFmpeg natives — the
    /// same parse/check/build split the Rust tool used for the same reason. Only the end-to-end
    /// test needs FFmpeg (and the built exe), and it skips cleanly when either is missing.
    /// </summary>
    public class RenderArgsCompatTests : IDisposable
    {
        private const long Ms = 10_000;               // 100ns ticks per millisecond
        private const long Second = 10_000_000;
        private const int ScreenW = 1920, ScreenH = 1080;

        // ----------------------------------------------------------------------------- fixtures

        private readonly List<string> _tempPaths = new List<string>();

        public void Dispose()
        {
            foreach (var path in _tempPaths)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch { /* best effort */ }
            }
        }

        private string TempPath(string extension)
        {
            string path = Path.Combine(Path.GetTempPath(), $"clowd-compat-test-{Guid.NewGuid():N}{extension}");
            _tempPaths.Add(path);
            return path;
        }

        /// <summary>A realistic v1 args file: two keep segments, a webcam overlay with a mask, a crf.
        /// Field names mirror <c>Clowd.Shared/Video/RenderArgs.cs</c> — that JSON is the contract.</summary>
        private static string ArgsJson(string input = "C:/in.mp4", string output = "C:/out.mp4",
            string segments = null, string webcam = null, string crf = null)
        {
            var parts = new List<string>
            {
                "\"version\":1",
                $"\"input\":{JsonSerializer.Serialize(input)}",
                $"\"output\":{JsonSerializer.Serialize(output)}",
            };
            if (segments != null)
                parts.Add("\"segments\":" + segments);
            if (webcam != null)
                parts.Add("\"webcam\":" + webcam);
            if (crf != null)
                parts.Add("\"crf\":" + crf);

            return "{" + String.Join(",", parts) + "}";
        }

        /// <summary>The probe of a 1920x1080 30fps recording with a 640x480 webcam track (stream 1)
        /// and one 48 kHz audio stream (stream 2) — the shape our own recordings have.</summary>
        private static MediaProbeResult Probe(bool webcam = true, bool audio = true,
            long durationTicks = 30 * Second, int fpsNum = 30, int fpsDen = 1, int rFpsNum = 30, int rFpsDen = 1)
        {
            var video = new List<VideoStreamProbe>
            {
                new VideoStreamProbe
                {
                    StreamIndex = 0,
                    Width = ScreenW,
                    Height = ScreenH,
                    AvgFrameRateNum = fpsNum,
                    AvgFrameRateDen = fpsDen,
                    RFrameRateNum = rFpsNum,
                    RFrameRateDen = rFpsDen,
                    DurationTicks = durationTicks,
                },
            };

            if (webcam)
                video.Add(new VideoStreamProbe
                {
                    StreamIndex = 1,
                    Width = 640,
                    Height = 480,
                    AvgFrameRateNum = 30,
                    AvgFrameRateDen = 1,
                    RFrameRateNum = 30,
                    RFrameRateDen = 1,
                    DurationTicks = durationTicks,
                });

            return new MediaProbeResult
            {
                Path = "C:/in.mp4",
                DurationTicks = durationTicks,
                VideoStreams = video,
                AudioStreams = audio
                    ? new[] { new AudioStreamProbe { StreamIndex = 2, SampleRate = 48000, Channels = 2, DurationTicks = durationTicks } }
                    : Array.Empty<AudioStreamProbe>(),
                HasAudio = audio,
            };
        }

        private static LegacyRenderPlan Build(string json, MediaProbeResult probe = null) =>
            RenderArgsCompat.Build(RenderArgsCompat.Parse(json), probe ?? Probe());

        private static IReadOnlyList<Item> ItemsOn(Project project, string trackName)
        {
            var track = project.Tracks.Single(t => t.Name == trackName);
            return project.Items.Where(i => i.TrackId == track.Id)
                                .OrderBy(i => i.TimelineStartTicks)
                                .ToList();
        }

        // -------------------------------------------------------------------------------- parse

        [Fact]
        public void Minimal_args_parse_with_defaults()
        {
            var plan = Build(ArgsJson());

            Assert.Equal("C:/in.mp4", plan.InputPath);
            Assert.Equal("C:/out.mp4", plan.OutputPath);
            Assert.Equal(RenderArgsCompat.DefaultCrf, plan.Crf);   // absent crf is not crf 0
            Assert.Null(plan.MaskPngPath);
        }

        [Fact]
        public void Crf_is_carried_to_the_render_settings()
        {
            Assert.Equal(28, Build(ArgsJson(crf: "28")).Crf);
            Assert.Equal(0, Build(ArgsJson(crf: "0")).Crf);
        }

        [Theory]
        [InlineData("{not json", "invalid args JSON")]
        [InlineData("{\"version\":3,\"input\":\"a\",\"output\":\"b\"}", "unsupported args version 3")]
        [InlineData("{\"version\":1,\"input\":\"\",\"output\":\"b\"}", "input path is empty")]
        [InlineData("{\"version\":1,\"input\":\"a\",\"output\":\"\"}", "output path is empty")]
        [InlineData("{\"version\":1,\"input\":\"a.mp4\",\"output\":\"a.mp4\"}", "equals input")]
        public void Rejects_malformed_args(string json, string expected)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => RenderArgsCompat.Parse(json));
            Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("[{\"start_ms\":5,\"end_ms\":5}]", "greater than")]
        [InlineData("[{\"start_ms\":10,\"end_ms\":5}]", "greater than")]
        [InlineData("[{\"start_ms\":-1,\"end_ms\":5}]", "negative")]
        [InlineData("[{\"start_ms\":0,\"end_ms\":5000},{\"start_ms\":4000,\"end_ms\":6000}]", "overlaps")]
        [InlineData("[{\"start_ms\":5000,\"end_ms\":6000},{\"start_ms\":0,\"end_ms\":1000}]", "overlaps or precedes")]
        public void Rejects_malformed_segments(string segments, string expected)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => RenderArgsCompat.Parse(ArgsJson(segments: segments)));
            Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("{\"stream_index\":1,\"rect\":{\"x\":0,\"y\":0,\"w\":0,\"h\":240}}", "non-positive")]
        [InlineData("{\"stream_index\":1,\"rect\":{\"x\":-4,\"y\":0,\"w\":32,\"h\":24}}", "negative")]
        [InlineData("{\"stream_index\":-1,\"rect\":{\"x\":0,\"y\":0,\"w\":32,\"h\":24}}", "stream_index")]
        public void Rejects_malformed_webcam(string webcam, string expected)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => RenderArgsCompat.Parse(ArgsJson(webcam: webcam)));
            Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Rejects_crf_out_of_range()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => RenderArgsCompat.Parse(ArgsJson(crf: "52")));
            Assert.Contains("crf 52", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Touching_segments_are_allowed()
        {
            var plan = Build(ArgsJson(segments: "[{\"start_ms\":0,\"end_ms\":5000},{\"start_ms\":5000,\"end_ms\":6000}]"));
            Assert.Equal(2, ItemsOn(plan.Project, "Screen").Count);
        }

        [Fact]
        public void Null_webcam_and_segments_are_absent()
        {
            var plan = RenderArgsCompat.Build(
                RenderArgsCompat.Parse("{\"version\":1,\"input\":\"a.mp4\",\"output\":\"b.mp4\",\"webcam\":null,\"segments\":null}"),
                Probe());

            Assert.DoesNotContain(plan.Project.Tracks, t => t.Name == "Webcam");
            Assert.Single(ItemsOn(plan.Project, "Screen"));
        }

        // --------------------------------------------------------------------------- item layout

        [Fact]
        public void Keep_segments_become_back_to_back_items_per_stream()
        {
            // keep [0,5s) and [9s,21s): 17s of output, the second item starting at 5s.
            var plan = Build(ArgsJson(
                segments: "[{\"start_ms\":0,\"end_ms\":5000},{\"start_ms\":9000,\"end_ms\":21000}]",
                webcam: "{\"stream_index\":1,\"rect\":{\"x\":1560,\"y\":780,\"w\":320,\"h\":240}}"));

            var project = plan.Project;
            Assert.Equal(Project.CurrentVersion, project.Version);
            Assert.Empty(project.Validate());
            Assert.Equal(17 * Second, project.GetDurationTicks());

            foreach (var trackName in new[] { "Screen", "Webcam", "Audio" })
            {
                var items = ItemsOn(project, trackName);
                Assert.Equal(2, items.Count);

                Assert.Equal(0, items[0].TimelineStartTicks);
                Assert.Equal(5 * Second, items[0].DurationTicks);
                Assert.Equal(0, ((MediaContent)items[0].Content).SourceInTicks);

                // back to back: item 1 starts exactly where item 0 ended…
                Assert.Equal(items[0].TimelineEndTicks, items[1].TimelineStartTicks);
                Assert.Equal(12 * Second, items[1].DurationTicks);
                // …while pointing at the far side of the cut in the source.
                Assert.Equal(9 * Second, ((MediaContent)items[1].Content).SourceInTicks);
            }
        }

        [Fact]
        public void Streams_land_on_their_own_tracks_in_composite_order()
        {
            var project = Build(ArgsJson(
                webcam: "{\"stream_index\":1,\"rect\":{\"x\":0,\"y\":0,\"w\":320,\"h\":240}}")).Project;

            var screen = project.Tracks.Single(t => t.Name == "Screen");
            var webcam = project.Tracks.Single(t => t.Name == "Webcam");
            var audio = project.Tracks.Single(t => t.Name == "Audio");

            Assert.Equal(TrackKind.Video, screen.Kind);
            Assert.Equal(TrackKind.Video, webcam.Kind);
            Assert.Equal(TrackKind.Audio, audio.Kind);
            // ascending order composites later — the PiP must draw over the screen.
            Assert.True(webcam.Order > screen.Order);

            Assert.Equal(0, ((MediaContent)ItemsOn(project, "Screen")[0].Content).StreamIndex);
            Assert.Equal(1, ((MediaContent)ItemsOn(project, "Webcam")[0].Content).StreamIndex);
            Assert.Equal(2, ((MediaContent)ItemsOn(project, "Audio")[0].Content).StreamIndex);

            // one recording, one source, and the streams it actually uses
            var source = Assert.Single(project.Sources);
            Assert.Equal(new[] { 0, 1, 2 }, source.Streams.Select(s => s.Index).ToArray());
            Assert.Equal(StreamKind.Audio, source.Streams.Single(s => s.Index == 2).Kind);
            Assert.All(project.Items, i => Assert.Equal(source.Id, ((MediaContent)i.Content).SourceId));
        }

        [Fact]
        public void Every_item_shares_one_link_group()
        {
            var project = Build(ArgsJson(
                segments: "[{\"start_ms\":0,\"end_ms\":1000},{\"start_ms\":2000,\"end_ms\":3000}]",
                webcam: "{\"stream_index\":1,\"rect\":{\"x\":0,\"y\":0,\"w\":320,\"h\":240}}")).Project;

            Assert.Equal(6, project.Items.Count); // 2 segments x 3 streams
            var groups = project.Items.Select(i => i.LinkGroupId).Distinct().ToList();
            var group = Assert.Single(groups);
            Assert.NotNull(group);
        }

        [Fact]
        public void No_segments_keeps_the_whole_file()
        {
            var project = Build(ArgsJson(), Probe(durationTicks: 12 * Second)).Project;

            var item = Assert.Single(ItemsOn(project, "Screen"));
            Assert.Equal(0, item.TimelineStartTicks);
            Assert.Equal(12 * Second, item.DurationTicks);
        }

        [Fact]
        public void Segments_are_clamped_to_the_probed_duration()
        {
            // a stale args file asking past the end must not produce a freeze-frame tail.
            var project = Build(
                ArgsJson(segments: "[{\"start_ms\":0,\"end_ms\":20000},{\"start_ms\":30000,\"end_ms\":40000}]"),
                Probe(durationTicks: 10 * Second)).Project;

            var item = Assert.Single(ItemsOn(project, "Screen"));
            Assert.Equal(10 * Second, item.DurationTicks);
        }

        [Fact]
        public void Millisecond_segments_convert_to_exact_ticks()
        {
            var project = Build(ArgsJson(segments: "[{\"start_ms\":1234,\"end_ms\":5678}]")).Project;

            var item = Assert.Single(ItemsOn(project, "Screen"));
            Assert.Equal(1234 * Ms, ((MediaContent)item.Content).SourceInTicks);
            Assert.Equal((5678 - 1234) * Ms, item.DurationTicks);
        }

        [Fact]
        public void An_edit_that_keeps_nothing_is_rejected()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                Build(ArgsJson(segments: "[{\"start_ms\":30000,\"end_ms\":40000}]"), Probe(durationTicks: 10 * Second)));
            Assert.Contains("keeps nothing", ex.Message, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------------------- output

        [Fact]
        public void Output_canvas_and_rate_come_from_the_screen_stream()
        {
            var output = Build(ArgsJson()).Project.Output;

            Assert.Equal(ScreenW, output.WidthPx);
            Assert.Equal(ScreenH, output.HeightPx);
            Assert.Equal(30, output.FpsNum);
            Assert.Equal(1, output.FpsDen);
            Assert.Equal(48000, output.SampleRate);   // from the input's audio stream
        }

        [Fact]
        public void Fractional_frame_rates_stay_rational()
        {
            var output = Build(ArgsJson(), Probe(fpsNum: 30000, fpsDen: 1001, rFpsNum: 30000, rFpsDen: 1001)).Project.Output;

            Assert.Equal(30000, output.FpsNum);
            Assert.Equal(1001, output.FpsDen);
        }

        [Fact]
        public void Frame_rate_falls_back_to_r_frame_rate_then_to_a_default()
        {
            var viaR = Build(ArgsJson(), Probe(fpsNum: 0, fpsDen: 0, rFpsNum: 60, rFpsDen: 1)).Project.Output;
            Assert.Equal(60, viaR.FpsNum);
            Assert.Equal(1, viaR.FpsDen);

            var viaDefault = Build(ArgsJson(), Probe(fpsNum: 0, fpsDen: 0, rFpsNum: 0, rFpsDen: 0)).Project.Output;
            Assert.Equal(RenderArgsCompat.FallbackFpsNum, viaDefault.FpsNum);
            Assert.Equal(1, viaDefault.FpsDen);
        }

        [Fact]
        public void A_silent_input_gets_no_audio_track()
        {
            var project = Build(ArgsJson(), Probe(audio: false)).Project;

            Assert.DoesNotContain(project.Tracks, t => t.Kind == TrackKind.Audio);
            Assert.Equal(RenderArgsCompat.FallbackSampleRate, project.Output.SampleRate);
            Assert.Empty(project.Validate());
        }

        // ------------------------------------------------------------------------------ webcam

        [Fact]
        public void Webcam_rect_becomes_a_normalized_transform()
        {
            // 320x240 at (1560,780) in a 1920x1080 frame: centre (1720, 900), width 1/6 of the frame.
            var project = Build(ArgsJson(
                webcam: "{\"stream_index\":1,\"rect\":{\"x\":1560,\"y\":780,\"w\":320,\"h\":240}}")).Project;

            var transform = ItemsOn(project, "Webcam")[0].Transform;
            Assert.Equal(1720 / (double)ScreenW, transform.X, 12);
            Assert.Equal(900 / (double)ScreenH, transform.Y, 12);
            Assert.Equal(320 / (double)ScreenW, transform.Scale, 12);
            Assert.Equal(1.0, transform.Opacity);
            Assert.Null(transform.Mask);

            // the screen fills the canvas with the default transform — no geometry at all.
            var screen = ItemsOn(project, "Screen")[0].Transform;
            Assert.Equal(0.5, screen.X);
            Assert.Equal(0.5, screen.Y);
            Assert.Equal(1.0, screen.Scale);
        }

        [Fact]
        public void Webcam_transform_round_trips_the_v1_overlay_geometry()
        {
            // WebcamOverlay stores a normalized centre + width; the UI expanded it into pixels.
            // Expanding and mapping back must land on the original numbers.
            const double centerX = 0.82, centerY = 0.78, width = 0.2;
            int w = (int)Math.Round(width * ScreenW);          // 384
            int h = (int)Math.Round(w * 480.0 / 640.0);        // camera aspect: 288
            int x = (int)Math.Round(centerX * ScreenW - w / 2.0);
            int y = (int)Math.Round(centerY * ScreenH - h / 2.0);

            var project = Build(ArgsJson(webcam:
                $"{{\"stream_index\":1,\"rect\":{{\"x\":{x},\"y\":{y},\"w\":{w},\"h\":{h}}}}}")).Project;

            var transform = ItemsOn(project, "Webcam")[0].Transform;
            Assert.Equal(centerX, transform.X, 3);
            Assert.Equal(centerY, transform.Y, 3);
            Assert.Equal(width, transform.Scale, 3);
        }

        [Fact]
        public void Webcam_transform_is_not_shared_between_items()
        {
            var project = Build(ArgsJson(
                segments: "[{\"start_ms\":0,\"end_ms\":1000},{\"start_ms\":2000,\"end_ms\":3000}]",
                webcam: "{\"stream_index\":1,\"rect\":{\"x\":0,\"y\":0,\"w\":320,\"h\":240}}")).Project;

            var items = ItemsOn(project, "Webcam");
            Assert.NotSame(items[0].Transform, items[1].Transform);
            Assert.Equal(items[0].Transform.X, items[1].Transform.X);
        }

        [Theory]
        [InlineData(0, "webcam stream_index 0 is the screen track itself")]
        [InlineData(7, "is not a video stream")]
        public void Rejects_an_impossible_webcam_stream(int streamIndex, string expected)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Build(ArgsJson(
                webcam: $"{{\"stream_index\":{streamIndex},\"rect\":{{\"x\":0,\"y\":0,\"w\":320,\"h\":240}}}}")));
            Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Rejects_a_webcam_rect_outside_the_frame()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Build(ArgsJson(
                webcam: "{\"stream_index\":1,\"rect\":{\"x\":1800,\"y\":0,\"w\":320,\"h\":240}}")));
            Assert.Contains("exceeds the 1920x1080 screen frame", ex.Message, StringComparison.Ordinal);
        }

        // -------------------------------------------------------------------------------- mask

        /// <summary>Writes the same mask the UI's <c>WebcamMaskRenderer</c> produces: black ground,
        /// white shape, antialiased edge, corner radius as a fraction of the height.</summary>
        private string WriteMask(int width, int height, MaskShape shape, double cornerRadiusFraction = 0)
        {
            string path = TempPath(".png");
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using (var surface = SKSurface.Create(info))
            {
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Black);
                using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
                var bounds = new SKRect(0, 0, width, height);

                if (shape == MaskShape.Circle)
                {
                    canvas.DrawOval(bounds, paint);
                }
                else
                {
                    float radius = (float)Math.Min(Math.Clamp(cornerRadiusFraction, 0, 0.5) * height,
                        Math.Min(width, height) / 2.0);
                    canvas.DrawRoundRect(bounds, radius, radius, paint);
                }

                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = File.Create(path);
                data.SaveTo(stream);
            }

            return path;
        }

        [Fact]
        public void Circle_mask_is_recognized()
        {
            var mask = RenderArgsCompat.InferMask(WriteMask(320, 240, MaskShape.Circle));

            Assert.NotNull(mask);
            Assert.Equal(MaskShape.Circle, mask.Shape);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.1)]
        [InlineData(0.25)]
        [InlineData(0.5)]
        public void Rounded_rect_mask_recovers_its_corner_radius(double fraction)
        {
            var mask = RenderArgsCompat.InferMask(WriteMask(320, 240, MaskShape.RoundedRect, fraction));

            Assert.NotNull(mask);
            Assert.Equal(MaskShape.RoundedRect, mask.Shape);
            // one pixel of antialiased edge is 1/240 of the height.
            Assert.Equal(fraction, mask.CornerRadius, 0.01);
        }

        [Fact]
        public void No_mask_means_no_clip()
        {
            Assert.Null(RenderArgsCompat.InferMask(null));
            Assert.Null(RenderArgsCompat.InferMask(""));
        }

        [Fact]
        public void The_mask_reaches_the_webcam_transform()
        {
            string mask = WriteMask(320, 240, MaskShape.Circle);
            var plan = Build(ArgsJson(webcam:
                $"{{\"stream_index\":1,\"rect\":{{\"x\":0,\"y\":0,\"w\":320,\"h\":240}},\"mask_png\":{JsonSerializer.Serialize(mask)}}}"));

            Assert.Equal(mask, plan.MaskPngPath);
            var transform = ItemsOn(plan.Project, "Webcam")[0].Transform;
            Assert.Equal(MaskShape.Circle, transform.Mask.Shape);
        }

        [Fact]
        public void A_missing_mask_file_is_a_load_error()
        {
            string argsPath = TempPath(".json");
            string input = TempPath(".mp4");
            File.WriteAllText(input, "not really an mp4, but CheckFiles runs before the probe");
            File.WriteAllText(argsPath, ArgsJson(input, TempPath(".mp4"),
                webcam: "{\"stream_index\":1,\"rect\":{\"x\":0,\"y\":0,\"w\":320,\"h\":240},\"mask_png\":\"C:/does/not/exist.png\"}"));

            var ex = Assert.Throws<InvalidOperationException>(() => RenderArgsCompat.Load(argsPath));
            Assert.Contains("mask file not found", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_missing_input_file_is_a_load_error()
        {
            string argsPath = TempPath(".json");
            File.WriteAllText(argsPath, ArgsJson("C:/does/not/exist.mp4", TempPath(".mp4")));

            var ex = Assert.Throws<InvalidOperationException>(() => RenderArgsCompat.Load(argsPath));
            Assert.Contains("input file not found", ex.Message, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------- exe protocol smoke

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

        /// <summary>The built Clowd.VideoRender.dll (run through <c>dotnet exec</c>, which needs no
        /// apphost and works on every RID). The exe project is a build-only reference of this test
        /// project, so it is up to date whenever the tests are.</summary>
        private static string FindRenderDll()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var project = Path.Combine(dir.FullName, "Clowd.VideoRender");
                if (Directory.Exists(project))
                {
                    var hit = Directory.GetFiles(project, "Clowd.VideoRender.dll", SearchOption.AllDirectories)
                                       .OrderByDescending(File.GetLastWriteTimeUtc)
                                       .FirstOrDefault();
                    if (hit != null)
                        return hit;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private sealed record ToolRun(int ExitCode, IReadOnlyList<string> Stdout, string Stderr);

        private static ToolRun RunTool(string argsPath, string ffmpegDirectory)
        {
            string dll = FindRenderDll();
            Assert.SkipWhen(dll == null, "Clowd.VideoRender.dll not found — build the exe project.");

            var psi = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(dll),
            };
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add(dll);
            if (argsPath != null)
                psi.ArgumentList.Add(argsPath);
            if (ffmpegDirectory != null)
                psi.Environment[FFmpegLoader.EnvVarName] = ffmpegDirectory;

            using var process = Process.Start(psi);
            // stderr on its own thread: FFmpeg chatter filling that pipe while we drain stdout
            // would deadlock the child.
            var stderrReader = System.Threading.Tasks.Task.Run(() => process.StandardError.ReadToEnd());
            string stdout = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(120_000), "the render tool did not exit within 2 minutes");
            string stderr = stderrReader.GetAwaiter().GetResult();

            var lines = stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return new ToolRun(process.ExitCode, lines, stderr);
        }

        /// <summary>A 2s 64x64 30fps mp4 with audio, encoded through Mp4Writer.</summary>
        private string WriteFixtureMp4(int width, int height, int fps, int seconds, int sampleRate)
        {
            string path = TempPath(".mp4");
            using var writer = new Mp4Writer(path, new Mp4WriterOptions
            {
                Width = width,
                Height = height,
                FpsNum = fps,
                FpsDen = 1,
                Audio = new Mp4AudioOptions { SampleRate = sampleRate, Channels = 2 },
            });

            var bgra = new byte[width * height * 4];
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                for (int n = 0; n < fps * seconds; n++)
                {
                    byte level = (byte)(n * 4);
                    for (int i = 0; i < bgra.Length; i += 4)
                    {
                        bgra[i] = level;
                        bgra[i + 1] = 0x80;
                        bgra[i + 2] = 0x20;
                        bgra[i + 3] = 0xFF;
                    }
                    writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), width * 4, width, height, n);
                }
            }
            finally
            {
                pin.Free();
            }

            int audioFrames = sampleRate * seconds;
            const int chunk = 4801;
            for (int fed = 0; fed < audioFrames; fed += chunk)
            {
                int n = Math.Min(chunk, audioFrames - fed);
                var samples = new float[n * 2];
                for (int i = 0; i < n; i++)
                {
                    float s = 0.25f * MathF.Sin(2f * MathF.PI * 440f * (fed + i) / sampleRate);
                    samples[i * 2] = s;
                    samples[i * 2 + 1] = s;
                }
                writer.SubmitAudioSamples(samples, n);
            }
            writer.Finish();

            return path;
        }

        [Fact]
        public void The_tool_renders_a_v1_args_file_and_speaks_the_protocol()
        {
            Assert.SkipUnless(FFmpegAvailable,
                $"FFmpeg natives not found (set {FFmpegLoader.EnvVarName} or build obs-express-rs): {FFmpegLoader.FailureReason}");

            const int Fps = 30, Rate = 48000;
            string input = WriteFixtureMp4(64, 64, Fps, seconds: 2, sampleRate: Rate);
            string output = TempPath(".mp4");
            string argsPath = TempPath(".json");
            // keep [0,500) + [1000,1500): 1s of output out of the 2s recording.
            File.WriteAllText(argsPath, ArgsJson(input, output,
                segments: "[{\"start_ms\":0,\"end_ms\":500},{\"start_ms\":1000,\"end_ms\":1500}]",
                crf: "30"));

            var run = RunTool(argsPath, FindFFmpegDirectory());

            Assert.Equal(0, run.ExitCode);
            Assert.NotEmpty(run.Stdout);

            // every line before the terminal one is a progress line: monotonic, 0..100.
            var progress = run.Stdout.Take(run.Stdout.Count - 1).ToList();
            Assert.All(progress, line => Assert.StartsWith("progress ", line, StringComparison.Ordinal));
            var values = progress.Select(l => Int32.Parse(l.AsSpan("progress ".Length), CultureInfo.InvariantCulture)).ToList();
            Assert.Equal(0, values.First());
            Assert.Equal(100, values.Last());
            for (int i = 1; i < values.Count; i++)
                Assert.True(values[i] > values[i - 1], $"progress went {values[i - 1]} -> {values[i]}");

            string terminal = run.Stdout[^1];
            Assert.StartsWith("done ", terminal, StringComparison.Ordinal);
            int lastSpace = terminal.LastIndexOf(' ');
            Assert.Equal(output, terminal.Substring("done ".Length, lastSpace - "done ".Length));
            long bytes = Int64.Parse(terminal.AsSpan(lastSpace + 1), CultureInfo.InvariantCulture);
            Assert.True(bytes > 0);

            Assert.True(File.Exists(output));
            Assert.Equal(bytes, new FileInfo(output).Length);

            var probe = MediaProbe.ProbeDetailed(output);
            Assert.True(probe.HasAudio);
            var video = Assert.Single(probe.VideoStreams);
            Assert.Equal(64, video.Width);
            Assert.Equal(64, video.Height);
            // 30 frames at 30fps: one second, within a frame of tolerance.
            Assert.InRange(probe.DurationTicks, Second - Second / Fps, Second + Second / Fps);
        }

        [Fact]
        public void A_bad_args_file_is_one_error_line_and_exit_code_1()
        {
            // the tool initializes FFmpeg before it reads the job, so without natives the error
            // line would be the loader's, not the args file's.
            Assert.SkipUnless(FFmpegAvailable, "FFmpeg natives not found: " + FFmpegLoader.FailureReason);

            string argsPath = TempPath(".json");
            File.WriteAllText(argsPath, "{\"version\":1,\"input\":\"\",\"output\":\"x.mp4\"}");

            var run = RunTool(argsPath, FindFFmpegDirectory());

            Assert.Equal(1, run.ExitCode);
            string line = Assert.Single(run.Stdout);
            Assert.StartsWith("error ", line, StringComparison.Ordinal);
            Assert.Contains("input path is empty", line, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', line);
        }

        [Fact]
        public void No_arguments_is_a_usage_error()
        {
            var run = RunTool(null, null);

            Assert.Equal(1, run.ExitCode);
            string line = Assert.Single(run.Stdout);
            Assert.StartsWith("error usage: Clowd.VideoRender", line, StringComparison.Ordinal);
        }
    }
}
