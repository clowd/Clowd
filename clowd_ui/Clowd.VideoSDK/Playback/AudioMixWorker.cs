using System;
using System.Collections.Generic;
using System.Threading;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Playback
{
    /// <summary>
    /// The preview's audio producer, replacing the single-stream <see cref="AudioDecodeWorker"/>:
    /// one thread mixing 20ms chunks of the project's audio items into the ring the platform sink
    /// drains. The mix itself is the <b>literal render mixer</b> — <see cref="AudioMixer.MixChunk"/>,
    /// per-sample gain <c>Item.Volume × TransitionMath entry/exit progress</c> — pulled over a
    /// <see cref="SeekableAudioSource"/>, so what preview plays is what render writes, by
    /// construction. The worker runs in <b>timeline time</b>: chunk positions are output sample
    /// frames of the timeline, and the sink's base pts after a flush is the timeline position
    /// itself — there is no source↔clock mapping on the audio path (a cut seam is just two
    /// adjacent items to the mixer, and timeline gaps mix to silence).
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

        private readonly AudioRingBuffer _ring;
        private readonly NAudioSink _sink;
        private readonly int _rate;
        private readonly int _chunkFrames;      // 20ms
        private readonly int _maxBufferedFloats;
        private readonly float[] _chunk;
        private readonly SeekableAudioSource _source;
        private readonly FaultIsolatingSource _readSource;

        // request slots (any thread); long.MinValue / null = empty. The initial pending seek to 0
        // makes the first chunk after Start establish flush + timing even without a PrepareSeek.
        private long _pendingSeekTicks;
        private Project _pendingProject;
        private Exception _error;
        private volatile bool _running;
        private volatile bool _eofReached;

        // mix-thread-owned state
        private Project _project;
        private AudioMixer _mixer;
        private long _nextFrame;
        private long _endFrame;
        private bool _basePtsPending = true;

        private Thread _thread;
        private bool _disposed;

        public AudioMixWorker(Project project, AudioRingBuffer ring, NAudioSink sink, int sampleRate)
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
            _endFrame = EndFrameOf(_project);
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

        /// <summary>Controller thread: restart production at the timeline position. The mix thread
        /// adopts it at the next chunk boundary — flushing the ring (it is the producer), resetting
        /// sink timing, and re-basing the sink's pts on the target directly (timeline time).</summary>
        public void PrepareSeek(TimeSpan target)
        {
            Interlocked.Exchange(ref _pendingSeekTicks, Math.Max(0, target.Ticks));
            // optimistic clear so the player's EOF/detach logic stops seeing a stale EOF the
            // moment a seek is requested; the mix thread re-derives the truth when it adopts.
            _eofReached = false;
        }

        /// <summary>Controller thread: swap in an edited project at the next chunk boundary
        /// (latest wins). Builds a fresh mixer snapshot over the SAME source — decoders are never
        /// touched, which is what keeps volume/transition edits cheap. The caller must hand over a
        /// snapshot it will not mutate (the mixer reads items live).</summary>
        public void UpdateProject(Project project)
        {
            ArgumentNullException.ThrowIfNull(project);
            Interlocked.Exchange(ref _pendingProject, project);
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
            var project = Interlocked.Exchange(ref _pendingProject, null);
            if (project != null)
                ApplyProject(project);

            long seek = Interlocked.Exchange(ref _pendingSeekTicks, long.MinValue);
            if (seek != long.MinValue)
            {
                _ring.Clear(); // mix thread IS the producer — legal here and nowhere else
                _sink.ResetTiming();
                _source.Reset(); // the timeline moved: every stream repositions on its next read
                _mixer = new AudioMixer(_project, _readSource);
                _nextFrame = AudioTime.SamplesFloor(seek, _rate);
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
                _nextFrame += frames;
            // an abandoned write means a newer seek superseded this chunk — the next iteration
            // flushes and re-mixes from the new position, so the partial samples never play.
        }

        private void ApplyProject(Project project)
        {
            project = NormalizeRate(project);
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
            _endFrame = EndFrameOf(project);
        }

        private long EndFrameOf(Project project)
        {
            long endTicks = Math.Min(project.GetDurationTicks(), AudioMixer.GetAudioEndTicks(project));
            return AudioTime.SamplesCeil(endTicks, _rate);
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
