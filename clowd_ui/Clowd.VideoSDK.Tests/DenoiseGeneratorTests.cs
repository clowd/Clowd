using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Clowd.VideoSDK.Ai;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="DenoiseGenerator"/> against the real <c>clowd_tractnni</c> binary: encode a sine
    /// fixture, generate its denoise sidecar, and assert the wav + companion honour the sidecar
    /// contract (float32 48 kHz, source-matched length, valid companion). This is the one place
    /// the dual-pipe pump (<see cref="TractnniClient"/>) runs for real — the design exists to
    /// avoid a stdin/stdout pipe deadlock, which only a real child process can exercise. Skips
    /// when FFmpeg, the binary (build <c>cargo build -p clowd_tractnni --release</c>) or an ONNX
    /// Runtime dylib (see BUILDING.md) is absent; the degrade-gracefully paths run everywhere.
    /// Collected with <see cref="MatteGeneratorTests"/>: both configure the process-wide
    /// <see cref="TractnniLoader"/>, so they must never run in parallel.
    /// </summary>
    [Collection("TractnniLoader")]
    public class DenoiseGeneratorTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30, Rate = 48000;

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

        /// <summary>The repo's own build of the inference binary. The ONNX Runtime is statically
        /// linked into it, so any built exe runs inference as-is.</summary>
        private static string FindUsableTractnni()
        {
            string exeName = OperatingSystem.IsWindows() ? "clowd_tractnni.exe" : "clowd_tractnni";
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

        public DenoiseGeneratorTests()
        {
            _cacheDir = Path.Combine(Path.GetTempPath(), $"clowd-dengen-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_cacheDir);
        }

        public void Dispose()
        {
            TractnniLoader.Configure(null);
            foreach (var f in _tempFiles)
            {
                try { File.Delete(f); }
                catch { /* best effort */ }
            }
            try { Directory.Delete(_cacheDir, recursive: true); }
            catch { /* best effort */ }
        }

        /// <summary>A 1s mp4 with a 440 Hz sine (video stream 0, audio stream 1) — the same
        /// fixture shape as <see cref="SequentialAudioSourceTests"/>.</summary>
        private string EncodeSineFixture(int channels = 2)
        {
            string path = Path.Combine(Path.GetTempPath(), $"clowd-dengen-fixture-{Guid.NewGuid():N}.mp4");
            _tempFiles.Add(path);

            using var writer = new Mp4Writer(path, new Mp4WriterOptions
            {
                Width = W,
                Height = H,
                FpsNum = Fps,
                FpsDen = 1,
                Audio = new Mp4AudioOptions { SampleRate = Rate, Channels = channels },
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

            var buf = new float[Rate * channels];
            for (int i = 0; i < Rate; i++)
            {
                float s = 0.25f * MathF.Sin(2f * MathF.PI * 440f * i / Rate);
                for (int c = 0; c < channels; c++)
                    buf[i * channels + c] = s;
            }
            writer.SubmitAudioSamples(buf, Rate);
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
                new SourceStream { Index = 1, Kind = StreamKind.Audio, DurationTicks = TimeSpan.TicksPerSecond },
            },
        };

        // -------------------------------------------------------------------------------- tests

        [Fact]
        public void Without_a_cache_directory_or_binary_the_generation_declines_quietly()
        {
            var source = SourceFor(@"C:\rec\input.mp4");

            Assert.False(DenoiseGenerator.Generate(source, 1, null));

            TractnniLoader.Configure(() => null);
            var hadEnv = Environment.GetEnvironmentVariable(TractnniLoader.EnvVarName);
            Assert.SkipWhen(!String.IsNullOrEmpty(hadEnv),
                $"{TractnniLoader.EnvVarName} is set in this environment; the no-binary path cannot be observed.");
            Assert.False(DenoiseGenerator.Generate(source, 1, _cacheDir));
        }

        [Fact]
        public void The_loader_prefers_the_configured_resolver_and_falls_back_to_the_env()
        {
            var configured = Path.Combine(_cacheDir, "tractnni-probe.exe");
            File.WriteAllBytes(configured, new byte[] { 1 });

            TractnniLoader.Configure(() => configured);
            Assert.Equal(Path.GetFullPath(configured), TractnniLoader.TryGetPath());

            // a resolver pointing nowhere falls through to the env (unset here → null)
            TractnniLoader.Configure(() => Path.Combine(_cacheDir, "missing.exe"));
            if (String.IsNullOrEmpty(Environment.GetEnvironmentVariable(TractnniLoader.EnvVarName)))
                Assert.Null(TractnniLoader.TryGetPath());

            // and a throwing resolver is treated as "not available", never propagated
            TractnniLoader.Configure(() => throw new InvalidOperationException("boom"));
            if (String.IsNullOrEmpty(Environment.GetEnvironmentVariable(TractnniLoader.EnvVarName)))
                Assert.Null(TractnniLoader.TryGetPath());
        }

        [Fact]
        public void The_mono_downmix_inverts_the_decoders_stereo_rematrix()
        {
            // what SyncAudioStreamDecoder hands back for a mono source: sqrt(1/2)·M per channel
            var original = new[] { 0.5f, -0.25f, 1.0f, 0.0f, -0.9f };
            var stereo = new float[original.Length * 2];
            for (int i = 0; i < original.Length; i++)
                stereo[i * 2] = stereo[i * 2 + 1] = 0.7071067811865476f * original[i];

            var mono = new float[original.Length];
            DenoiseGenerator.DownmixInto(stereo, mono, original.Length);

            for (int i = 0; i < original.Length; i++)
                Assert.Equal(original[i], mono[i], 6);
        }

        [Fact]
        public void The_wav_frame_cap_sits_exactly_at_the_riff_32_bit_size_limit()
        {
            foreach (var channels in new[] { 1, 2 })
            {
                long blockAlign = channels * 4;
                long max = DenoiseGenerator.MaxWavFrames(channels);
                Assert.True(max * blockAlign + 36 <= uint.MaxValue);
                Assert.True((max + 1) * blockAlign + 36 > uint.MaxValue);
            }

            // the headline number: ~3.1 hours of stereo float32 at 48 kHz
            double hours = DenoiseGenerator.MaxWavFrames(2) / (double)DenoiseGenerator.SampleRate / 3600;
            Assert.InRange(hours, 3.0, 3.2);
        }

        [Fact]
        public void A_stream_too_long_for_the_wav_format_declines_before_inference()
        {
            Assert.SkipUnless(FFmpegAvailable,
                $"FFmpeg natives not found (set {FFmpegLoader.EnvVarName} or build obs-express-rs): {FFmpegLoader.FailureReason}");

            // the guard fires after the channel probe but before the process starts, so a stub
            // binary that could never run is proof no inference was attempted
            var stub = Path.Combine(_cacheDir, "tractnni-stub.exe");
            File.WriteAllBytes(stub, new byte[] { 1 });
            TractnniLoader.Configure(() => stub);

            var source = SourceFor(EncodeSineFixture());
            source.Streams[1].DurationTicks = TimeSpan.FromHours(4).Ticks;

            var ex = Assert.Throws<NotSupportedException>(() =>
                DenoiseGenerator.Generate(source, 1, _cacheDir));
            Assert.Contains("too long", ex.Message);
            Assert.False(File.Exists(AiSidecars.DenoisePath(_cacheDir, source.Id, 1)));
        }

        [Fact]
        public void Generation_writes_a_contract_shaped_sidecar_the_validator_accepts()
        {
            Assert.SkipUnless(FFmpegAvailable,
                $"FFmpeg natives not found (set {FFmpegLoader.EnvVarName} or build obs-express-rs): {FFmpegLoader.FailureReason}");
            var exe = FindUsableTractnni();
            Assert.SkipWhen(exe == null,
                "clowd_tractnni.exe with a resolvable ONNX Runtime not found (cargo build -p clowd_tractnni --release, see BUILDING.md).");

            TractnniLoader.Configure(() => exe);
            var source = SourceFor(EncodeSineFixture());
            var progress = new List<double>();

            Assert.True(DenoiseGenerator.Generate(source, 1, _cacheDir,
                new SynchronousProgress(progress.Add), CancellationToken.None));

            var wavPath = AiSidecars.DenoisePath(_cacheDir, source.Id, 1);
            Assert.True(AiSidecars.IsValid(wavPath, source.Path, out var info));
            Assert.Equal(new FileInfo(source.Path).Length, info.SourceFileLength);
            Assert.Contains(1.0, progress);

            using var stream = File.OpenRead(wavPath);
            var header = new byte[44];
            Assert.Equal(44, stream.Read(header, 0, 44));
            Assert.Equal("RIFF"u8.ToArray(), header[0..4]);
            Assert.Equal("WAVE"u8.ToArray(), header[8..12]);
            Assert.Equal(3, BitConverter.ToInt16(header, 20));       // IEEE float
            Assert.Equal(2, BitConverter.ToInt16(header, 22));       // stereo source → 2 channels
            Assert.Equal(Rate, BitConverter.ToInt32(header, 24));
            Assert.Equal(32, BitConverter.ToInt16(header, 34));

            // duration matches the source stream: 1s of 48 kHz stereo, modulo the AAC codec's
            // trailing frame padding (one 1024-sample frame at most)
            long dataBytes = BitConverter.ToUInt32(header, 40);
            Assert.Equal(stream.Length - 44, dataBytes);
            long frames = dataBytes / 8;
            Assert.InRange(frames, Rate - 1024, Rate + 1024);
        }

        [Fact]
        public void Generation_from_a_mono_source_writes_a_mono_sidecar()
        {
            Assert.SkipUnless(FFmpegAvailable,
                $"FFmpeg natives not found (set {FFmpegLoader.EnvVarName} or build obs-express-rs): {FFmpegLoader.FailureReason}");
            var exe = FindUsableTractnni();
            Assert.SkipWhen(exe == null,
                "clowd_tractnni.exe with a resolvable ONNX Runtime not found (cargo build -p clowd_tractnni --release, see BUILDING.md).");

            TractnniLoader.Configure(() => exe);
            var source = SourceFor(EncodeSineFixture(channels: 1));

            Assert.True(DenoiseGenerator.Generate(source, 1, _cacheDir));

            var wavPath = AiSidecars.DenoisePath(_cacheDir, source.Id, 1);
            Assert.True(AiSidecars.IsValid(wavPath, source.Path, out _));

            using var stream = File.OpenRead(wavPath);
            var header = new byte[44];
            Assert.Equal(44, stream.Read(header, 0, 44));
            Assert.Equal(1, BitConverter.ToInt16(header, 22));       // mono source → 1 channel
            long frames = BitConverter.ToUInt32(header, 40) / 4;
            Assert.InRange(frames, Rate - 1024, Rate + 1024);
        }

        [Fact]
        public void A_cancelled_generation_throws_and_leaves_no_sidecar_behind()
        {
            Assert.SkipUnless(FFmpegAvailable,
                $"FFmpeg natives not found (set {FFmpegLoader.EnvVarName} or build obs-express-rs): {FFmpegLoader.FailureReason}");
            var exe = FindUsableTractnni();
            Assert.SkipWhen(exe == null,
                "clowd_tractnni.exe with a resolvable ONNX Runtime not found (cargo build -p clowd_tractnni --release, see BUILDING.md).");

            TractnniLoader.Configure(() => exe);
            var source = SourceFor(EncodeSineFixture());
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(() =>
                DenoiseGenerator.Generate(source, 1, _cacheDir, null, cts.Token));
            Assert.False(File.Exists(AiSidecars.DenoisePath(_cacheDir, source.Id, 1)));
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
