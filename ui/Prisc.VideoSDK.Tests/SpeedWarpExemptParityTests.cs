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
using Clowd.VideoSDK.Render;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // Preview/render parity for a SpeedWarpExempt clip under a warp, over a real encoded file:
    // the preview's AudioMixWorker and the render's WarpAudioResampler must produce the same
    // samples for the clip, and both must be the clip's own first second decoded straight (an
    // exempt clip is never bent, so it is a verbatim copy of its source on the output clock).
    // Same harness as AudioMixWorkerWarpTests; skips without the FFmpeg natives.
    public class SpeedWarpExemptParityTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30;
        private const int Rate = 48000;
        private const long Second = 10_000_000;
        private const int Chunk = Rate / 50;

        private static void RequireFFmpeg() =>
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);

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
            string path = Path.Combine(Path.GetTempPath(), $"clowd-exempt-parity-test-{Guid.NewGuid():N}.mp4");
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

        private static Item AddAudioItem(Project project, Guid sourceId, long startTicks, long durationTicks, bool exempt,
            double speed = 1.0, long fadeInTicks = 0)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Order = project.Tracks.Count, Name = "Audio" };
            project.Tracks.Add(track);
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 1, SourceInTicks = 0, SpeedWarpExempt = exempt, Speed = speed },
                Entry = fadeInTicks > 0 ? new Transition { Kind = TransitionKind.Fade, DurationTicks = fadeInTicks } : null,
            };
            project.Items.Add(item);
            return item;
        }

        /// <summary>The output frame the worker stops at: the audio end mapped onto the output
        /// clock, the way <c>AudioMixWorker.UpdateEndFrames</c> computes it.</summary>
        private static int OutputEndFrame(Project project, TimeWarp warp)
        {
            long endTicks = Math.Min(project.GetDurationTicks(), AudioMixer.GetAudioEndTicks(project));
            return (int)AudioTime.SamplesCeil(warp.ToOutput(endTicks), Rate);
        }

        /// <summary>Output samples [0, frames) through the render's warp stage, in the same
        /// chunking the render loop uses.</summary>
        private static float[] RenderMix(Project project, TimeWarp warp, int frames)
        {
            var rendered = new float[frames * 2];
            using var seq = new SequentialAudioSource(project);
            var resampler = new WarpAudioResampler(new AudioMixer(project, seq, warp), warp, Rate);
            var chunk = new float[Chunk * 2];
            for (int f = 0; f < frames; f += Chunk)
            {
                int n = Math.Min(Chunk, frames - f);
                resampler.ReadChunk(f, n, chunk);
                Array.Copy(chunk, 0, rendered, f * 2, n * 2);
            }
            return rendered;
        }

        /// <summary>The preview's output samples from <paramref name="seekProjectTicks"/> (0 for
        /// the start) to the end of the audio, drained through the worker.</summary>
        private static float[] PreviewMix(Project project, TimeWarp warp, long seekProjectTicks, int maxFrames, out int frames)
        {
            var ring = new AudioRingBuffer(Rate);
            using var sink = new NAudioSink(Rate, 2, ring, new SilentAudioOutput());
            using var worker = new AudioMixWorker(project, ring, sink, Rate, warp);
            if (seekProjectTicks > 0)
                worker.PrepareSeek(TimeSpan.FromTicks(seekProjectTicks)); // adopted by the first chunk
            worker.Start();
            var previewed = DrainToEof(ring, worker, maxFrames, out frames);
            Assert.Null(worker.Error);
            return previewed;
        }

        private static double Rms(float[] samples, int firstFrame, int frames)
        {
            double sum = 0;
            for (int i = firstFrame * 2; i < (firstFrame + frames) * 2; i++)
                sum += (double)samples[i] * samples[i];
            return Math.Sqrt(sum / (frames * 2));
        }

        private static void AssertSameSamples(float[] expected, float[] actual, int frames, string what)
        {
            for (int i = 0; i < frames * 2; i++)
            {
                if (expected[i] != actual[i])
                    Assert.Fail($"{what} sample {i / 2} ch{i % 2}: expected {expected[i]}, got {actual[i]}");
            }
        }

        private static void AddSpeedItem(Project project, long start, long duration, double factor)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Order = project.Tracks.Count, Name = "Speed" };
            project.Tracks.Add(track);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = start,
                DurationTicks = duration,
                Content = new SpeedContent { Factor = factor, PitchCorrect = false },
            });
        }

        private static float[] DrainToEof(AudioRingBuffer ring, AudioMixWorker worker, int maxFrames,
            out int framesRead, int timeoutMs = 30000)
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

        /// <summary>The straight (unwarped, identity-warp) mix of the first
        /// <paramref name="frames"/> frames of a project, in the worker's own chunking.</summary>
        private static float[] StraightMix(Project project, int frames)
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

        [Fact]
        public void Exempt_clip_under_2x_is_the_same_verbatim_second_in_preview_and_render()
        {
            RequireFFmpeg();

            // 2s of project under 2x is 1s of output; the exempt clip covers all of it and plays
            // its first source second unbent. Without a speed item the same project's first
            // second IS that source second, so the straight mix is the reference for both paths.
            string fixture = SineFixture(440, 0.30f, 2);

            var reference = NewProject();
            var refSource = AddSource(reference, fixture);
            AddAudioItem(reference, refSource, 0, 2 * Second, exempt: true);
            var expected = StraightMix(reference, Rate);

            var project = NewProject();
            var source = AddSource(project, fixture);
            AddAudioItem(project, source, 0, 2 * Second, exempt: true);
            AddSpeedItem(project, 0, 2 * Second, 2.0);
            Assert.Empty(project.Validate());

            var warp = TimeWarp.Build(project);
            Assert.Equal(Second, warp.OutputDurationTicks);

            // render path
            var rendered = new float[Rate * 2];
            using (var seq = new SequentialAudioSource(project))
            {
                var resampler = new WarpAudioResampler(new AudioMixer(project, seq, warp), warp, Rate);
                var chunk = new float[Chunk * 2];
                for (int f = 0; f < Rate; f += Chunk)
                {
                    int n = Math.Min(Chunk, Rate - f);
                    resampler.ReadChunk(f, n, chunk);
                    Array.Copy(chunk, 0, rendered, f * 2, n * 2);
                }
            }

            // preview path
            var ring = new AudioRingBuffer(Rate);
            using var sink = new NAudioSink(Rate, 2, ring, new SilentAudioOutput());
            float[] previewed;
            int frames;
            using (var worker = new AudioMixWorker(project, ring, sink, Rate, warp))
            {
                worker.Start();
                previewed = DrainToEof(ring, worker, 2 * Rate, out frames);
                Assert.Null(worker.Error);
            }
            Assert.InRange(frames, Rate - Chunk, Rate);

            for (int i = 0; i < frames * 2; i++)
            {
                if (expected[i] != rendered[i])
                    Assert.Fail($"render sample {i / 2} ch{i % 2}: straight {expected[i]}, rendered {rendered[i]}");
                if (expected[i] != previewed[i])
                    Assert.Fail($"preview sample {i / 2} ch{i % 2}: straight {expected[i]}, previewed {previewed[i]}");
            }

            // and it is a real signal, not a silent parity
            double energy = 0;
            for (int i = 0; i < frames * 2; i++)
                energy += expected[i] * expected[i];
            Assert.True(energy > 1, $"reference energy {energy}");
        }

        // A clip that starts mid speed item at a project tick whose output image is not on a
        // sample boundary (0.3000003s under 2x is output 0.15000015s = frame 7200.0096), so the
        // output anchor and the source offset are both fractional; then the same with the
        // clip's own Speed (the SrcBase interpolation path) and with a fade-in (a ramp defined
        // in project time, mapped back through the warp per sample).
        private const long OffGridStart = 3_000_003;

        private Project OffGridProject(string fixture, double speed, long fadeInTicks, out TimeWarp warp, out Item clip)
        {
            var project = NewProject();
            var source = AddSource(project, fixture);
            clip = AddAudioItem(project, source, OffGridStart, 2 * Second, exempt: true, speed, fadeInTicks);
            AddSpeedItem(project, 0, 4 * Second, 2.0);
            Assert.Empty(project.Validate());

            warp = TimeWarp.Build(project);
            long outputStart = warp.ToOutput(OffGridStart);
            Assert.NotEqual(0, outputStart * Rate % Second); // the point of the fixture
            return project;
        }

        [Theory]
        [InlineData(1.0, 0L)]
        [InlineData(1.5, 0L)]
        [InlineData(1.0, Second / 2)]
        public void Off_grid_exempt_clip_is_the_same_in_preview_and_render(double speed, long fadeInTicks)
        {
            RequireFFmpeg();

            string fixture = SineFixture(440, 0.30f, 3);
            var project = OffGridProject(fixture, speed, fadeInTicks, out var warp, out var clip);
            int outEnd = OutputEndFrame(project, warp);

            var rendered = RenderMix(project, warp, outEnd);
            var previewed = PreviewMix(project, warp, 0, outEnd + Chunk, out int frames);
            Assert.InRange(frames, outEnd - Chunk, outEnd);
            AssertSameSamples(rendered, previewed, frames, "preview");

            int clipStart = (int)AudioTime.SamplesCeil(warp.ToOutput(clip.TimelineStartTicks), Rate);
            int clipEnd = (int)AudioTime.SamplesCeil(warp.ToOutput(clip.TimelineEndTicks), Rate);
            Assert.InRange(clipEnd, clipStart + Rate - 2, clipStart + Rate + 2); // 2s of project under 2x

            // silence before the clip's output start, signal inside it
            Assert.Equal(0, Rms(rendered, 0, clipStart - 1));
            Assert.InRange(Rms(rendered, clipStart + Rate / 2, Rate / 4), 0.15, 0.25);

            if (fadeInTicks > 0)
            {
                // the half-second project fade is a quarter second of output: quiet at the
                // front, at full level after
                Assert.True(Rms(rendered, clipStart, Rate / 20) < 0.3 * Rms(rendered, clipStart + Rate / 2, Rate / 20),
                    "fade-in did not ramp");
            }
            else if (speed == 1.0)
            {
                // unbent, the clip is a verbatim copy of its source placed at its output span:
                // the straight mix of a project holding it there is the reference for both
                var reference = NewProject();
                var refSource = AddSource(reference, fixture);
                AddAudioItem(reference, refSource, warp.ToOutput(clip.TimelineStartTicks),
                    warp.ToOutput(clip.TimelineEndTicks) - warp.ToOutput(clip.TimelineStartTicks), exempt: true);
                var expected = StraightMix(reference, frames);
                AssertSameSamples(expected, rendered, frames, "render");
            }
        }

        [Fact]
        public void Preview_seek_into_an_exempt_clip_matches_the_render_from_that_output_frame()
        {
            RequireFFmpeg();

            // seek to project 1.5s = output 0.75s = frame 36000, inside the clip's output span:
            // the preview's exempt window restarts there and must produce exactly the samples
            // the render produces at those output frames when it reads from 0.
            string fixture = SineFixture(440, 0.30f, 3);
            var project = OffGridProject(fixture, 1.0, 0, out var warp, out var clip);
            int outEnd = OutputEndFrame(project, warp);
            const long seekTicks = 15_000_000;
            int seekFrame = (int)AudioTime.SamplesFloor(warp.ToOutput(seekTicks), Rate);
            Assert.Equal(36000, seekFrame);
            Assert.True(seekFrame > AudioTime.SamplesCeil(warp.ToOutput(clip.TimelineStartTicks), Rate));
            Assert.True(seekFrame < outEnd);

            var rendered = RenderMix(project, warp, outEnd);
            var previewed = PreviewMix(project, warp, seekTicks, outEnd - seekFrame + Chunk, out int frames);
            Assert.InRange(frames, outEnd - seekFrame - Chunk, outEnd - seekFrame);

            var fromSeek = new float[frames * 2];
            Array.Copy(rendered, seekFrame * 2, fromSeek, 0, frames * 2);
            AssertSameSamples(fromSeek, previewed, frames, "preview after seek");
            Assert.InRange(Rms(previewed, 0, Rate / 4), 0.15, 0.25);
        }
    }
}
