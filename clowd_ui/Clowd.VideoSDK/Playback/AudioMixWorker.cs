using System;
using System.Collections.Generic;
using System.Threading;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Media;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Playback
{
    /// <summary>
    /// The preview's audio producer, replacing the single-stream <see cref="AudioDecodeWorker"/>:
    /// one thread mixing 20ms chunks of the project's audio items into the ring the platform sink
    /// drains. The mix itself is the <b>literal render mixer</b> — <see cref="AudioMixer.MixChunk"/>,
    /// per-sample gain <c>Item.Volume × TransitionMath entry/exit progress</c> — pulled over a
    /// <see cref="SeekableAudioSource"/>, so what preview plays is what render writes, by
    /// construction. The worker's stream domain is <b>warped output time</b>: chunk positions and
    /// the sink's base pts after a flush live in the domain the player's clock runs in. While no
    /// speed item bends time the domains coincide and the mix runs untouched in timeline time —
    /// there is no source↔clock mapping on the audio path (a cut seam is just two adjacent items
    /// to the mixer, and timeline gaps mix to silence). Under a <see cref="TimeWarp"/> each
    /// device frame's project position comes from the warp's spans and the mixed project frames
    /// are resampled per-sample onto the output grid (see <see cref="MixWarpedChunk"/>), so a
    /// speed ramp glides continuously — pitch riding with the speed — with no flush or timing
    /// rebase anywhere inside it.
    ///
    /// <para>
    /// Concurrency, copied from <see cref="AudioDecodeWorker"/>'s shape: ALL mixing state (mixer,
    /// source, positions) is owned by the mix thread; other threads communicate exclusively
    /// through latest-wins request slots (<see cref="PrepareSeek"/>/<see cref="UpdateProject"/>)
    /// the thread adopts at chunk boundaries. The ring's SPSC contract holds — the mix thread is
    /// the ring's only producer, so it alone calls <c>Clear</c>. A project swap builds a fresh
    /// <see cref="AudioMixer"/> over the long-lived source (decoders untouched — that is the
    /// cheap path volume/transition edits ride); a seek flushes ring + sink timing and resets the
    /// source's positions through the seam built for it (<see cref="SeekableAudioSource.Reset"/>).
    /// </para>
    ///
    /// <para>
    /// Production stops at <c>min(timeline duration, AudioMixer.GetAudioEndTicks)</c>
    /// (<see cref="EofReached"/>); the player then detaches the audio master so video plays out
    /// on the stopwatch, exactly as before. The ring is deliberately only filled to
    /// ~half its capacity: mixed gain is baked into the samples, so buffered lead is exactly the
    /// latency of a volume/transition edit — half a ring keeps edits audible quickly while still
    /// cushioning a container-seek stall. A decode failure surfaces through <see cref="Error"/>
    /// and turns that stream to silence — it never kills video (or the other streams).
    /// </para>
    /// </summary>
    internal sealed class AudioMixWorker : IDisposable
    {
        private const int Channels = AudioMixer.Channels;

        /// <summary>Timeline frames carried between resampled chunks. The overlap is at most two
        /// frames (the interpolation reads one frame past the last output sample); the rest is
        /// slack so a rounding surprise degrades to a carry reset, not to reading past the
        /// buffer.</summary>
        private const int CarryFrames = 8;

        private readonly AudioRingBuffer _ring;
        private readonly NAudioSink _sink;
        private readonly int _rate;
        private readonly int _chunkFrames;      // 20ms
        private readonly int _maxBufferedFloats;
        private readonly float[] _chunk;
        private readonly SeekableAudioSource _source;
        private readonly FaultIsolatingSource _readSource;

        // request slots (any thread); long.MinValue / null / 0 = empty. The initial pending seek
        // to 0 makes the first chunk after Start establish flush + timing even without a
        // PrepareSeek.
        private long _pendingSeekTicks;
        private PendingUpdate _pendingUpdate;
        private double _pendingSpeed;
        private Exception _error;
        private volatile bool _running;
        private volatile bool _eofReached;

        // mix-thread-owned state. _nextFrame/_endFrame are stream-domain sample frames — output
        // frames under a warp, timeline frames otherwise; _srcEndFrame is always the
        // project-domain mixing limit (equal to _endFrame while unwarped).
        private Project _project;
        private AudioMixer _mixer;
        private long _nextFrame;
        private long _endFrame;
        private long _srcEndFrame;
        private bool _basePtsPending = true;

        // warp state (mix-thread-owned; the ctor seeds it before Start). The span table mirrors
        // Render.WarpAudioResampler's: direct (speed-1) spans keep an exact integer frame offset
        // so unwarped stretches stay verbatim copies of the project mix, warped spans map each
        // output instant through the warp inverse. _outPos is the fractional output frame the
        // next device frame sits at, _lastPos the monotone clamp on project positions across
        // span joins.
        private TimeWarp _warp;
        private bool _warped;
        private WarpSpan[] _spans;
        private int _spanIndex;
        private double _outPos;
        private double _lastPos = -1;
        private double[] _positions = Array.Empty<double>();

        // playback speed state (mix-thread-owned; all unused while _speed == 1). _srcPos is the
        // fractional timeline frame the next output frame samples at, _carry holds the tail
        // timeline frames of the previous chunk that the next one still interpolates from — the
        // mixer's sources are forward-only, so an overlapping frame must be carried, never
        // re-requested.
        private double _speed = 1.0;
        private double _srcPos;
        private float[] _src = Array.Empty<float>();
        private readonly float[] _carry = new float[CarryFrames * Channels];
        private long _carryStart;
        private int _carryCount;

        private Thread _thread;
        private bool _disposed;

        public AudioMixWorker(Project project, AudioRingBuffer ring, NAudioSink sink, int sampleRate,
            TimeWarp warp = null)
        {
            ArgumentNullException.ThrowIfNull(project);
            ArgumentNullException.ThrowIfNull(ring);
            ArgumentNullException.ThrowIfNull(sink);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

            _ring = ring;
            _sink = sink;
            _rate = sampleRate;
            _chunkFrames = Math.Max(1, sampleRate / 50);
            _maxBufferedFloats = ring.Capacity / 2;
            _chunk = new float[_chunkFrames * Channels];

            _project = NormalizeRate(project);
            _source = new SeekableAudioSource(_project);
            _readSource = new FaultIsolatingSource(this);
            _mixer = new AudioMixer(_project, _readSource);
            AdoptWarp(warp);
            UpdateEndFrames(_project);
        }

        /// <summary>True once every producible sample up to the audio end is in the ring; cleared
        /// by a seek back into range or a project update that extends the audio.</summary>
        public bool EofReached => _eofReached;

        /// <summary>First decode failure (that stream mixes as silence from then on); null while
        /// healthy. Audio errors never take playback down — video keeps running.</summary>
        public Exception Error => Volatile.Read(ref _error);

        /// <summary>Decoders opened by the underlying source so far (test/diagnostic): cheap-path
        /// project swaps must not move it.</summary>
        internal int DecoderOpenCount => _source.DecoderOpenCount;

        /// <summary>Container seeks performed by the underlying source so far (test/diagnostic):
        /// a video seam hop must not move it — only a real timeline seek repositions the mix.</summary>
        internal int SourceRepositionCount => _source.RepositionCount;

        public void Start()
        {
            _running = true;
            _thread = new Thread(MixLoop)
            {
                Name = "clowd-amix",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }

        /// <summary>Controller thread: restart production at the timeline (project-time) position.
        /// The mix thread adopts it at the next chunk boundary — flushing the ring (it is the
        /// producer), resetting sink timing, and re-basing the sink's pts on the target mapped
        /// into the stream domain (its warped output instant; the target itself while no warp
        /// bends time).</summary>
        public void PrepareSeek(TimeSpan target)
        {
            Interlocked.Exchange(ref _pendingSeekTicks, Math.Max(0, target.Ticks));
            // optimistic clear so the player's EOF/detach logic stops seeing a stale EOF the
            // moment a seek is requested; the mix thread re-derives the truth when it adopts.
            _eofReached = false;
        }

        /// <summary>Controller thread: change playback speed (media time per device frame). The
        /// mix thread adopts it at the next chunk boundary and flushes — production restarts at
        /// the current position so the sink's timing describes the new mapping from its first
        /// sample. Samples are linearly resampled, so pitch rides with the speed (no time
        /// stretching), exactly like a player's speed control.</summary>
        public void SetSpeed(double speed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(speed, 0.0);
            Interlocked.Exchange(ref _pendingSpeed, speed);
        }

        /// <summary>Controller thread: swap in an edited project at the next chunk boundary
        /// (latest wins). Builds a fresh mixer snapshot over the SAME source — decoders are never
        /// touched, which is what keeps volume/transition edits cheap. The caller must hand over a
        /// snapshot it will not mutate (the mixer reads items live). A null <paramref name="warp"/>
        /// keeps the current warp; a caller changing the mapping must pair the update with a
        /// <see cref="PrepareSeek"/> (the player does), since the sink's timing describes the old
        /// mapping until the flush lands.</summary>
        public void UpdateProject(Project project, TimeWarp warp = null)
        {
            ArgumentNullException.ThrowIfNull(project);
            Interlocked.Exchange(ref _pendingUpdate, new PendingUpdate { Project = project, Warp = warp });
        }

        private sealed class PendingUpdate
        {
            public Project Project;
            public TimeWarp Warp;
        }

        private void MixLoop()
        {
            while (_running)
            {
                try
                {
                    MixIteration();
                }
                catch (Exception ex)
                {
                    // never let the mix thread die: record, and idle briefly so a persistent
                    // failure cannot spin the CPU.
                    RecordError(ex);
                    Thread.Sleep(10);
                }
            }
        }

        private void MixIteration()
        {
            var update = Interlocked.Exchange(ref _pendingUpdate, null);
            if (update != null)
                ApplyProject(update);

            long seek = Interlocked.Exchange(ref _pendingSeekTicks, long.MinValue);

            double speed = Interlocked.Exchange(ref _pendingSpeed, 0);
            if (speed > 0 && speed != _speed)
            {
                _speed = speed;
                // the sink maps device frames to media time through the speed, so the change
                // only makes sense from a flushed stream on: force one if none was asked for
                // (the seek slot carries project time — map the stream cursor back for it).
                if (seek == long.MinValue)
                {
                    seek = AudioTime.TicksFloor(_nextFrame, _rate);
                    if (_warped)
                        seek = _warp.ToProject(seek);
                }
            }

            if (seek != long.MinValue)
            {
                _ring.Clear(); // mix thread IS the producer — legal here and nowhere else
                _sink.ResetTiming(_speed);
                _source.Reset(); // the timeline moved: every stream repositions on its next read
                _mixer = new AudioMixer(_project, _readSource);
                long streamTicks = _warped ? _warp.ToOutput(seek) : seek;
                _nextFrame = AudioTime.SamplesFloor(streamTicks, _rate);
                _srcPos = _nextFrame;
                _outPos = _nextFrame;
                _spanIndex = 0;
                _lastPos = -1;
                _carryCount = 0;
                _basePtsPending = true;
            }

            bool eof = _nextFrame >= _endFrame;
            _eofReached = eof;
            if (eof)
            {
                Thread.Sleep(10); // idle until a seek/update/stop arrives
                return;
            }

            if (_ring.Available >= _maxBufferedFloats)
            {
                Thread.Sleep(5); // enough lead buffered; wait for the device to drain
                return;
            }

            if (_warped)
            {
                MixWarpedChunk();
                return;
            }

            if (_speed != 1.0)
            {
                MixResampledChunk();
                return;
            }

            int frames = (int)Math.Min(_chunkFrames, _endFrame - _nextFrame);
            try
            {
                _mixer.MixChunk(_nextFrame, frames, _chunk);
            }
            catch (Exception ex)
            {
                // per-stream failures are silenced inside FaultIsolatingSource; anything that
                // still escapes MixChunk degrades this chunk to silence rather than stalling.
                RecordError(ex);
                Array.Clear(_chunk, 0, frames * Channels);
            }

            if (_basePtsPending)
            {
                // first chunk after a flush: media time restarts at the chunk's own timeline
                // instant (ResetTiming above cleared the old base).
                _sink.TrySetBasePts(new TimeSpan(AudioTime.TicksFloor(_nextFrame, _rate)));
                _basePtsPending = false;
            }

            if (WriteToRing(new ReadOnlySpan<float>(_chunk, 0, frames * Channels)))
            {
                _nextFrame += frames;
                _srcPos = _nextFrame;
            }
            // an abandoned write means a newer seek superseded this chunk — the next iteration
            // flushes and re-mixes from the new position, so the partial samples never play.
        }

        /// <summary>
        /// The speed != 1 chunk: mix the stretch of timeline the device chunk covers, then
        /// linearly resample it down (or up) to one device chunk. The mixer is asked only for
        /// frames it has not produced yet — the one or two frames of overlap the interpolation
        /// still needs are carried in <see cref="_carry"/>, because the sources underneath are
        /// forward-only and re-requesting a frame would reposition them.
        /// </summary>
        private void MixResampledChunk()
        {
            int outFrames = _chunkFrames;
            long mixStart = _carryCount > 0 ? _carryStart + _carryCount : (long)Math.Floor(_srcPos);
            // the last timeline frame the chunk's final output sample interpolates against
            long lastNeeded = (long)Math.Floor(_srcPos + (outFrames - 1) * _speed) + 1;
            long needed = Math.Min(lastNeeded + 1 - mixStart, _endFrame - mixStart);
            if (needed < 0)
                needed = 0;

            if (needed > 0)
            {
                EnsureSrcCapacity((int)needed);
                try
                {
                    _mixer.MixChunk(mixStart, (int)needed, _src);
                }
                catch (Exception ex)
                {
                    RecordError(ex);
                    Array.Clear(_src, 0, (int)needed * Channels);
                }
            }

            long avail = mixStart + needed; // exclusive: frames below it are readable
            int produced = 0;
            for (int i = 0; i < outFrames; i++)
            {
                double p = _srcPos + i * _speed;
                long f0 = (long)Math.Floor(p);
                if (f0 >= avail)
                    break; // ran out of audio inside this chunk

                long f1 = Math.Min(f0 + 1, avail - 1);
                float frac = (float)(p - f0);
                for (int ch = 0; ch < Channels; ch++)
                {
                    float a = SampleAt(f0, mixStart, ch);
                    float b = SampleAt(f1, mixStart, ch);
                    _chunk[i * Channels + ch] = a + (b - a) * frac;
                }

                produced++;
            }

            if (produced == 0)
            {
                _nextFrame = _endFrame; // the cursor sits past the last sample: end of audio
                _eofReached = true;
                Thread.Sleep(10);
                return;
            }

            if (_basePtsPending)
            {
                _sink.TrySetBasePts(new TimeSpan(AudioTime.TicksFloor((long)Math.Floor(_srcPos), _rate)));
                _basePtsPending = false;
            }

            if (!WriteToRing(new ReadOnlySpan<float>(_chunk, 0, produced * Channels)))
                return; // superseded by a seek — cursor and carry stay where they were

            double nextPos = _srcPos + produced * _speed;
            long nextCursor = Math.Min((long)Math.Floor(nextPos), avail);
            int overlap = (int)(avail - nextCursor);
            if (overlap > 0 && overlap <= CarryFrames)
            {
                for (int i = 0; i < overlap; i++)
                {
                    for (int ch = 0; ch < Channels; ch++)
                        _carry[i * Channels + ch] = SampleAt(nextCursor + i, mixStart, ch);
                }

                _carryStart = nextCursor;
                _carryCount = overlap;
            }
            else
            {
                // no overlap (or, defensively, more than the carry holds): the next chunk starts
                // its mix at the cursor itself.
                _carryCount = 0;
                if (overlap > CarryFrames)
                    nextPos = avail;
            }

            _srcPos = nextPos;
            _nextFrame = (long)Math.Floor(nextPos);
        }

        /// <summary>
        /// The warped chunk (any speed): each device frame's output position advances by the
        /// playback speed; its project position comes from the span table — direct spans keep an
        /// exact integer frame offset, warped spans map the instant through the warp inverse,
        /// clamped monotone across span joins — and the mixed project frames are linearly
        /// interpolated at those positions. This is <see cref="Render.WarpAudioResampler"/>'s
        /// per-sample design run over this worker's carry window, so the mixer is still asked for
        /// each project frame exactly once and a mixer snapshot swap (volume edit) never
        /// repositions a source. On a direct span at speed 1 the positions are exact integers and
        /// the interpolation degenerates to verbatim copies — unwarped stretches keep the
        /// preview/render sample parity.
        /// </summary>
        private void MixWarpedChunk()
        {
            int outFrames = _chunkFrames;
            if (_positions.Length < outFrames)
                _positions = new double[outFrames];

            double last = _lastPos;
            for (int i = 0; i < outFrames; i++)
                last = _positions[i] = ProjectPositionAt(_outPos + i * _speed, last);

            long mixStart = _carryCount > 0 ? _carryStart + _carryCount : (long)Math.Floor(_positions[0]);
            long lastNeeded = (long)Math.Floor(_positions[outFrames - 1]) + 1;
            long needed = Math.Min(lastNeeded + 1 - mixStart, _srcEndFrame - mixStart);
            if (needed < 0)
                needed = 0;

            if (needed > 0)
            {
                EnsureSrcCapacity((int)needed);
                try
                {
                    _mixer.MixChunk(mixStart, (int)needed, _src);
                }
                catch (Exception ex)
                {
                    RecordError(ex);
                    Array.Clear(_src, 0, (int)needed * Channels);
                }
            }

            long avail = mixStart + needed;
            int produced = 0;
            for (int i = 0; i < outFrames; i++)
            {
                double p = _positions[i];
                long f0 = (long)Math.Floor(p);
                if (f0 >= avail)
                    break; // the project audio ran out inside this chunk

                long f1 = Math.Min(f0 + 1, avail - 1);
                float frac = (float)(p - f0);
                for (int ch = 0; ch < Channels; ch++)
                {
                    float a = SampleAt(f0, mixStart, ch);
                    float b = SampleAt(f1, mixStart, ch);
                    _chunk[i * Channels + ch] = a + (b - a) * frac;
                }

                produced++;
            }

            if (produced == 0)
            {
                _nextFrame = _endFrame; // the cursor sits past the last sample: end of audio
                _eofReached = true;
                Thread.Sleep(10);
                return;
            }

            if (_basePtsPending)
            {
                _sink.TrySetBasePts(new TimeSpan(AudioTime.TicksFloor((long)Math.Floor(_outPos), _rate)));
                _basePtsPending = false;
            }

            if (!WriteToRing(new ReadOnlySpan<float>(_chunk, 0, produced * Channels)))
                return; // superseded by a seek — cursors and carry stay where they were

            double nextOut = _outPos + produced * _speed;
            double nextP = ProjectPositionAt(nextOut, last);
            long nextCursor = Math.Min((long)Math.Floor(nextP), avail);
            int overlap = (int)(avail - nextCursor);
            if (overlap > 0 && overlap <= CarryFrames)
            {
                for (int i = 0; i < overlap; i++)
                {
                    for (int ch = 0; ch < Channels; ch++)
                        _carry[i * Channels + ch] = SampleAt(nextCursor + i, mixStart, ch);
                }

                _carryStart = nextCursor;
                _carryCount = overlap;
            }
            else
            {
                _carryCount = 0;
            }

            _outPos = nextOut;
            _lastPos = nextP;
            _nextFrame = (long)Math.Floor(nextOut);
        }

        /// <summary>The fractional project frame a fractional output frame samples at, clamped
        /// monotone (span joins quantize two ways and can regress a fraction of a frame).</summary>
        private double ProjectPositionAt(double outputFrames, double floor)
        {
            while (outputFrames >= _spans[_spanIndex].OutEnd)
                _spanIndex++;
            var span = _spans[_spanIndex];
            double p;
            if (span.Direct)
            {
                p = outputFrames + span.OffsetFrames;
            }
            else
            {
                long whole = (long)outputFrames;
                long tick = AudioTime.TicksFloor(whole, _rate)
                            + (long)((outputFrames - whole) * TimeBase.TicksPerSecond / _rate);
                p = _warp.ToProject(tick) * (double)_rate / TimeBase.TicksPerSecond;
            }

            return p < floor ? floor : p;
        }

        /// <summary>One timeline frame of the resampler's window: carried tail frames first, then
        /// the freshly mixed chunk starting at <paramref name="mixStart"/>.</summary>
        private float SampleAt(long frame, long mixStart, int channel)
        {
            if (frame < mixStart)
            {
                long index = frame - _carryStart;
                if (index < 0)
                    index = 0; // unreachable: the cursor never moves behind the carry
                return _carry[index * Channels + channel];
            }

            return _src[(frame - mixStart) * Channels + channel];
        }

        private void EnsureSrcCapacity(int frames)
        {
            int floats = frames * Channels;
            if (_src.Length < floats)
                _src = new float[floats];
        }

        private void ApplyProject(PendingUpdate update)
        {
            var project = NormalizeRate(update.Project);
            AudioMixer mixer;
            try
            {
                mixer = new AudioMixer(project, _readSource);
            }
            catch (Exception ex)
            {
                RecordError(ex); // malformed update: keep mixing the old project; a later one supersedes
                return;
            }

            _project = project;
            _mixer = mixer;
            if (update.Warp != null && !ReferenceEquals(update.Warp, _warp))
                AdoptWarp(update.Warp);
            UpdateEndFrames(project);
        }

        private void AdoptWarp(TimeWarp warp)
        {
            _warp = warp;
            _warped = warp is { IsIdentity: false };
            _spans = _warped ? BuildSpans(warp, _rate) : null;
            _spanIndex = 0;
        }

        private void UpdateEndFrames(Project project)
        {
            long endTicks = Math.Min(project.GetDurationTicks(), AudioMixer.GetAudioEndTicks(project));
            _srcEndFrame = AudioTime.SamplesCeil(endTicks, _rate);
            _endFrame = _warped ? AudioTime.SamplesCeil(_warp.ToOutput(endTicks), _rate) : _srcEndFrame;
        }

        /// <summary>One warp segment in the output sample-frame domain (the audio mirror of
        /// <see cref="Render.WarpAudioResampler"/>'s span table): direct (speed-1) spans read
        /// project frame <c>output + OffsetFrames</c>, others map through the warp inverse.</summary>
        private readonly struct WarpSpan
        {
            public WarpSpan(long outEnd, bool direct, long offsetFrames)
            {
                OutEnd = outEnd;
                Direct = direct;
                OffsetFrames = offsetFrames;
            }

            public long OutEnd { get; }      // exclusive; the trailing span is long.MaxValue
            public bool Direct { get; }
            public long OffsetFrames { get; }
        }

        private static WarpSpan[] BuildSpans(TimeWarp warp, int rate)
        {
            var spans = new List<WarpSpan>();
            long projectEndTicks = 0, outputEndTicks = 0;
            foreach (var seg in warp.Segments)
            {
                // segments tile in ticks, so ceiling both boundaries keeps the sample spans
                // contiguous (a segment narrower than one sample simply contributes none)
                long outStart = AudioTime.SamplesCeil(seg.OutputStartTicks, rate);
                long outEnd = AudioTime.SamplesCeil(seg.OutputEndTicks, rate);
                projectEndTicks = seg.ProjectEndTicks;
                outputEndTicks = seg.OutputEndTicks;
                if (outEnd <= outStart)
                    continue;

                bool direct = !seg.IsRamp && seg.Speed == 1.0;
                spans.Add(new WarpSpan(outEnd, direct, direct
                    ? AudioTime.SamplesNearest(seg.ProjectStartTicks - seg.OutputStartTicks, rate)
                    : 0));
            }

            // past the last segment the warp continues at speed 1 — a trailing direct span
            spans.Add(new WarpSpan(long.MaxValue, true,
                AudioTime.SamplesNearest(projectEndTicks - outputEndTicks, rate)));
            return spans.ToArray();
        }

        /// <summary>The mix rate is pinned for the pipeline's lifetime (ring/sink formats, and it
        /// is part of the player's stream signature) — a project carrying no usable rate of its
        /// own is re-wrapped shallowly so the mixer/source math runs at the pipeline rate.</summary>
        private Project NormalizeRate(Project project)
        {
            var output = project.Output;
            if (output != null && output.SampleRate == _rate)
                return project;

            return new Project
            {
                Version = project.Version,
                Output = new OutputSettings
                {
                    WidthPx = output?.WidthPx ?? 0,
                    HeightPx = output?.HeightPx ?? 0,
                    FpsNum = output?.FpsNum ?? 0,
                    FpsDen = output?.FpsDen ?? 1,
                    SampleRate = _rate,
                },
                Sources = project.Sources,
                Tracks = project.Tracks,
                Items = project.Items,
            };
        }

        private bool WriteToRing(ReadOnlySpan<float> samples)
        {
            int offset = 0;
            while (offset < samples.Length)
            {
                if (!_running || Interlocked.Read(ref _pendingSeekTicks) != long.MinValue)
                    return false; // stop/seek: abandon — the flush discards these samples anyway

                int written = _ring.Write(samples.Slice(offset));
                if (written == 0)
                {
                    Thread.Sleep(5); // ring at capacity; wait for the device to drain
                    continue;
                }

                offset += written;
            }

            return true;
        }

        private void RecordError(Exception ex)
        {
            Interlocked.CompareExchange(ref _error, ex, null);
        }

        /// <summary>Wraps the source so one stream's decode failure (missing/corrupt file) mixes
        /// as silence for that stream while the rest keep playing. A failed stream stays silenced
        /// for this pipeline's lifetime — relinking the file changes the stream signature and
        /// rebuilds the pipelines, which is the retry path. Mix-thread only.</summary>
        private sealed class FaultIsolatingSource : IAudioSource
        {
            private readonly AudioMixWorker _owner;
            private HashSet<(Guid, int)> _dead;

            public FaultIsolatingSource(AudioMixWorker owner)
            {
                _owner = owner;
            }

            public bool ReadSamples(Guid sourceId, int streamIndex, long sourcePosFrames, float[] dst,
                int frames, out int framesRead)
            {
                if (_dead == null || !_dead.Contains((sourceId, streamIndex)))
                {
                    try
                    {
                        return _owner._source.ReadSamples(sourceId, streamIndex, sourcePosFrames,
                            dst, frames, out framesRead);
                    }
                    catch (Exception ex)
                    {
                        _dead ??= new HashSet<(Guid, int)>();
                        _dead.Add((sourceId, streamIndex));
                        _owner.RecordError(ex);
                    }
                }

                Array.Clear(dst, 0, frames * Channels);
                framesRead = frames;
                return true;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _running = false;
            _thread?.Join(3000);
            _source.Dispose();
        }
    }
}
