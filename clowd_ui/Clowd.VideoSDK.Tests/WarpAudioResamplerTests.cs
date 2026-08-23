using System;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Clowd.VideoSDK.Render;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // The render pipeline's warp-aware audio stage against a fake IAudioSource — no FFmpeg:
    // bit-exact passthrough where the warp does not bend time (identity warps AND speed-1 spans
    // of warped timelines — the preview/render parity invariant), the output-length math,
    // constant-factor slope, ramp monotonicity, and chunking independence.
    public class WarpAudioResamplerTests
    {
        private const int Rate = 48000;
        private const long Second = 10_000_000;

        // ----------------------------------------------------------------------------- fixture

        /// <summary>Sample value is a pure function of the source position, so any project
        /// sample's expected value is computable without a mixer: ch0 = f(pos), ch1 = -f(pos).</summary>
        private sealed class PositionAudioSource : IAudioSource
        {
            public Func<long, float> ValueOf { get; set; } = HashValue;

            public bool ReadSamples(Guid sourceId, int streamIndex, long sourcePosFrames,
                float[] dst, int frames, out int framesRead)
            {
                for (int i = 0; i < frames; i++)
                {
                    float v = ValueOf(sourcePosFrames + i);
                    dst[i * 2] = v;
                    dst[i * 2 + 1] = -v;
                }
                framesRead = frames;
                return true;
            }
        }

        private static float HashValue(long pos) => ((pos * 31) % 997) / 1000f;

        private static Project NewProject() => new Project
        {
            Output = new OutputSettings
            {
                WidthPx = 64,
                HeightPx = 64,
                FpsNum = 30,
                FpsDen = 1,
                SampleRate = Rate,
            },
        };

        private static void AddAudioItem(Project project, long startTicks, long durationTicks)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Order = project.Tracks.Count };
            project.Tracks.Add(track);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new MediaContent { SourceId = Guid.NewGuid(), StreamIndex = 0, SourceInTicks = 0 },
            });
        }

        private static Item AddSpeedItem(Project project, long startTicks, long durationTicks,
            double factor, long entryRampTicks = 0, long exitRampTicks = 0)
        {
            var track = new Track
            {
                Id = Guid.NewGuid(),
                Kind = TrackKind.Effect,
                Order = project.Tracks.Count,
                Name = "Speed",
            };
            project.Tracks.Add(track);
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new SpeedContent { Factor = factor },
                Entry = entryRampTicks > 0
                    ? new Transition { Kind = TransitionKind.Ramp, DurationTicks = entryRampTicks }
                    : null,
                Exit = exitRampTicks > 0
                    ? new Transition { Kind = TransitionKind.Ramp, DurationTicks = exitRampTicks }
                    : null,
            };
            project.Items.Add(item);
            return item;
        }

        private static WarpAudioResampler NewResampler(Project project, TimeWarp warp,
            Func<long, float> valueOf = null)
        {
            var source = new PositionAudioSource();
            if (valueOf != null)
                source.ValueOf = valueOf;
            return new WarpAudioResampler(new AudioMixer(project, source), warp, Rate);
        }

        /// <summary>Reads [0, totalFrames) through the resampler in forward chunks of
        /// <paramref name="chunkFrames"/>, returning the interleaved result.</summary>
        private static float[] ReadAll(WarpAudioResampler resampler, long totalFrames, int chunkFrames)
        {
            var result = new float[totalFrames * AudioMixer.Channels];
            var chunk = new float[chunkFrames * AudioMixer.Channels];
            long pos = 0;
            while (pos < totalFrames)
            {
                int n = (int)Math.Min(chunkFrames, totalFrames - pos);
                resampler.ReadChunk(pos, n, chunk);
                Array.Copy(chunk, 0, result, pos * AudioMixer.Channels, n * AudioMixer.Channels);
                pos += n;
            }
            return result;
        }

        // ------------------------------------------------------------------------------- tests

        [Fact]
        public void Identity_warp_is_a_bit_exact_passthrough()
        {
            var project = NewProject();
            AddAudioItem(project, 0, 2 * Second);

            var warp = TimeWarp.Build(project);
            Assert.True(warp.IsIdentity);

            var resampler = NewResampler(project, warp);
            var mixer = new AudioMixer(project, new PositionAudioSource());

            long total = 2 * Rate;
            var warped = ReadAll(resampler, total, 1601);
            var straight = new float[total * AudioMixer.Channels];
            var chunk = new float[1601 * AudioMixer.Channels];
            long p = 0;
            while (p < total)
            {
                int n = (int)Math.Min(1601, total - p);
                mixer.MixChunk(p, n, chunk);
                Array.Copy(chunk, 0, straight, p * AudioMixer.Channels, n * AudioMixer.Channels);
                p += n;
            }

            Assert.Equal(straight, warped); // element-wise exact float equality
        }

        [Fact]
        public void Speed_one_spans_of_a_warped_timeline_copy_project_samples_verbatim()
        {
            // 3s of audio; 2x from 1s to 2s. Output: [0, 1s) untouched, [1s, 1.5s) warped,
            // [1.5s, 2.5s) the project's last second at a constant half-second offset.
            var project = NewProject();
            AddAudioItem(project, 0, 3 * Second);
            AddSpeedItem(project, Second, Second, 2.0);

            var warp = TimeWarp.Build(project);
            Assert.False(warp.IsIdentity);
            Assert.Equal(Second * 5 / 2, warp.ToOutput(3 * Second));

            var resampler = NewResampler(project, warp);
            var dst = ReadAll(resampler, Rate * 5 / 2, 1601);

            for (long o = 0; o < Rate; o++)
            {
                Assert.Equal(HashValue(o), dst[o * 2]);
                Assert.Equal(-HashValue(o), dst[o * 2 + 1]);
            }

            // 1.5s of output onwards reads project frame o + 24000, bit-exact
            for (long o = Rate * 3 / 2; o < Rate * 5 / 2; o++)
            {
                Assert.Equal(HashValue(o + Rate / 2), dst[o * 2]);
                Assert.Equal(-HashValue(o + Rate / 2), dst[o * 2 + 1]);
            }
        }

        [Fact]
        public void Constant_factor_two_halves_the_output_and_doubles_the_slope()
        {
            var project = NewProject();
            AddAudioItem(project, 0, 2 * Second);
            AddSpeedItem(project, 0, 2 * Second, 2.0);

            var warp = TimeWarp.Build(project);
            Assert.Equal(Second, warp.ToOutput(2 * Second)); // 2s of project in 1s of output

            const float scale = 1e-6f;
            var resampler = NewResampler(project, warp, pos => pos * scale);
            var dst = ReadAll(resampler, Rate, 1601);

            // output sample o plays project sample ~2o (tick quantization is under 3 samples)
            for (long o = 0; o < Rate; o++)
            {
                Assert.InRange(dst[o * 2], (2 * o - 3) * scale, (2 * o + 3) * scale);
                Assert.InRange(dst[o * 2 + 1], -(2 * o + 3) * scale, -(2 * o - 3) * scale);
            }
        }

        [Fact]
        public void Ramped_speed_stays_monotone_over_a_monotone_signal()
        {
            // the audio outlives the speed item, so the monotone signal keeps rising through the
            // ramp ends and the identity tail (a shorter item would drop to silence at its end)
            var project = NewProject();
            AddAudioItem(project, 0, 3 * Second);
            AddSpeedItem(project, 0, 2 * Second, 4.0,
                entryRampTicks: Second / 2, exitRampTicks: Second / 2);

            var warp = TimeWarp.Build(project);
            long total = AudioTime.SamplesCeil(warp.ToOutput(3 * Second), Rate);
            Assert.InRange(total, Rate * 3 / 2, 2 * Rate); // 2s warped faster than 1x, plus 1s

            const float scale = 1e-5f;
            var resampler = NewResampler(project, warp, pos => pos * scale);
            var dst = ReadAll(resampler, total, 1601);

            for (long o = 1; o < total; o++)
            {
                Assert.True(dst[o * 2] >= dst[(o - 1) * 2] - 1e-6f,
                    $"channel 0 regressed at output frame {o}");
                Assert.True(dst[o * 2 + 1] <= dst[(o - 1) * 2 + 1] + 1e-6f,
                    $"channel 1 regressed at output frame {o}");
            }
        }

        [Fact]
        public void Chunk_boundaries_do_not_change_the_output()
        {
            var project = NewProject();
            AddAudioItem(project, 0, 3 * Second);
            AddSpeedItem(project, Second / 2, Second, 3.0,
                entryRampTicks: Second / 4, exitRampTicks: Second / 4);

            var warp = TimeWarp.Build(project);
            long total = AudioTime.SamplesCeil(warp.ToOutput(3 * Second), Rate);

            var small = ReadAll(NewResampler(project, warp), total, 480);
            var large = ReadAll(NewResampler(project, warp), total, 1601);

            Assert.Equal(small, large);
        }

        [Fact]
        public void Backward_chunk_requests_are_rejected()
        {
            var project = NewProject();
            AddAudioItem(project, 0, Second);
            AddSpeedItem(project, 0, Second, 2.0);

            var resampler = NewResampler(project, TimeWarp.Build(project));
            var chunk = new float[256 * AudioMixer.Channels];
            resampler.ReadChunk(0, 256, chunk);
            Assert.Throws<InvalidOperationException>(() => resampler.ReadChunk(128, 128, chunk));
        }

        [Fact]
        public void Rejects_null_arguments()
        {
            var project = NewProject();
            AddAudioItem(project, 0, Second);
            var mixer = new AudioMixer(project, new PositionAudioSource());
            var warp = TimeWarp.Build(project);

            Assert.Throws<ArgumentNullException>(() => new WarpAudioResampler(null, warp, Rate));
            Assert.Throws<ArgumentNullException>(() => new WarpAudioResampler(mixer, null, Rate));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WarpAudioResampler(mixer, warp, 0));
        }
    }
}
