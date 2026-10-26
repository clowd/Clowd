using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Playback;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="AudioSilenceScan"/> — the editor's "does this track hold anything?" question,
    /// answered from the samples. Real decoding, so it skips without the FFmpeg natives like the
    /// waveform tests do; fixtures are written with <see cref="Mp4Writer"/> the same way.
    /// </summary>
    public sealed class AudioSilenceScanTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30;
        private const int Rate = 48000;
        private const int Seconds = 3;

        /// <summary>Mp4Writer adds video first, so the audio is stream 1.</summary>
        private const int AudioStream = 1;

        private readonly List<string> _tempFiles = new List<string>();

        public AudioSilenceScanTests()
        {
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);
        }

        public void Dispose()
        {
            foreach (var f in _tempFiles)
            {
                try { File.Delete(f); }
                catch { /* best effort */ }
            }
        }

        /// <summary>A short mp4 whose audio is <paramref name="fill"/>(sample index) on both
        /// channels; null fills digital silence.</summary>
        private string Fixture(Func<int, float> fill)
        {
            string path = Path.Combine(Path.GetTempPath(), $"clowd-silence-test-{Guid.NewGuid():N}.mp4");
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
                for (int n = 0; n < Fps * Seconds; n++)
                    writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), W * 4, W, H, n);
            }
            finally
            {
                pin.Free();
            }

            int total = Rate * Seconds;
            var buf = new float[total * 2];
            if (fill != null)
            {
                for (int i = 0; i < total; i++)
                {
                    float s = fill(i);
                    buf[i * 2] = s;
                    buf[i * 2 + 1] = s;
                }
            }
            writer.SubmitAudioSamples(buf, total);
            writer.Finish();

            return path;
        }

        private static AudioStreamProbe[] Streams(params int[] indices)
        {
            var streams = new AudioStreamProbe[indices.Length];
            for (int i = 0; i < indices.Length; i++)
                streams[i] = new AudioStreamProbe { StreamIndex = indices[i], SampleRate = Rate, Channels = 2 };
            return streams;
        }

        [Fact]
        public void Digital_silence_is_silent()
        {
            var path = Fixture(null);

            Assert.True(AudioSilenceScan.IsSilent(path, AudioStream));
            Assert.Equal(new[] { AudioStream }, AudioSilenceScan.FindSilentStreams(path, Streams(AudioStream)));
        }

        [Fact]
        public void A_tone_is_not_silent()
        {
            var path = Fixture(i => Tone(i, 0.25f));

            Assert.False(AudioSilenceScan.IsSilent(path, AudioStream));
            Assert.Empty(AudioSilenceScan.FindSilentStreams(path, Streams(AudioStream)));
        }

        /// <summary>The early exit must not become an early verdict: a track that is silent until
        /// its last second still carries audio, so the scan has to read to the end before it says
        /// silent.</summary>
        [Fact]
        public void A_blip_at_the_very_end_is_not_silent()
        {
            int total = Rate * Seconds;
            var path = Fixture(i => i >= total - Rate / 2 ? Tone(i, 0.5f) : 0f);

            Assert.False(AudioSilenceScan.IsSilent(path, AudioStream));
        }

        /// <summary>Below the waveform's quantization floor the row would draw flat, and a flat
        /// row is what the scan exists to remove — so a hair of noise floor is still silent, and
        /// an amplitude the waveform can draw is not. Both sides keep a margin from the floor:
        /// the fixture goes through a lossy encoder.</summary>
        [Fact]
        public void The_threshold_is_the_waveforms_quantization_floor()
        {
            var below = Fixture(i => Tone(i, AudioSilenceScan.Threshold * 0.2f));
            var above = Fixture(i => Tone(i, AudioSilenceScan.Threshold * 4f));

            Assert.True(AudioSilenceScan.IsSilent(below, AudioStream));
            Assert.False(AudioSilenceScan.IsSilent(above, AudioStream));
        }

        private static float Tone(int i, float amplitude) =>
            amplitude * (float)Math.Sin(2 * Math.PI * 440 * i / Rate);

        /// <summary>A stream that cannot be decoded is a problem to surface, not a row to hide:
        /// the tolerant form keeps it, the strict form throws.</summary>
        [Fact]
        public void An_undecodable_stream_is_kept_rather_than_called_silent()
        {
            var missing = Path.Combine(Path.GetTempPath(), $"clowd-silence-missing-{Guid.NewGuid():N}.mp4");

            Assert.ThrowsAny<Exception>(() => AudioSilenceScan.IsSilent(missing, AudioStream));
            Assert.Empty(AudioSilenceScan.FindSilentStreams(missing, Streams(AudioStream)));
        }

        [Fact]
        public void Nothing_to_scan_is_nothing_silent()
        {
            Assert.Empty(AudioSilenceScan.FindSilentStreams("whatever.mp4", null));
            Assert.Empty(AudioSilenceScan.FindSilentStreams("whatever.mp4", Array.Empty<AudioStreamProbe>()));
        }
    }
}
