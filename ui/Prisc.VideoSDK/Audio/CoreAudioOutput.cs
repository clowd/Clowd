using System;
using System.Runtime.Versioning;
using System.Threading;
using Prisc.Interop;

namespace Prisc.VideoSDK.Audio
{
    /// <summary>
    /// CoreAudio output on macOS: the default-output AudioUnit, which is what every macOS media
    /// app plays through, opened through <see cref="CoreAudioOutputDevice"/> (prisc_interop). The unit
    /// is created on the first <see cref="Play"/>, because opening one binds a device and spins up its
    /// render thread, and the editor builds an output long before anything asks it to play.
    /// <para>
    /// The render callback runs on CoreAudio's own real-time thread. It must not allocate, lock or
    /// block, so it does nothing but hand the unit's buffer to <see cref="AudioRenderCallback"/> as
    /// a span over unmanaged memory, the same zero-copy shape WASAPI's byte buffer gets. The device
    /// roots the handler for exactly as long as the unit is open and unroots it only after the unit
    /// is torn down, because CoreAudio keeps a raw pointer to it and the GC has no idea. That root
    /// keeps this object alive too, so an output that is never disposed lives (and holds its unit
    /// and IO thread) for the life of the process: dispose it, as <see cref="Playback.NAudioSink"/>
    /// does.
    /// </para>
    /// <para>
    /// As on Windows, nothing here touches a device volume: attenuation is the sink's job (see
    /// <see cref="Playback.NAudioSink"/>). <c>kAudioUnitProperty_MaximumFramesPerSlice</c> and the
    /// device's own buffer frame size are left at their defaults: the requested
    /// <c>latencyMs</c> is what the caller corrects media time by, and quietly moving the device
    /// off it would put the correction and the device out of step, which is precisely what
    /// <see cref="IAudioOutput.Initialize"/> forbids.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("macos")]
    public sealed class CoreAudioOutput : IAudioOutput
    {
        private readonly object _sync = new object();

        private int _sampleRate;
        private int _channels;
        private AudioRenderCallback _render;

        private CoreAudioOutputDevice _device;
        private bool _playing;
        private bool _disposed;
        private int _requestedLatencyMs;
        private int _actualLatencyMs;

        public void Initialize(int sampleRate, int channels, int latencyMs, AudioRenderCallback render)
        {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            // Checked here as well as in CoreAudioOutputDevice.Open: the unit opens lazily in Play,
            // so this fails early, with every other argument check, instead of out of Play.
            if (channels <= 0 || channels > CoreAudioOutputDevice.MaxChannels)
                throw new ArgumentOutOfRangeException(nameof(channels));

            lock (_sync)
            {
                _render = render ?? throw new ArgumentNullException(nameof(render));
                _sampleRate = sampleRate;
                _channels = channels;
                // stands in until the unit is opened and can be asked; the device almost always
                // has far less latency than the caller asked for, never more.
                _requestedLatencyMs = latencyMs;
                Volatile.Write(ref _actualLatencyMs, latencyMs);
            }
        }

        /// <summary>
        /// What the device actually runs at, measured once the unit is open: its IO buffer plus the
        /// device's own latency plus its safety offset. Typically ~14 ms (512 + 72 + 76 frames at
        /// 48 kHz), against the 100 ms a caller asks for; reporting the request instead would put
        /// the master clock 86 ms behind the sound.
        /// </summary>
        /// <remarks>Deliberately lock-free: the master clock reads this on every presented frame,
        /// and it is one <see langword="int"/> written once when the unit opens.</remarks>
        public int ActualLatencyMs => Volatile.Read(ref _actualLatencyMs);

        public void Play()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                if (_render == null)
                    throw new InvalidOperationException("Initialize must be called before Play.");
                if (_playing)
                    return;

                if (_device == null)
                {
                    CoreAudioOutputDevice device = null;
                    DeviceCall(() => device = CoreAudioOutputDevice.Open(_sampleRate, _channels, Render));
                    _device = device;
                    // The latency frames count at the device's own rate (the unit resamples from
                    // ours), so that is the rate that turns them into time. A device that publishes
                    // none of the three properties keeps the requested figure.
                    Volatile.Write(ref _actualLatencyMs,
                        _device.LatencyFrames is int frames && _device.DeviceSampleRateHz > 0
                            ? (int)Math.Round(frames * 1000.0 / _device.DeviceSampleRateHz)
                            : _requestedLatencyMs);
                }

                DeviceCall(_device.Start);
                _playing = true;
            }
        }

        public void Pause()
        {
            lock (_sync)
            {
                if (!_playing || _device == null)
                    return;
                DeviceCall(_device.Stop);
                _playing = false;
            }
        }

        /// <summary>The unit has no position of its own; it plays whatever the callback fills, so
        /// stopping the pull is the whole of Stop.</summary>
        public void Stop() => Pause();

        /// <summary>Runs one device call with the exception contract Play and Pause have always had:
        /// everything that comes from the device or the library is an
        /// <see cref="InvalidOperationException"/>, the one type callers catch (the no-usable-device
        /// skip in the tests, play-before-initialize, an OSStatus failure).</summary>
        private static void DeviceCall(Action call)
        {
            try
            {
                call();
            }
            catch (Exception ex) when (ex is InteropException or InteropUnavailableException)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
        }

        /// <summary>CoreAudio's render thread, one frame past the interop thunk.</summary>
        private void Render(Span<float> buffer, int frames, int channels)
        {
            // deliberately not under _sync: the lock is held across device calls that can block,
            // and stalling the real-time thread on one is how you get a glitch. Play/Pause bracket
            // the callback themselves, and a torn read here can only cost one buffer of silence.
            var render = _render;
            if (render == null)
            {
                buffer.Clear();
                return;
            }

            render(buffer);
        }

        public void Dispose()
        {
            CoreAudioOutputDevice device;
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _playing = false;
                device = _device;
                _device = null;
                _render = null;
            }

            // stop, uninitialize and dispose the unit, and only then unroot the handler.
            device?.Dispose();
        }
    }
}
