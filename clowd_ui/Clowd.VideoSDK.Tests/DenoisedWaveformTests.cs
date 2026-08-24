using System;
using Clowd.UI.VideoEditor.Timeline;
using Clowd.VideoSDK.Thumbs;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// What an audio row draws once its track is AI-denoised: the peaks the timeline projects come
    /// from the denoise sidecar, blended over the raw ones at the track's strength — the same
    /// dry/wet number <see cref="Audio.DenoisedAudioSource"/> lerps samples with, so the row and
    /// the render agree. Pure math over hand-built snapshots, no Avalonia runtime and no decoding.
    /// </summary>
    public class DenoisedWaveformTests
    {
        private const int BucketsPerSecond = 200;

        /// <summary>One second of peaks at a constant amplitude, fully analyzed.</summary>
        private static WaveformSnapshot Flat(double amplitude, bool complete = true, int seconds = 1)
        {
            int buckets = BucketsPerSecond * seconds;
            var pairs = new sbyte[buckets * 2];
            var level = (sbyte)Math.Round(amplitude * 127);
            for (int i = 0; i < buckets; i++)
            {
                pairs[i * 2] = (sbyte)-level;
                pairs[i * 2 + 1] = level;
            }

            return new WaveformSnapshot(BucketsPerSecond, pairs, buckets, complete);
        }

        /// <summary>A pass that has only reached <paramref name="readySeconds"/> of the stream.</summary>
        private static WaveformSnapshot Partial(double amplitude, double readySeconds)
        {
            var pairs = new sbyte[BucketsPerSecond * 2];
            var level = (sbyte)Math.Round(amplitude * 127);
            int ready = (int)(readySeconds * BucketsPerSecond);
            for (int i = 0; i < ready; i++)
            {
                pairs[i * 2] = (sbyte)-level;
                pairs[i * 2 + 1] = level;
            }

            return new WaveformSnapshot(BucketsPerSecond, pairs, ready, false);
        }

        private static AudioPeaksRequest Request() =>
            new AudioPeaksRequest(Guid.NewGuid(), 1, 0, TimeSpan.TicksPerSecond,
                TimeSpan.TicksPerSecond / 100);

        private static float PeakOf(AudioPeaks peaks)
        {
            peaks.TryGetBucket(0, out _, out var max);
            return max;
        }

        [Fact]
        public void A_raw_row_still_projects_the_raw_peaks()
        {
            var peaks = new TimelinePreviewProvider.PeaksCache().Project(Request(), Flat(0.8));

            Assert.Equal(0.8f, PeakOf(peaks), 2);
            Assert.True(peaks.IsComplete);
        }

        [Fact]
        public void Full_strength_draws_the_sidecar_alone()
        {
            var peaks = new TimelinePreviewProvider.PeaksCache()
                .Project(Request(), Flat(0.8), Flat(0.2), strength: 1);

            Assert.Equal(0.2f, PeakOf(peaks), 2);
            Assert.True(peaks.IsComplete);
        }

        [Fact]
        public void Half_strength_lands_halfway_between_the_two()
        {
            var peaks = new TimelinePreviewProvider.PeaksCache()
                .Project(Request(), Flat(0.8), Flat(0.2), strength: 0.5);

            Assert.Equal(0.5f, PeakOf(peaks), 2);
        }

        /// <summary>The toggle is on but the sidecar has not been generated yet — the row must
        /// keep showing the audio that will actually play, which is still the raw stream.</summary>
        [Fact]
        public void No_sidecar_leaves_the_raw_peaks_standing()
        {
            var peaks = new TimelinePreviewProvider.PeaksCache()
                .Project(Request(), Flat(0.8), denoised: null, strength: 1);

            Assert.Equal(0.8f, PeakOf(peaks), 2);
            Assert.True(peaks.IsComplete);
        }

        /// <summary>Half a denoised waveform growing over a row that already shows the raw one
        /// reads as a glitch: the row waits for the sidecar's pass to cover the item, then swaps
        /// once.</summary>
        [Fact]
        public void A_sidecar_still_being_analyzed_does_not_half_draw()
        {
            var cache = new TimelinePreviewProvider.PeaksCache();
            var request = Request();

            var midway = cache.Project(request, Flat(0.8), Partial(0.2, 0.4), strength: 1);
            Assert.Equal(0.8f, PeakOf(midway), 2);
            Assert.False(midway.IsComplete); // so the timeline keeps asking

            var settled = cache.Project(request, Flat(0.8), Flat(0.2), strength: 1);
            Assert.Equal(0.2f, PeakOf(settled), 2);
            Assert.True(settled.IsComplete);
        }

        /// <summary>The timeline reuses its geometry for as long as the same peaks instance comes
        /// back, so a strength edit has to produce a new one — and an unchanged row must not.</summary>
        [Fact]
        public void The_projection_is_reused_until_something_actually_changes()
        {
            var cache = new TimelinePreviewProvider.PeaksCache();
            var request = Request();
            var raw = Flat(0.8);
            var denoised = Flat(0.2);

            var first = cache.Project(request, raw, denoised, strength: 1);
            Assert.Same(first, cache.Project(request, raw, denoised, strength: 1));

            var quieter = cache.Project(request, raw, denoised, strength: 0.5);
            Assert.NotSame(first, quieter);
            Assert.Equal(0.5f, PeakOf(quieter), 2);

            var off = cache.Project(request, raw);
            Assert.NotSame(quieter, off);
            Assert.Equal(0.8f, PeakOf(off), 2);
        }
    }
}
