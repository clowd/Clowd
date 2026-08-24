using System;

namespace Clowd.VideoSDK.Audio
{
    /// <summary>
    /// A forward-moving carry window of mixed project sample frames, shared by every consumer
    /// that bends the mix through a warp (<see cref="Render.WarpAudioResampler"/> and the
    /// preview's <c>AudioMixWorker</c>): the mixer's sources are forward-only, so each project
    /// frame is mixed exactly once and held here until every reader that still interpolates
    /// against it has moved past. <see cref="Ensure"/> drops frames the cursor has passed
    /// (keeping a small guard of slack behind it) and mixes forward to cover the requested
    /// range; requests must therefore advance monotonically, give or take the guard.
    ///
    /// <para>
    /// The mixer reference is swappable mid-stream: the preview replaces its mixer snapshot on
    /// a volume edit (same sources, new gains) and after a seek, and the window deliberately
    /// keeps its already-mixed frames across the swap — a gain edit becomes audible on the next
    /// mixed frame, never by re-mixing (and so repositioning) a source. <see cref="LimitFrame"/>
    /// caps how far the mixer is asked to produce: frames at or past it read as silence, so a
    /// reader can run to the end of a chunk without dragging the sources past the audio end.
    /// </para>
    /// </summary>
    public sealed class MixerWindow
    {
        private const int Channels = AudioMixer.Channels;

        private readonly int _guardFrames;
        private readonly Action<Exception> _onMixError;

        private float[] _window = Array.Empty<float>();
        private long _windowStart;
        private int _windowFrames;
        private float[] _scratch = Array.Empty<float>();

        /// <param name="guardFrames">Project frames kept behind the requested start when the
        /// window advances: consumers whose positions can regress a fraction of a frame across
        /// span joins keep those frames readable instead of degrading to a clamped duplicate.</param>
        /// <param name="onMixError">When set, an exception out of <see cref="AudioMixer.MixChunk"/>
        /// is reported here and the failed range reads as silence (the preview's per-chunk error
        /// isolation); when null the exception propagates (the render fails the job).</param>
        public MixerWindow(AudioMixer mixer, int guardFrames, Action<Exception> onMixError = null)
        {
            ArgumentNullException.ThrowIfNull(mixer);
            ArgumentOutOfRangeException.ThrowIfNegative(guardFrames);
            Mixer = mixer;
            _guardFrames = guardFrames;
            _onMixError = onMixError;
        }

        /// <summary>The mixer new frames are pulled from; swap freely (see class remarks).</summary>
        public AudioMixer Mixer { get; set; }

        /// <summary>Exclusive project frame the mixer is never asked past; requested frames at or
        /// beyond it fill as silence. Default unbounded.</summary>
        public long LimitFrame { get; set; } = long.MaxValue;

        /// <summary>The window's frames, interleaved stereo; index 0 is <see cref="StartFrame"/>.</summary>
        public float[] Buffer => _window;

        /// <summary>Absolute project frame of the buffer's first held frame.</summary>
        public long StartFrame => _windowStart;

        /// <summary>Frames currently held.</summary>
        public int Frames => _windowFrames;

        /// <summary>Forgets everything; the next <see cref="Ensure"/> starts a fresh window (the
        /// seek path, where the sources reposition anyway).</summary>
        public void Reset()
        {
            _windowStart = 0;
            _windowFrames = 0;
        }

        /// <summary>Drops window frames below <paramref name="firstNeeded"/> −
        /// <see cref="_guardFrames"/> and mixes forward so the window covers
        /// [<paramref name="firstNeeded"/>, <paramref name="endNeeded"/>).</summary>
        public void Ensure(long firstNeeded, long endNeeded)
        {
            if (firstNeeded < 0)
                firstNeeded = 0;

            long dropBelow = firstNeeded - _guardFrames;
            if (_windowFrames > 0 && dropBelow > _windowStart)
            {
                int drop = (int)Math.Min(dropBelow - _windowStart, _windowFrames);
                Array.Copy(_window, drop * Channels, _window, 0, (_windowFrames - drop) * Channels);
                _windowStart += drop;
                _windowFrames -= drop;
            }
            if (_windowFrames == 0 && firstNeeded > _windowStart)
                _windowStart = firstNeeded;

            long readStart = _windowStart + _windowFrames;
            int readCount = (int)Math.Max(0, endNeeded - readStart);
            if (readCount == 0)
                return;

            int haveFloats = _windowFrames * Channels;
            int needFloats = haveFloats + readCount * Channels;
            if (_window.Length < needFloats)
            {
                var grown = new float[Math.Max(needFloats, _window.Length * 2)];
                Array.Copy(_window, grown, haveFloats);
                _window = grown;
            }

            int mixCount = (int)Math.Clamp(LimitFrame - readStart, 0, readCount);
            if (_scratch.Length < readCount * Channels)
                _scratch = new float[readCount * Channels];
            if (mixCount > 0)
            {
                try
                {
                    Mixer.MixChunk(readStart, mixCount, _scratch);
                }
                catch (Exception ex) when (_onMixError != null)
                {
                    _onMixError(ex);
                    Array.Clear(_scratch, 0, mixCount * Channels);
                }
            }
            if (mixCount < readCount)
                Array.Clear(_scratch, mixCount * Channels, (readCount - mixCount) * Channels);

            Array.Copy(_scratch, 0, _window, haveFloats, readCount * Channels);
            _windowFrames += readCount;
        }

        /// <summary>Clamps a project frame into the held range — the readable fallback when a
        /// span join regresses under a frame or a position runs past the window's end.</summary>
        public long Clamp(long frame)
        {
            if (frame < _windowStart)
                return _windowStart;
            long last = _windowStart + _windowFrames - 1;
            return frame > last ? last : frame;
        }
    }
}
