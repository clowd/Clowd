using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Playback
{
    /// <summary>
    /// Plays a <see cref="Project"/> for preview: the generalization of
    /// <see cref="FFmpegVideoPlayer"/> from "one file, skip ranges" to "a timeline of items over N
    /// media streams". One demux/decode pipeline runs per referenced <b>video</b>
    /// (sourceId, streamIndex) — reusing <see cref="Demuxer"/>/<see cref="VideoDecodeWorker"/>
    /// unchanged — against ONE shared <see cref="PlaybackClock"/> that runs in <b>output-timeline
    /// time</b>. Workers pace their source-stamped frames through the project's timeline↔source
    /// mapping (<see cref="ProjectTimelineMap"/>); presented frames land in the
    /// <see cref="PlaybackFrameSource"/> the preview's draw operation composes from
    /// (<see cref="TryGetFrameSource"/>). Audio never rides the demuxers: every unmuted audio
    /// stream is mixed in timeline time by one <see cref="AudioMixWorker"/> running the literal
    /// render mixer (<see cref="Clowd.VideoSDK.Audio.AudioMixer"/>) over its own per-stream
    /// decoders — gaps are silence, per-item volume and transition ramps are per-sample, and
    /// preview therefore sounds like the render by construction. The player's own
    /// <see cref="Volume"/> is a pure master gain on the sink.
    ///
    /// Cuts: back-to-back items whose source in-points jump are played by decoding continuously
    /// until the seam, then hopping the pipeline with an internal exact seek — the same mechanism
    /// (and acceptable ~100ms hiccup) as <see cref="FFmpegVideoPlayer"/>'s skip ranges, with the
    /// ranges now derived from the project instead of set by the UI. The frame source additionally
    /// filters any cut-source frames presented inside the detection window, so cut material never
    /// reaches the canvas.
    ///
    /// <see cref="UpdateProject"/> applies live edits: as long as the set of referenced streams is
    /// unchanged (every trim/cut/transform/volume edit), the new mapping is swapped in atomically
    /// and no decoder is reopened; only adding/removing/relinking streams rebuilds pipelines,
    /// which happens on a background task (latest update wins) — never on the caller's thread.
    ///
    /// Remaining pacing caveat (video streams only): streams are paced correctly when their items
    /// share the primary stream's timeline placement (true for our recordings, where screen and
    /// webcam rows are cut as one link group). The timeline-domain audio mix has no such limit.
    /// </summary>
    public sealed class CompositionPlayer : IDisposable
    {
        /// <summary>How far the audio sink's played time may sit from the playhead and still be
        /// trusted as the master clock when re-attaching after an EOF detach (covers the sink's
        /// device latency plus a tick of stopwatch drift). Timing further out means the sink's
        /// base pts describes where production parked, not the playhead — attaching it would
        /// snap the clock backwards.</summary>
        private static readonly TimeSpan AudioReattachTolerance = TimeSpan.FromMilliseconds(250);

        private readonly Action<Action> _dispatch;
        private readonly object _stateSync = new object();
        private readonly object _lifecycleSync = new object(); // serializes seeks vs update/reopen/dispose
        private readonly PlaybackFrameSource _frameSource = new PlaybackFrameSource();

        private volatile PipelineSet _pipelines;
        private volatile ProjectTimelineMap _map;
        private volatile Project _project;
        private PlaybackClock _clock;
        private VideoOpenOptions _options;
        private Timer _tickTimer;

        private volatile PlayerState _state = PlayerState.Idle;
        private double _volume = 1.0;
        private bool _audioDetached;
        private int _skipSeekBusy;
        /// <summary>Source-timeline offset of the primary-stream segment the pipelines were last
        /// synced to; a differing offset at the playing position means a cut seam was crossed.</summary>
        private long _activeOffsetTicks = long.MinValue;
        private int _decoderOpens;
        private bool _disposed;
        private Task _openTask;

        // seek coalescing — same discipline as FFmpegVideoPlayer
        private readonly object _seekSync = new object();
        private TimeSpan _pendingSeekPos;
        private SeekMode _pendingSeekMode;
        private bool _pendingSeekFlushAudio;
        private bool _hasPendingSeek;
        private bool _seekLoopActive;
        private Task _seekTask = Task.CompletedTask;

        // update coalescing — a changed stream set rebuilds pipelines on a background task
        // (mirroring how OpenAsync offloads OpenCore); overlapping updates coalesce, latest
        // project wins. Lock order: _updateSync outer, _lifecycleSync inner.
        private readonly object _updateSync = new object();
        private Project _pendingUpdate;
        private bool _updateLoopActive;
        private Task _updateTask = Task.CompletedTask;

        /// <param name="eventDispatcher">Marshals <see cref="PositionChanged"/> /
        /// <see cref="StateChanged"/> onto the UI thread. Null = raise on the calling thread.</param>
        public CompositionPlayer(Action<Action> eventDispatcher = null)
        {
            _dispatch = eventDispatcher ?? (a => a());
        }

        // ------------------------------------------------------------------------ pipeline model

        private sealed class VideoPipe
        {
            public (Guid SourceId, int StreamIndex) Key;
            public VideoDecodeWorker Worker;
            public PooledFrameSink Sink;
        }

        private sealed class FilePipe
        {
            public Demuxer Demuxer;
            public readonly List<PacketQueue> Queues = new List<PacketQueue>();
            public readonly List<VideoPipe> Video = new List<VideoPipe>();
        }

        private sealed class PipelineSet
        {
            public FilePipe[] Files = Array.Empty<FilePipe>();
            public VideoPipe[] AllVideo = Array.Empty<VideoPipe>();
            public VideoPipe Primary;
            public AudioMixWorker MixWorker;
            public NAudioSink AudioSink;
            public AudioRingBuffer AudioRing;
            public int MixRate;
            public string Signature;
        }

        // -------------------------------------------------------------------------------- state

        public PlayerState State => _state;

        /// <summary>The project currently playing (the instance last passed to Open/Update).</summary>
        public Project Project => _project;

        /// <summary>Timeline length — the duration transport/seeking operates in.</summary>
        public TimeSpan Duration => new TimeSpan(_map?.DurationTicks ?? 0);

        /// <summary>The frame source the preview's draw operation composes from. Stable for the
        /// player's lifetime (pipeline rebuilds re-register their streams into it).</summary>
        public PlaybackFrameSource FrameSource => _frameSource;

        /// <summary>Diagnostic/test hook: number of decoder pipelines constructed over the
        /// player's lifetime. Live edits that only change the mapping never increment it.</summary>
        public int DecoderOpenCount => Volatile.Read(ref _decoderOpens);

        /// <summary>Test/diagnostic: container seeks performed by the audio mix's source for the
        /// current pipeline set. A video cut-seam hop must never move it — the mix decodes
        /// straight through seams, which is what keeps preview audio sample-exact with render.</summary>
        internal int AudioRepositionCount => _pipelines?.MixWorker?.SourceRepositionCount ?? 0;

        public event EventHandler PositionChanged;
        public event EventHandler<PlayerState> StateChanged;

        /// <summary>The exception behind the most recent transition to
        /// <see cref="PlayerState.Failed"/> (a failed open or live rebuild); null otherwise.
        /// Read it from a <see cref="StateChanged"/> handler — a failed background rebuild
        /// surfaces exclusively through the Failed state, never as a thrown exception.</summary>
        public Exception LastError { get; private set; }

        public TimeSpan Position
        {
            get
            {
                var clock = _clock;
                var map = _map;
                if (clock == null || map == null)
                    return TimeSpan.Zero;
                long dur = map.DurationTicks;
                if (_state == PlayerState.Ended)
                    return new TimeSpan(dur);

                var pos = clock.Position;
                if (pos < TimeSpan.Zero)
                    return TimeSpan.Zero;
                if (dur > 0 && pos.Ticks > dur)
                    return new TimeSpan(dur);
                return pos;
            }
        }

        /// <summary>Master gain, applied at the sink. Per-item volume and transition ramps are
        /// baked into the mixed samples by <see cref="AudioMixWorker"/> — exactly as in render.</summary>
        public double Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0.0, 1.0);
                var sink = _pipelines?.AudioSink;
                if (sink != null)
                    sink.Volume = _volume;
            }
        }

        // --------------------------------------------------------------------------------- open

        /// <summary>Opens the project's media and presents the first frame (paused). One project
        /// per player instance; use <see cref="UpdateProject"/> for edits.</summary>
        public Task OpenAsync(Project project, VideoOpenOptions options = null)
        {
            ArgumentNullException.ThrowIfNull(project);
            if (_disposed)
                throw new ObjectDisposedException(nameof(CompositionPlayer));
            if (_state != PlayerState.Idle)
                throw new InvalidOperationException("Player already has a project open; create a new player, or use UpdateProject for edits.");

            SetState(PlayerState.Opening);
            var task = Task.Run(() =>
            {
                try
                {
                    OpenCore(project, options ?? new VideoOpenOptions());
                    SetState(PlayerState.Paused);
                }
                catch (Exception ex)
                {
                    LastError = ex;
                    SetState(PlayerState.Failed);
                    throw;
                }
            });

            // kept so Dispose can defer teardown behind OpenCore (same race as FFmpegVideoPlayer:
            // closing the editor while it is still loading).
            _openTask = task;
            return task;
        }

        private void OpenCore(Project project, VideoOpenOptions options)
        {
            _options = options;
            _clock = new PlaybackClock();

            var map = ProjectTimelineMap.Build(project);
            _project = project;
            _map = map;
            _frameSource.SetCutSchedules(BuildCutSchedules(map));

            var set = BuildPipelines(project, map);
            _pipelines = set;

            PrimeAndStart(set, map, positionTicks: 0);

            // A long leading trim would decode-discard from the file head; land it with a real
            // container seek instead (coalesced through the normal seek path).
            if (map.PrimaryVideo is { } pk && map.TryGetVideo(pk, out var pm)
                && pm.TimelineToSource(0) > 2 * TimeSpan.TicksPerSecond)
            {
                _ = SeekAsync(TimeSpan.Zero, SeekMode.Exact);
            }

            _tickTimer = new Timer(OnTick, null, 100, 100);
        }

        /// <summary>Serial-0 Prepare/OnSeeked pairing + thread start, mirroring
        /// <c>FFmpegVideoPlayer.OpenCore</c>: each stream immediately presents its frame at the
        /// given timeline position (Exact, so leading source material before an item's in-point
        /// is discarded, not shown).</summary>
        private void PrimeAndStart(PipelineSet set, ProjectTimelineMap map, long positionTicks)
        {
            foreach (var pipe in set.AllVideo)
            {
                pipe.Worker.PrepareSeek(new TimeSpan(VideoSourceTarget(map, pipe.Key, positionTicks)), SeekMode.Exact);
                pipe.Worker.OnSeeked(0);
            }

            // the mix worker runs in timeline time — the position is its seek target directly.
            set.MixWorker?.PrepareSeek(new TimeSpan(positionTicks));

            Volatile.Write(ref _activeOffsetTicks, PrimaryOffsetAt(map, positionTicks));

            foreach (var file in set.Files)
            {
                file.Demuxer.Start();
                foreach (var pipe in file.Video)
                    pipe.Worker.Start();
            }

            set.MixWorker?.Start();
        }

        private PipelineSet BuildPipelines(Project project, ProjectTimelineMap map)
        {
            var audioStreams = CollectAudioStreams(project);
            int mixRate = MixRate(project);
            var set = new PipelineSet
            {
                Signature = StreamSignature(project, map, audioStreams, mixRate),
                MixRate = mixRate,
            };
            if (map.VideoStreams.Count == 0 && audioStreams.Count == 0)
                return set; // media-less project (solids/text): transport still works off the clock

            FFmpegLoader.EnsureInitialized();

            // group the needed video streams by source file (audio never rides the demuxers —
            // the mix worker's per-stream decoders own their files)
            var bySource = new Dictionary<Guid, List<(Guid SourceId, int StreamIndex)>>();
            foreach (var key in map.VideoStreams.Keys)
            {
                if (!bySource.TryGetValue(key.SourceId, out var list))
                    bySource[key.SourceId] = list = new List<(Guid, int)>();
                list.Add(key);
            }

            var files = new List<FilePipe>();
            var allVideo = new List<VideoPipe>();

            try
            {
                foreach (var (sourceId, videoKeys) in bySource)
                {
                    var source = FindSource(project, sourceId)
                        ?? throw new InvalidOperationException($"Project has no source {sourceId}.");

                    // registered in files BEFORE any native open, so a failure part-way through
                    // the build leaves everything reachable for the catch block's cleanup.
                    var file = new FilePipe { Demuxer = new Demuxer() };
                    files.Add(file);
                    file.Demuxer.Open(source.Path);

                    // primary first so it lands in stats slot 0 (parity with screen-track-first today)
                    videoKeys.Sort((a, b) =>
                    {
                        int pa = map.PrimaryVideo == a ? 0 : 1;
                        int pb = map.PrimaryVideo == b ? 0 : 1;
                        return pa != pb ? pa.CompareTo(pb) : a.StreamIndex.CompareTo(b.StreamIndex);
                    });

                    foreach (var key in videoKeys)
                    {
                        var queue = new PacketQueue(32);
                        file.Queues.Add(queue);
                        file.Demuxer.AttachQueue(key.StreamIndex, queue);

                        var sink = new PooledFrameSink();
                        var keyCopy = key;
                        var worker = new VideoDecodeWorker(
                            file.Demuxer, key.StreamIndex, queue, _options, _clock,
                            () => sink,
                            () => _state == PlayerState.Playing,
                            OnImmediatePresented,
                            srcPts => MapVideoPtsToClock(keyCopy, srcPts));
                        Interlocked.Increment(ref _decoderOpens);

                        var pipe = new VideoPipe { Key = key, Worker = worker, Sink = sink };
                        file.Video.Add(pipe);
                        allVideo.Add(pipe);
                        _frameSource.RegisterStream(key, sink);
                        if (map.PrimaryVideo == key)
                            set.Primary = pipe;
                    }
                }

                if (audioStreams.Count > 0)
                {
                    set.AudioRing = new AudioRingBuffer(mixRate); // ~500ms float stereo
                    set.AudioSink = new NAudioSink(mixRate, 2, set.AudioRing, _options.CreateAudioOutput?.Invoke());
                    set.AudioSink.Volume = _volume;
                    set.MixWorker = new AudioMixWorker(project, set.AudioRing, set.AudioSink, mixRate);
                    Interlocked.Increment(ref _decoderOpens); // the audio pipeline, as one
                    _clock.SetAudioSource(set.AudioSink);
                }
            }
            catch
            {
                // Construction failed part-way (missing/corrupt file, decoder init failure).
                // Everything built so far lives only in these locals — _pipelines is never
                // assigned, so DisposeCore could never reach it: open AVFormatContexts (each
                // holding an OS file handle on the source mp4), decoder contexts and sinks
                // already registered with the frame source would all leak for the process
                // lifetime. FFmpegVideoPlayer kept partial state reachable in fields; here the
                // partial build must be torn down eagerly instead, before the throw escapes.
                set.Files = files.ToArray();
                set.AllVideo = allVideo.ToArray();
                _frameSource.Clear();
                _clock?.SetAudioSource(null);
                DisposePipelineSet(set);
                throw;
            }

            set.Files = files.ToArray();
            set.AllVideo = allVideo.ToArray();
            set.Primary ??= allVideo.Count > 0 ? allVideo[0] : null;
            return set;
        }

        // ------------------------------------------------------------------------- live editing

        /// <summary>
        /// Applies an edited project. When the set of referenced media streams is unchanged (all
        /// trim/cut/move/transform/volume edits), the timeline mapping and cut schedules are
        /// swapped atomically on the calling thread — decoders keep running and, while playing,
        /// the seam logic re-syncs the pipelines to the new mapping on the next tick; the
        /// returned task is already completed. Only a changed stream set (streams added/removed,
        /// files relinked) rebuilds the pipelines, preserving position and state — and that
        /// rebuild runs on a background task, because it tears decoder threads down and re-runs
        /// avformat/avcodec opens (hundreds of ms of blocking work that must never execute on
        /// the caller's UI thread). The returned task completes once the rebuild has been
        /// applied; overlapping updates coalesce (latest project wins). A failed rebuild lands
        /// the player in <see cref="PlayerState.Failed"/> (see <see cref="LastError"/>) instead
        /// of faulting the task.
        /// </summary>
        public Task UpdateProject(Project project)
        {
            ArgumentNullException.ThrowIfNull(project);

            lock (_updateSync)
            {
                // The synchronous fast path is only safe while no rebuild is queued or in
                // flight — once one is, every update must funnel through the queue so the
                // latest project always wins (a cheap edit racing ahead of a queued rebuild
                // would otherwise be clobbered by the stale rebuild finishing after it).
                if (!_updateLoopActive)
                {
                    lock (_lifecycleSync)
                    {
                        if (_disposed)
                            return Task.CompletedTask;
                        if (_state is PlayerState.Idle or PlayerState.Opening or PlayerState.Failed)
                            throw new InvalidOperationException("UpdateProject requires an open player (await OpenAsync first).");

                        var map = ProjectTimelineMap.Build(project);
                        var set = _pipelines;
                        if (set != null && StreamSignature(project, map, CollectAudioStreams(project), MixRate(project)) == set.Signature)
                        {
                            ApplyMappingSwap(project, map, set);
                            return Task.CompletedTask;
                        }
                    }
                }

                _pendingUpdate = project;
                if (!_updateLoopActive)
                {
                    _updateLoopActive = true;
                    _updateTask = Task.Run(UpdateLoop);
                }

                return _updateTask;
            }
        }

        /// <summary>The stream-set-unchanged edit path: swap mapping + cut schedules atomically,
        /// no decoder touched. The mix worker gets the edited project as a mixer-snapshot swap at
        /// its next chunk boundary — volume/transition edits ride this without a decoder in
        /// sight. Caller holds <see cref="_lifecycleSync"/>.</summary>
        private void ApplyMappingSwap(Project project, ProjectTimelineMap map, PipelineSet set)
        {
            _project = project;
            _map = map;
            _frameSource.SetCutSchedules(BuildCutSchedules(map));
            if (set.MixWorker != null)
            {
                bool wasEof = set.MixWorker.EofReached;
                set.MixWorker.UpdateProject(project);
                // an edit that extends the audio revives a worker idling at the old audio end;
                // without a re-base it would resume mixing THERE (its next-frame cursor and the
                // sink's timing base froze at the old end), and OnTick's re-attach would yank
                // the master clock backwards. Flush production onto the live position instead —
                // the mix thread adopts the project before the seek in the same iteration, so
                // the grown end is seen first and the seek then re-bases cursor + sink timing.
                if (wasEof)
                    set.MixWorker.PrepareSeek(Position);
            }
            // _activeOffsetTicks intentionally kept: if the current position's segment
            // offset changed under the new map (e.g. a cut moved past the playhead), the
            // next tick's seam check sees the mismatch and hops the pipelines.
        }

        /// <summary>Drains queued project updates on a background task, same discipline as
        /// <see cref="SeekLoop"/>: coalesced latest-wins, serialized under
        /// <see cref="_lifecycleSync"/>. Re-checks the stream signature per iteration — a
        /// queued rebuild superseded by an edit that restored the current stream set collapses
        /// back into the cheap mapping swap.</summary>
        private void UpdateLoop()
        {
            while (true)
            {
                Project project;
                lock (_updateSync)
                {
                    project = _pendingUpdate;
                    _pendingUpdate = null;
                    if (project == null || _disposed)
                    {
                        _updateLoopActive = false;
                        return;
                    }
                }

                try
                {
                    lock (_lifecycleSync)
                    {
                        if (_disposed)
                            continue; // next iteration clears the loop flag and exits

                        var map = ProjectTimelineMap.Build(project);
                        var set = _pipelines;
                        if (set != null && StreamSignature(project, map, CollectAudioStreams(project), MixRate(project)) == set.Signature)
                            ApplyMappingSwap(project, map, set);
                        else
                            ReopenCore(project, map); // contains its own failures (Failed state)
                    }
                }
                catch
                {
                    // a malformed project failed mapping; drop this update — a later one can
                    // still supersede it, and faulting the shared task would tear the loop down.
                }
            }
        }

        /// <summary>Full pipeline rebuild for a changed stream set, preserving position/state.
        /// Runs on the update loop's background task, under <see cref="_lifecycleSync"/>.
        /// Never throws: the old set is already gone when the rebuild starts, so a failure
        /// (source file deleted/moved, decoder init error) disposes anything partially built
        /// (see <see cref="BuildPipelines"/>), records <see cref="LastError"/> and lands the
        /// player in <see cref="PlayerState.Failed"/> — surfaced through
        /// <see cref="StateChanged"/>, never as an exception escaping into a caller's
        /// PropertyChanged handler. A later update retries the rebuild (self-heal).</summary>
        private void ReopenCore(Project project, ProjectTimelineMap map)
        {
            var pos = Position;
            bool wasPlaying = _state == PlayerState.Playing;
            if (wasPlaying)
                Pause();
            if (_state == PlayerState.Ended)
                SetState(PlayerState.Paused);

            var old = _pipelines;
            _pipelines = null;
            _frameSource.Clear();
            _clock.SetAudioSource(null);
            _audioDetached = false;
            if (old != null)
                DisposePipelineSet(old);

            _project = project;
            _map = map;
            _frameSource.SetCutSchedules(BuildCutSchedules(map));

            if (pos.Ticks > map.DurationTicks)
                pos = new TimeSpan(map.DurationTicks);

            PipelineSet set;
            try
            {
                set = BuildPipelines(project, map); // disposes its own partial build on failure
            }
            catch (Exception ex)
            {
                LastError = ex;
                SetState(PlayerState.Failed);
                return;
            }

            _pipelines = set;
            PrimeAndStart(set, map, pos.Ticks);
            _clock.SetPosition(pos);
            if (_state == PlayerState.Failed)
                SetState(PlayerState.Paused); // a retried rebuild succeeded — transport is back

            // land the position with a real container seek (PrimeAndStart decodes from the file
            // head otherwise), then resume if we interrupted playback.
            var seek = SeekAsync(pos, SeekMode.Exact);
            if (wasPlaying)
                seek.ContinueWith(_ => Play(), TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        // ---------------------------------------------------------------------------- transport

        public void Play()
        {
            if (_state == PlayerState.Ended)
            {
                var t = SeekAsync(TimeSpan.Zero, SeekMode.Exact);
                t.ContinueWith(_ => PlayCore(), TaskContinuationOptions.OnlyOnRanToCompletion);
                return;
            }

            PlayCore();
        }

        private void PlayCore()
        {
            if (_state != PlayerState.Paused && _state != PlayerState.Ended)
                return;

            SetState(PlayerState.Playing);
            _pipelines?.AudioSink?.Play();
            _clock.Start();
            WakeAllPresenters();
        }

        public void Pause()
        {
            if (_state != PlayerState.Playing)
                return;

            _clock.Stop();
            _pipelines?.AudioSink?.Pause();
            SetState(PlayerState.Paused);
            WakeAllPresenters();
        }

        /// <summary>Seek in timeline time; concurrent calls coalesce (last position wins).</summary>
        public Task SeekAsync(TimeSpan position, SeekMode mode) => SeekAsync(position, mode, flushAudio: true);

        /// <summary>Internal seeks can skip the audio flush: the mix runs in timeline time, so a
        /// video cut-seam hop (which does not move the timeline position) is a semantic no-op for
        /// audio — flushing there would discard the ring's correct buffered lead (an audible
        /// dropout at every cut) and force the mix's streams off the decode-discard path that
        /// keeps preview seams sample-exact with render. Coalescing stays last-wins: a no-flush
        /// hop targets the playhead itself, where the mix already is.</summary>
        private Task SeekAsync(TimeSpan position, SeekMode mode, bool flushAudio)
        {
            if (_map == null)
                throw new InvalidOperationException("No project open.");

            if (position < TimeSpan.Zero)
                position = TimeSpan.Zero;
            var dur = Duration;
            if (dur > TimeSpan.Zero && position > dur)
                position = dur;

            lock (_seekSync)
            {
                _pendingSeekPos = position;
                _pendingSeekMode = mode;
                _pendingSeekFlushAudio = flushAudio;
                _hasPendingSeek = true;

                if (!_seekLoopActive)
                {
                    _seekLoopActive = true;
                    _seekTask = Task.Run(SeekLoop);
                }

                return _seekTask;
            }
        }

        private void SeekLoop()
        {
            while (true)
            {
                TimeSpan pos;
                SeekMode mode;
                bool flushAudio;
                lock (_seekSync)
                {
                    if (!_hasPendingSeek || _disposed)
                    {
                        _seekLoopActive = false;
                        return;
                    }

                    pos = _pendingSeekPos;
                    mode = _pendingSeekMode;
                    flushAudio = _pendingSeekFlushAudio;
                    _hasPendingSeek = false;
                }

                try
                {
                    lock (_lifecycleSync)
                    {
                        if (!_disposed)
                            DoSeek(pos, mode, flushAudio);
                    }
                }
                catch
                {
                    // a failed seek leaves the pipeline at its old position; not fatal.
                }
            }
        }

        private void DoSeek(TimeSpan target, SeekMode mode, bool flushAudio)
        {
            if (_state == PlayerState.Ended)
                SetState(PlayerState.Paused);

            var set = _pipelines;
            var map = _map;
            long tl = target.Ticks;

            // the mix worker seeks in timeline time — no source-domain mapping exists for audio.
            if (flushAudio)
                set?.MixWorker?.PrepareSeek(target);

            if (set != null && set.Files.Length > 0)
            {
                // 1. record per-worker post-flush behaviour (source-domain targets).
                foreach (var pipe in set.AllVideo)
                    pipe.Worker.PrepareSeek(new TimeSpan(VideoSourceTarget(map, pipe.Key, tl)), mode);

                // 2/3. flush + container seek per file, then unblock that file's pipelines.
                foreach (var file in set.Files)
                {
                    int serial = file.Demuxer.SeekAndFlush(new TimeSpan(FileSeekTarget(map, file, tl)));
                    foreach (var pipe in file.Video)
                        pipe.Worker.OnSeeked(serial);
                }
            }

            // re-attach the audio master if it was detached at end-of-audio (also for
            // audio-only projects, which have no file pipes). A no-flush seam hop leaves the
            // audio state entirely alone — the mix was not re-based, so its timing still
            // describes wherever it last was, not this target.
            if (flushAudio && _audioDetached && set?.AudioSink != null)
            {
                _audioDetached = false;
                _clock.SetAudioSource(set.AudioSink);
            }

            Volatile.Write(ref _activeOffsetTicks, PrimaryOffsetAt(map, tl));
            _clock.SetPosition(target);
            RaisePositionChanged();
        }

        public async Task StepFrameAsync(int direction)
        {
            var set = _pipelines;
            if (_map == null || set?.Primary == null)
                return;

            if (_state == PlayerState.Playing)
                Pause();

            var primary = set.Primary;

            if (direction < 0)
            {
                var target = Position - primary.Worker.FrameDuration;
                if (target < TimeSpan.Zero)
                    target = TimeSpan.Zero;
                await SeekAsync(target, SeekMode.Exact);
                return;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            primary.Worker.RequestStep(tcs, pts =>
            {
                _clock.SetPosition(new TimeSpan(MapVideoPtsToClock(primary.Key, pts.Ticks)));
                RaisePositionChanged();
            });

            foreach (var pipe in set.AllVideo)
            {
                if (pipe != primary)
                    pipe.Worker.RequestStep(null, null);
            }

            try
            {
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
            }

            // stepping across a cut seam leaves the pipeline about to decode cut-out source; the
            // stepped frame's mapped position already sits in the next segment, so re-sync there.
            long off = PrimaryOffsetAt(_map, Position.Ticks);
            if (off != long.MinValue && off != Volatile.Read(ref _activeOffsetTicks))
                await SeekAsync(Position, SeekMode.Exact);
        }

        // ----------------------------------------------------------------------------- composing

        /// <summary>
        /// Snapshot for the preview's draw operation: the frame source to
        /// <see cref="PlaybackFrameSource.Pump"/>+compose from, and the timeline instant to
        /// compose at. Returns false before a project is open.
        /// </summary>
        public bool TryGetFrameSource(out PlaybackFrameSource source, out long timelineTicks)
        {
            source = _frameSource;
            timelineTicks = Position.Ticks;
            return _state is PlayerState.Paused or PlayerState.Playing or PlayerState.Ended;
        }

        // -------------------------------------------------------------------------------- ticks

        private void OnImmediatePresented(VideoDecodeWorker worker, TimeSpan pts)
        {
            // the primary track's immediately-presented post-seek frame defines the position a
            // paused UI shows, mapped into timeline time.
            var set = _pipelines;
            if (set?.Primary != null && worker == set.Primary.Worker && _state != PlayerState.Playing)
            {
                _clock.SetPosition(new TimeSpan(MapVideoPtsToClock(set.Primary.Key, pts.Ticks)));
                RaisePositionChanged();
            }
        }

        private void OnTick(object state)
        {
            if (_disposed || _state != PlayerState.Playing)
                return;

            try
            {
                var set = _pipelines;
                var map = _map;
                var pos = Position;
                long tl = pos.Ticks;

                // cut seam crossed: the source-timeline offset at the playhead no longer matches
                // what the pipelines are decoding — hop them with an internal exact seek. This is
                // FFmpegVideoPlayer's skip-range hop with the discontinuities read straight from
                // the project.
                long off = PrimaryOffsetAt(map, tl);
                if (set != null && set.AllVideo.Length > 0
                    && off != long.MinValue && off != Volatile.Read(ref _activeOffsetTicks)
                    && Interlocked.CompareExchange(ref _skipSeekBusy, 1, 0) == 0)
                {
                    // flushAudio: false — the timeline position does not change across a video
                    // seam, so the timeline-domain mix is already producing the right samples;
                    // flushing it would dump correct buffered lead and reposition its decoders.
                    SeekAsync(pos, SeekMode.Exact, flushAudio: false)
                        .ContinueWith(_ => Interlocked.Exchange(ref _skipSeekBusy, 0));
                }

                // audio finished before video: hand the clock to the stopwatch so video plays
                // out. The reverse transition brings the audio master back when the worker
                // produces again (a seek's optimistic EofReached clear lost the race to the mix
                // thread, or an edit extended the audio). Both directions are serialized against
                // DoSeek/ReopenCore — TryEnter, never lock: the transition self-retries next
                // tick, and the timer thread must not block behind a hundreds-of-ms rebuild.
                if (set?.MixWorker != null && Monitor.TryEnter(_lifecycleSync))
                {
                    try
                    {
                        // re-check under the lock: a rebuild may have swapped the set out
                        if (!_disposed && _pipelines == set)
                        {
                            bool audioEof = set.MixWorker.EofReached && set.AudioRing.Available == 0;
                            if (!_audioDetached && audioEof)
                            {
                                _audioDetached = true;
                                _clock.SetAudioSource(null);
                            }
                            else if (_audioDetached && !set.MixWorker.EofReached)
                            {
                                // only trust the sink as master when its timing describes the
                                // playhead: after an EOF idle the base pts is still frozen where
                                // production parked, and attaching that snaps the clock backwards.
                                var sink = set.AudioSink;
                                if (sink.HasTiming && (pos - sink.PlayedTime).Duration() <= AudioReattachTolerance)
                                {
                                    _audioDetached = false;
                                    _clock.SetAudioSource(sink);
                                }
                                else
                                {
                                    // flush + re-base production on the playhead; re-attach on a
                                    // later tick once the re-based timing lands.
                                    set.MixWorker.PrepareSeek(pos);
                                }
                            }
                        }
                    }
                    finally
                    {
                        Monitor.Exit(_lifecycleSync);
                    }
                }

                // end of timeline (trailing trim never reaches file EOF), or end of all media.
                long dur = map?.DurationTicks ?? 0;
                bool ended = dur > 0 && tl >= dur;
                if (!ended && set != null && set.Files.Length > 0)
                {
                    bool eof = true;
                    foreach (var file in set.Files)
                        eof &= file.Demuxer.IsEof;
                    bool videoDone = set.AllVideo.Length > 0;
                    foreach (var pipe in set.AllVideo)
                        videoDone &= pipe.Worker.EofPresented;
                    bool audioDone = set.MixWorker == null
                                     || (set.MixWorker.EofReached && set.AudioRing.Available == 0);
                    ended = eof && videoDone && audioDone;
                }

                if (ended)
                {
                    _clock.Stop();
                    set?.AudioSink?.Pause();
                    SetState(PlayerState.Ended);
                }

                RaisePositionChanged();
            }
            catch
            {
                // never let a tick take down the timer thread.
            }
        }

        public PlaybackStatistics GetStatistics()
        {
            var set = _pipelines;
            var workers = set?.AllVideo ?? Array.Empty<VideoPipe>();
            var tracks = new TrackStatistics[workers.Length];
            for (int i = 0; i < workers.Length; i++)
                tracks[i] = workers[i].Worker.GetIntervalStatistics();

            return new PlaybackStatistics
            {
                Video = tracks,
                HasAudio = set?.MixWorker != null,
                AudioBufferedSeconds = set?.AudioRing != null
                    ? set.AudioRing.Available / 2.0 / set.MixRate
                    : 0,
            };
        }

        // ------------------------------------------------------------------------------ mapping

        private long MapVideoPtsToClock((Guid, int) key, long srcTicks)
        {
            var map = _map;
            if (map != null && map.TryGetVideo(key, out var sm))
                return sm.SourceToTimeline(srcTicks);
            return srcTicks;
        }

        private static long VideoSourceTarget(ProjectTimelineMap map, (Guid, int) key, long tlTicks)
        {
            if (map != null && map.TryGetVideo(key, out var sm))
                return sm.TimelineToSource(tlTicks);
            return tlTicks;
        }

        /// <summary>The container seek target for one file, mapped through its first video
        /// stream's timeline (streams of one recording share their placement; files exist only
        /// for video streams — audio has its own decoders).</summary>
        private static long FileSeekTarget(ProjectTimelineMap map, FilePipe file, long tlTicks)
            => VideoSourceTarget(map, file.Video[0].Key, tlTicks);

        private static long PrimaryOffsetAt(ProjectTimelineMap map, long tlTicks)
        {
            if (map?.PrimaryVideo is { } pk && map.TryGetVideo(pk, out var sm))
                return sm.OffsetAtTimeline(tlTicks);
            return long.MinValue;
        }

        private static Dictionary<(Guid, int), SkipRangeSchedule> BuildCutSchedules(ProjectTimelineMap map)
        {
            var cuts = new Dictionary<(Guid, int), SkipRangeSchedule>();
            foreach (var (key, stream) in map.VideoStreams)
            {
                if (stream.SourceCuts.Ranges.Count > 0)
                    cuts[key] = stream.SourceCuts;
            }

            return cuts;
        }

        private static Source FindSource(Project project, Guid sourceId)
        {
            if (project.Sources == null)
                return null;
            foreach (var source in project.Sources)
            {
                if (source.Id == sourceId)
                    return source;
            }

            return null;
        }

        /// <summary>The mix rate for a project: its own output rate, falling back to the open
        /// options when the project does not carry one.</summary>
        private int MixRate(Project project)
            => project.Output != null && project.Output.SampleRate > 0
                ? project.Output.SampleRate
                : _options.AudioSampleRate;

        /// <summary>The distinct audio streams referenced by items on unmuted audio tracks,
        /// sorted — the audio half of the pipeline identity. Mute/unmute and stream add/remove
        /// change it (rebuild); volume/transition edits do not (cheap mixer swap).</summary>
        private static List<(Guid SourceId, int StreamIndex)> CollectAudioStreams(Project project)
        {
            var audioTracks = new HashSet<Guid>();
            foreach (var track in project.Tracks ?? new List<Track>())
            {
                if (track.Kind == TrackKind.Audio && !track.Muted)
                    audioTracks.Add(track.Id);
            }

            var keys = new List<(Guid SourceId, int StreamIndex)>();
            foreach (var item in project.Items ?? new List<Item>())
            {
                if (item.Content is MediaContent media && item.DurationTicks > 0
                    && audioTracks.Contains(item.TrackId)
                    && !keys.Contains((media.SourceId, media.StreamIndex)))
                    keys.Add((media.SourceId, media.StreamIndex));
            }

            keys.Sort((a, b) =>
            {
                int bySource = a.SourceId.CompareTo(b.SourceId);
                return bySource != 0 ? bySource : a.StreamIndex.CompareTo(b.StreamIndex);
            });
            return keys;
        }

        /// <summary>Identity of the decode-pipeline set: which streams of which files feed it
        /// (one line per video stream and per referenced audio item stream), plus the mix rate.
        /// Edits that keep this equal never rebuild pipelines.</summary>
        private static string StreamSignature(Project project, ProjectTimelineMap map,
            List<(Guid SourceId, int StreamIndex)> audioStreams, int mixRate)
        {
            var keys = new List<(Guid SourceId, int StreamIndex)>(map.VideoStreams.Keys);
            keys.Sort((a, b) =>
            {
                int bySource = a.SourceId.CompareTo(b.SourceId);
                return bySource != 0 ? bySource : a.StreamIndex.CompareTo(b.StreamIndex);
            });

            var sb = new StringBuilder();
            foreach (var key in keys)
                sb.Append("v:").Append(key.SourceId).Append(':').Append(key.StreamIndex)
                  .Append(':').Append(FindSource(project, key.SourceId)?.Path).Append('\n');
            foreach (var key in audioStreams)
                sb.Append("a:").Append(key.SourceId).Append(':').Append(key.StreamIndex)
                  .Append(':').Append(FindSource(project, key.SourceId)?.Path).Append('\n');
            if (audioStreams.Count > 0)
                sb.Append("rate:").Append(mixRate);
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------ helpers

        private void WakeAllPresenters()
        {
            var set = _pipelines;
            if (set == null)
                return;
            foreach (var pipe in set.AllVideo)
                pipe.Worker.WakePresent();
        }

        private void SetState(PlayerState state)
        {
            lock (_stateSync)
            {
                if (_state == state)
                    return;
                _state = state;
            }

            var handler = StateChanged;
            if (handler != null)
                _dispatch(() => handler(this, state));
        }

        private void RaisePositionChanged()
        {
            var handler = PositionChanged;
            if (handler != null)
                _dispatch(() => handler(this, EventArgs.Empty));
        }

        // ------------------------------------------------------------------------------ dispose

        public void Dispose()
        {
            lock (_seekSync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _hasPendingSeek = false;
            }

            // defer teardown behind an in-flight OpenCore (native use-after-free otherwise).
            var open = _openTask;
            if (open != null && !open.IsCompleted)
            {
                open.ContinueWith(_ => DisposeCore(), TaskScheduler.Default);
                return;
            }

            DisposeCore();
        }

        private void DisposeCore()
        {
            lock (_lifecycleSync)
            {
                _tickTimer?.Dispose();
                _tickTimer = null;

                var set = _pipelines;
                _pipelines = null;
                _frameSource.Dispose();
                if (set != null)
                    DisposePipelineSet(set);

                _state = PlayerState.Idle;
            }
        }

        private static void DisposePipelineSet(PipelineSet set)
        {
            // stop order matters (see FFmpegVideoPlayer.DisposeCore): demux threads first, then
            // wake every consumer via queue Stop, then join workers, then free queues/contexts.
            foreach (var file in set.Files)
                file.Demuxer.Stop();

            foreach (var file in set.Files)
            {
                foreach (var queue in file.Queues)
                    queue.Stop();
            }

            foreach (var file in set.Files)
            {
                foreach (var pipe in file.Video)
                {
                    pipe.Worker.Dispose();
                    pipe.Sink.Dispose();
                }
            }

            set.MixWorker?.Dispose(); // joins the mix thread before its sink/ring go away
            set.AudioSink?.Dispose();

            foreach (var file in set.Files)
            {
                foreach (var queue in file.Queues)
                    queue.Dispose();
                file.Demuxer.Dispose();
            }
        }
    }
}
