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
    /// are bent onto the output grid (see <see cref="MixWarpedChunk"/>) — time-stretched through
    /// a <see cref="WsolaStretcher"/> where the span's speed item asks for pitch correction,
    /// resampled per-sample (pitch riding with the speed) where it does not — so a speed ramp
    /// glides continuously with no flush or timing rebase anywhere inside it.
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

        /// <summary>Project frames the warp path's <see cref="MixerWindow"/> keeps behind its
        /// cursor: span joins quantize positions two ways and can regress under a frame.</summary>
        private const int WarpGuardFrames = 4;

        private readonly AudioRingBuffer _ring;
        private readonly NAudioSink _sink;
        private readonly int _rate;
        private readonly int _chunkFrames;      // 20ms
        private readonly int _maxBufferedFloats;
        private readonly float[] _chunk;
        private readonly SeekableAudioSource _source;
        private readonly DenoisedAudioSource _denoised;
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
        private MixerWindow _window;
        private WsolaStretcher _stretcher;
        private int _stretchSpanIndex = -1;
        private Func<long, double> _stretchTarget;

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
            TimeWarp warp = null, string sidecarCacheDir = null)
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
            _denoised = new DenoisedAudioSource(_source, _project, sidecarCacheDir);
            _readSource = new FaultIsolatingSource(this);
            _mixer = new AudioMixer(_project, _readSource);
            _window = new MixerWindow(_mixer, WarpGuardFrames, RecordError);
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
                _denoised.Reset();
                _mixer = new AudioMixer(_project, _readSource);
                _window.Reset();
                _window.Mixer = _mixer;
                _stretcher?.Reset();
                _stretchSpanIndex = -1;
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
        /// playback speed; the chunk is walked span by span, and each run is produced by the
        /// span's own algorithm — <see cref="WsolaStretcher"/> time-stretching on a
        /// pitch-corrected span, per-sample linear interpolation of warp-mapped positions
        /// otherwise (which on a direct speed-1 span at playback speed 1 degenerates to exact
        /// integer positions and verbatim copies — unwarped stretches keep the preview/render
        /// sample parity). All project frames come through the shared <see cref="MixerWindow"/>,
        /// so the mixer is asked for each frame exactly once and a mixer snapshot swap (volume
        /// edit) never repositions a source; pitch-corrected spans synthesize the same hops the
        /// render does.
        /// </summary>
        private void MixWarpedChunk()
        {
            // clamp the chunk at the output end: past _endFrame there is nothing to produce
            int outFrames = _chunkFrames;
            double remaining = (_endFrame - _outPos) / _speed;
            if (remaining < outFrames)
                outFrames = (int)Math.Ceiling(remaining);
            if (outFrames <= 0)
            {
                _nextFrame = _endFrame;
                _eofReached = true;
                Thread.Sleep(10);
                return;
            }

            int produced = 0;
            while (produced < outFrames)
            {
                double pos = _outPos + produced * _speed;
                while (pos >= _spans[_spanIndex].OutEnd)
                    _spanIndex++;
                var span = _spans[_spanIndex];

                int run = outFrames - produced;
                if (span.OutEnd != long.MaxValue)
                {
                    double inSpan = (span.OutEnd - pos) / _speed;
                    if (inSpan < run)
                        run = Math.Max(1, (int)Math.Ceiling(inSpan));
                }

                if (span.Pitch)
                    StretchRun(span, pos, run, produced * Channels);
                else
                    InterpRun(pos, run, produced * Channels);
                produced += run;
            }

            if (_basePtsPending)
            {
                _sink.TrySetBasePts(new TimeSpan(AudioTime.TicksFloor((long)Math.Floor(_outPos), _rate)));
                _basePtsPending = false;
            }

            if (!WriteToRing(new ReadOnlySpan<float>(_chunk, 0, produced * Channels)))
                return; // superseded by a seek — the seek path resets window and stretcher anyway

            _outPos += produced * _speed;
            _nextFrame = (long)Math.Floor(_outPos);
        }

        /// <summary>A run of device frames through a direct or plainly-resampled span: warp-map
        /// each frame's output position to a fractional project frame and interpolate between its
        /// neighbors in the window. Integer positions (a direct span at playback speed 1) reduce
        /// to verbatim copies.</summary>
        private void InterpRun(double firstPos, int run, int dstOffset)
        {
            if (_positions.Length < run)
                _positions = new double[run];

            double last = _lastPos;
            for (int i = 0; i < run; i++)
                last = _positions[i] = ProjectPositionAt(firstPos + i * _speed, last);
            _lastPos = last;

            long firstNeeded = (long)Math.Floor(_positions[0]);
            long endNeeded = (long)Math.Floor(_positions[run - 1]) + 2; // right neighbor, exclusive
            _window.Ensure(firstNeeded, endNeeded);

            long windowLast = _window.StartFrame + _window.Frames - 1;
            var buffer = _window.Buffer;
            for (int i = 0; i < run; i++)
            {
                double p = _positions[i];
                long f0 = _window.Clamp((long)Math.Floor(p));
                long f1 = Math.Min(f0 + 1, windowLast);
                float frac = (float)(p - f0);
                if (frac < 0f)
                    frac = 0f;
                else if (frac > 1f)
                    frac = 1f;

                int b0 = (int)(f0 - _window.StartFrame) * Channels;
                int b1 = (int)(f1 - _window.StartFrame) * Channels;
                int di = dstOffset + i * Channels;
                for (int ch = 0; ch < Channels; ch++)
                {
                    float a = buffer[b0 + ch];
                    float b = buffer[b1 + ch];
                    _chunk[di + ch] = a + (b - a) * frac;
                }
            }
        }

        /// <summary>A run of device frames through a pitch-corrected span: the stretcher
        /// synthesizes the span's output on its own hop grid (anchored at the span start, so
        /// preview and render synthesize the same hops) and the device frames interpolate that
        /// buffer at their playback-speed-scaled positions.</summary>
        private void StretchRun(in WarpSpan span, double firstPos, int run, int dstOffset)
        {
            if (_stretchSpanIndex != _spanIndex)
            {
                _stretcher ??= new WsolaStretcher(_rate);
                _stretchTarget ??= o => _warp.ToProject(AudioTime.TicksFloor(o, _rate)) * (double)_rate
                    / TimeBase.TicksPerSecond;
                _stretcher.Reset();
                _stretchSpanIndex = _spanIndex;
            }

            double lastPos = firstPos + (run - 1) * _speed;
            long firstFrame = (long)Math.Floor(firstPos);
            long endFrame = (long)Math.Floor(lastPos) + 2; // right neighbor, exclusive
            _stretcher.EnsureOutput(span.OutStart, firstFrame, endFrame, _stretchTarget, _window);

            var buffer = _stretcher.OutputBuffer;
            long bufStart = _stretcher.OutputStart;
            long bufLast = bufStart + _stretcher.OutputFrames - 1;
            for (int i = 0; i < run; i++)
            {
                double p = firstPos + i * _speed;
                long f0 = (long)Math.Floor(p);
                if (f0 < bufStart)
                    f0 = bufStart;
                long f1 = Math.Min(f0 + 1, bufLast);
                float frac = (float)(p - f0);
                if (frac < 0f)
                    frac = 0f;
                else if (frac > 1f)
                    frac = 1f;

                int b0 = (int)(f0 - bufStart) * Channels;
                int b1 = (int)(f1 - bufStart) * Channels;
                int di = dstOffset + i * Channels;
                for (int ch = 0; ch < Channels; ch++)
                {
                    float a = buffer[b0 + ch];
                    float b = buffer[b1 + ch];
                    _chunk[di + ch] = a + (b - a) * frac;
                }
            }

            // the span's project cursor moved under the stretcher; keep the join clamp current
            _lastPos = Math.Max(_lastPos, _stretchTarget((long)Math.Floor(lastPos)));
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

        /// <summary>One timeline frame of the resampled (unwarped speed) path's buffers: carried
        /// tail frames first, then the freshly mixed chunk starting at <paramref name="mixStart"/>.</summary>
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
            _denoised.UpdateProject(project);
            _mixer = mixer;
            // the window keeps its already-mixed frames: a volume edit becomes audible on the
            // next mixed frame, never by re-mixing (and so repositioning) a source
            _window.Mixer = mixer;
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
            _stretcher?.Reset();
            _stretchSpanIndex = -1;
            _stretchTarget = null; // the lambda closes over the adopted warp; rebuild lazily
        }

        private void UpdateEndFrames(Project project)
        {
            long endTicks = Math.Min(project.GetDurationTicks(), AudioMixer.GetAudioEndTicks(project));
            _srcEndFrame = AudioTime.SamplesCeil(endTicks, _rate);
            _endFrame = _warped ? AudioTime.SamplesCeil(_warp.ToOutput(endTicks), _rate) : _srcEndFrame;
            _window.LimitFrame = _srcEndFrame;
        }

        /// <summary>One warp segment in the output sample-frame domain (the audio mirror of
        /// <see cref="Render.WarpAudioResampler"/>'s span table): direct (speed-1) spans read
        /// project frame <c>output + OffsetFrames</c>, others map through the warp inverse.</summary>
        private readonly struct WarpSpan
        {
            public WarpSpan(long outStart, long outEnd, bool direct, long offsetFrames, bool pitch)
            {
                OutStart = outStart;
                OutEnd = outEnd;
                Direct = direct;
                OffsetFrames = offsetFrames;
                Pitch = pitch;
            }

            public long OutStart { get; }    // the stretcher anchors its hop grid here
            public long OutEnd { get; }      // exclusive; the trailing span is long.MaxValue
            public bool Direct { get; }
            public long OffsetFrames { get; }
            public bool Pitch { get; }
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
                spans.Add(new WarpSpan(outStart, outEnd, direct, direct
                    ? AudioTime.SamplesNearest(seg.ProjectStartTicks - seg.OutputStartTicks, rate)
                    : 0, !direct && seg.PitchCorrect));
            }

            // past the last segment the warp continues at speed 1 — a trailing direct span
            spans.Add(new WarpSpan(AudioTime.SamplesCeil(outputEndTicks, rate), long.MaxValue, true,
                AudioTime.SamplesNearest(projectEndTicks - outputEndTicks, rate), false));
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
                        return _owner._denoised.ReadSamples(sourceId, streamIndex, sourcePosFrames,
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
            _denoised.Dispose();
            _source.Dispose();
        }
    }
}
