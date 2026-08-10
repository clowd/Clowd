using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Clowd.VideoSDK.Audio
{
    /// <summary>
    /// WASAPI shared-mode output. The device is created on the first <see cref="Play"/> because
    /// endpoint enumeration is not free, and volume set before that point is applied then.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WasapiAudioOutput : IAudioOutput
    {
        /// <summary>Adapts NAudio's byte-buffer pull to the span callback.</summary>
        private sealed class CallbackWaveProvider : IWaveProvider
        {
            private readonly WasapiAudioOutput _owner;
            public CallbackWaveProvider(WasapiAudioOutput owner) { _owner = owner; }
            public WaveFormat WaveFormat => _owner._format;
            public int Read(byte[] buffer, int offset, int count) => _owner.Read(buffer, offset, count);
        }

        private WaveFormat _format;
        private AudioRenderCallback _render;
        private int _latencyMs;
        private WasapiOut _device;
        private float _volume = 1.0f;
        private bool _disposed;

        public void Initialize(int sampleRate, int channels, int latencyMs, AudioRenderCallback render)
        {
            _render = render ?? throw new ArgumentNullException(nameof(render));
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _latencyMs = latencyMs;
        }

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0.0f, 1.0f);
                var device = _device;
                if (device != null)
                {
                    try { device.Volume = _volume; }
                    catch { }
                }
            }
        }

        public void Play()
        {
            if (_disposed)
                return;
            if (_render == null)
                throw new InvalidOperationException("Initialize must be called before Play.");

            if (_device == null)
            {
                _device = new WasapiOut(AudioClientShareMode.Shared, true, _latencyMs);
                _device.Init(new CallbackWaveProvider(this));
                try { _device.Volume = _volume; }
                catch { }
            }

            _device.Play();
        }

        public void Pause()
        {
            if (_device != null && _device.PlaybackState == PlaybackState.Playing)
                _device.Pause();
        }

        public void Stop()
        {
            _device?.Stop();
        }

        private int Read(byte[] buffer, int offset, int count)
        {
            // interpret the byte buffer as floats in place; count is always a multiple of the
            // float-stereo block align in shared mode.
            var floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count & ~3));
            _render(floats);
            return count;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _device?.Stop(); }
            catch { }
            try { _device?.Dispose(); }
            catch { }
            _device = null;
        }
    }
}
