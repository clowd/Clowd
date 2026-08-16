using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // The preview's audio producer, driven directly against a bare AudioRingBuffer and an
    // NAudioSink over SilentAudioOutput (never started) — no real device anywhere; the test
    // thread plays the ring's consumer. The stakes: the worker mixes with the LITERAL render
    // mixer over seekable decoders, so its output must be the render's mix (the parity test at
    // the bottom asserts bit-exact equality), volume/transition edits must swap the mixer
    // without touching a decoder, and seeks must restart the sink's media time at the timeline
    // target. Skips when the FFmpeg natives are absent (same resolver as EncoderTests).
    public class AudioMixWorkerTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30;
        private const int Rate = 48000;
        private const long Second = 10_000_000;
        private const int Chunk = Rate / 50; // the worker's 20ms chunk

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

        /// <summary>Encodes an mp4 whose audio is a steady stereo sine (video stream 0, audio
        /// stream 1 — Mp4Writer adds them in that order; both channels equal).</summary>
        private string SineFixture(double freq, float amplitude, int seconds)
        {
            string path = Path.Combine(Path.GetTempPath(), $"clowd-amix-test-{Guid.NewGuid():N}.mp4");
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
                for (int n = 0; n < Fps * seconds; n++)
                    writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), W * 4, W, H, n);
            }
            finally
            {
                pin.Free();
            }

            int total = Rate * seconds;
            var buf = new float[total * 2];
            for (int i = 0; i < total; i++)
            {
                float s = amplitude * (float)Math.Sin(2 * Math.PI * freq * i / Rate);
                buf[i * 2] = s;
                buf[i * 2 + 1] = s;
            }
            writer.SubmitAudioSamples(buf, total);
            writer.Finish();
            return path;
        }

        private static Project NewProject() => new Project
        {
            Output = new OutputSettings { WidthPx = W, HeightPx = H, FpsNum = Fps, FpsDen = 1, SampleRate = Rate },
        };

        private static Guid AddSource(Project project, string path)
        {
            var id = Guid.NewGuid();
            project.Sources.Add(new Source
            {
                Id = id,
                Path = path,
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video, Width = W, Height = H, AvgFrameRateNum = Fps, AvgFrameRateDen = 1 },
                    new SourceStream { Index = 1, Kind = StreamKind.Audio },
                },
            });
            return id;
        }

        private static Item AddAudioItem(Project project, Guid sourceId, long startTicks,
            long durationTicks, long sourceInTicks = 0, double volume = 1.0)
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
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Volume = volume,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 1, SourceInTicks = sourceInTicks },
            };
            project.Items.Add(item);
            return item;
        }

        // ------------------------------------------------------------------------------ helpers

        /// <summary>Ring + sink over a silent output that is never started — the test thread is
        /// the ring's single consumer, standing in for the device callback.</summary>
        private static (AudioRingBuffer Ring, NAudioSink Sink) NewSink()
        {
            var ring = new AudioRingBuffer(Rate); // same ~500ms the player allocates
            var sink = new NAudioSink(Rate, 2, ring, new SilentAudioOutput());
            return (ring, sink);
        }

        /// <summary>Consumes exactly <paramref name="frames"/> frames from the ring (the worker
        /// produces concurrently). Fails rather than hangs when the worker stops producing.</summary>
        private static float[] DrainFrames(AudioRingBuffer ring, AudioMixWorker worker, int frames,
            int timeoutMs = 20000)
        {
            var dst = new float[frames * 2];
            int got = 0;
            var sw = Stopwatch.StartNew();
            while (got < dst.Length)
            {
                int read = ring.Read(dst.AsSpan(got));
                got += read;
                if (got >= dst.Length)
                    break;
                if (read == 0)
                {
                    if (worker.EofReached && ring.Available == 0)
                        Assert.Fail($"worker hit EOF after {got / 2} of {frames} frames (error: {worker.Error?.Message ?? "none"})");
                    if (sw.ElapsedMilliseconds > timeoutMs)
                        Assert.Fail($"worker produced only {got / 2} of {frames} frames in {timeoutMs}ms (error: {worker.Error?.Message ?? "none"})");
                    Thread.Sleep(2);
                }
            }

            return dst;
        }

        /// <summary>Consumes until the worker reports EOF and the ring is empty; returns
        /// everything read.</summary>
        private static float[] DrainToEof(AudioRingBuffer ring, AudioMixWorker worker,
            int maxFrames, out int framesRead, int timeoutMs = 30000)
        {
            var dst = new float[maxFrames * 2];
            int got = 0;
            var sw = Stopwatch.StartNew();
            while (true)
            {
                int read = got < dst.Length ? ring.Read(dst.AsSpan(got)) : 0;
                got += read;
                if (read == 0)
                {
                    if (worker.EofReached && ring.Available == 0)
                        break;
                    Assert.True(sw.ElapsedMilliseconds < timeoutMs,
                        $"no EOF after {timeoutMs}ms ({got / 2} frames read, error: {worker.Error?.Message ?? "none"})");
                    Thread.Sleep(2);
                }
            }

            framesRead = got / 2;
            return dst;
        }

        /// <summary>RMS of the left channel over [firstFrame, firstFrame + frames).</summary>
        private static double Rms(float[] interleaved, int firstFrame, int frames)
        {
            double sum = 0;
            for (int i = 0; i < frames; i++)
            {
                double v = interleaved[(firstFrame + i) * 2];
                sum += v * v;
            }
            return Math.Sqrt(sum / frames);
        }

        // -------------------------------------------------------------------------------- tests

        [Fact]
        public void Two_tracks_mix_to_the_sum()
        {
            RequireFFmpeg();

            // distinct amplitudes AND frequencies: uncorrelated sines sum in power, so the mixed
            // RMS pins both contributions (√(0.3²/2 + 0.2²/2) ≈ 0.255) — a dropped track lands
            // at 0.21/0.14, well outside the window.
            var project = NewProject();
            var a = AddSource(project, SineFixture(440, 0.30f, 2));
            var b = AddSource(project, SineFixture(1000, 0.20f, 2));
            AddAudioItem(project, a, 0, 2 * Second);
            AddAudioItem(project, b, 0, 2 * Second);

            var (ring, sink) = NewSink();
            using (sink)
            using (var worker = new AudioMixWorker(project, ring, sink, Rate))
            {
                worker.Start();
                var mix = DrainFrames(ring, worker, 2 * Rate);

                Assert.InRange(Rms(mix, Rate / 4, Rate), 0.23, 0.28);
                Assert.Null(worker.Error);
            }
        }

        [Fact]
        public void Timeline_gap_mixes_exact_silence()
        {
            RequireFFmpeg();

            var project = NewProject();
            var a = AddSource(project, SineFixture(440, 0.30f, 2));
            AddAudioItem(project, a, 0, Second);                        // [0, 1s)
            AddAudioItem(project, a, 2 * Second, Second, Second);       // [2s, 3s) ← source [1s, 2s)

            var (ring, sink) = NewSink();
            using (sink)
            using (var worker = new AudioMixWorker(project, ring, sink, Rate))
            {
                worker.Start();
                var mix = DrainToEof(ring, worker, 4 * Rate, out int frames);
                Assert.Equal(3 * Rate, frames); // production stops at the audio end

                // nothing covers the gap: the mixer writes literal zeros, not near-silence
                for (int f = Rate; f < 2 * Rate; f++)
                {
                    if (mix[f * 2] != 0f || mix[f * 2 + 1] != 0f)
                        Assert.Fail($"gap frame {f} is not silent: {mix[f * 2]}");
                }

                Assert.InRange(Rms(mix, Rate / 4, Rate / 2), 0.18, 0.24);          // inside item 1
                Assert.InRange(Rms(mix, 2 * Rate + Rate / 4, Rate / 2), 0.18, 0.24); // inside item 2
            }
        }

        [Fact]
        public void Unequal_stream_lengths_silence_after_the_short_one_and_eof_at_audio_end()
        {
            RequireFFmpeg();

            var project = NewProject();
            var a = AddSource(project, SineFixture(440, 0.30f, 2));
            var b = AddSource(project, SineFixture(1000, 0.20f, 3));
            AddAudioItem(project, a, 0, Second);          // ends at 1s
            AddAudioItem(project, b, 0, 2 * Second);      // ends at 2s = GetAudioEndTicks

            Assert.Equal(2 * Second, AudioMixer.GetAudioEndTicks(project));

            var (ring, sink) = NewSink();
            using (sink)
            using (var worker = new AudioMixWorker(project, ring, sink, Rate))
            {
                worker.Start();
                var mix = DrainToEof(ring, worker, 3 * Rate, out int frames);

                Assert.Equal(2 * Rate, frames); // EofReached exactly at the audio end
                Assert.True(worker.EofReached);
                Assert.InRange(Rms(mix, Rate / 4, Rate / 2), 0.23, 0.28);        // both tracks
                Assert.InRange(Rms(mix, Rate + Rate / 4, Rate / 2), 0.12, 0.16); // b alone after a ends
            }
        }

        [Theory]
        [InlineData(2.0)]
        [InlineData(0.5)]
        public void Speed_resamples_the_timeline_into_the_device_chunk(double speed)
        {
            RequireFFmpeg();

            // a 2s item at 2x is 1s of device audio (and 4s at 0.5x): the worker consumes the
            // timeline at the speed and resamples it back onto the device's own rate.
            var project = NewProject();
            var a = AddSource(project, SineFixture(440, 0.30f, 2));
            AddAudioItem(project, a, 0, 2 * Second);

            var (ring, sink) = NewSink();
            using (sink)
            using (var worker = new AudioMixWorker(project, ring, sink, Rate))
            {
                worker.SetSpeed(speed);
                worker.Start();

                int expected = (int)(2 * Rate / speed);
                var mix = DrainToEof(ring, worker, expected + Rate, out int frames);

                // the tail rounds within one 20ms chunk of the resampler's cursor
                Assert.InRange(frames, expected - Chunk, expected + Chunk);
                Assert.InRange(Rms(mix, Rate / 4, Rate / 2), 0.18, 0.24); // resampled, not silence
                Assert.Null(worker.Error);
            }
        }

        [Fact]
        public void PrepareSeek_restarts_the_base_pts_at_the_target()
        {
            RequireFFmpeg();

            string path = SineFixture(440, 0.30f, 4);
            var project = NewProject();
            var a = AddSource(project, path);
            AddAudioItem(project, a, 0, 4 * Second);

            var (ring, sink) = NewSink();
            using (sink)
            using (var worker = new AudioMixWorker(project, ring, sink, Rate))
            {
                worker.Start();
                DrainFrames(ring, worker, Rate / 2); // play 0.5s in
                Assert.Equal(0, sink.PlayedTime.Ticks); // nothing consumed through the sink itself

                worker.PrepareSeek(new TimeSpan(3 * Second / 2));

                // the flush resets sink timing and the first post-seek chunk re-bases media time
                // on the timeline target directly (1.5s lands exactly on a sample boundary).
                var sw = Stopwatch.StartNew();
                while (sink.PlayedTime.Ticks != 3 * Second / 2 && sw.ElapsedMilliseconds < 5000)
                    Thread.Sleep(5);
                Assert.Equal(3 * Second / 2, sink.PlayedTime.Ticks);

                // and the samples now in the ring are the source's 1.5s mark: compare against the
                // render source decoding forward into the same window. A container seek is
                // sample-exact within decoder noise, not bit-exact (see SeekableAudioSourceTests);
                // a one-sample misplacement of this sine moves ~1.7e-2, two orders above the bar.
                var reference = new float[4800 * 2];
                using (var seq = new SequentialAudioSource(project))
                    Assert.True(seq.ReadSamples(a, 1, 3 * Rate / 2, reference, 4800, out _));

                var actual = DrainFrames(ring, worker, 4800);
                for (int i = 0; i < reference.Length; i++)
                {
                    if (Math.Abs(reference[i] - actual[i]) > 1e-3f)
                        Assert.Fail($"sample {i / 2} ch{i % 2} after seek: expected {reference[i]}, got {actual[i]}");
                }

                Assert.Null(worker.Error);
            }
        }

        [Fact]
        public void UpdateProject_volume_swap_changes_gain_without_touching_decoders()
        {
            RequireFFmpeg();

            string path = SineFixture(440, 0.30f, 4);
            var project = NewProject();
            var a = AddSource(project, path);
            AddAudioItem(project, a, 0, 4 * Second);

            var (ring, sink) = NewSink();
            using (sink)
            using (var worker = new AudioMixWorker(project, ring, sink, Rate))
            {
                worker.Start();
                var before = DrainFrames(ring, worker, Rate / 2);
                double fullRms = Rms(before, Rate / 4, Rate / 8);
                Assert.InRange(fullRms, 0.18, 0.24);

                int opens = worker.DecoderOpenCount;
                Assert.Equal(1, opens);

                // the edit arrives as a fresh snapshot, exactly as the player hands it over
                var edited = Project.FromJson(project.ToJson());
                edited.Items[0].Volume = 0.25;
                worker.UpdateProject(edited);

                // samples mixed before the swap are already in flight (up to the buffered lead);
                // scan forward until the new gain shows up, then hold it.
                int quiet = -1;
                for (int c = 0; c < Rate / Chunk; c++) // up to 1s of chunks
                {
                    var chunk = DrainFrames(ring, worker, Chunk);
                    if (Rms(chunk, 0, Chunk) < fullRms * 0.5)
                    {
                        quiet = c;
                        break;
                    }
                }

                Assert.True(quiet >= 0, "gain never dropped after the volume edit");
                var after = DrainFrames(ring, worker, Rate / 4);
                Assert.InRange(Rms(after, 0, Rate / 4), fullRms * 0.25 * 0.8, fullRms * 0.25 * 1.2);

                Assert.Equal(opens, worker.DecoderOpenCount); // the cheap path: no decoder touched
                Assert.Null(worker.Error);
            }
        }

        [Fact]
        public void Update_that_extends_audio_after_eof_rebases_via_prepare_seek()
        {
            RequireFFmpeg();

            // The player's protocol for an edit that revives an EOF-idle worker
            // (CompositionPlayer.ApplyMappingSwap): adopt the project, then flush onto the live
            // position. The sink must re-base its media time on that position — never resume at
            // the old audio end with the stale timing base (which would yank the master clock
            // backwards on re-attach).
            string path = SineFixture(440, 0.30f, 4);
            var project = NewProject();
            var a = AddSource(project, path);
            AddAudioItem(project, a, 0, Second);

            var (ring, sink) = NewSink();
            using (sink)
            using (var worker = new AudioMixWorker(project, ring, sink, Rate))
            {
                worker.Start();
                DrainToEof(ring, worker, 2 * Rate, out int frames);
                Assert.Equal(Rate, frames);
                Assert.True(worker.EofReached);

                var edited = Project.FromJson(project.ToJson());
                edited.Items[0].DurationTicks = 3 * Second;
                worker.UpdateProject(edited);
                worker.PrepareSeek(new TimeSpan(2 * Second)); // playhead well past the old end

                // the mix thread adopts the project first (growing the end), then the seek —
                // re-basing production and sink timing on the playhead, not the old audio end.
                var sw = Stopwatch.StartNew();
                while (sink.PlayedTime.Ticks != 2 * Second && sw.ElapsedMilliseconds < 5000)
                    Thread.Sleep(5);
                Assert.Equal(2 * Second, sink.PlayedTime.Ticks);

                // and the produced samples are the source's 2s mark, not a resume at 1s (same
                // container-seek tolerance as the PrepareSeek test: sample-exact within decoder
                // noise; a one-sample misplacement of this sine moves ~1.7e-2).
                var reference = new float[4800 * 2];
                using (var seq = new SequentialAudioSource(edited))
                    Assert.True(seq.ReadSamples(a, 1, 2 * Rate, reference, 4800, out _));

                var actual = DrainFrames(ring, worker, 4800);
                for (int i = 0; i < reference.Length; i++)
                {
                    if (Math.Abs(reference[i] - actual[i]) > 1e-3f)
                        Assert.Fail($"sample {i / 2} ch{i % 2} after revive: expected {reference[i]}, got {actual[i]}");
                }

                Assert.Null(worker.Error);
            }
        }

        [Fact]
        public void Missing_file_mixes_silence_and_surfaces_the_error()
        {
            RequireFFmpeg();

            var project = NewProject();
            var good = AddSource(project, SineFixture(440, 0.30f, 2));
            var missing = AddSource(project, Path.Combine(Path.GetTempPath(), $"clowd-amix-missing-{Guid.NewGuid():N}.mp4"));
            AddAudioItem(project, good, 0, 2 * Second);
            AddAudioItem(project, missing, 0, 2 * Second);

            var (ring, sink) = NewSink();
            using (sink)
            using (var worker = new AudioMixWorker(project, ring, sink, Rate))
            {
                worker.Start();
                var mix = DrainToEof(ring, worker, 3 * Rate, out int frames);

                Assert.Equal(2 * Rate, frames);            // the healthy stream played to its end
                Assert.NotNull(worker.Error);              // the failure is surfaced, not swallowed
                Assert.InRange(Rms(mix, Rate / 4, Rate), 0.18, 0.24); // good stream alone, unharmed
            }
        }

        [Fact]
        public void Project_without_a_usable_rate_mixes_at_the_pipeline_rate()
        {
            RequireFFmpeg();

            // Project.Validate rejects a non-positive sample rate, so this only reaches the worker
            // through OpenAsync on a hand-built/partly-loaded project — but if the re-wrap missed
            // any field, the mixer would place items with a rate of zero (or lose the sources) and
            // the mix would be silent or misplaced rather than merely mis-rated. Bit-exact against
            // the same project carrying the rate proves the shallow re-wrap is complete.
            string path = SineFixture(440, 0.30f, 2);
            var rated = NewProject();
            var a = AddSource(rated, path);
            AddAudioItem(rated, a, 0, 2 * Second);

            var unrated = Project.FromJson(rated.ToJson());
            unrated.Output.SampleRate = 0;

            var (ring, sink) = NewSink();
            using (sink)
            using (var worker = new AudioMixWorker(unrated, ring, sink, Rate))
            {
                worker.Start();
                var actual = DrainToEof(ring, worker, 3 * Rate, out int frames);
                Assert.Equal(2 * Rate, frames); // item length measured at the pipeline rate

                var expected = new float[2 * Rate * 2];
                using (var seq = new SequentialAudioSource(rated))
                {
                    var mixer = new AudioMixer(rated, seq);
                    var chunk = new float[Chunk * 2];
                    for (int f = 0; f < 2 * Rate; f += Chunk)
                    {
                        int n = Math.Min(Chunk, 2 * Rate - f);
                        mixer.MixChunk(f, n, chunk);
                        Array.Copy(chunk, 0, expected, f * 2, n * 2);
                    }
                }

                for (int i = 0; i < expected.Length; i++)
                {
                    if (expected[i] != actual[i])
                        Assert.Fail($"sample {i / 2} ch{i % 2}: rated mix {expected[i]}, unrated mix {actual[i]}");
                }

                Assert.Null(worker.Error);
            }
        }

        // ----------------------------------------------------------------- render/preview parity

        [Fact]
        public void Worker_output_is_bit_exact_with_the_render_mix()
        {
            RequireFFmpeg();

            // A project exercising everything the gain math covers: a cut seam (decode-discard
            // stays on the render path inside the source), per-item volume, an entry fade ramp,
            // and a second overlapping track. Reference = the literal render pass (AudioMixer
            // over SequentialAudioSource) in the worker's own 20ms chunking; both sides run the
            // same mixer code over sources that decode the same samples, so the outputs must be
            // IDENTICAL — bit-exact, not just close.
            string pathA = SineFixture(440, 0.30f, 4);
            string pathB = SineFixture(1000, 0.20f, 2);

            var project = NewProject();
            var a = AddSource(project, pathA);
            var b = AddSource(project, pathB);
            var first = AddAudioItem(project, a, 0, Second);                              // [0, 1s) ← A[0, 1s)
            AddAudioItem(project, a, Second, 3 * Second / 2, 3 * Second / 2, volume: 0.7); // [1s, 2.5s) ← A[1.5s, 3s)
            AddAudioItem(project, b, 3 * Second / 10, 3 * Second / 2, Second / 5);         // [0.3s, 1.8s) ← B[0.2s, 1.7s)
            first.Entry = new Transition
            {
                Kind = TransitionKind.Fade,
                DurationTicks = Second / 2,
                Easing = TransitionEasing.CubicOut,
            };

            int totalFrames = (int)(5 * Rate / 2); // timeline runs to 2.5s
            var expected = new float[totalFrames * 2];
            using (var seq = new SequentialAudioSource(project))
            {
                var mixer = new AudioMixer(project, seq);
                var chunk = new float[Chunk * 2];
                for (int f = 0; f < totalFrames; f += Chunk)
                {
                    int frames = Math.Min(Chunk, totalFrames - f);
                    mixer.MixChunk(f, frames, chunk);
                    Array.Copy(chunk, 0, expected, f * 2, frames * 2);
                }
            }

            var (ring, sink) = NewSink();
            using (sink)
            using (var worker = new AudioMixWorker(project, ring, sink, Rate))
            {
                worker.Start();
                var actual = DrainToEof(ring, worker, totalFrames + Rate, out int frames);
                Assert.Equal(totalFrames, frames);

                for (int i = 0; i < totalFrames * 2; i++)
                {
                    if (expected[i] != actual[i])
                        Assert.Fail($"sample {i / 2} ch{i % 2}: render mixed {expected[i]}, preview mixed {actual[i]}");
                }

                Assert.Null(worker.Error);
            }
        }
    }
}
