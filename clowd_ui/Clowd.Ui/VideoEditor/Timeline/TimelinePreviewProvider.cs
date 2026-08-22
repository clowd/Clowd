using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Thumbs;

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>
    /// The timeline's window onto the SDK's preview-visual services: one
    /// <see cref="FilmstripProvider"/> and one <see cref="WaveformProvider"/> per editor window,
    /// resolved from the session's <see cref="Project"/> (items reference a source by id, the
    /// decoders want a path) and adapted to the shapes <see cref="ITimelinePreviewProvider"/>
    /// promises: Avalonia bitmaps instead of BGRA buffers, peaks re-bucketed to whatever the
    /// current zoom asked for, and <see cref="PreviewReady"/> on the UI thread.
    ///
    /// <para>
    /// Both SDK providers announce progress on their own threads; this class coalesces those into
    /// at most one queued <see cref="Dispatcher.UIThread"/> post, so a decode pass that publishes
    /// tens of times a second still costs the timeline one throttled repaint.
    /// </para>
    ///
    /// <para>
    /// Dispose with the window: that cancels every decode pass and releases the bitmaps. Nothing
    /// here outlives the editor.
    /// </para>
    /// </summary>
    public sealed class TimelinePreviewProvider : ITimelinePreviewProvider, IDisposable
    {
        /// <summary>Matches <c>ThumbnailDecoder</c>'s own clamp, so the key this class caches
        /// bitmaps under is the key the SDK caches thumbnails under.</summary>
        private const int MinThumbHeightPx = 8;

        private const int MaxThumbHeightPx = 512;

        /// <summary>How many spans a strip may be aimed with. Past this the closest pairs merge —
        /// the SDK re-scans every span's slots per pick, and a screen showing more than a handful
        /// of segments of one stream is too zoomed-out for refinement to matter anyway.</summary>
        private const int MaxViewportSpans = 8;

        private readonly EditorSession _session;
        private readonly string _cacheDir;
        private readonly FilmstripProvider _filmstrip = new FilmstripProvider();
        private readonly WaveformProvider _waveform = new WaveformProvider();

        private readonly Dictionary<(Guid SourceId, int StreamIndex, int ThumbHeightPx), StripCache> _strips =
            new Dictionary<(Guid, int, int), StripCache>();

        private readonly Dictionary<(Guid SourceId, int StreamIndex), PeaksCache> _peaks =
            new Dictionary<(Guid, int), PeaksCache>();

        private long _viewStartTicks;
        private long _viewEndTicks;
        private int _readyPosted;
        private bool _disposed;

        /// <param name="session">The edit being drawn — the live project is re-read on every call,
        /// so undo (which replaces the project instance) needs no notification here.</param>
        /// <param name="cacheDir">Where waveforms are cached between opens: the directory holding
        /// <c>videoedit.json</c>, or null in the <c>--video-edit</c> dev harness, which has no
        /// session directory and analyzes in memory.</param>
        public TimelinePreviewProvider(EditorSession session, string cacheDir)
        {
            ArgumentNullException.ThrowIfNull(session);

            _session = session;
            _cacheDir = cacheDir;
            _filmstrip.Changed += OnProviderChanged;
            _waveform.Changed += OnProviderChanged;
        }

        public event EventHandler PreviewReady;

        public ThumbnailStrip GetThumbnails(in ThumbnailRequest request)
        {
            if (_disposed)
                return ThumbnailStrip.Empty;

            var path = ResolvePath(request.SourceId);
            if (path == null)
                return ThumbnailStrip.Empty;

            var height = Math.Clamp(request.ThumbHeightPx, MinThumbHeightPx, MaxThumbHeightPx);
            var key = (request.SourceId, request.StreamIndex, height);
            if (!_strips.TryGetValue(key, out var cache))
            {
                cache = new StripCache(path);
                _strips[key] = cache;
            }

            // The interval is the grid the rows are drawn on right now, so it is also the grid
            // refinement should fill. Quantized here (identically on both sides) rather than taken
            // from the snapshot: the snapshot reports the last grid the provider was aimed at,
            // which lags a zoom by one frame, and drawing slots on a stale grid draws nothing.
            cache.Path = path;
            cache.IntervalTicks = ThumbnailRequest.QuantizeInterval(request.IntervalTicks);

            // The keyframe pass is queued BEFORE the viewport aims refinement: both land on one
            // shared worker, and whichever is enqueued first wins an idle thread — the pass (which
            // gives every row something to draw) must never lose that race to its own refinement.
            var snapshot = _filmstrip.GetOrStart(path, request.StreamIndex, height);
            if (ApplyViewport(key, cache))
                snapshot = _filmstrip.GetOrStart(path, request.StreamIndex, height);

            return cache.Project(snapshot);
        }

        public AudioPeaks GetAudioPeaks(in AudioPeaksRequest request)
        {
            if (_disposed)
                return AudioPeaks.Silent(request);

            var path = ResolvePath(request.SourceId);
            if (path == null)
                return AudioPeaks.Silent(request);

            // the disk cache is keyed by the source's model id: stream indices are container-
            // relative, so a recording and an import (audio at stream 1 in both) must not share a
            // file, and the id — unlike a path hash — survives the session directory moving.
            var snapshot = _waveform.GetOrStart(path, request.StreamIndex, _cacheDir,
                request.SourceId.ToString("N"));

            var key = (request.SourceId, request.StreamIndex);
            if (!_peaks.TryGetValue(key, out var cache))
            {
                cache = new PeaksCache();
                _peaks[key] = cache;
            }

            return cache.Project(request, snapshot);
        }

        /// <summary>
        /// Queues waveform analysis for every audio stream the project references, ahead of the
        /// first paint. Rows are painted top-down (video above audio), so without this the
        /// filmstrip work would reach the shared decode thread first and every audio row would
        /// draw flat until a whole-file demux finished — the exact inversion the scheduler's
        /// priority bands exist to prevent. Called once by the window, before the timeline gets
        /// the provider; anything it misses (a later import) still lands through
        /// <see cref="GetAudioPeaks"/>, just without the head start.
        /// </summary>
        public void Prime()
        {
            if (_disposed)
                return;

            var project = _session.Project;
            if (project?.Tracks == null || project.Items == null)
                return;

            var seen = new HashSet<(Guid SourceId, int StreamIndex)>();
            foreach (var track in project.Tracks)
            {
                if (track.Kind != TrackKind.Audio)
                    continue;

                foreach (var item in project.Items)
                {
                    if (item.TrackId != track.Id || item.Content is not MediaContent media ||
                        !seen.Add((media.SourceId, media.StreamIndex)))
                        continue;

                    var path = ResolvePath(media.SourceId);
                    if (path != null)
                        _waveform.GetOrStart(path, media.StreamIndex, _cacheDir, media.SourceId.ToString("N"));
                }
            }
        }

        /// <summary>The visible span in <b>timeline</b> ticks. Each filmstrip wants the spans of
        /// its own <b>source</b>, so this maps the viewport through every item that references the
        /// stream — one provider serves every row, and a row's source time is nothing like its
        /// timeline time once the recording has been cut. Strips whose items all left the screen
        /// have their aim retired and their platform bitmaps released here (the SDK keeps the raw
        /// pixels under its own cap; re-entering the screen rebuilds a bitmap with a memcpy), and
        /// strips whose source is gone entirely (item deleted, import undone) are dropped — they
        /// could otherwise never be projected again and would hold their bitmaps until the window
        /// closed.</summary>
        public void SetViewport(long startTicks, long endTicks)
        {
            if (_disposed)
                return;

            _viewStartTicks = Math.Min(startTicks, endTicks);
            _viewEndTicks = Math.Max(startTicks, endTicks);

            List<(Guid SourceId, int StreamIndex, int ThumbHeightPx)> deadStrips = null;
            foreach (var pair in _strips)
            {
                if (ResolvePath(pair.Key.SourceId) == null)
                {
                    (deadStrips ??= new List<(Guid, int, int)>()).Add(pair.Key);
                    continue;
                }

                ApplyViewport(pair.Key, pair.Value);
                if (!pair.Value.Applied)
                    pair.Value.Release();
            }

            if (deadStrips != null)
            {
                foreach (var key in deadStrips)
                {
                    _strips[key].Dispose();
                    _strips.Remove(key);
                }
            }

            if (_peaks.Count > 0)
            {
                List<(Guid SourceId, int StreamIndex)> deadPeaks = null;
                foreach (var key in _peaks.Keys)
                {
                    if (ResolvePath(key.SourceId) == null)
                        (deadPeaks ??= new List<(Guid, int)>()).Add(key);
                }

                if (deadPeaks != null)
                {
                    foreach (var key in deadPeaks)
                        _peaks.Remove(key);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _filmstrip.Changed -= OnProviderChanged;
            _waveform.Changed -= OnProviderChanged;
            _filmstrip.Dispose();
            _waveform.Dispose();

            foreach (var strip in _strips.Values)
                strip.Dispose();
            _strips.Clear();
            _peaks.Clear();
        }

        private string ResolvePath(Guid sourceId)
        {
            var project = _session.Project;
            var source = project?.Sources?.FirstOrDefault(s => s.Id == sourceId);
            return String.IsNullOrEmpty(source?.Path) ? null : source.Path;
        }

        /// <summary>Aims one strip's refinement at the parts of it that are on screen — one span
        /// per visible item, never their min/max union: after a cut, the union spans the removed
        /// material, and refinement would decode (and the cache would protect) frames nothing
        /// displays. Only forwarded when something actually moved: the SDK treats every call as a
        /// viewport change and drops the snapshot it has cached for the strip. With nothing of the
        /// stream on screen the aim is retired instead, so refinement stops and the strip's
        /// thumbnails become the SDK cache's first eviction candidates. Returns true when the SDK
        /// was told anything.</summary>
        private bool ApplyViewport((Guid SourceId, int StreamIndex, int ThumbHeightPx) key, StripCache cache)
        {
            if (cache.Path == null || cache.IntervalTicks <= 0)
                return false;

            var spans = VisibleSourceSpans(key.SourceId, key.StreamIndex);
            if (spans.Count == 0)
            {
                if (!cache.Applied)
                    return false;

                cache.Applied = false;
                _filmstrip.ClearViewport(cache.Path, key.StreamIndex, key.ThumbHeightPx);
                return true;
            }

            if (cache.Applied && cache.AppliedInterval == cache.IntervalTicks &&
                SpansEqual(cache.AppliedSpans, spans))
                return false;

            cache.Applied = true;
            cache.AppliedSpans = spans.ToArray();
            cache.AppliedInterval = cache.IntervalTicks;
            _filmstrip.SetViewport(cache.Path, key.StreamIndex, key.ThumbHeightPx, spans, cache.IntervalTicks);
            return true;
        }

        /// <summary>The visible parts of every item drawn from one stream, mapped to source ticks:
        /// sorted, overlapping/touching entries merged, at most <see cref="MaxViewportSpans"/>.
        /// Empty when none of them intersects the viewport.</summary>
        private List<(long Start, long End)> VisibleSourceSpans(Guid sourceId, int streamIndex)
        {
            var spans = new List<(long Start, long End)>();

            var items = _session.Project?.Items;
            if (items == null || _viewEndTicks <= _viewStartTicks)
                return spans;

            foreach (var item in items)
            {
                if (item.Content is not MediaContent media ||
                    media.SourceId != sourceId || media.StreamIndex != streamIndex)
                    continue;

                var from = Math.Max(item.TimelineStartTicks, _viewStartTicks);
                var to = Math.Min(item.TimelineEndTicks, _viewEndTicks);
                if (to <= from)
                    continue;

                spans.Add((media.SourceInTicks + (from - item.TimelineStartTicks),
                    media.SourceInTicks + (to - item.TimelineStartTicks)));
            }

            if (spans.Count <= 1)
                return spans;

            spans.Sort((a, b) => a.Start.CompareTo(b.Start));

            var merged = new List<(long Start, long End)>(spans.Count) { spans[0] };
            for (var i = 1; i < spans.Count; i++)
            {
                var last = merged[^1];
                if (spans[i].Start <= last.End)
                    merged[^1] = (last.Start, Math.Max(last.End, spans[i].End));
                else
                    merged.Add(spans[i]);
            }

            while (merged.Count > MaxViewportSpans)
            {
                var narrowest = 1;
                var narrowestGap = Int64.MaxValue;
                for (var i = 1; i < merged.Count; i++)
                {
                    var gap = merged[i].Start - merged[i - 1].End;
                    if (gap < narrowestGap)
                    {
                        narrowestGap = gap;
                        narrowest = i;
                    }
                }

                merged[narrowest - 1] = (merged[narrowest - 1].Start, merged[narrowest].End);
                merged.RemoveAt(narrowest);
            }

            return merged;
        }

        private static bool SpansEqual((long Start, long End)[] applied, List<(long Start, long End)> spans)
        {
            if (applied == null || applied.Length != spans.Count)
                return false;

            for (var i = 0; i < applied.Length; i++)
            {
                if (applied[i] != spans[i])
                    return false;
            }

            return true;
        }

        /// <summary>Both SDK providers already throttle themselves; this only has to make sure a
        /// burst from the two of them lands as one post on the UI thread.</summary>
        private void OnProviderChanged(object sender, EventArgs e)
        {
            if (_disposed || Interlocked.Exchange(ref _readyPosted, 1) != 0)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _readyPosted, 0);
                if (!_disposed)
                    PreviewReady?.Invoke(this, EventArgs.Empty);
            }, DispatcherPriority.Background);
        }

        private static Bitmap ToBitmap(in FilmstripThumbnail thumb)
        {
            var bitmap = new WriteableBitmap(new PixelSize(thumb.Width, thumb.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);

            using (var buffer = bitmap.Lock())
            {
                var rowBytes = thumb.Width * 4;
                for (var y = 0; y < thumb.Height; y++)
                {
                    Marshal.Copy(thumb.Pixels, y * thumb.Stride,
                        IntPtr.Add(buffer.Address, y * buffer.RowBytes), rowBytes);
                }
            }

            return bitmap;
        }

        /// <summary>
        /// One video stream's thumbnails as Avalonia bitmaps. The SDK hands out an immutable
        /// snapshot object that only changes when its content does, so an unchanged snapshot short-
        /// circuits to the strip built last time and a per-frame repaint costs a reference compare.
        /// When it does change, bitmaps are carried over by pixel-buffer identity — the thumbnails
        /// that survived are not re-uploaded — and the ones that did not survive are disposed.
        /// </summary>
        private sealed class StripCache : IDisposable
        {
            private Dictionary<long, Entry> _bitmaps = new Dictionary<long, Entry>();
            private FilmstripSnapshot _snapshot;
            private ThumbnailStrip _strip;
            private long _stripInterval;

            public StripCache(string path) => Path = path;

            public string Path;

            /// <summary>The quantized grid the rows are currently drawn on.</summary>
            public long IntervalTicks;

            /// <summary>Whether the SDK currently holds an aim for this strip — false while
            /// nothing of the stream is on screen (the aim was retired).</summary>
            public bool Applied;

            public (long Start, long End)[] AppliedSpans;
            public long AppliedInterval;

            public ThumbnailStrip Project(FilmstripSnapshot snapshot)
            {
                if (ReferenceEquals(snapshot, _snapshot) && _strip != null && _stripInterval == IntervalTicks)
                    return _strip;

                var thumbnails = snapshot.Thumbnails;
                var mapped = new TimelineThumbnail[thumbnails.Count];
                var kept = new Dictionary<long, Entry>(thumbnails.Count);

                for (var i = 0; i < thumbnails.Count; i++)
                {
                    var thumb = thumbnails[i];
                    if (!_bitmaps.TryGetValue(thumb.SourceTicks, out var entry) ||
                        !ReferenceEquals(entry.Pixels, thumb.Pixels))
                    {
                        entry = new Entry(thumb.Pixels, ToBitmap(thumb));
                    }
                    else
                    {
                        _bitmaps.Remove(thumb.SourceTicks);
                    }

                    kept[thumb.SourceTicks] = entry;
                    mapped[i] = new TimelineThumbnail(thumb.SourceTicks, entry.Bitmap);
                }

                // whatever is left was evicted (or replaced) by the SDK cache and is not drawn again
                foreach (var stale in _bitmaps.Values)
                    stale.Bitmap.Dispose();
                _bitmaps.Clear();
                _bitmaps = kept;

                _snapshot = snapshot;
                _stripInterval = IntervalTicks;
                _strip = new ThumbnailStrip(IntervalTicks, mapped, snapshot.IsComplete);
                return _strip;
            }

            /// <summary>Drops the platform bitmaps of a strip nobody is drawing. The SDK keeps the
            /// raw pixels under its own cap, so scrolling back in rebuilds each surviving bitmap
            /// with a memcpy — without this, the adapter would retain Σ(per-strip peak) bitmaps
            /// while the SDK believed the cache was bounded.</summary>
            public void Release()
            {
                foreach (var entry in _bitmaps.Values)
                    entry.Bitmap.Dispose();
                _bitmaps.Clear();
                _snapshot = null;
                _strip = null;
            }

            public void Dispose() => Release();

            private readonly struct Entry
            {
                public Entry(byte[] pixels, Bitmap bitmap)
                {
                    Pixels = pixels;
                    Bitmap = bitmap;
                }

                public byte[] Pixels { get; }

                public Bitmap Bitmap { get; }
            }
        }

        /// <summary>
        /// Re-buckets one audio stream's peaks to the bucket size the current zoom asked for. The
        /// SDK analyzes at a fixed 200 buckets/s and the timeline draws roughly one bucket per
        /// pixel, so this is always a reduction: each output bucket takes the extremes of the SDK
        /// buckets it covers. The last result is kept — an unchanged (request, snapshot) pair is
        /// the common case while the user drags a playhead across a finished waveform.
        /// </summary>
        private sealed class PeaksCache
        {
            private WaveformSnapshot _snapshot;
            private AudioPeaksRequest _request;
            private AudioPeaks _peaks;

            public AudioPeaks Project(in AudioPeaksRequest request, WaveformSnapshot snapshot)
            {
                if (_peaks != null && ReferenceEquals(snapshot, _snapshot) && _request == request)
                    return _peaks;

                var perBucket = Math.Max(1, request.TicksPerBucket);
                var count = (int)Math.Clamp((request.DurationTicks + perBucket - 1) / perBucket, 0, Int32.MaxValue / 2);
                var minMax = new float[count * 2];

                for (var i = 0; i < count; i++)
                {
                    var from = request.SourceInTicks + (long)i * perBucket;
                    var first = snapshot.BucketAt(from);
                    var last = snapshot.BucketAt(from + perBucket - 1);

                    float min = 0, max = 0;
                    for (var b = first; b <= last; b++)
                    {
                        if (!snapshot.TryGetBucket(b, out var bucketMin, out var bucketMax))
                            continue;
                        if (bucketMin < min)
                            min = bucketMin;
                        if (bucketMax > max)
                            max = bucketMax;
                    }

                    minMax[i * 2] = min;
                    minMax[i * 2 + 1] = max;
                }

                // complete once the analysis has passed the end of this item, even when the rest of
                // the stream is still being decoded: the row is final and stops rebuilding.
                var analyzed = snapshot.IsComplete ||
                    snapshot.ReadyTicks >= request.SourceInTicks + request.DurationTicks;

                _snapshot = snapshot;
                _request = request;
                _peaks = new AudioPeaks(request.SourceInTicks, perBucket, minMax, analyzed);
                return _peaks;
            }
        }
    }
}
