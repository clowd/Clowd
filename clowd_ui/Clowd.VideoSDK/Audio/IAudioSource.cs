using System;

namespace Clowd.VideoSDK.Audio
{
    /// <summary>
    /// Delivers decoded source audio to <see cref="AudioMixer"/> — the audio half of the
    /// dual-source pattern (<c>IFrameSource</c> is the video half): the render path uses the
    /// sequential <see cref="SequentialAudioSource"/>, a future playback path substitutes its own
    /// implementation, and the mixer cannot tell the difference.
    ///
    /// <para>
    /// The delivery format is fixed across the SDK: <b>interleaved stereo float</b>
    /// (<see cref="AudioMixer.Channels"/>) at the project's output sample rate — the same shape
    /// <c>AudioDecodeWorker</c> feeds the playback ring and <c>Mp4Writer.SubmitAudioSamples</c>
    /// consumes, so mixed chunks flow straight into the encoder with no further conversion.
    /// </para>
    ///
    /// <para>
    /// <b>Positions are output-rate sample frames, not ticks</b> — a deliberate deviation from
    /// <c>IFrameSource</c>'s tick-based lookup. Video frames are sparse so a tick is the natural
    /// key; audio samples are dense and sample-exact: converting each chunk boundary through
    /// ticks and back rounds by ±1 sample per chunk, which accumulates into audible drift over a
    /// timeline. Sample position <c>p</c> means normalized source time
    /// <c>[p/rate, (p+1)/rate)</c> seconds (source <c>start_time</c> already subtracted, matching
    /// the model's <c>SourceInTicks</c> convention). Callers convert once per item with integer
    /// math (see <see cref="AudioTime"/>) and then step positions by sample counts.
    /// </para>
    /// </summary>
    public interface IAudioSource
    {
        /// <summary>
        /// Reads <paramref name="frames"/> sample frames of interleaved stereo starting at
        /// normalized source position <paramref name="sourcePosFrames"/> (output-rate frames,
        /// see interface remarks) for the given stream. <paramref name="dst"/> (length at least
        /// <c>frames * 2</c>) is always fully written: regions the stream does not cover —
        /// before its first sample, inside a timestamp gap, past end of stream — are silence.
        /// <paramref name="framesRead"/> is the count of frames at or before the stream's end
        /// (== <paramref name="frames"/> until end of stream is reached). Returns false only
        /// when the stream yields no audio at all.
        /// Sequential (render) implementations require non-decreasing, non-overlapping requests
        /// per stream — a regression throws.
        /// </summary>
        bool ReadSamples(Guid sourceId, int streamIndex, long sourcePosFrames, float[] dst,
            int frames, out int framesRead);
    }
}
