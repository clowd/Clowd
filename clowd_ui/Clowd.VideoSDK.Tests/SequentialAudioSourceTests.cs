using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // Real-decode round trip for the render-side audio source: Mp4Writer encodes a sine fixture,
    // SequentialAudioSource decodes it back at the output rate and the sequential pull contract
    // (contiguity, EOF zero-padding, monotonic guard) is asserted. Skips when the FFmpeg natives
    // are absent (same resolver as EncoderTests).
    public class SequentialAudioSourceTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30;
        private const int Rate = 48000;
        private const float Amplitude = 0.25f;
        private const int FixtureSeconds = 1;

        private static bool FFmpegAvailable => TestFFmpeg.Available;


        private static void RequireFFmpeg() =>
            Assert.SkipUnless(FFmpegAvailable,
                TestFFmpeg.SkipReason);

        // ----------------------------------------------------------------------------- fixture

        private readonly List<string> _tempFiles = new List<string>();

        public void Dispose()
        {
            foreach (var f in _tempFiles)
            {
                try { File.Delete(f); }
                catch { /* best effort */ }
            }
        }

        /// <summary>Encodes a 1s mp4 with a 440 Hz stereo sine at 0.25 amplitude (video stream 0,
        /// audio stream 1 — Mp4Writer adds them in that order).</summary>
        private string EncodeSineFixture()
        {
            string path = Path.Combine(Path.GetTempPath(), $"clowd-seqaudio-test-{Guid.NewGuid():N}.mp4");
            _tempFiles.Add(path);

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
                for (int n = 0; n < Fps * FixtureSeconds; n++)
                    writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), W * 4, W, H, n);
            }
            finally
            {
                pin.Free();
            }

            int total = Rate * FixtureSeconds;
            var buf = new float[total * 2];
            for (int i = 0; i < total; i++)
            {
                float s = Amplitude * MathF.Sin(2f * MathF.PI * 440f * i / Rate);
                buf[i * 2] = s;
                buf[i * 2 + 1] = s;
            }
            writer.SubmitAudioSamples(buf, total);
            writer.Finish();
            return path;
        }

        private static Project ProjectFor(string path, out Guid sourceId)
        {
            sourceId = Guid.NewGuid();
            var p = new Project
            {
                Output = new OutputSettings { WidthPx = W, HeightPx = H, FpsNum = Fps, FpsDen = 1, SampleRate = Rate },
            };
            p.Sources.Add(new Source
            {
                Id = sourceId,
                Path = path,
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video, Width = W, Height = H, AvgFrameRateNum = Fps, AvgFrameRateDen = 1 },
                    new SourceStream { Index = 1, Kind = StreamKind.Audio },
                },
            });
            return p;
        }

        private static double Rms(float[] samples, int frames)
        {
            double sum = 0;
            for (int i = 0; i < frames; i++)
            {
                double v = samples[i * 2]; // left channel
                sum += v * v;
            }
            return Math.Sqrt(sum / frames);
        }

        // ------------------------------------------------------------------------------- tests

        [Fact]
        public void Decodes_sine_at_expected_level_and_length()
        {
            RequireFFmpeg();
            var project = ProjectFor(EncodeSineFixture(), out var sourceId);
            using var source = new SequentialAudioSource(project);

            var dst = new float[Rate * 2];
            Assert.True(source.ReadSamples(sourceId, 1, 0, dst, Rate, out int read));

            // aac priming/padding may shave or pad the tail slightly, never by much
            Assert.InRange(read, Rate - 3000, Rate);

            // RMS of a sine at 0.25 amplitude is 0.25/√2 ≈ 0.1768; aac at 160 kb/s keeps that
            // easily within 20% (measure the middle to avoid the encoder's fade-in)
            double rms = Rms(dst[(8000 * 2)..(40000 * 2)], 32000);
            Assert.InRange(rms, 0.14, 0.22);
        }

        [Fact]
        public void Chunked_reads_match_one_contiguous_read()
        {
            RequireFFmpeg();
            string path = EncodeSineFixture();

            var projectA = ProjectFor(path, out var idA);
            using var whole = new SequentialAudioSource(projectA);
            var expected = new float[24000 * 2];
            whole.ReadSamples(idA, 1, 0, expected, 24000, out _);

            var projectB = ProjectFor(path, out var idB);
            using var chunked = new SequentialAudioSource(projectB);
            var actual = new float[24000 * 2];
            var chunk = new float[1000 * 2];
            for (int c = 0; c < 24; c++)
            {
                Assert.True(chunked.ReadSamples(idB, 1, c * 1000, chunk, 1000, out int read));
                Assert.Equal(1000, read);
                Array.Copy(chunk, 0, actual, c * 1000 * 2, 1000 * 2);
            }

            // float aac decode is deterministic — chunk boundaries must be sample-exact
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void Zero_pads_past_end_of_stream()
        {
            RequireFFmpeg();
            var project = ProjectFor(EncodeSineFixture(), out var sourceId);
            using var source = new SequentialAudioSource(project);

            var dst = new float[1000 * 2];
            for (int i = 0; i < dst.Length; i++)
                dst[i] = 99f; // must be overwritten

            Assert.True(source.ReadSamples(sourceId, 1, 3L * Rate, dst, 1000, out int read));
            Assert.Equal(0, read);
            foreach (var v in dst)
                Assert.Equal(0f, v);
        }

        [Fact]
        public void Forward_gap_requests_are_allowed()
        {
            RequireFFmpeg();
            var project = ProjectFor(EncodeSineFixture(), out var sourceId);
            using var source = new SequentialAudioSource(project);

            var dst = new float[1000 * 2];
            Assert.True(source.ReadSamples(sourceId, 1, 0, dst, 1000, out _));

            // jump forward past undecoded material: decode-and-discard, still real data
            Assert.True(source.ReadSamples(sourceId, 1, 30000, dst, 1000, out int read));
            Assert.Equal(1000, read);
            Assert.InRange(Rms(dst, 1000), 0.14, 0.22);
        }

        [Fact]
        public void Regressing_request_repositions_and_unknown_source_throws()
        {
            RequireFFmpeg();
            string path = EncodeSineFixture();

            // reference: the same window reached by decoding forward to it from the start
            var reference = ProjectFor(path, out var refId);
            using var forward = new SequentialAudioSource(reference);
            var expected = new float[1000 * 2];
            Assert.True(forward.ReadSamples(refId, 1, 5000, expected, 1000, out _));
            Assert.Equal(0, forward.RepositionCount);

            var project = ProjectFor(path, out var sourceId);
            using var source = new SequentialAudioSource(project);
            var dst = new float[1000 * 2];
            Assert.True(source.ReadSamples(sourceId, 1, 20000, dst, 1000, out _)); // read ahead ...
            Assert.True(source.ReadSamples(sourceId, 1, 5000, dst, 1000, out int read)); // ... then back
            Assert.Equal(1000, read);
            Assert.Equal(1, source.RepositionCount);

            // aac frames decoded after a mid-stream seek carry decoder state the container cannot
            // reproduce, worth ~1e-5; a one-sample misplacement would move this 440 Hz fixture by
            // ~1.4e-2, two orders of magnitude more than the tolerance.
            for (int i = 0; i < expected.Length; i++)
            {
                if (Math.Abs(expected[i] - dst[i]) > 1e-3f)
                    Assert.Fail($"sample {i / 2} ch{i % 2}: expected {expected[i]}, got {dst[i]} — the reposition is off by at least a sample");
            }

            Assert.Throws<ArgumentException>(
                () => source.ReadSamples(Guid.NewGuid(), 1, 0, dst, 1000, out _));
        }

        [Fact]
        public void Video_stream_index_throws()
        {
            RequireFFmpeg();
            var project = ProjectFor(EncodeSineFixture(), out var sourceId);
            using var source = new SequentialAudioSource(project);

            var dst = new float[100 * 2];
            Assert.Throws<ArgumentException>(
                () => source.ReadSamples(sourceId, 0, 0, dst, 100, out _));
        }
    }
}
