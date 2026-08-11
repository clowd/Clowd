using System;
using System.Collections.Generic;
using System.Threading;

namespace Clowd.VideoSDK.Thumbs
{
    /// <summary>
    /// Waveforms for the timeline's audio rows: ask for a stream, get whatever peaks exist right
    /// now, and be told when there are more. Nothing here blocks — the first
    /// <see cref="GetOrStart"/> for a stream loads the disk cache or queues a decode pass on the
    /// shared <see cref="ThumbWorkScheduler"/>, and every later call is a dictionary lookup
    /// returning the pass's latest <see cref="WaveformSnapshot"/>.
    ///
    /// <para>
    /// <see cref="Changed"/> is raised on a thread-pool thread, coalesced to about 10 Hz: it
    /// carries no payload precisely because a consumer re-reads through
    /// <see cref="GetOrStart"/>, so a raise can never be stale, only redundant. The Clowd.Ui
    /// adapter marshals it to the UI thread (the timeline's provider contract requires that).
    /// </para>
    ///
    /// <para>
    /// <see cref="Dispose"/> cancels every pass this provider started; queued ones evaporate and a
    /// running one stops within a decode chunk. It does not wait for them, and no
    /// <see cref="Changed"/> is raised afterwards.
    /// </para>
    /// </summary>
    public sealed class WaveformProvider : IDisposable
    {
        /// <summary>The resolution every snapshot this provider hands out is built at.</summary>
        public const int BucketsPerSecond = WaveformBuilder.DefaultBucketsPerSecond;

        /// <summary>Minimum gap between <see cref="Changed"/> raises. A waveform pass publishes
        /// every ~20 ms of decoded audio; the timeline only needs to repaint at roughly this rate.</summary>
        private const int ChangedThrottleMs = 100;

        private readonly object _gate = new object();
        private readonly Dictionary<(string Path, int Stream), Entry> _entries =
            new Dictionary<(string, int), Entry>(StreamKeyComparer.Instance);

        private readonly ThumbWorkScheduler _scheduler;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private Timer _changedTimer;
        private bool _changePending;
        private long _lastRaisedTicks;
        private int _builds;
        private int _cacheHits;
        private Exception _error;
        private bool _disposed;

        public WaveformProvider()
            : this(null)
        {
        }

        internal WaveformProvider(ThumbWorkScheduler scheduler)
        {
            _scheduler = scheduler ?? ThumbWorkScheduler.Shared;
        }

        /// <summary>Raised (coalesced, on a thread-pool thread) when some stream has more peaks
        /// than the last time the consumer asked.</summary>
        public event EventHandler Changed;

        /// <summary>The first analysis failure, or null while healthy. A stream that cannot be
        /// decoded settles as an empty complete waveform — a flat row, never a retry loop.</summary>
        public Exception Error => Volatile.Read(ref _error);

        /// <summary>Decode passes started (test/diagnostic): a cache hit must not move it.</summary>
        internal int BuildCount => Volatile.Read(ref _builds);

        /// <summary>Waveforms served straight from disk (test/diagnostic).</summary>
        internal int CacheHitCount => Volatile.Read(ref _cacheHits);

        /// <summary>
        /// The peaks known for one stream of one file, starting the analysis on first ask.
        /// </summary>
        /// <param name="sourcePath">The media file — analysed through its own format context, so
        /// this never contends with playback.</param>
        /// <param name="streamIndex">The audio stream's index in that file.</param>
        /// <param name="cacheDir">Where the stream's <c>.cwf</c> file lives (the directory holding
        /// the project's <c>videoedit.json</c>); null disables the disk cache, which is what the
        /// dev harness — a recording with no session directory — runs with.</param>
        /// <param name="cacheKey">Stable identity of the source for the cache file name (see
        /// <see cref="WaveformCache.FileNameFor"/>); null falls back to a hash of the path.</param>
        public WaveformSnapshot GetOrStart(string sourcePath, int streamIndex, string cacheDir,
            string cacheKey = null)
        {
            ArgumentNullException.ThrowIfNull(sourcePath);

            Entry entry;
            lock (_gate)
            {
                if (_disposed)
                    return WaveformSnapshot.Empty;

                var key = (sourcePath, streamIndex);
                if (!_entries.TryGetValue(key, out entry))
                {
                    entry = new Entry(sourcePath, streamIndex, cacheDir, cacheKey);
                    _entries[key] = entry;
                    entry.Handle = _scheduler.Enqueue(ThumbWorkPriority.Waveform,
                        token => Run(entry, token), _cts.Token);
                }
            }

            return entry.Snapshot;
        }

        /// <summary>Scheduler thread: cache, else one forward pass, then cache it.</summary>
        private void Run(Entry entry, CancellationToken token)
        {
            try
            {
                var cached = WaveformCache.TryLoad(entry.CacheDir, entry.SourcePath, entry.StreamIndex,
                    BucketsPerSecond, entry.CacheKey);
                if (cached != null)
                {
                    Interlocked.Increment(ref _cacheHits);
                    entry.Snapshot = cached;
                    RaiseChanged();
                    return;
                }

                var buffer = new WaveformBuffer(BucketsPerSecond);
                Interlocked.Increment(ref _builds);

                bool complete = WaveformBuilder.Build(entry.SourcePath, entry.StreamIndex, buffer,
                    () =>
                    {
                        entry.Snapshot = buffer.Snapshot;
                        RaiseChanged();
                    },
                    token);

                entry.Snapshot = buffer.Snapshot;
                if (!complete)
                    return; // cancelled: keep the partial peaks, never cache them

                RaiseChanged();
                WaveformCache.TrySave(entry.CacheDir, entry.SourcePath, entry.StreamIndex, entry.Snapshot,
                    entry.CacheKey);
            }
            catch (Exception ex)
            {
                // a missing or undecodable stream draws as a flat line for the rest of the session
                // rather than re-queuing on every repaint.
                Interlocked.CompareExchange(ref _error, ex, null);
                entry.Snapshot = new WaveformSnapshot(BucketsPerSecond, Array.Empty<sbyte>(), 0, isComplete: true);
                RaiseChanged();
            }
        }

        private void RaiseChanged()
        {
            lock (_gate)
            {
                if (_disposed || _changePending)
                    return;

                _changePending = true;
                long due = Math.Max(0, ChangedThrottleMs - (Environment.TickCount64 - _lastRaisedTicks));
                _changedTimer ??= new Timer(OnChangedDue, null, Timeout.Infinite, Timeout.Infinite);
                _changedTimer.Change(due, Timeout.Infinite);
            }
        }

        private void OnChangedDue(object state)
        {
            EventHandler handler;
            lock (_gate)
            {
                if (_disposed)
                    return;

                _changePending = false;
                _lastRaisedTicks = Environment.TickCount64;
                handler = Changed;
            }

            handler?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Timer timer;
            List<Entry> entries;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;

                timer = _changedTimer;
                _changedTimer = null;
                entries = new List<Entry>(_entries.Values);
            }

            // the token stops a pass mid-decode; cancelling the handles drops the queued ones. The
            // source itself is deliberately NOT disposed — a pass still inside a decode chunk reads
            // the token afterwards, and outliving one CancellationTokenSource costs nothing.
            _cts.Cancel();
            foreach (var entry in entries)
                entry.Handle?.Cancel();

            timer?.Dispose();
        }

        private sealed class Entry
        {
            private WaveformSnapshot _snapshot = WaveformSnapshot.Empty;

            public Entry(string sourcePath, int streamIndex, string cacheDir, string cacheKey)
            {
                SourcePath = sourcePath;
                StreamIndex = streamIndex;
                CacheDir = cacheDir;
                CacheKey = cacheKey;
            }

            public string SourcePath { get; }

            public int StreamIndex { get; }

            public string CacheDir { get; }

            public string CacheKey { get; }

            public ThumbWorkHandle Handle { get; set; }

            public WaveformSnapshot Snapshot
            {
                get => Volatile.Read(ref _snapshot);
                set => Volatile.Write(ref _snapshot, value);
            }
        }

        /// <summary>Keys streams by path the way the file system compares them on this platform —
        /// the same file reached through two spellings must not be analysed twice on Windows, and
        /// must not be conflated on Linux.</summary>
        private sealed class StreamKeyComparer : IEqualityComparer<(string Path, int Stream)>
        {
            public static readonly StreamKeyComparer Instance = new StreamKeyComparer();

            private static readonly StringComparer PathComparer = OperatingSystem.IsLinux()
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;

            public bool Equals((string Path, int Stream) x, (string Path, int Stream) y) =>
                x.Stream == y.Stream && PathComparer.Equals(x.Path, y.Path);

            public int GetHashCode((string Path, int Stream) obj) =>
                HashCode.Combine(PathComparer.GetHashCode(obj.Path), obj.Stream);
        }
    }
}
