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
    // Real-decode round trip for the preview-side audio source. The gate everywhere here is
    // agreement with the render path: whatever SeekableAudioSource returns must be what
    // SequentialAudioSource decodes forward into the same window. Two bars, deliberately
    // different — the decode-discard paths (plain playback, small cut-seam hops) are asserted
    // bit-identical, because that is the sample-exactness a WYSIWYG preview of a cut rests on;
    // windows reached through a container seek are asserted sample-exact within decoder noise
    // (see SeekNoiseTolerance).
    //
    // The fixture is a chirp, not a steady sine: every sample is distinct, so a reposition that
    // lands a few samples off shows up as an array mismatch instead of hiding inside a periodic
    // waveform. Skips when the FFmpeg natives are absent (same resolver as EncoderTests).
    public class SeekableAudioSourceTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30;
        private const int Rate = 48000;
        private const float Amplitude = 0.25f;
        private const int FixtureSeconds = 4;

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

        // ----------------------------------------------------------------------------- fixture

        private readonly List<string> _tempFiles = new List<string>();
        private string _fixture;

        public void Dispose()
        {
            foreach (var f in _tempFiles)
            {
                try { File.Delete(f); }
                catch { /* best effort */ }
            }
        }

        /// <summary>Encodes (once per test) a 4s mp4 whose audio is a 200→1800 Hz stereo chirp
        /// (video stream 0, audio stream 1 — Mp4Writer adds them in that order).</summary>
        private string ChirpFixture()
        {
            if (_fixture != null)
                return _fixture;

            string path = Path.Combine(Path.GetTempPath(), $"clowd-seekaudio-test-{Guid.NewGuid():N}.mp4");
            _tempFiles.Add(path);

            using (var writer = new Mp4Writer(path, new Mp4WriterOptions
            {
                Width = W,
                Height = H,
                FpsNum = Fps,
                FpsDen = 1,
                Audio = new Mp4AudioOptions { SampleRate = Rate, Channels = 2 },
            }))
            {
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
                    double t = i / (double)Rate;
                    double phase = 2 * Math.PI * (200 * t + 400 * t * t / 2); // 200 Hz sweeping up
                    float s = Amplitude * (float)Math.Sin(phase);
                    buf[i * 2] = s;
                    buf[i * 2 + 1] = 0.5f * s;
                }
                writer.SubmitAudioSamples(buf, total);
                writer.Finish();
            }

            _fixture = path;
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

        /// <summary>Reads through the render source — the reference every seek path is held to.</summary>
        private static float[] ForwardRead(string path, long posFrames, int frames,
            params (long Pos, int Frames)[] before)
        {
            var project = ProjectFor(path, out var sourceId);
            using var source = new SequentialAudioSource(project);

            foreach (var (p, f) in before)
                source.ReadSamples(sourceId, 1, p, new float[f * 2], f, out _);

            var dst = new float[frames * 2];
            Assert.True(source.ReadSamples(sourceId, 1, posFrames, dst, frames, out _));
            return dst;
        }

        private static float[] Read(SeekableAudioSource source, Guid sourceId, long posFrames, int frames)
        {
            var dst = new float[frames * 2];
            Assert.True(source.ReadSamples(sourceId, 1, posFrames, dst, frames, out _));
            return dst;
        }

        private static void AssertSamplesEqual(float[] expected, float[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                    Assert.Fail($"sample {i / 2} ch{i % 2}: expected {expected[i]}, got {actual[i]} (max diff over the window {MaxDiff(expected, actual)})");
            }
        }

        private static float MaxDiff(float[] a, float[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            float max = 0;
            for (int i = 0; i < n; i++)
                max = Math.Max(max, Math.Abs(a[i] - b[i]));
            return max;
        }

        /// <summary>
        /// The bar for a window reached through a container seek: <b>sample-exact</b>, i.e. every
        /// sample lands on the position a forward decode put it at, to within decoder noise.
        /// It cannot be bit-exact — an AAC frame decoded after a mid-stream seek carries decoder
        /// state the container cannot reproduce (noise substitution is driven by a generator that
        /// evolves per decoded frame), which perturbs low-energy bands by ~1e-5. A misplacement of
        /// even one sample would show up two orders of magnitude larger (the fixture chirp moves
        /// ~1.3e-2 per sample at 0.5 s, more later), which
        /// <see cref="AssertMisalignmentWouldBeCaught"/> checks explicitly.
        /// </summary>
        private const float SeekNoiseTolerance = 1e-3f;

        private static void AssertSamplesClose(float[] expected, float[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);
            float diff = MaxDiff(expected, actual);
            Assert.True(diff <= SeekNoiseTolerance,
                $"max deviation {diff} exceeds {SeekNoiseTolerance} — the reposition is off by at least a sample");
            AssertMisalignmentWouldBeCaught(expected, actual);
        }

        /// <summary>Guards the tolerance itself: the same window compared one sample out of step
        /// must blow past it, so <see cref="AssertSamplesClose"/> is really measuring position.</summary>
        private static void AssertMisalignmentWouldBeCaught(float[] expected, float[] actual)
        {
            var shifted = new float[actual.Length];
            Array.Copy(actual, 2, shifted, 0, actual.Length - 2);
            Array.Copy(expected, expected.Length - 2, shifted, actual.Length - 2, 2);
            Assert.True(MaxDiff(expected, shifted) > SeekNoiseTolerance,
                "a one-sample shift stays inside the tolerance — this window cannot detect misalignment");
        }

        private static double Rms(float[] samples, int frames)
        {
            double sum = 0;
            for (int i = 0; i < frames; i++)
            {
                double v = samples[i * 2];
                sum += v * v;
            }
            return Math.Sqrt(sum / frames);
        }

        // ------------------------------------------------------------------------------- tests

        [Fact]
        public void Forward_sequential_reads_match_the_render_source()
        {
            RequireFFmpeg();
            string path = ChirpFixture();

            var expected = ForwardRead(path, 0, 144000); // 3 s in one pull

            var project = ProjectFor(path, out var sourceId);
            using var seekable = new SeekableAudioSource(project);
            var actual = new float[144000 * 2];
            var chunk = new float[960 * 2]; // 20 ms, the preview mixer's chunk size
            for (int c = 0; c < 150; c++)
            {
                Assert.True(seekable.ReadSamples(sourceId, 1, c * 960L, chunk, 960, out int read));
                Assert.Equal(960, read);
                Array.Copy(chunk, 0, actual, c * 960 * 2, 960 * 2);
            }

            AssertSamplesEqual(expected, actual);
            Assert.Equal(0, seekable.RepositionCount); // pure forward playback never seeks
            Assert.InRange(Rms(actual, 144000), 0.14, 0.22);
        }

        [Fact]
        public void Backward_reposition_matches_a_fresh_forward_read()
        {
            RequireFFmpeg();
            string path = ChirpFixture();

            var expected = ForwardRead(path, 24000, 4800); // 0.5 s .. 0.6 s

            var project = ProjectFor(path, out var sourceId);
            using var seekable = new SeekableAudioSource(project);

            Read(seekable, sourceId, 120000, 4800); // play 2.5 s in ...
            var actual = Read(seekable, sourceId, 24000, 4800); // ... then scrub back to 0.5 s

            AssertSamplesClose(expected, actual);
            Assert.Equal(2, seekable.RepositionCount); // the 2.5 s entry hop, then the rewind
        }

        [Fact]
        public void Large_forward_jump_matches_the_decode_discard_path()
        {
            RequireFFmpeg();
            string path = ChirpFixture();

            // reference: the render source decode-discards its way to 3 s after an initial read
            var expected = ForwardRead(path, 144000, 4800, (0L, 960));

            var project = ProjectFor(path, out var sourceId);
            using var seekable = new SeekableAudioSource(project);

            Read(seekable, sourceId, 0, 960);
            var actual = Read(seekable, sourceId, 144000, 4800); // 3 s ahead of the head: seeks

            AssertSamplesClose(expected, actual);
            Assert.Equal(1, seekable.RepositionCount);
        }

        [Fact]
        public void Small_forward_jump_keeps_decoding_and_discarding()
        {
            RequireFFmpeg();
            string path = ChirpFixture();

            // a cut seam hops forward inside the same stream; staying on the decode-discard path
            // is what keeps those samples identical to the render's.
            var expected = ForwardRead(path, 48000, 4800, (0L, 960));

            var project = ProjectFor(path, out var sourceId);
            using var seekable = new SeekableAudioSource(project);

            Read(seekable, sourceId, 0, 960);
            var actual = Read(seekable, sourceId, 48000, 4800); // 1 s ahead: under the threshold

            AssertSamplesEqual(expected, actual);
            Assert.Equal(0, seekable.RepositionCount);
        }

        [Fact]
        public void Repeating_a_read_is_served_from_the_retained_window()
        {
            RequireFFmpeg();
            string path = ChirpFixture();

            var project = ProjectFor(path, out var sourceId);
            using var seekable = new SeekableAudioSource(project);

            // a mixer swapped in mid-chunk (volume edit) re-reads the chunk it was built for
            var first = Read(seekable, sourceId, 120000, 960);
            var again = Read(seekable, sourceId, 120000, 960);

            AssertSamplesEqual(first, again);
            Assert.Equal(1, seekable.RepositionCount); // the entry hop only
        }

        [Fact]
        public void Reset_forces_the_next_read_to_reposition()
        {
            RequireFFmpeg();
            string path = ChirpFixture();

            var expected = ForwardRead(path, 960, 960);

            var project = ProjectFor(path, out var sourceId);
            using var seekable = new SeekableAudioSource(project);

            Read(seekable, sourceId, 0, 960);
            Assert.Equal(0, seekable.RepositionCount);

            seekable.Reset(); // player seeked: forget the decode head
            var actual = Read(seekable, sourceId, 960, 960);

            Assert.Equal(1, seekable.RepositionCount);
            AssertSamplesClose(expected, actual);
        }

        [Fact]
        public void Zero_pads_past_end_of_stream_and_can_scrub_back()
        {
            RequireFFmpeg();
            string path = ChirpFixture();

            var project = ProjectFor(path, out var sourceId);
            using var seekable = new SeekableAudioSource(project);

            var dst = new float[1000 * 2];
            for (int i = 0; i < dst.Length; i++)
                dst[i] = 99f; // must be overwritten

            Assert.True(seekable.ReadSamples(sourceId, 1, 10L * Rate, dst, 1000, out int read));
            Assert.Equal(0, read);
            foreach (var v in dst)
                Assert.Equal(0f, v);

            // EOF is not terminal for the preview source: scrubbing back plays again
            var expected = ForwardRead(path, 48000, 4800);
            AssertSamplesClose(expected, Read(seekable, sourceId, 48000, 4800));
        }

        [Fact]
        public void Decoder_seek_matches_a_fresh_decoders_output_at_that_position()
        {
            RequireFFmpeg();
            string path = ChirpFixture();

            using var fresh = new SyncAudioStreamDecoder(path, 1, Rate);
            var expected = CollectFrom(fresh, 48000, 4800); // 1.0 s .. 1.1 s

            using var seeked = new SyncAudioStreamDecoder(path, 1, Rate);
            CollectFrom(seeked, 144000, 4800); // decode out to 3 s first
            seeked.Seek(10_000_000 - 2_500_000); // back to 0.75 s (1 s minus the source's preroll)
            var actual = CollectFrom(seeked, 48000, 4800);

            AssertSamplesClose(expected, actual);
            Assert.NotEqual(0.0, Rms(expected, 4800)); // guard: not comparing two silences
        }

        /// <summary>Decodes forward collecting the window [startSample, startSample+frames) —
        /// the anchor-then-accumulate positioning the sources use, in miniature.</summary>
        private static float[] CollectFrom(SyncAudioStreamDecoder decoder, long startSample, int frames)
        {
            var result = new float[frames * 2];
            long end = startSample + frames;
            long writeAbs = 0;
            bool positioned = false;

            while (decoder.DecodeNext(out long ptsTicks, out float[] samples, out int n))
            {
                if (n <= 0)
                    continue;

                if (!positioned)
                {
                    writeAbs = ptsTicks == long.MinValue ? 0 : ptsTicks * Rate / TimeBase.TicksPerSecond;
                    positioned = true;
                }

                long s = Math.Max(writeAbs, startSample);
                long e = Math.Min(writeAbs + n, end);
                if (e > s)
                    Array.Copy(samples, (int)(s - writeAbs) * 2, result, (int)(s - startSample) * 2, (int)(e - s) * 2);

                writeAbs += n;
                if (writeAbs >= end)
                    break;
            }

            return result;
        }
    }
}
