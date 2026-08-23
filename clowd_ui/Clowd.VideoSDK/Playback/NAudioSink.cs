using System;
using System.Threading;
using Clowd.VideoSDK.Audio;

namespace Clowd.VideoSDK.Playback
{
    /// <summary>
    /// Drains the decode ring into the platform audio output (<see cref="IAudioOutput"/>), and is
    /// the audio master clock. <see cref="PlayedTime"/> is the media time actually heard: the base
    /// pts set after each open/seek plus samples delivered to the device, minus the (fixed) device
    /// latency. The render callback fills silence on underrun without advancing media time.
    /// </summary>
    internal sealed class NAudioSink : IDisposable, IAudioClockSource
    {
        /// <summary>What we ask the device for. What we actually get back — and correct the
        /// clock by — is <see cref="IAudioOutput.ActualLatencyMs"/>, read live rather than cached:
        /// a backend that binds its device lazily (CoreAudio does, on the first Play) does not know
        /// the real figure until after this sink was constructed.</summary>
        private const int RequestedLatencyMs = 100;

        private readonly AudioRingBuffer _ring;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly IAudioOutput _out;
        private long _consumedFrames;              // device frames delivered to the device
        private long _basePtsTicks = long.MinValue; // long.MinValue = no timing yet
        private long _underrunSamples;
        private float _volume = 1.0f;
        // playback speed of the samples in the ring: the producer resamples so one device frame
        // carries this much media time (see AudioMixWorker). Only ever changed together with a
        // timing reset, so base pts and consumed frames always describe one mapping.
        private double _speed = 1.0;
        private bool _disposed;

        /// <param name="output">Audio backend; null picks the platform default. Injected by tests
        /// (and embedders) that must not touch a real device.</param>
        public NAudioSink(int sampleRate, int channels, AudioRingBuffer ring, IAudioOutput output = null)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _ring = ring;
            _out = output ?? AudioOutputFactory.Create();
            _out.Initialize(sampleRate, channels, RequestedLatencyMs, RenderRead);
        }

        public AudioRingBuffer Ring => _ring;
        public long UnderrunSamples => Interlocked.Read(ref _underrunSamples);

        public bool HasTiming => Interlocked.Read(ref _basePtsTicks) != long.MinValue;

        public TimeSpan PlayedTime
        {
            get
            {
                long baseTicks = Interlocked.Read(ref _basePtsTicks);
                if (baseTicks == long.MinValue)
                    return TimeSpan.Zero;
                long frames = Interlocked.Read(ref _consumedFrames);
                double speed = Volatile.Read(ref _speed);
                long ticks = baseTicks
                             + (long)(frames * speed * TimeSpan.TicksPerSecond / _sampleRate)
                             - (long)(_out.ActualLatencyMs * TimeSpan.TicksPerMillisecond * speed);
                if (ticks < baseTicks)
                    ticks = baseTicks;
                return new TimeSpan(ticks);
            }
        }

        /// <summary>
        /// Master preview gain in [0, 1], applied to the samples in the render callback. It is
        /// deliberately not handed to the device: the platform volume knobs (WASAPI's endpoint
        /// master, a mixer session) belong to the user, and a player that moves them changes what
        /// every other app sounds like. See <see cref="Audio.IAudioOutput"/>.
        /// </summary>
        public double Volume
        {
            get => Volatile.Read(ref _volume);
            set => Volatile.Write(ref _volume, (float)Math.Clamp(value, 0.0, 1.0));
        }

        /// <summary>The output opens its device lazily on this first call (device enumeration is
        /// not free).</summary>
        public void Play()
        {
            if (_disposed)
                return;
            _out.Play();
        }

        public void Pause()
        {
            _out.Pause();
        }

        /// <summary>Called by the audio decode thread after a flush: timing restarts from the
        /// first sample written after the seek.</summary>
        public void ResetTiming() => ResetTiming(Volatile.Read(ref _speed));

        /// <summary>The flush form the producer uses when it also changes playback speed: the new
        /// media-time-per-device-frame mapping takes effect with the first sample of the flushed
        /// stream, so <see cref="PlayedTime"/> never mixes two mappings.</summary>
        public void ResetTiming(double speed)
        {
            Volatile.Write(ref _speed, speed);
            Interlocked.Exchange(ref _basePtsTicks, long.MinValue);
            Interlocked.Exchange(ref _consumedFrames, 0);
        }

        /// <summary>First pts written after open/flush establishes the timing base.</summary>
        public void TrySetBasePts(TimeSpan pts)
        {
            Interlocked.CompareExchange(ref _basePtsTicks, pts.Ticks, long.MinValue);
        }

        private void RenderRead(Span<float> buffer)
        {
            int read = _ring.Read(buffer);
            if (read < buffer.Length)
            {
                buffer.Slice(read).Clear();
                Interlocked.Add(ref _underrunSamples, buffer.Length - read);
            }

            // gain rides on top of the read, not on the clock: the frames counted below are the
            // frames handed to the device whatever their amplitude, so muting does not stop time.
            float volume = Volatile.Read(ref _volume);
            if (volume != 1.0f)
            {
                var samples = buffer.Slice(0, read);
                for (int i = 0; i < samples.Length; i++)
                    samples[i] *= volume;
            }

            Interlocked.Add(ref _consumedFrames, read / _channels);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _out.Stop(); }
            catch { }
            try { _out.Dispose(); }
            catch { }
        }
    }
}
