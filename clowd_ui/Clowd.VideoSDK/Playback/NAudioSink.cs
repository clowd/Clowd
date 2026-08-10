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
        private const int LatencyMs = 100;

        private readonly AudioRingBuffer _ring;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly IAudioOutput _out;
        private long _consumedFrames;              // media frames delivered to the device
        private long _basePtsTicks = long.MinValue; // long.MinValue = no timing yet
        private long _underrunSamples;
        private float _volume = 1.0f;
        private bool _disposed;

        public NAudioSink(int sampleRate, int channels, AudioRingBuffer ring)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _ring = ring;
            _out = AudioOutputFactory.Create();
            _out.Initialize(sampleRate, channels, LatencyMs, RenderRead);
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
                long ticks = baseTicks
                             + (long)(frames * (double)TimeSpan.TicksPerSecond / _sampleRate)
                             - LatencyMs * TimeSpan.TicksPerMillisecond;
                if (ticks < baseTicks)
                    ticks = baseTicks;
                return new TimeSpan(ticks);
            }
        }

        public double Volume
        {
            get => _volume;
            set
            {
                _volume = (float)Math.Clamp(value, 0.0, 1.0);
                _out.Volume = _volume;
            }
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
        public void ResetTiming()
        {
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
