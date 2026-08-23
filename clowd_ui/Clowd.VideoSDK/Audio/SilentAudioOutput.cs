using System;
using System.Threading;
using Clowd.VideoSDK.Playback;

namespace Clowd.VideoSDK.Audio
{
    /// <summary>
    /// An output that makes no sound but behaves like a device for <em>timing</em>: it pulls the
    /// render callback in real time off a monotonic clock, so the sink's played-sample count —
    /// and with it the A/V master clock and the decode ring's drain rate — advance exactly as
    /// they would against real hardware. Used wherever no backend exists yet (macOS, Linux),
    /// where the alternative is playback that either throws or never advances.
    /// </summary>
    public sealed class SilentAudioOutput : IAudioOutput
    {
        private const int PumpIntervalMs = 10;

        /// <summary>A stalled pump (thread-pool starvation, a debugger break) must not fire a
        /// multi-second burst of callbacks when it resumes; past this it skips ahead instead,
        /// which is what a real device does with the samples it never asked for.</summary>
        private static readonly TimeSpan MaxCatchUp = TimeSpan.FromSeconds(1);

        private readonly IMonotonicTime _time;
        private readonly object _sync = new object();

        private AudioRenderCallback _render;
        private int _sampleRate;
        private int _channels;
        private float[] _block = Array.Empty<float>();

        private Timer _pump;
        private int _latencyMs;
        private int _pumping;
        private bool _playing;
        private TimeSpan _basePosition;     // position accumulated over earlier play spans
        private TimeSpan _playStartElapsed; // clock reading when the current span started
        private long _deliveredFrames;
        private bool _disposed;

        /// <param name="time">Wall-clock source; defaults to a stopwatch (injected in tests).</param>
        public SilentAudioOutput(IMonotonicTime time = null)
        {
            _time = time ?? new StopwatchTime();
        }

        /// <summary>
        /// The device's playback position: time pulled through the render callback since the
        /// last <see cref="Stop"/>, advancing with wall time while playing and frozen otherwise.
        /// </summary>
        public TimeSpan Position
        {
            get { lock (_sync) return PositionLocked(); }
        }

        /// <summary>The requested latency, reported back unchanged. There is no device here, so
        /// there is nothing truer to say — and the clock this feeds is the same fiction either
        /// way, which is the point of the silent output.</summary>
        public int ActualLatencyMs => Volatile.Read(ref _latencyMs);

        public void Initialize(int sampleRate, int channels, int latencyMs, AudioRenderCallback render)
        {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0)
                throw new ArgumentOutOfRangeException(nameof(channels));
            if (render == null)
                throw new ArgumentNullException(nameof(render));

            lock (_sync)
            {
                _sampleRate = sampleRate;
                _channels = channels;
                _render = render;
                _latencyMs = latencyMs;
                // one pump interval of interleaved samples per callback, so the callback sees
                // block sizes in the same ballpark a shared-mode device would ask for.
                _block = new float[Math.Max(1, sampleRate / (1000 / PumpIntervalMs)) * channels];
            }
        }

        public void Play()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                if (_render == null)
                    throw new InvalidOperationException("Initialize must be called before Play.");

                if (!_playing)
                {
                    _playStartElapsed = _time.Elapsed;
                    _playing = true;
                }

                // created on the first Play, and re-armed on every one after: Pause and Stop park
                // it rather than leaving a 100 Hz wake-up running for as long as the editor is
                // open on a paused preview.
                if (_pump == null)
                    _pump = new Timer(Pump, null, PumpIntervalMs, PumpIntervalMs);
                else
                    _pump.Change(PumpIntervalMs, PumpIntervalMs);
            }
        }

        public void Pause()
        {
            lock (_sync)
            {
                if (!_playing)
                    return;
                _basePosition = PositionLocked();
                _playing = false;
                ParkPumpLocked();
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                _playing = false;
                _basePosition = TimeSpan.Zero;
                _deliveredFrames = 0;
                ParkPumpLocked();
            }
        }

        /// <summary>Stops the timer waking us while nothing is playing. A pump already on a thread
        /// pool thread still runs to completion; it sees <c>_playing == false</c> and returns.
        /// </summary>
        private void ParkPumpLocked() => _pump?.Change(Timeout.Infinite, Timeout.Infinite);

        private TimeSpan PositionLocked()
        {
            if (!_playing)
                return _basePosition;
            return _basePosition + (_time.Elapsed - _playStartElapsed);
        }

        private long FramesAtLocked(TimeSpan position)
        {
            if (position <= TimeSpan.Zero)
                return 0;
            return position.Ticks * _sampleRate / TimeSpan.TicksPerSecond;
        }

        /// <summary>Pulls whatever the clock says the device would have consumed by now.</summary>
        private void Pump(object state)
        {
            // the timer can re-enter if a callback runs long; one pump at a time keeps _block
            // and the delivered count single-writer.
            if (Interlocked.CompareExchange(ref _pumping, 1, 0) != 0)
                return;

            try
            {
                while (true)
                {
                    AudioRenderCallback render;
                    int samples;
                    lock (_sync)
                    {
                        if (_disposed || !_playing)
                            return;

                        long target = FramesAtLocked(PositionLocked());
                        long behind = target - _deliveredFrames;
                        if (behind <= 0)
                            return;

                        long maxCatchUp = FramesAtLocked(MaxCatchUp);
                        if (behind > maxCatchUp)
                        {
                            _deliveredFrames = target - maxCatchUp;
                            behind = maxCatchUp;
                        }

                        int frames = (int)Math.Min(behind, _block.Length / _channels);
                        _deliveredFrames += frames;
                        samples = frames * _channels;
                        render = _render;
                    }

                    // outside the lock: the callback reaches into the decode ring and the sink's
                    // counters, and must never run under this device's own lock.
                    render(_block.AsSpan(0, samples));
                }
            }
            catch
            {
                // a throwing render callback must not take down the timer thread, exactly as a
                // device callback must not throw into the driver.
            }
            finally
            {
                Interlocked.Exchange(ref _pumping, 0);
            }
        }

        public void Dispose()
        {
            Timer pump;
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _playing = false;
                pump = _pump;
                _pump = null;
            }

            pump?.Dispose();
        }
    }
}
