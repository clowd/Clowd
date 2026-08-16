using System;

namespace Clowd.VideoSDK.Audio
{
    /// <summary>
    /// Pull callback the output device drains audio through, called on the device's render
    /// thread. The implementation must fill the <em>entire</em> span with interleaved float
    /// samples, padding silence on underrun — the device plays whatever is in the buffer.
    /// </summary>
    public delegate void AudioRenderCallback(Span<float> buffer);

    /// <summary>
    /// The platform audio device, behind the minimum surface <see cref="Playback.NAudioSink"/>
    /// actually uses: configure the format once, then start and stop the pull. Implementations own
    /// their own device lifetime — the device may be created lazily on the first <see cref="Play"/>
    /// (enumeration is not free) and must be torn down by <see cref="IDisposable.Dispose"/>.
    /// <para>
    /// There is deliberately no volume on this interface. The device-level volume knobs are the
    /// user's, not ours: NAudio's <c>WasapiOut.Volume</c> writes the endpoint's master scalar (the
    /// Windows system volume) and even a per-session volume would move the app's slider in the
    /// volume mixer. Preview volume is a gain the sink applies to the samples instead, which is
    /// what every other player does.
    /// </para>
    /// </summary>
    public interface IAudioOutput : IDisposable
    {
        /// <summary>
        /// Sets the interleaved IEEE-float format and the callback the device pulls through.
        /// Must be called before <see cref="Play"/>.
        /// </summary>
        /// <param name="latencyMs">Requested device buffer latency. The caller uses the same
        /// value to correct media time, so an implementation must not silently substitute
        /// another one.</param>
        void Initialize(int sampleRate, int channels, int latencyMs, AudioRenderCallback render);

        /// <summary>Starts or resumes pulling. Idempotent while already playing.</summary>
        void Play();

        /// <summary>Stops pulling, keeping the playback position. Idempotent while not playing.</summary>
        void Pause();

        /// <summary>Stops pulling and resets the device's playback position to zero.</summary>
        void Stop();
    }
}
