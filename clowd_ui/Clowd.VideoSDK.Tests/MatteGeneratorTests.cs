using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Clowd.VideoSDK.Ai;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="MatteGenerator"/>: the model rules (which streams want a matte, the analysis
    /// resolution) run everywhere; the end-to-end generation runs against the real
    /// <c>clowd_ai</c> binary — encode a small fixture, generate its matte sidecar, and
    /// assert the mp4 + companion honor the sidecar contract (analysis-sized gray-in-luma
    /// frames on the source's PTS grid, valid companion). Skips when FFmpeg, the binary
    /// (build <c>cargo build -p clowd_ai --release</c>) or an ONNX Runtime dylib (see
    /// BUILDING.md) is absent — the same gating as <see cref="DenoiseGeneratorTests"/>.
    /// Shares that suite's collection: both configure the process-wide
    /// <see cref="AiLoader"/>, so they must never run in parallel.
    /// </summary>
    [Collection("AiLoader")]
    public class MatteGeneratorTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 16, Frames = 16;

        private static bool FFmpegAvailable => TestFFmpeg.Available;


        /// <summary>The repo's own build of the inference binary. The ONNX Runtime is statically
        /// linked into it, so any built exe runs inference as-is.</summary>
        private static string FindUsableAi()
        {
            string exeName = OperatingSystem.IsWindows() ? "clowd_ai.exe" : "clowd_ai";
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "target", "release", exeName);
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }

            return null;
        }

        // ------------------------------------------------------------------------------ fixture

        private readonly string _cacheDir;
        private readonly List<string> _tempFiles = new List<string>();

        public MatteGeneratorTests()
        {
            _cacheDir = Path.Combine(Path.GetTempPath(), $"clowd-mattegen-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_cacheDir);
        }

        public void Dispose()
        {
            AiLoader.Configure(null);
            foreach (var f in _tempFiles)
            {
                try { File.Delete(f); }
                catch { /* best effort */ }
            }
            try { Directory.Delete(_cacheDir, recursive: true); }
            catch { /* best effort */ }
        }

        /// <summary>A 1s, 16-frame mp4 whose frames carry a moving bright square on a dark
        /// ground — arbitrary content is fine, the assertions are about the container contract,
        /// not the model's segmentation.</summary>
        private string EncodeVideoFixture()
        {
            string path = Path.Combine(Path.GetTempPath(), $"clowd-mattegen-fixture-{Guid.NewGuid():N}.mp4");
            _tempFiles.Add(path);

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
                for (int n = 0; n < Frames; n++)
                {
                    for (int y = 0; y < H; y++)
                    {
                        for (int x = 0; x < W; x++)
                        {
                            bool square = x >= n * 2 && x < n * 2 + 16 && y >= 24 && y < 40;
                            byte v = square ? (byte)220 : (byte)30;
                            int i = (y * W + x) * 4;
                            bgra[i] = v;
                            bgra[i + 1] = v;
                            bgra[i + 2] = v;
                            bgra[i + 3] = 0xFF;
                        }
                    }
                    writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), W * 4, W, H, n);
                }
            }
            finally
            {
                pin.Free();
            }

            writer.Finish();
            return path;
        }

        private static Source SourceFor(string path) => new Source
        {
            Id = Guid.NewGuid(),
            Path = path,
            Streams =
            {
                new SourceStream { Index = 0, Kind = StreamKind.Video, Width = W, Height = H, AvgFrameRateNum = Fps, AvgFrameRateDen = 1, DurationTicks = TimeSpan.TicksPerSecond },
            },
        };

        // -------------------------------------------------------------------------------- tests

        [Fact]
        public void Without_a_cache_directory_or_binary_the_generation_declines_quietly()
        {
            var source = SourceFor(@"C:\rec\input.mp4");

            Assert.False(MatteGenerator.Generate(source, 0, null));

            AiLoader.Configure(() => null);
            var hadEnv = Environment.GetEnvironmentVariable(AiLoader.EnvVarName);
            Assert.SkipWhen(!String.IsNullOrEmpty(hadEnv),
                $"{AiLoader.EnvVarName} is set in this environment; the no-binary path cannot be observed.");
            Assert.False(MatteGenerator.Generate(source, 0, _cacheDir));
        }

        [Theory]
        [InlineData(1920, 1080, 960, 540)]   // shorter side capped at 540
        [InlineData(3840, 2160, 960, 540)]
        [InlineData(1080, 1920, 540, 960)]   // portrait: the width is the shorter side
        [InlineData(640, 480, 640, 480)]     // already small enough: kept
        [InlineData(540, 540, 540, 540)]
        [InlineData(123, 77, 124, 78)]       // odd dims round to even
        [InlineData(1919, 1079, 960, 540)]
        public void Analysis_size_caps_the_short_side_and_keeps_dimensions_even(
            int srcW, int srcH, int expectedW, int expectedH)
        {
            var (w, h) = MatteGenerator.AnalysisSize(srcW, srcH);
            Assert.Equal((expectedW, expectedH), (w, h));
            Assert.Equal(0, w % 2);
            Assert.Equal(0, h % 2);
            Assert.True(Math.Min(w, h) <= MatteGenerator.MaxAnalysisSide);
        }

        [Fact]
        public void Matte_streams_are_the_segmented_items_on_visible_video_tracks()
        {
            var sourceId = Guid.NewGuid();
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0 };
            var hidden = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 1, Hidden = true };
            var project = new Project { Tracks = { track, hidden } };

            Item AddItem(Track t, int streamIndex, VideoEffect effect)
            {
                var item = new Item
                {
                    Id = Guid.NewGuid(),
                    TrackId = t.Id,
                    DurationTicks = TimeSpan.TicksPerSecond,
                    Content = new MediaContent { SourceId = sourceId, StreamIndex = streamIndex },
                    Effect = effect,
                };
                project.Items.Add(item);
                return item;
            }

            var blurred = AddItem(track, 0, new VideoEffect { Kind = VideoEffectKind.Blur });
            AddItem(track, 1, new VideoEffect { Kind = VideoEffectKind.BgBlur });
            AddItem(track, 2, new VideoEffect { Kind = VideoEffectKind.BgRemove });
            AddItem(hidden, 3, new VideoEffect { Kind = VideoEffectKind.BgRemove });

            var streams = MatteGenerator.CollectMatteStreams(project);
            Assert.Equal(new HashSet<(Guid, int)> { (sourceId, 1), (sourceId, 2) }, streams);

            // a plain blur never joins the set, whatever its dial says
            blurred.Effect.Amount = 1.0;
            Assert.DoesNotContain((sourceId, 0), MatteGenerator.CollectMatteStreams(project));
        }

        [Fact]
        public void Generation_writes_a_contract_shaped_sidecar_the_validator_accepts()
        {
            Assert.SkipUnless(FFmpegAvailable,
                TestFFmpeg.SkipReason);
            var exe = FindUsableAi();
            Assert.SkipWhen(exe == null,
                "clowd_ai.exe with a resolvable ONNX Runtime not found (cargo build -p clowd_ai --release, see BUILDING.md).");

            AiLoader.Configure(() => exe);
            var source = SourceFor(EncodeVideoFixture());
            var progress = new List<double>();

            Assert.True(MatteGenerator.Generate(source, 0, _cacheDir,
                new SynchronousProgress(progress.Add), CancellationToken.None));

            var mattePath = AiSidecars.MattePath(_cacheDir, source.Id, 0);
            Assert.True(AiSidecars.IsValid(mattePath, source.Path, out var info));
            Assert.Equal(new FileInfo(source.Path).Length, info.SourceFileLength);
            Assert.Equal(W, info.Width);  // 64x64 is under the cap: analysis == source size
            Assert.Equal(H, info.Height);
            Assert.Contains(1.0, progress);

            // decode the sidecar back: one matte per source frame, analysis-sized, on the
            // source's PTS grid (the pairing contract), every pixel a legal gray.
            using var pool = new FrameBufferPool();
            using var decoder = new SyncStreamDecoder(mattePath, 0, pool);
            Assert.Equal(W, decoder.Width);
            Assert.Equal(H, decoder.Height);

            int count = 0;
            while (decoder.DecodeNext(out long ptsTicks, out var buffer, out _, out _, out _))
            {
                long expected = TimeBase.FrameIndexToTicks(count, Fps, 1);
                Assert.InRange(ptsTicks, expected - 10_000, expected + 10_000);
                buffer.Return();
                count++;
            }

            Assert.Equal(Frames, count);
        }

        [Fact]
        public void A_canceled_generation_throws_and_leaves_no_sidecar_behind()
        {
            Assert.SkipUnless(FFmpegAvailable,
                TestFFmpeg.SkipReason);
            var exe = FindUsableAi();
            Assert.SkipWhen(exe == null,
                "clowd_ai.exe with a resolvable ONNX Runtime not found (cargo build -p clowd_ai --release, see BUILDING.md).");

            AiLoader.Configure(() => exe);
            var source = SourceFor(EncodeVideoFixture());
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(() =>
                MatteGenerator.Generate(source, 0, _cacheDir, null, cts.Token));
            Assert.False(File.Exists(AiSidecars.MattePath(_cacheDir, source.Id, 0)));
        }

        /// <summary>IProgress without the SynchronizationContext post — the generator runs
        /// synchronously and the assertions read the list right after.</summary>
        private sealed class SynchronousProgress : IProgress<double>
        {
            private readonly Action<double> _report;
            public SynchronousProgress(Action<double> report) => _report = report;
            public void Report(double value) => _report(value);
        }
    }
}
