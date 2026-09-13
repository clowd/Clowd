using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Clowd.VideoSDK.Playback;

namespace Clowd.VideoSDK.Audio
{
    /// <summary>
    /// Answers "does this audio stream hold anything at all?" by decoding it — the file is the
    /// authority. A recorder lays out a track per configured device whether or not the device is
    /// capturing (that is what lets one be switched on mid-recording), an imported file can carry
    /// a silent track of its own, and neither the container nor the probe can tell a track that
    /// held silence from one that held speech. So the editor asks the samples.
    ///
    /// <para>
    /// The scan stops at the first sample that clears <see cref="Threshold"/>: a live track
    /// answers within its first chunk of audio, so only a track that really is silent end to end
    /// pays for a full decode. The threshold is the timeline waveform's own quantization floor
    /// (peaks store as <c>sbyte</c> at ±127 = ±1.0), which makes the rule exactly "a row that would
    /// draw as a flat line is not a row" — a recorder's muted source writes digital zeros and is
    /// caught trivially; dither or a whisper of noise floor in an external file draws flat and is
    /// caught too.
    /// </para>
    /// </summary>
    public static class AudioSilenceScan
    {
        /// <summary>Absolute sample value at or above which a stream counts as carrying audio:
        /// the smallest amplitude the timeline waveform can draw as anything but zero.</summary>
        public const float Threshold = 1f / 127;

        /// <summary>Decode rate. Peaks do not care about the project's rate, and a fixed one keeps
        /// the resampler out of the way for the recorder's own 48 kHz output.</summary>
        private const int DecodeSampleRate = 48000;

        /// <summary>
        /// True when every sample of stream <paramref name="streamIndex"/> of <paramref name="path"/>
        /// sits below <see cref="Threshold"/>. Throws when the stream cannot be opened or decoded
        /// (see <see cref="FindSilentStreams"/> for the tolerant form). Cancellation is checked once
        /// per decoded chunk and surfaces as <see cref="OperationCanceledException"/>.
        /// </summary>
        public static bool IsSilent(string path, int streamIndex, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(path);

            using var decoder = new SyncAudioStreamDecoder(path, streamIndex, DecodeSampleRate);
            while (decoder.DecodeNext(out _, out var samples, out var frames))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var count = frames * 2; // interleaved stereo by the decoder's contract
                for (var i = 0; i < count; i++)
                {
                    if (Math.Abs(samples[i]) >= Threshold)
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The mp4 stream indices, among <paramref name="audioStreams"/>, that <see cref="IsSilent"/>
        /// says hold nothing. A stream that fails to decode is <b>not</b> reported silent: the row
        /// stays, and whatever is wrong with it shows up where the user can act on it (playback,
        /// the waveform) rather than as a row that quietly never appeared. An empty set for an
        /// empty or null list. Shaped to be handed straight to the persistence layer's
        /// <c>findSilentAudioStreams</c> as a method group.
        /// </summary>
        public static IReadOnlyCollection<int> FindSilentStreams(string path,
            IReadOnlyList<AudioStreamProbe> audioStreams)
        {
            var silent = new HashSet<int>();
            if (audioStreams == null || audioStreams.Count == 0)
                return silent;

            foreach (var stream in audioStreams)
            {
                try
                {
                    if (IsSilent(path, stream.StreamIndex))
                        silent.Add(stream.StreamIndex);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Silence scan of stream {stream.StreamIndex} failed; keeping its row: {ex.Message}");
                }
            }

            return silent;
        }
    }
}
