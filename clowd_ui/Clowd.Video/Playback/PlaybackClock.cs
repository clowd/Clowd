using System;
using System.Diagnostics;

namespace Clowd.Video.Playback
{
    /// <summary>Source of "wall time elapsed" — indirected so clock logic is unit-testable.</summary>
    public interface IMonotonicTime
    {
        TimeSpan Elapsed { get; }
    }

    internal sealed class StopwatchTime : IMonotonicTime
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        public TimeSpan Elapsed => _sw.Elapsed;
    }

    /// <summary>The audio renderer's notion of how much media time has actually been heard.</summary>
    public interface IAudioClockSource
    {
        /// <summary>False until audio has flowed after open/seek (then the stopwatch drives).</summary>
        bool HasTiming { get; }
        TimeSpan PlayedTime { get; }
    }

    /// <summary>
    /// The master playback clock. Audio-master when an audio source is attached and has produced
    /// timing (video slaves to what has been *heard*); otherwise a monotonic stopwatch from the
    /// last set position. Pausing freezes it; SetPosition rebases it (seek).
    /// All members are safe to call from any thread.
    /// </summary>
    public sealed class PlaybackClock
    {
        /// <summary>How far the clock may run ahead of the last audio position it actually saw.
        /// Interpolation smooths the renderer's coarse updates; this stops it from inventing time
        /// indefinitely if audio stops advancing altogether (device lost, permanent underrun),
        /// where freezing is the honest answer.</summary>
        private static readonly TimeSpan MaxAudioInterpolation = TimeSpan.FromMilliseconds(500);

        private readonly object _sync = new object();
        private readonly IMonotonicTime _time;
        private IAudioClockSource _audio;
        private TimeSpan _basePosition;
        private TimeSpan _baseElapsed;
        private bool _running;

        // audio-master interpolation: the last position the renderer reported, and when we saw it.
        private TimeSpan _audioAnchor;
        private TimeSpan _audioAnchorElapsed;
        private bool _hasAudioAnchor;

        public PlaybackClock(IMonotonicTime time = null)
        {
            _time = time ?? new StopwatchTime();
        }

        /// <summary>Attach/detach the audio master (detached when audio ends before video).</summary>
        public void SetAudioSource(IAudioClockSource audio)
        {
            lock (_sync)
            {
                // preserve continuity: rebase the stopwatch fallback at the current position.
                var pos = PositionLocked();
                _audio = audio;
                _basePosition = pos;
                _baseElapsed = _time.Elapsed;
                ResetAudioAnchorLocked();
            }
        }

        public bool IsRunning
        {
            get { lock (_sync) return _running; }
        }

        public TimeSpan Position
        {
            get { lock (_sync) return PositionLocked(); }
        }

        private TimeSpan PositionLocked()
        {
            // audio only drives the clock while running: paused, the position is whatever was
            // last set (seek target / stepped frame pts) — the audio renderer's notion is stale
            // and would pin the position (e.g. frame steps would not move it).
            if (_running && _audio != null && _audio.HasTiming)
                return InterpolatedAudioLocked();
            if (!_running)
                return _basePosition;
            return _basePosition + (_time.Elapsed - _baseElapsed);
        }

        /// <summary>
        /// The audio position, carried forward by wall time between renderer updates. WASAPI only
        /// moves its played-time once per device callback (~10ms), so reading it raw makes the
        /// master clock a step function: video frames due inside a step all come due at once, and
        /// the presenter drops the ones that land more than a frame late. Anchoring on each new
        /// audio value and interpolating from it keeps the clock smooth without letting it drift —
        /// every update re-anchors, so the error can never exceed one renderer callback.
        /// </summary>
        private TimeSpan InterpolatedAudioLocked()
        {
            var played = _audio.PlayedTime;
            var elapsed = _time.Elapsed;

            if (!_hasAudioAnchor || played != _audioAnchor)
            {
                _audioAnchor = played;
                _audioAnchorElapsed = elapsed;
                _hasAudioAnchor = true;
                return played;
            }

            var lead = elapsed - _audioAnchorElapsed;
            if (lead > MaxAudioInterpolation)
                lead = MaxAudioInterpolation;
            return _audioAnchor + lead;
        }

        /// <summary>Drops the interpolation anchor so the next read re-syncs to the renderer
        /// (audio source swapped, seek, or playback stopped).</summary>
        private void ResetAudioAnchorLocked()
        {
            _hasAudioAnchor = false;
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_running)
                    return;
                _baseElapsed = _time.Elapsed;
                _running = true;
                ResetAudioAnchorLocked();
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                if (!_running)
                    return;
                _basePosition = PositionLocked();
                _running = false;
                ResetAudioAnchorLocked();
            }
        }

        /// <summary>Rebase to a new media position (seek / immediate present).</summary>
        public void SetPosition(TimeSpan position)
        {
            lock (_sync)
            {
                _basePosition = position;
                _baseElapsed = _time.Elapsed;
                ResetAudioAnchorLocked();
            }
        }
    }
}
