using System;
using System.Collections.Generic;
using System.Threading;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Audio
{
    /// <summary>
    /// The preview path's <see cref="IAudioSource"/>: the repositionable twin of
    /// <see cref="SequentialAudioSource"/>. Each requested (sourceId, streamIndex) lazily opens its
    /// own <see cref="SyncAudioStreamDecoder"/>, and the pending-window / gap-silence / anchor
    /// bookkeeping is <b>the same logic as the render source</b> — deliberately copied rather than
    /// shared, so the render path keeps a source that provably cannot seek.
    ///
    /// <para>
    /// <b>What differs:</b> requests do not have to be monotonic. A request that starts before the
    /// samples still held (the caller went backwards) or more than
    /// <see cref="ForwardSeekThresholdSeconds"/> past the decode head (a big forward hop) issues a
    /// container seek (<see cref="SyncAudioStreamDecoder.Seek"/>) to
    /// <see cref="SeekPrerollTicks"/> before the wanted position and then decode-discards to the
    /// exact sample. Smaller forward jumps keep decoding and discarding, exactly like the render
    /// source, so a cut seam in preview reads the same samples the render will.
    /// </para>
    ///
    /// <para>
    /// <b>No monotonic guard, by design.</b> <see cref="SequentialAudioSource"/> keeps a
    /// <c>NextReadPos</c> and throws on a regression because <see cref="AudioMixer"/> pulls each
    /// item as one forward run and anything else is a bug in render. In preview the mixer is a
    /// disposable object — <c>AudioMixWorker</c> builds a new one on every project edit and every
    /// seek, over the <i>same</i> long-lived source (the decoders must survive; reopening files per
    /// edit is what the design avoids) — so a "regression" is normal traffic and coordinating a
    /// guard across mixer swaps would be state the source does not need. Positioning is therefore
    /// decided from data the source owns: the retained window (<c>BaseAbs</c>) and the decode head
    /// (<c>WriteAbs</c>). <see cref="Reset"/> stays as the explicit invalidation hook for
    /// "the timeline moved, forget where you were" (player seek), forcing each stream to
    /// reposition on its next read instead of trusting a window that now belongs to another
    /// playback position.
    /// </para>
    /// </summary>
    public sealed class SeekableAudioSource : IAudioSource, IDisposable
    {
        private const int Channels = AudioMixer.Channels;

        /// <summary>Forward PTS jumps smaller than this are treated as encoder jitter and
        /// ignored; larger jumps (up to <see cref="MaxGapTicks"/>) insert silence.</summary>
        private const long GapToleranceTicks = 500_000; // 50 ms

        /// <summary>Jumps beyond this are treated as corrupt timestamps and ignored rather than
        /// flooding the pending buffer with silence.</summary>
        private const long MaxGapTicks = 60L * 10_000_000; // 60 s

        /// <summary>How far before the wanted position a container seek lands. A compressed audio
        /// frame is decoded from its own packet plus the previous packet's transform overlap, so
        /// even one frame of preroll makes the first kept sample bit-identical to a forward decode;
        /// 250 ms is decode-cheap and covers long frame sizes and imprecise seek points.</summary>
        private const long SeekPrerollTicks = 2_500_000; // 250 ms

        /// <summary>Forward hops larger than this container-seek instead of decode-discarding.
        /// Below it the decode-discard path is both faster than a seek and sample-exact with the
        /// render source, which is what keeps cut seams identical between preview and render.</summary>
        private const int ForwardSeekThresholdSeconds = 2;

        private readonly Project _project;
        private readonly int _rate;
        private readonly Dictionary<(Guid SourceId, int StreamIndex), StreamState> _streams
            = new Dictionary<(Guid, int), StreamState>();
        private int _decoderOpens;
        private int _repositionCount;
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
            public bool NeedsReposition; // set by Reset(): next read repositions wherever it lands

            public void Dispose() => Decoder?.Dispose();
        }

        /// <param name="project">Resolves <c>SourceId</c> to file paths and supplies the output
        /// sample rate (<c>Output.SampleRate</c>). Not mutated.</param>
        public SeekableAudioSource(Project project)
        {
            ArgumentNullException.ThrowIfNull(project);
            if (project.Output == null || project.Output.SampleRate <= 0)
                throw new ArgumentException("Project has no positive output sample rate.", nameof(project));
            _project = project;
            _rate = project.Output.SampleRate;
        }

        /// <summary>Container seeks performed so far (test/diagnostic; reads happen on the owning
        /// thread, but the player's seam-hop assertions read it from outside).</summary>
        internal int RepositionCount => Volatile.Read(ref _repositionCount);

        /// <summary>Decoders opened so far (test/diagnostic; reads happen on the owning thread,
        /// but <c>AudioMixWorker</c>'s cheap-path assertions read it from outside).</summary>
        internal int DecoderOpenCount => Volatile.Read(ref _decoderOpens);

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

            long end = sourcePosFrames + frames;

            if (ShouldReposition(state, sourcePosFrames))
                Reposition(state, sourcePosFrames);

            Array.Clear(dst, 0, frames * Channels);

            // decode forward until the window is covered (or the stream ends), discarding samples
            // before the window — after a reposition this is what walks off the preroll.
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

            // keep the window that was just read: a mixer swapped in mid-chunk re-reads it, and a
            // short step back stays a memcpy instead of a seek. Bounded by one read plus one
            // decoded frame either way.
            DiscardBefore(state, sourcePosFrames);

            framesRead = (int)Math.Clamp(Math.Min(end, state.WriteAbs) - sourcePosFrames, 0, frames);
            return true;
        }

        /// <summary>
        /// Forgets where every stream was, so the next read repositions to whatever it asks for.
        /// The preview player calls this when the timeline position jumps (seek): the retained
        /// windows describe a position that is no longer being played, and their decode heads
        /// would otherwise decide the read is a cheap forward continuation.
        /// </summary>
        public void Reset()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var state in _streams.Values)
                state.NeedsReposition = true;
        }

        /// <summary>True when <paramref name="pos"/> cannot be served by decoding forward from
        /// where this stream stands (see class remarks).</summary>
        private bool ShouldReposition(StreamState state, long pos)
        {
            if (state.NeedsReposition)
                return true;
            if (state.Positioned && pos < state.BaseAbs)
                return true; // backwards past the retained samples — they are gone
            if (state.Eof)
                return false; // forward of the stream's end: silence, no point reopening the hunt
            long head = state.Positioned ? state.WriteAbs : 0;
            return pos - head > (long)ForwardSeekThresholdSeconds * _rate;
        }

        private void Reposition(StreamState state, long pos)
        {
            state.Decoder.Seek(Math.Max(0, AudioTime.TicksFloor(pos, _rate) - SeekPrerollTicks));
            Interlocked.Increment(ref _repositionCount);

            state.OffsetFloats = 0;
            state.Frames = 0;
            state.BaseAbs = 0;
            state.WriteAbs = 0;
            state.Positioned = false; // re-anchored from the first chunk decoded after the seek
            state.Eof = false;
            state.NeedsReposition = false;
        }

        /// <summary>Appends a decoded chunk, anchoring the stream position on the first chunk
        /// and inserting gap silence when the input PTS jumped forward (see class remarks).</summary>
        private void Append(StreamState state, float[] samples, int frames, long ptsTicks)
        {
            if (frames <= 0)
                return;

            if (!state.Positioned)
            {
                // Nearest, NOT floor: this pts is a sample position rendered into ticks, and the
                // rendering rounds — flooring it back shifts the anchor (and with it every sample
                // of a post-seek window) down a sample whenever the rounding fell short. The
                // render source keeps its floor: its one anchor is the stream's first chunk,
                // whose pts is 0 after start_time normalization, where the two agree — so
                // forward reads stay bit-identical with it.
                long basePos = ptsTicks == long.MinValue ? 0 : AudioTime.SamplesNearest(ptsTicks, _rate);
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

        /// <summary>Drops pending frames strictly before <paramref name="pos"/>; anything earlier
        /// is reachable again only through a reposition.</summary>
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

            var state = new StreamState
            {
                Decoder = new SyncAudioStreamDecoder(source.Path, streamIndex, _rate),
            };
            Interlocked.Increment(ref _decoderOpens);
            return state;
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
