using System;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The sink's master gain. It is applied to the samples in the render callback and nowhere
    /// else — the device is never told about it, because the platform's volume knobs (WASAPI's
    /// endpoint master among them) are the user's system volume, not the preview's.
    /// </summary>
    public class NAudioSinkTests
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;

        /// <summary>Hands the render callback back to the test so it can pull like a device would,
        /// with no timer and no hardware.</summary>
        private sealed class ManualAudioOutput : IAudioOutput
        {
            public AudioRenderCallback Render { get; private set; }

            public void Initialize(int sampleRate, int channels, int latencyMs, AudioRenderCallback render)
                => Render = render;

            public void Play() { }
            public void Pause() { }
            public void Stop() { }
            public void Dispose() { }
        }

        private static NAudioSink Create(out ManualAudioOutput output, out AudioRingBuffer ring)
        {
            output = new ManualAudioOutput();
            ring = new AudioRingBuffer(SampleRate * Channels);
            return new NAudioSink(SampleRate, Channels, ring, output);
        }

        private static float[] Pull(ManualAudioOutput output, AudioRingBuffer ring, float[] source,
            double volume, NAudioSink sink)
        {
            sink.Volume = volume;
            Assert.Equal(source.Length, ring.Write(source));

            var buffer = new float[source.Length];
            output.Render(buffer);
            return buffer;
        }

        [Fact]
        public void Full_volume_passes_the_samples_through()
        {
            using var sink = Create(out var output, out var ring);
            var heard = Pull(output, ring, new[] { 1.0f, -0.5f, 0.25f, -1.0f }, 1.0, sink);

            Assert.Equal(new[] { 1.0f, -0.5f, 0.25f, -1.0f }, heard);
        }

        [Fact]
        public void Volume_attenuates_the_samples_the_device_pulls()
        {
            using var sink = Create(out var output, out var ring);
            var heard = Pull(output, ring, new[] { 1.0f, -0.5f, 0.25f, -1.0f }, 0.5, sink);

            Assert.Equal(new[] { 0.5f, -0.25f, 0.125f, -0.5f }, heard);
        }

        [Fact]
        public void Zero_volume_is_silence()
        {
            using var sink = Create(out var output, out var ring);
            var heard = Pull(output, ring, new[] { 1.0f, -0.5f, 0.25f, -1.0f }, 0.0, sink);

            Assert.All(heard, s => Assert.Equal(0.0f, s));
        }

        [Fact]
        public void Volume_is_clamped_to_unity()
        {
            using var sink = Create(out _, out _);

            sink.Volume = 4.0;
            Assert.Equal(1.0, sink.Volume);

            sink.Volume = -1.0;
            Assert.Equal(0.0, sink.Volume);
        }

        /// <summary>Muting must not stop media time: the frames still went to the device, they were
        /// simply quiet, so the audio master clock advances exactly as at full volume.</summary>
        [Fact]
        public void Gain_does_not_move_the_clock()
        {
            using var sink = Create(out var output, out var ring);
            sink.TrySetBasePts(TimeSpan.Zero);

            var block = new float[SampleRate * Channels / 2]; // half a second of frames
            Pull(output, ring, block, 0.0, sink);
            var muted = sink.PlayedTime;

            using var loud = Create(out var loudOutput, out var loudRing);
            loud.TrySetBasePts(TimeSpan.Zero);
            Pull(loudOutput, loudRing, block, 1.0, loud);

            Assert.Equal(loud.PlayedTime, muted);
        }
    }
}
