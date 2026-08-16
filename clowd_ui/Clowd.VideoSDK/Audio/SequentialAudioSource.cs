using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Audio
{
    /// <summary>
    /// The render path's <see cref="IAudioSource"/>: monotonic, forward-only, no seeking — the
    /// audio twin of <c>SequentialFrameSource</c>. Each requested (sourceId, streamIndex) lazily
    /// opens its own <see cref="SyncAudioStreamDecoder"/> and decodes forward on demand, keeping
    /// only the small window of samples between the last consumed position and the decode head.
    ///
    /// <para>
    /// Positioning: the stream's first decoded chunk anchors its absolute sample position (from
    /// its normalized PTS); after that, position accumulates by sample count — the only correct
    /// choice under resampling, where swr's internal delay makes per-chunk PTS lag the output.
    /// A forward PTS jump beyond <see cref="GapToleranceTicks"/> (a recording gap) inserts
    /// silence to realign; backwards PTS never rewind (clamped, mirroring the video cursor).
    /// </para>
    ///
    /// <para>
    /// Requests per stream are expected to run forward and not overlap (each read consumes through
    /// its end) — the mixer's per-item forward runs satisfy that by construction, and the whole
    /// stream then decodes exactly once. A request that goes backwards means the timeline reads the
    /// stream out of source order (a clip moved behind an earlier one, split halves swapped, the
    /// same span used twice): the decoder container-seeks to <see cref="SeekPrerollTicks"/> before
    /// the wanted position and decode-discards to the exact sample, the same reposition
    /// <see cref="SeekableAudioSource"/> performs, so the samples land where a forward decode put
    /// them. Forward requests never seek — they keep the decode-discard path this class has always
    /// used, which is what keeps cut seams identical to earlier renders.
    /// </para>
    /// </summary>
    public sealed class SequentialAudioSource : IAudioSource, IDisposable
    {
        private const int Channels = AudioMixer.Channels;

        /// <summary>Forward PTS jumps smaller than this are treated as encoder jitter and
        /// ignored; larger jumps (up to <see cref="MaxGapTicks"/>) insert silence.</summary>
        private const long GapToleranceTicks = 500_000; // 50 ms

        /// <summary>Jumps beyond this are treated as corrupt timestamps and ignored rather than
        /// flooding the pending buffer with silence.</summary>
        private const long MaxGapTicks = 60L * 10_000_000; // 60 s

        /// <summary>How far before the wanted position a backwards reposition lands, so the first
        /// kept sample carries the previous packet's transform overlap and matches a forward decode
        /// (see <see cref="SeekableAudioSource"/>, which uses the same preroll).</summary>
        private const long SeekPrerollTicks = 2_500_000; // 250 ms

        private readonly Project _project;
        private readonly int _rate;
        private readonly Dictionary<(Guid SourceId, int StreamIndex), StreamState> _streams
            = new Dictionary<(Guid, int), StreamState>();
        private int _repositions;
        private bool _disposed;

        private sealed class StreamState : IDisposable
        {
            public SyncAudioStreamDecoder Decoder;

            // contiguous unconsumed samples: Pending[OffsetFloats ..] holds Frames sample frames
            // starting at absolute position BaseAbs; WriteAbs is where the next append lands.
            public float[] Pending = Array.Empty<float>();
            public int OffsetFloats;
            public int Frames;
            public long BaseAbs;
            public long WriteAbs;
            public bool Positioned;
            public bool Eof;
            public long NextReadPos = long.MinValue; // end of the last read; earlier = reposition

            public void Dispose() => Decoder?.Dispose();
        }

        /// <param name="project">Resolves <c>SourceId</c> to file paths and supplies the output
        /// sample rate (<c>Output.SampleRate</c>). Not mutated.</param>
        public SequentialAudioSource(Project project)
        {
            ArgumentNullException.ThrowIfNull(project);
            if (project.Output == null || project.Output.SampleRate <= 0)
                throw new ArgumentException("Project has no positive output sample rate.", nameof(project));
            _project = project;
            _rate = project.Output.SampleRate;
        }

        /// <summary>Container seeks performed so far because a stream was read out of source order
        /// (test/diagnostic; a project whose items run forward never repositions).</summary>
        internal int RepositionCount => _repositions;

        public bool ReadSamples(Guid sourceId, int streamIndex, long sourcePosFrames, float[] dst,
            int frames, out int framesRead)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(dst);
            ArgumentOutOfRangeException.ThrowIfNegative(frames);
            ArgumentOutOfRangeException.ThrowIfNegative(sourcePosFrames);
            if ((long)frames * Channels > dst.Length)
                throw new ArgumentOutOfRangeException(nameof(frames),
                    $"{frames} stereo frames do not fit in a buffer of {dst.Length} floats.");

            var key = (sourceId, streamIndex);
            if (!_streams.TryGetValue(key, out var state))
            {
                state = OpenStream(sourceId, streamIndex);
                _streams[key] = state;
            }

            if (sourcePosFrames < state.NextReadPos)
                Reposition(state, sourcePosFrames);

            long end = sourcePosFrames + frames;
            state.NextReadPos = end;

            Array.Clear(dst, 0, frames * Channels);

            // decode forward until the window is covered (or the stream ends), discarding
            // samples before the window as they can never be requested again.
            while (!state.Eof && (!state.Positioned || state.WriteAbs < end))
            {
                if (!state.Decoder.DecodeNext(out long ptsTicks, out float[] samples, out int n))
                {
                    state.Eof = true;
                    break;
                }

                Append(state, samples, n, ptsTicks);
                DiscardBefore(state, sourcePosFrames);
            }

            if (!state.Positioned)
            {
                framesRead = 0;
                return false; // the stream yielded no audio at all
            }

            long overlapStart = Math.Max(sourcePosFrames, state.BaseAbs);
            long overlapEnd = Math.Min(end, state.BaseAbs + state.Frames);
            if (overlapEnd > overlapStart)
            {
                Array.Copy(state.Pending,
                    state.OffsetFloats + (int)(overlapStart - state.BaseAbs) * Channels,
                    dst, (int)(overlapStart - sourcePosFrames) * Channels,
                    (int)(overlapEnd - overlapStart) * Channels);
            }

            DiscardBefore(state, end);

            framesRead = (int)Math.Clamp(Math.Min(end, state.WriteAbs) - sourcePosFrames, 0, frames);
            return true;
        }

        /// <summary>Container-seeks the stream back to <paramref name="pos"/> (less the preroll)
        /// and drops the retained window, so the read that follows re-anchors from the decoder's
        /// new position and decode-discards to the exact sample.</summary>
        private void Reposition(StreamState state, long pos)
        {
            state.Decoder.Seek(Math.Max(0, AudioTime.TicksFloor(pos, _rate) - SeekPrerollTicks));
            _repositions++;

            state.OffsetFloats = 0;
            state.Frames = 0;
            state.BaseAbs = 0;
            state.WriteAbs = 0;
            state.Positioned = false;
            state.Eof = false;
        }

        /// <summary>Appends a decoded chunk, anchoring the stream position on the first chunk
        /// and inserting gap silence when the input PTS jumped forward (see class remarks).</summary>
        private void Append(StreamState state, float[] samples, int frames, long ptsTicks)
        {
            if (frames <= 0)
                return;

            if (!state.Positioned)
            {
                long basePos = ptsTicks == long.MinValue ? 0 : AudioTime.SamplesFloor(ptsTicks, _rate);
                state.BaseAbs = basePos;
                state.WriteAbs = basePos;
                state.Positioned = true;
            }
            else if (ptsTicks != long.MinValue)
            {
                long expectedTicks = AudioTime.TicksFloor(state.WriteAbs, _rate);
                long jump = ptsTicks - expectedTicks;
                if (jump > GapToleranceTicks && jump <= MaxGapTicks)
                {
                    long silence = AudioTime.SamplesFloor(ptsTicks, _rate) - state.WriteAbs;
                    if (silence > 0)
                        AppendFrames(state, null, 0, (int)silence);
                }
                // jump <= tolerance (jitter/resampler delay) or backwards: accumulate — the
                // position never rewinds.
            }

            AppendFrames(state, samples, 0, frames);
        }

        /// <summary>Appends frames to the pending window (null source = silence), compacting and
        /// growing the buffer as needed.</summary>
        private static void AppendFrames(StreamState state, float[] src, int srcOffsetFloats, int frames)
        {
            int needFloats = frames * Channels;
            int usedFloats = state.Frames * Channels;

            // compact consumed head first, then grow if still needed
            if (state.OffsetFloats > 0 &&
                state.OffsetFloats + usedFloats + needFloats > state.Pending.Length)
            {
                Array.Copy(state.Pending, state.OffsetFloats, state.Pending, 0, usedFloats);
                state.OffsetFloats = 0;
            }
            if (usedFloats + needFloats > state.Pending.Length)
            {
                var grown = new float[Math.Max(usedFloats + needFloats, state.Pending.Length * 2)];
                Array.Copy(state.Pending, state.OffsetFloats, grown, 0, usedFloats);
                state.Pending = grown;
                state.OffsetFloats = 0;
            }

            int dstFloats = state.OffsetFloats + usedFloats;
            if (src != null)
                Array.Copy(src, srcOffsetFloats, state.Pending, dstFloats, needFloats);
            else
                Array.Clear(state.Pending, dstFloats, needFloats);

            state.Frames += frames;
            state.WriteAbs += frames;
        }

        /// <summary>Drops pending frames strictly before <paramref name="pos"/> — the monotonic
        /// guard means they can never be requested again.</summary>
        private static void DiscardBefore(StreamState state, long pos)
        {
            if (!state.Positioned)
                return;
            long drop = Math.Clamp(pos - state.BaseAbs, 0, state.Frames);
            if (drop <= 0)
                return;
            state.OffsetFloats += (int)drop * Channels;
            state.Frames -= (int)drop;
            state.BaseAbs += drop;
        }

        private StreamState OpenStream(Guid sourceId, int streamIndex)
        {
            Source source = null;
            if (_project.Sources != null)
            {
                foreach (var s in _project.Sources)
                {
                    if (s.Id == sourceId)
                    {
                        source = s;
                        break;
                    }
                }
            }

            if (source == null)
                throw new ArgumentException($"Project has no source {sourceId}.", nameof(sourceId));

            return new StreamState
            {
                Decoder = new SyncAudioStreamDecoder(source.Path, streamIndex, _rate),
            };
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var state in _streams.Values)
                state.Dispose();
            _streams.Clear();
        }
    }
}
