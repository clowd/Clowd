using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;

namespace Clowd.Video.Playback
{
    /// <summary>
    /// The in-process FFmpeg playback engine: one demux thread feeding up to two video decode
    /// workers (screen + webcam) and an audio worker, an audio-master clock, coalesced seeks,
    /// skip-range cut preview and frame stepping. See <see cref="IVideoPlayer"/> for the
    /// swappable boundary contract.
    /// </summary>
    public sealed class FFmpegVideoPlayer : IVideoPlayer
    {
        private readonly Action<Action> _dispatch;
        private readonly object _stateSync = new object();

        private Demuxer _demuxer;
        private readonly List<PacketQueue> _queues = new List<PacketQueue>();
        private VideoDecodeWorker[] _videoWorkers = Array.Empty<VideoDecodeWorker>();
        private AudioDecodeWorker _audioWorker;
        private NAudioSink _audioSink;
        private AudioRingBuffer _audioRing;
        private PlaybackClock _clock;
        private VideoOpenOptions _options;
        private Timer _tickTimer;

        private volatile PlayerState _state = PlayerState.Idle;
        private volatile SkipRangeSchedule _skips = SkipRangeSchedule.Empty;
        private double _volume = 1.0;
        private bool _audioDetached;
        private int _skipSeekBusy;
        private bool _disposed;
        private Task _openTask; // Dispose defers teardown until an in-flight OpenCore finishes

        // seek coalescing
        private readonly object _seekSync = new object();
        private TimeSpan _pendingSeekPos;
        private SeekMode _pendingSeekMode;
        private bool _hasPendingSeek;
        private bool _seekLoopActive;
        private Task _seekTask = Task.CompletedTask;

        /// <param name="eventDispatcher">Marshals <see cref="PositionChanged"/> /
        /// <see cref="StateChanged"/> onto the UI thread (e.g. Dispatcher.UIThread.Post).
        /// Null = raise on the calling thread.</param>
        public FFmpegVideoPlayer(Action<Action> eventDispatcher = null)
        {
            _dispatch = eventDispatcher ?? (a => a());
        }

        public MediaInfo Info { get; private set; }
        public PlayerState State => _state;
        public IFrameSink ScreenSink { get; set; }
        public IFrameSink WebcamSink { get; set; }

        public event EventHandler PositionChanged;
        public event EventHandler<PlayerState> StateChanged;

        public TimeSpan Position
        {
            get
            {
                var info = Info;
                if (info == null || _clock == null)
                    return TimeSpan.Zero;
                if (_state == PlayerState.Ended)
                    return info.Duration;

                var pos = _clock.Position;
                if (pos < TimeSpan.Zero)
                    return TimeSpan.Zero;
                if (info.Duration > TimeSpan.Zero && pos > info.Duration)
                    return info.Duration;
                return pos;
            }
        }

        public double Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0.0, 1.0);
                if (_audioSink != null)
                    _audioSink.Volume = _volume;
            }
        }

        public Task<MediaInfo> OpenAsync(string path, VideoOpenOptions options)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FFmpegVideoPlayer));
            if (_state != PlayerState.Idle)
                throw new InvalidOperationException("Player already has media open; create a new player per file.");

            SetState(PlayerState.Opening);
            var task = Task.Run(() =>
            {
                try
                {
                    var info = OpenCore(path, options ?? new VideoOpenOptions());
                    SetState(PlayerState.Paused);
                    return info;
                }
                catch
                {
                    SetState(PlayerState.Failed);
                    throw;
                }
            });

            // kept so Dispose can defer teardown behind OpenCore instead of racing its native
            // open/decoder-init calls (dispose-during-open: user closes the editor while loading).
            _openTask = task;
            return task;
        }

        private unsafe MediaInfo OpenCore(string path, VideoOpenOptions options)
        {
            FFmpegLoader.EnsureInitialized();
            _options = options;
            _clock = new PlaybackClock();

            _demuxer = new Demuxer();
            var info = _demuxer.Open(path);
            Info = info;

            // video: first stream = screen, second = webcam (recorder writes them in that order).
            int videoCount = Math.Min(info.VideoStreams.Count, 2);
            var workers = new VideoDecodeWorker[videoCount];
            for (int i = 0; i < videoCount; i++)
            {
                var queue = new PacketQueue(32);
                _queues.Add(queue);
                int streamIndex = info.VideoStreams[i].StreamIndex;
                _demuxer.AttachQueue(streamIndex, queue);

                int trackIdx = i;
                workers[i] = new VideoDecodeWorker(
                    _demuxer, streamIndex, queue, options, _clock,
                    trackIdx == 0 ? () => ScreenSink : () => WebcamSink,
                    () => _state == PlayerState.Playing,
                    OnImmediatePresented);
            }

            _videoWorkers = workers;

            // audio
            int audioStream = ffmpeg.av_find_best_stream(_demuxer.FormatContext,
                AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (audioStream >= 0)
            {
                var queue = new PacketQueue(64);
                _queues.Add(queue);
                _demuxer.AttachQueue(audioStream, queue);

                // ~500ms of float stereo
                _audioRing = new AudioRingBuffer(options.AudioSampleRate); // rate/2 frames * 2ch
                _audioSink = new NAudioSink(options.AudioSampleRate, 2, _audioRing);
                _audioSink.Volume = _volume;
                _audioWorker = new AudioDecodeWorker(_demuxer, audioStream, queue, _audioRing, _audioSink, options);
                _clock.SetAudioSource(_audioSink);
            }

            // the first frame of each track is presented immediately (paused preview). Mirrors
            // the DoSeek Prepare/OnSeeked pairing, but with the un-flushed serial 0 — no demux
            // flush happens at open, so PrepareSeek's Serial+1 gate would never match. Runs
            // before Start so OnSeeked's frame flush cannot discard already-decoded frames.
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i].PrepareSeek(TimeSpan.Zero, SeekMode.Fast);
                workers[i].OnSeeked(0);
            }

            _demuxer.Start();
            for (int i = 0; i < workers.Length; i++)
                workers[i].Start();
            _audioWorker?.Start();

            _tickTimer = new Timer(OnTick, null, 100, 100);
            return info;
        }

        private void OnImmediatePresented(VideoDecodeWorker worker, TimeSpan pts)
        {
            // the screen track's immediately-presented post-seek frame defines the position a
            // paused UI shows (a Fast seek lands on a keyframe, not the requested target).
            if (_videoWorkers.Length > 0 && worker == _videoWorkers[0] && _state != PlayerState.Playing)
            {
                _clock.SetPosition(pts);
                RaisePositionChanged();
            }
        }

        public void Play()
        {
            if (_state == PlayerState.Ended)
            {
                // restart from the top.
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
            _audioSink?.Play();
            _clock.Start();
            WakeAllPresenters();
        }

        public void Pause()
        {
            if (_state != PlayerState.Playing)
                return;

            _clock.Stop();
            _audioSink?.Pause();
            SetState(PlayerState.Paused);
            WakeAllPresenters();
        }

        public void SetSkipRanges(IReadOnlyList<TimeRange> ranges)
        {
            _skips = ranges == null || ranges.Count == 0 ? SkipRangeSchedule.Empty : new SkipRangeSchedule(ranges);
        }

        public Task SeekAsync(TimeSpan position, SeekMode mode)
        {
            if (Info == null)
                throw new InvalidOperationException("No media open.");

            if (position < TimeSpan.Zero)
                position = TimeSpan.Zero;
            if (Info.Duration > TimeSpan.Zero && position > Info.Duration)
                position = Info.Duration;

            lock (_seekSync)
            {
                _pendingSeekPos = position;
                _pendingSeekMode = mode;
                _hasPendingSeek = true;

                if (!_seekLoopActive)
                {
                    _seekLoopActive = true;
                    _seekTask = Task.Run(SeekLoop);
                }

                return _seekTask;
            }
        }

        /// <summary>Drains coalesced seek requests: a fast drag collapses to "latest wins".</summary>
        private void SeekLoop()
        {
            while (true)
            {
                TimeSpan pos;
                SeekMode mode;
                lock (_seekSync)
                {
                    if (!_hasPendingSeek || _disposed)
                    {
                        _seekLoopActive = false;
                        return;
                    }

                    pos = _pendingSeekPos;
                    mode = _pendingSeekMode;
                    _hasPendingSeek = false;
                }

                try
                {
                    DoSeek(pos, mode);
                }
                catch
                {
                    // a failed seek leaves the pipeline at its old position; not fatal.
                }
            }
        }

        private void DoSeek(TimeSpan target, SeekMode mode)
        {
            if (_state == PlayerState.Ended)
                SetState(PlayerState.Paused);

            // 1. tell every worker what to do once packets of the new generation arrive.
            _audioWorker?.PrepareSeek(target, mode);
            var workers = _videoWorkers;
            for (int i = 0; i < workers.Length; i++)
                workers[i].PrepareSeek(target, mode);

            // 2. flush queues + keyframe-backward container seek (runs on the demux thread).
            int newSerial = _demuxer.SeekAndFlush(target);

            // 3. unblock pipelines and reset per-track eof/presentation state.
            for (int i = 0; i < workers.Length; i++)
                workers[i].OnSeeked(newSerial);

            // 4. rebase the clock; audio timing rebuilds itself from the first post-flush sample.
            // Re-attach the audio master if it was detached at end-of-media — safe because
            // HasTiming stays false until that first sample, so the stopwatch drives meanwhile.
            if (_audioDetached && _audioSink != null)
            {
                _audioDetached = false;
                _clock.SetAudioSource(_audioSink);
            }

            _clock.SetPosition(target);
            RaisePositionChanged();
        }

        public async Task StepFrameAsync(int direction)
        {
            if (Info == null || _videoWorkers.Length == 0)
                return;

            if (_state == PlayerState.Playing)
                Pause();

            var screen = _videoWorkers[0];

            if (direction < 0)
            {
                var frameDur = screen.FrameDuration;
                var target = Position - frameDur;
                if (target < TimeSpan.Zero)
                    target = TimeSpan.Zero;
                await SeekAsync(target, SeekMode.Exact);
                return;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            screen.RequestStep(tcs, pts =>
            {
                _clock.SetPosition(pts);
                RaisePositionChanged();
            });

            // the webcam track follows along one frame; nobody awaits it.
            if (_videoWorkers.Length > 1)
                _videoWorkers[1].RequestStep(null, null);

            try
            {
                // at EOF there is no next frame — don't hang the caller.
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
            }
        }

        private void OnTick(object state)
        {
            if (_disposed || _state != PlayerState.Playing)
                return;

            try
            {
                var pos = Position;

                // cut preview: entering a skip range triggers an internal exact seek to its end.
                // The 2ms epsilon absorbs sub-sample clock rounding right after that seek lands —
                // without it a resume at exactly the range end can re-trigger the skip forever.
                var skips = _skips;
                if (skips.TryGetSkipEnd(pos, out var resumeAt) &&
                    resumeAt - pos > TimeSpan.FromMilliseconds(2) &&
                    Interlocked.CompareExchange(ref _skipSeekBusy, 1, 0) == 0)
                {
                    SeekAsync(resumeAt, SeekMode.Exact)
                        .ContinueWith(_ => Interlocked.Exchange(ref _skipSeekBusy, 0));
                }

                // audio finished before video: hand the clock to the stopwatch so video plays out.
                if (!_audioDetached && _audioWorker != null && _audioWorker.EofReached && _audioRing.Available == 0)
                {
                    _audioDetached = true;
                    _clock.SetAudioSource(null);
                }

                // end of media: every video track presented its EOF marker and audio is drained.
                bool videoDone = _videoWorkers.Length > 0;
                for (int i = 0; i < _videoWorkers.Length; i++)
                    videoDone &= _videoWorkers[i].EofPresented;
                bool audioDone = _audioWorker == null || (_audioWorker.EofReached && _audioRing.Available == 0);
                if (videoDone && audioDone && _demuxer.IsEof)
                {
                    _clock.Stop();
                    _audioSink?.Pause();
                    SetState(PlayerState.Ended);
                }

                RaisePositionChanged();
            }
            catch
            {
                // never let a stats/skip tick take down the timer thread.
            }
        }

        public PlaybackStatistics GetStatistics()
        {
            var workers = _videoWorkers;
            var tracks = new TrackStatistics[workers.Length];
            for (int i = 0; i < workers.Length; i++)
                tracks[i] = workers[i].GetIntervalStatistics();

            return new PlaybackStatistics
            {
                Video = tracks,
                HasAudio = _audioWorker != null,
                AudioBufferedSeconds = _audioRing != null
                    ? _audioRing.Available / 2.0 / (_options?.AudioSampleRate ?? 48000)
                    : 0,
            };
        }

        private void WakeAllPresenters()
        {
            var workers = _videoWorkers;
            for (int i = 0; i < workers.Length; i++)
                workers[i].WakePresent();
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

        public void Dispose()
        {
            lock (_seekSync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _hasPendingSeek = false;
            }

            // OpenCore runs on a thread-pool thread and touches native state (_demuxer/_queues/
            // workers) with no lock; tearing down concurrently is a native use-after-free. If the
            // open is still in flight, defer the teardown until it finishes (success or failure —
            // ContinueWith runs either way); the pipeline it built is then disposed normally.
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
            _tickTimer?.Dispose();
            _tickTimer = null;

            // stop order matters: demux thread first, then wake every consumer via queue Stop so
            // decode threads fall out of Get(), then join workers.
            _demuxer?.Stop();
            foreach (var q in _queues)
                q.Stop();

            foreach (var w in _videoWorkers)
                w.Dispose();
            _videoWorkers = Array.Empty<VideoDecodeWorker>();

            _audioWorker?.Dispose();
            _audioWorker = null;
            _audioSink?.Dispose();
            _audioSink = null;

            foreach (var q in _queues)
                q.Dispose();
            _queues.Clear();

            _demuxer?.Dispose();
            _demuxer = null;

            _state = PlayerState.Idle;
        }
    }
}
