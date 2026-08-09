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
        private readonly object _sync = new object();
        private readonly IMonotonicTime _time;
        private IAudioClockSource _audio;
        private TimeSpan _basePosition;
        private TimeSpan _baseElapsed;
        private bool _running;

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
                return _audio.PlayedTime;
            if (!_running)
                return _basePosition;
            return _basePosition + (_time.Elapsed - _baseElapsed);
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_running)
                    return;
                _baseElapsed = _time.Elapsed;
                _running = true;
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
            }
        }

        /// <summary>Rebase to a new media position (seek / immediate present).</summary>
        public void SetPosition(TimeSpan position)
        {
            lock (_sync)
            {
                _basePosition = position;
                _baseElapsed = _time.Elapsed;
            }
        }
    }
}
