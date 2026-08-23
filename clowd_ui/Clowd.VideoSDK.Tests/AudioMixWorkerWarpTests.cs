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
    // AudioMixWorker × TimeWarp: under a warp the worker's stream domain is output time — each
    // device frame's project position comes from the warp's spans and the mixed project frames
    // are resampled per-sample onto the output grid, mirroring Render.WarpAudioResampler. The
    // stakes: an identity warp must be an exact no-op, speed-1 spans of a warped timeline must
    // stay verbatim copies of the project mix (preview/render parity), a 2x span must halve the
    // produced sample count, a ramp must glide the pitch continuously, and a seek must re-base
    // the sink's media time at the target's OUTPUT instant (the domain the player's clock runs
    // in). Same harness as AudioMixWorkerTests: bare ring + NAudioSink over SilentAudioOutput,
    // the test thread playing the device. Skips without the FFmpeg natives.
    public class AudioMixWorkerWarpTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30;
        private const int Rate = 48000;
        private const long Second = 10_000_000;
        private const int Chunk = Rate / 50; // the worker's 20ms chunk

        private static bool FFmpegAvailable => TestFFmpeg.Available;


        private static void RequireFFmpeg() =>
            Assert.SkipUnless(FFmpegAvailable,
                TestFFmpeg.SkipReason);

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

        private string SineFixture(double freq, float amplitude, int seconds)
        {
            string path = Path.Combine(Path.GetTempPath(), $"clowd-amix-warp-test-{Guid.NewGuid():N}.mp4");
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

        private static void AddAudioItem(Project project, Guid sourceId, long startTicks, long durationTicks)
        {
            var track = new Track
            {
                Id = Guid.NewGuid(),
                Kind = TrackKind.Audio,
                Order = project.Tracks.Count,
            };
            project.Tracks.Add(track);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 1 },
            });
        }

        private static void AddSpeedItem(Project project, long start, long duration, double factor,
            Transition entry = null, Transition exit = null)
        {
            var track = new Track
            {
                Id = Guid.NewGuid(),
                Kind = TrackKind.Effect,
                Name = "Speed",
                Order = project.Tracks.Count,
            };
            project.Tracks.Add(track);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = start,
                DurationTicks = duration,
                Content = new SpeedContent { Factor = factor },
                Entry = entry,
                Exit = exit,
            });
        }

        // ------------------------------------------------------------------------------ helpers

        private static (AudioRingBuffer Ring, NAudioSink Sink) NewSink()
        {
            var ring = new AudioRingBuffer(Rate);
            var sink = new NAudioSink(Rate, 2, ring, new SilentAudioOutput());
            return (ring, sink);
        }

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

        /// <summary>The straight (unwarped) render mix over the timeline's first
        /// <paramref name="frames"/> sample frames, in the worker's own 20ms chunking.</summary>
        private static float[] ReferenceMix(Project project, int frames)
        {
            var expected = new float[frames * 2];
            using var seq = new SequentialAudioSource(project);
            var mixer = new AudioMixer(project, seq);
            var chunk = new float[Chunk * 2];
            for (int f = 0; f < frames; f += Chunk)
            {
                int n = Math.Min(Chunk, frames - f);
                mixer.MixChunk(f, n, chunk);
                Array.Copy(chunk, 0, expected, f * 2, n * 2);
            }

            return expected;
        }

        /// <summary>Left-channel sign changes over output frames
        /// [<paramref name="firstFrame"/>, <paramref name="firstFrame"/> + <paramref name="frames"/>).</summary>
        private static int ZeroCrossings(float[] interleaved, int firstFrame, int frames)
        {
            int crossings = 0;
            for (int i = 1; i < frames; i++)
            {
                float a = interleaved[(firstFrame + i - 1) * 2];
                float b = interleaved[(firstFrame + i) * 2];
                if ((a < 0f && b >= 0f) || (a >= 0f && b < 0f))
                    crossings++;
            }

            return crossings;
        }

        // -------------------------------------------------------------------------------- tests

        [Fact]
        public void Identity_warp_is_a_bit_exact_pass_through()
        {
            RequireFFmpeg();

            var project = NewProject();
            var a = AddSource(project, SineFixture(440, 0.30f, 2));
            AddAudioItem(project, a, 0, 2 * Second);

            var warp = TimeWarp.Build(project);
            Assert.True(warp.IsIdentity);

            var expected = ReferenceMix(project, 2 * Rate);

            var (ring, sink) = NewSink();
            using (sink)
            using (var worker = new AudioMixWorker(project, ring, sink, Rate, warp))
            {
                worker.Start();
                var actual = DrainToEof(ring, worker, 3 * Rate, out int frames);
                Assert.Equal(2 * Rate, frames);

                for (int i = 0; i < 2 * Rate * 2; i++)
                {
                    if (expected[i] != actual[i])
                        Assert.Fail($"sample {i / 2} ch{i % 2}: unwarped mix {expected[i]}, identity-warp mix {actual[i]}");
                }

                Assert.Null(worker.Error);
            }
        }

        [Fact]
        public void Speed_one_spans_stay_sample_exact_around_a_2x_span()
        {
            RequireFFmpeg();

            // 3s of audio with a 2x span over [1s, 2s): output runs 1s + 0.5s + 1s. The spans at
            // speed 1 must be VERBATIM copies of the project mix — the leading one at offset 0,
            // the trailing one at the span's constant offset of half a second (24000 frames) —
            // because unwarped audio keeps bit-exact preview/render parity even when a speed
            // item bends time elsewhere on the timeline.
            var project = NewProject();
            var a = AddSource(project, SineFixture(440, 0.30f, 3));
            AddAudioItem(project, a, 0, 3 * Second);
            AddSpeedItem(project, Second, Second, 2.0);

            var warp = TimeWarp.Build(project);
            Assert.Equal(5 * Second / 2, warp.OutputDurationTicks);

            var expected = ReferenceMix(project, 3 * Rate);
            int totalOut = 5 * Rate / 2;

            var (ring, sink) = NewSink();
            using (sink)
            using (var worker = new AudioMixWorker(project, ring, sink, Rate, warp))
            {
                worker.Start();
                var actual = DrainToEof(ring, worker, totalOut + Rate, out int frames);
                Assert.InRange(frames, totalOut - Chunk, totalOut);

                for (int o = 0; o < Rate; o++)
                {
                    if (expected[o * 2] != actual[o * 2] || expected[o * 2 + 1] != actual[o * 2 + 1])
                        Assert.Fail($"leading span frame {o}: project mixed {expected[o * 2]}, warped mixed {actual[o * 2]}");
                }

                int trailingStart = 3 * Rate / 2, offset = Rate / 2;
                for (int o = trailingStart; o < frames; o++)
                {
                    if (expected[(o + offset) * 2] != actual[o * 2] || expected[(o + offset) * 2 + 1] != actual[o * 2 + 1])
                        Assert.Fail($"trailing span frame {o}: project mixed {expected[(o + offset) * 2]}, warped mixed {actual[o * 2]}");
                }

                // the 2x middle is resampled audio, not silence or copies
                double sum = 0;
                for (int o = Rate; o < trailingStart; o++)
                    sum += (double)actual[o * 2] * actual[o * 2];
                Assert.InRange(Math.Sqrt(sum / (trailingStart - Rate)), 0.18, 0.24);

                Assert.Null(worker.Error);
            }
        }

        [Fact]
        public void Ramped_span_glides_the_pitch_continuously()
        {
            RequireFFmpeg();

            // a 2s linear entry ramp 1→2x over a 440Hz sine: the heard pitch must rise
            // continuously through the ramp — zero-crossing counts per 0.2s output window may
            // never fall and must clearly grow — where the old quantized pacing produced a
            // staircase with a flush at every step.
            var project = NewProject();
            var a = AddSource(project, SineFixture(440, 0.30f, 4));
            AddAudioItem(project, a, 0, 4 * Second);
            AddSpeedItem(project, 0, 4 * Second, 2.0,
                entry: new Transition { Kind = TransitionKind.Ramp, DurationTicks = 2 * Second, Easing = TransitionEasing.Linear });

            var warp = TimeWarp.Build(project);
            int totalOut = (int)((warp.OutputDurationTicks * Rate + Second - 1) / Second);

            var (ring, sink) = NewSink();
            using (sink)
            using (var worker = new AudioMixWorker(project, ring, sink, Rate, warp))
            {
                worker.Start();
                var mix = DrainToEof(ring, worker, totalOut + Rate, out int frames);
                Assert.InRange(frames, totalOut - Chunk, totalOut + Chunk);

                int window = Rate / 5; // 0.2s of output time
                int prev = -1, first = -1, last = -1;
                for (int w = 0; w < 6 && (w + 1) * window <= frames; w++)
                {
                    int crossings = ZeroCrossings(mix, w * window, window);
                    if (prev >= 0)
                    {
                        Assert.True(crossings >= prev - 4,
                            $"pitch fell back inside the ramp: window {w - 1} had {prev} crossings, window {w} has {crossings}");
                    }
                    else
                    {
                        first = crossings;
                    }

                    prev = last = crossings;
                }

                Assert.True(last > first * 1.4,
                    $"pitch never rose through the ramp: first window {first} crossings, last {last}");
                Assert.Null(worker.Error);
            }
        }

        [Fact]
        public void Seek_under_a_warp_re_bases_the_sink_in_output_time()
        {
            RequireFFmpeg();

            // the sink's media time is the domain the player's clock runs in — warped output
            // time. A seek to project 2s under a whole-timeline 2x span must therefore restart
            // the sink's base at OUTPUT 1s, and production must stop after the remaining 1s of
            // output audio.
            var project = NewProject();
            var a = AddSource(project, SineFixture(440, 0.30f, 4));
            AddAudioItem(project, a, 0, 4 * Second);
            AddSpeedItem(project, 0, 4 * Second, 2.0);

            var warp = TimeWarp.Build(project);
            Assert.Equal(Second, warp.ToOutput(2 * Second));

            var (ring, sink) = NewSink();
            using (sink)
            using (var worker = new AudioMixWorker(project, ring, sink, Rate, warp))
            {
                worker.Start();
                worker.PrepareSeek(new TimeSpan(2 * Second));

                var sw = Stopwatch.StartNew();
                while (sink.PlayedTime.Ticks != Second && sw.ElapsedMilliseconds < 5000)
                    Thread.Sleep(5);
                Assert.Equal(Second, sink.PlayedTime.Ticks);

                DrainToEof(ring, worker, 2 * Rate, out int frames);
                Assert.InRange(frames, Rate - Chunk, Rate + Chunk); // (4s − 2s) of project at 2x
                Assert.Null(worker.Error);
            }
        }
    }
}
