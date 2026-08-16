using System;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The frame-at-time-t selection state machine of the render path, extracted from
    /// <see cref="SequentialFrameSource"/> so the PTS logic is testable without FFmpeg.
    ///
    /// Holds current + next per stream and advances monotonically: <see cref="TryAdvance"/>
    /// pulls frames forward while <c>next.pts &lt;= t</c>, so the answer is always <b>the frame
    /// with the latest PTS at or before t</b> — correct for CFR and VFR alike. A PTS gap or
    /// freeze holds the current frame; a source faster than the output has intermediate frames
    /// pulled (they may be decode references) but immediately discarded; a request before the
    /// first frame's PTS returns that first frame (hold-first, so a stream whose timestamps
    /// start marginally late never flashes empty). Backwards PTS jumps from the puller are
    /// clamped to the last seen PTS — the cursor never rewinds.
    ///
    /// Ownership: frames the caller never sees (skipped, or replaced before delivery) go to the
    /// discard callback exactly once. A frame handed out through <c>newFrame</c> belongs to the
    /// caller and is never discarded by the cursor. Requests must be non-decreasing; a regression
    /// throws — the owner repositions the puller and calls <see cref="Rewind"/> first, which is how
    /// <see cref="SequentialFrameSource"/> serves a project that reads one stream out of source
    /// order (a clip moved behind an earlier one).
    /// </summary>
    internal sealed class SequentialFrameCursor<T> : IDisposable where T : class
    {
        /// <summary>Decodes the next frame in stream order. False = end of stream (final).</summary>
        public delegate bool PullDelegate(out long ptsTicks, out T frame);

        private readonly PullDelegate _pull;
        private readonly Action<T> _discard;

        private bool _started;
        private bool _eof;
        private bool _disposed;

        private bool _hasCurrent;
        private long _currentPts;
        private T _undelivered; // the current frame, if the caller has not taken it yet

        private bool _hasNext;
        private long _nextPts;
        private T _next;

        private long _lastRequestTicks = long.MinValue;
        private long _lastPulledPts = long.MinValue;

        public SequentialFrameCursor(PullDelegate pull, Action<T> discard)
        {
            ArgumentNullException.ThrowIfNull(pull);
            ArgumentNullException.ThrowIfNull(discard);
            _pull = pull;
            _discard = discard;
        }

        /// <summary>
        /// Positions the cursor at <paramref name="ticks"/> (non-decreasing across calls) and
        /// reports the covering frame. Returns false only when the stream yielded no frames at
        /// all. <paramref name="newFrame"/> is non-null exactly when the covering frame changed
        /// (or was never delivered) — ownership of it passes to the caller; on repeat requests
        /// for an unchanged frame it is null and <paramref name="ptsTicks"/> still identifies
        /// the frame.
        /// </summary>
        public bool TryAdvance(long ticks, out long ptsTicks, out T newFrame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ticks < _lastRequestTicks)
                throw new InvalidOperationException(
                    $"Sequential frame requests must be non-decreasing (got {ticks} after {_lastRequestTicks}).");
            _lastRequestTicks = ticks;

            if (!_started)
            {
                _started = true;
                if (Pull(out _currentPts, out _undelivered))
                {
                    _hasCurrent = true;
                    _hasNext = Pull(out _nextPts, out _next);
                }
            }

            while (_hasNext && _nextPts <= ticks)
            {
                if (_undelivered != null)
                {
                    _discard(_undelivered);
                    _undelivered = null;
                }

                _currentPts = _nextPts;
                _undelivered = _next;
                _next = null;
                _hasNext = Pull(out _nextPts, out _next);
            }

            if (!_hasCurrent)
            {
                ptsTicks = 0;
                newFrame = null;
                return false;
            }

            ptsTicks = _currentPts;
            newFrame = _undelivered;
            _undelivered = null;
            return true;
        }

        /// <summary>
        /// Drops every held frame and forgets the position, so the next <see cref="TryAdvance"/>
        /// starts the stream over from wherever the puller now stands (its owner has just
        /// repositioned it). Requests are non-decreasing again from that call on.
        /// </summary>
        public void Rewind()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_undelivered != null)
            {
                _discard(_undelivered);
                _undelivered = null;
            }

            if (_next != null)
            {
                _discard(_next);
                _next = null;
            }

            _started = false;
            _eof = false;
            _hasCurrent = false;
            _hasNext = false;
            _currentPts = 0;
            _nextPts = 0;
            _lastRequestTicks = long.MinValue;
            _lastPulledPts = long.MinValue;
        }

        private bool Pull(out long ptsTicks, out T frame)
        {
            ptsTicks = 0;
            frame = null;
            if (_eof)
                return false;

            if (!_pull(out ptsTicks, out frame))
            {
                _eof = true;
                frame = null;
                return false;
            }

            // clamp non-monotonic PTS: a backwards jump never rewinds the cursor.
            if (_lastPulledPts != long.MinValue && ptsTicks < _lastPulledPts)
                ptsTicks = _lastPulledPts;
            _lastPulledPts = ptsTicks;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_undelivered != null)
            {
                _discard(_undelivered);
                _undelivered = null;
            }

            if (_next != null)
            {
                _discard(_next);
                _next = null;
            }
        }
    }
}
