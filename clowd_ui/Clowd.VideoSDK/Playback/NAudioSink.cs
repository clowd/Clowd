using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Clowd.VideoSDK.Playback
{
    /// <summary>
    /// WASAPI shared-mode audio output draining the decode ring, and the audio master clock.
    /// <see cref="PlayedTime"/> is the media time actually heard: the base pts set after each
    /// open/seek plus samples delivered to the device, minus the (fixed) device latency.
    /// The render callback fills silence on underrun without advancing media time.
    /// </summary>
    internal sealed class NAudioSink : IDisposable, IAudioClockSource
    {
        private const int LatencyMs = 100;

        private sealed class RingWaveProvider : IWaveProvider
        {
            private readonly NAudioSink _owner;
            public RingWaveProvider(NAudioSink owner) { _owner = owner; }
            public WaveFormat WaveFormat => _owner._format;
            public int Read(byte[] buffer, int offset, int count) => _owner.RenderRead(buffer, offset, count);
        }

        private readonly WaveFormat _format;
        private readonly AudioRingBuffer _ring;
        private readonly int _sampleRate;
        private readonly int _channels;
        private WasapiOut _out;
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
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
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
                var device = _out;
                if (device != null)
                {
                    try { device.Volume = _volume; }
                    catch { }
                }
            }
        }

        /// <summary>Creates the device lazily on first play (device enumeration is not free).</summary>
        public void Play()
        {
            if (_disposed)
                return;
            if (_out == null)
            {
                _out = new WasapiOut(AudioClientShareMode.Shared, true, LatencyMs);
                _out.Init(new RingWaveProvider(this));
                try { _out.Volume = _volume; }
                catch { }
            }

            _out.Play();
        }

        public void Pause()
        {
            if (_out != null && _out.PlaybackState == PlaybackState.Playing)
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

        private int RenderRead(byte[] buffer, int offset, int count)
        {
            // interpret the byte buffer as floats in place; count is always a multiple of the
            // float-stereo block align in shared mode.
            var floats = System.Runtime.InteropServices.MemoryMarshal
                .Cast<byte, float>(buffer.AsSpan(offset, count & ~3));

            int read = _ring.Read(floats);
            if (read < floats.Length)
            {
                floats.Slice(read).Clear();
                Interlocked.Add(ref _underrunSamples, floats.Length - read);
            }

            Interlocked.Add(ref _consumedFrames, read / _channels);
            return count;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _out?.Stop(); }
            catch { }
            try { _out?.Dispose(); }
            catch { }
            _out = null;
        }
    }
}
