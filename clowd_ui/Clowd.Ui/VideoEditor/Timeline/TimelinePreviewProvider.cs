using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Clowd.VideoSDK.Ai;
using Clowd.VideoSDK.Audio;
using Clowd.VideoSDK.Composition;
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
    /// current zoom asked for, and <see cref="PreviewReady"/> on the UI thread. The input
    /// overlays' previews ride along: the recording's capture sidecar is read once on a pool
    /// thread (through the SDK's own process-wide <see cref="InputActivity"/> cache, which the
    /// composer shares) and announced the same way.
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

        /// <summary>The denoise sidecar each audio stream's peaks are read from, or null where
        /// there is no usable one (raw passthrough). Probing the disk is what makes an entry, so
        /// this is only rebuilt when a generation finishes — see <see cref="InvalidateDenoise"/> —
        /// never on the strength edits that arrive a frame at a time while a slider drags.</summary>
        private readonly Dictionary<(Guid SourceId, int StreamIndex), string> _denoisePaths =
            new Dictionary<(Guid, int), string>();

        /// <summary>One per capture file in use, by path: its background read and the cursor/keys
        /// projections built from it.</summary>
        private readonly Dictionary<string, CaptureEntry> _captures =
            new Dictionary<string, CaptureEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The project's denoise settings as the mixer reads them, rebuilt on the next
        /// ask after any edit (null while stale). Cached because every audio row asks on every
        /// repaint and the answer is a walk of the whole project.</summary>
        private Dictionary<(Guid SourceId, int StreamIndex), double> _denoiseStrengths;

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
            _session.ProjectChanged += OnProjectChanged;
        }

        /// <summary>Any edit can have moved a denoise toggle, a strength, a mute or an item
        /// between tracks; the settings are re-read on the next row that asks.</summary>
        private void OnProjectChanged(object sender, ProjectChangedEventArgs e) => _denoiseStrengths = null;

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

            // the row must show what will be heard: a denoised stream's peaks come from its
            // sidecar, blended with the raw ones at exactly the strength the mixer blends the
            // samples at (see DenoisedAudioSource). Peaks of a blend are not the blend of peaks,
            // but they agree at both ends and move monotonically between them — close enough for
            // a row whose job is to show where the speech is.
            var strength = DenoiseStrengthFor(request.SourceId, request.StreamIndex);
            WaveformSnapshot denoised = null;
            if (strength > 0)
            {
                var sidecar = DenoiseSidecarFor(request.SourceId, request.StreamIndex, path);
                if (sidecar != null)
                {
                    denoised = _waveform.GetOrStart(sidecar, DenoiseStreamIndex, _cacheDir,
                        DenoiseCacheKey(request.SourceId, request.StreamIndex));
                }
            }

            var key = (request.SourceId, request.StreamIndex);
            if (!_peaks.TryGetValue(key, out var cache))
            {
                cache = new PeaksCache();
                _peaks[key] = cache;
            }

            return cache.Project(request, snapshot, denoised, denoised == null ? 0 : strength);
        }

        /// <summary>
        /// Forgets which streams have usable denoise sidecars, and drops the peaks read from the
        /// ones already found — called when a generation run finishes, which is both when a
        /// sidecar first appears and when an existing one is rewritten under the same name. The
        /// next paint re-probes the disk and re-analyzes whatever it finds.
        /// </summary>
        public void InvalidateDenoise()
        {
            if (_disposed)
                return;

            foreach (var sidecar in _denoisePaths.Values)
            {
                if (sidecar != null)
                    _waveform.Invalidate(sidecar, DenoiseStreamIndex);
            }

            _denoisePaths.Clear();
        }

        /// <summary>How much of a stream's audio the mixer will take from its denoise sidecar,
        /// read from the live project through the SDK's own rule so the row cannot disagree with
        /// what is rendered. Zero for every stream when nothing is denoised, which is the usual
        /// case and costs one walk of the project's tracks.</summary>
        private double DenoiseStrengthFor(Guid sourceId, int streamIndex)
        {
            if (_denoiseStrengths == null)
            {
                var project = _session.Project;
                _denoiseStrengths = project == null
                    ? new Dictionary<(Guid, int), double>()
                    : DenoisedAudioSource.CollectDenoisedStreams(project);
            }

            return _denoiseStrengths.TryGetValue((sourceId, streamIndex), out var strength) ? strength : 0;
        }

        /// <summary>The stream's denoise sidecar when one exists and still describes the source,
        /// else null (the row stays raw). The answer is cached because it costs a stat and a small
        /// json read; <see cref="InvalidateDenoise"/> is what expires it.</summary>
        private string DenoiseSidecarFor(Guid sourceId, int streamIndex, string sourcePath)
        {
            var key = (sourceId, streamIndex);
            if (_denoisePaths.TryGetValue(key, out var cached))
                return cached;

            var wavPath = AiSidecars.DenoisePath(_cacheDir, sourceId, streamIndex);
            var sidecar = wavPath != null && AiSidecars.IsValid(wavPath, sourcePath) ? wavPath : null;
            _denoisePaths[key] = sidecar;
            return sidecar;
        }

        /// <summary>The sidecar wav holds one stream, and the mixer reads it as stream 0.</summary>
        private const int DenoiseStreamIndex = 0;

        /// <summary>Cache identity for a sidecar's peaks: the raw stream's key plus a suffix, so
        /// the two waveforms of one stream cannot land in the same <c>.cwf</c>.</summary>
        private static string DenoiseCacheKey(Guid sourceId, int streamIndex) =>
            sourceId.ToString("N") + "denoise" + streamIndex.ToString(CultureInfo.InvariantCulture);

        public CursorActivity GetCursorActivity(in CursorActivityRequest request)
        {
            if (_disposed)
                return CursorActivity.None(request);

            var entry = CaptureFor(request.SourceId);
            if (entry == null)
                return CursorActivity.None(request);

            var motion = entry.Motion;
            return motion == null ? CursorActivity.Pending(request) : entry.Cursor.Project(request, motion);
        }

        public KeyRuns GetKeyRuns(in KeyRunsRequest request)
        {
            if (_disposed)
                return KeyRuns.None;

            var entry = CaptureFor(request.SourceId);
            if (entry == null)
                return KeyRuns.None;

            // segmentation itself is cheap once the capture is in memory (and cached per setting
            // by the SDK); only the read is deferred, so a run request waits on the same load the
            // cursor row started.
            if (entry.Motion == null)
                return KeyRuns.Pending;

            return entry.Keys.Project(request, entry.Path);
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

            // the overlay rows' captures too: the read is a file parse on a pool thread, and
            // starting it here rather than at first paint is what lets the rows draw full on the
            // first frame of a reopened project
            foreach (var item in project.Items)
            {
                var sourceId = item.Content switch
                {
                    CursorContent cursor => cursor.SourceId,
                    KeyboardContent keyboard => keyboard.SourceId,
                    _ => Guid.Empty,
                };
                if (sourceId != Guid.Empty)
                    CaptureFor(sourceId);
            }

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
                    if (path == null)
                        continue;

                    _waveform.GetOrStart(path, media.StreamIndex, _cacheDir, media.SourceId.ToString("N"));

                    // a denoised row draws its sidecar's peaks, so that analysis needs the same
                    // head start — without it the row would open showing the raw waveform and
                    // visibly swap once the sidecar pass caught up.
                    if (DenoiseStrengthFor(media.SourceId, media.StreamIndex) > 0)
                    {
                        var sidecar = DenoiseSidecarFor(media.SourceId, media.StreamIndex, path);
                        if (sidecar != null)
                        {
                            _waveform.GetOrStart(sidecar, DenoiseStreamIndex, _cacheDir,
                                DenoiseCacheKey(media.SourceId, media.StreamIndex));
                        }
                    }
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

            if (_captures.Count > 0)
            {
                List<string> deadCaptures = null;
                foreach (var pair in _captures)
                {
                    if (ResolveCapturePath(pair.Value.SourceId) == null)
                        (deadCaptures ??= new List<string>()).Add(pair.Key);
                }

                if (deadCaptures != null)
                {
                    foreach (var key in deadCaptures)
                        _captures.Remove(key);
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
            _session.ProjectChanged -= OnProjectChanged;
            _filmstrip.Dispose();
            _waveform.Dispose();

            foreach (var strip in _strips.Values)
                strip.Dispose();
            _strips.Clear();
            _peaks.Clear();
            _denoisePaths.Clear();
            _captures.Clear();
        }

        private string ResolvePath(Guid sourceId)
        {
            var project = _session.Project;
            var source = project?.Sources?.FirstOrDefault(s => s.Id == sourceId);
            return String.IsNullOrEmpty(source?.Path) ? null : source.Path;
        }

        private string ResolveCapturePath(Guid sourceId)
        {
            var project = _session.Project;
            var source = project?.Sources?.FirstOrDefault(s => s.Id == sourceId);
            return String.IsNullOrEmpty(source?.InputCapturePath) ? null : source.InputCapturePath;
        }

        /// <summary>The capture entry for a source, starting its read on first sight. Null for a
        /// source with no capture sidecar (the rows then draw nothing, completely).</summary>
        private CaptureEntry CaptureFor(Guid sourceId)
        {
            var path = ResolveCapturePath(sourceId);
            if (path == null)
                return null;

            if (_captures.TryGetValue(path, out var entry))
                return entry;

            entry = new CaptureEntry(sourceId, path);
            _captures[path] = entry;

            // the SDK's InputCapture.Get blocks for the parse and is what the composer reads
            // through too; running it here means whichever of the two asks first pays, and the
            // timeline never pays on its thread.
            Task.Run(() =>
            {
                var motion = InputActivity.GetCursorMotion(path);
                Volatile.Write(ref entry.Motion, motion);
                OnProviderChanged(this, EventArgs.Empty);
            });
            return entry;
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
        /// buckets it covers. A denoised row blends the sidecar's peaks over the raw ones at the
        /// track's strength, so the row shows what the mixer will play. The last result is kept —
        /// an unchanged (request, snapshots, strength) tuple is the common case while the user
        /// drags a playhead across a finished waveform, and the timeline reuses its geometry for
        /// as long as the same instance comes back.
        /// </summary>
        internal sealed class PeaksCache
        {
            private WaveformSnapshot _snapshot;
            private WaveformSnapshot _denoised;
            private double _strength;
            private AudioPeaksRequest _request;
            private AudioPeaks _peaks;

            /// <param name="snapshot">The raw stream's peaks.</param>
            /// <param name="denoised">The denoise sidecar's peaks, or null when the row draws raw
            /// audio (no sidecar yet, or the track's toggle is off).</param>
            /// <param name="strength">How far towards <paramref name="denoised"/> to blend, in
            /// [0, 1] — the track's denoise strength, the same number the mixer lerps samples
            /// with.</param>
            public AudioPeaks Project(in AudioPeaksRequest request, WaveformSnapshot snapshot,
                WaveformSnapshot denoised = null, double strength = 0)
            {
                if (_peaks != null && ReferenceEquals(snapshot, _snapshot) &&
                    ReferenceEquals(denoised, _denoised) && _strength == strength && _request == request)
                    return _peaks;

                var perBucket = Math.Max(1, request.TicksPerBucket);
                var count = (int)Math.Clamp((request.DurationTicks + perBucket - 1) / perBucket, 0, Int32.MaxValue / 2);
                var minMax = new float[count * 2];

                // the sidecar's peaks are only used once they cover the whole item: half a
                // denoised waveform growing over a row that already shows the raw one reads as a
                // glitch, where one swap when the pass lands reads as the row catching up.
                var wet = denoised != null && IsAnalyzed(denoised, request)
                    ? Math.Clamp(strength, 0, 1)
                    : 0;

                Fill(minMax, request, perBucket, count, wet >= 1 ? denoised : snapshot);
                if (wet > 0 && wet < 1)
                {
                    var wetMinMax = new float[count * 2];
                    Fill(wetMinMax, request, perBucket, count, denoised);
                    for (var i = 0; i < minMax.Length; i++)
                        minMax[i] += (float)((wetMinMax[i] - minMax[i]) * wet);
                }

                // complete once the analysis has passed the end of this item, even when the rest of
                // the stream is still being decoded: the row is final and stops rebuilding. A
                // denoised row is not final until its sidecar's pass has covered the item too.
                var analyzed = IsAnalyzed(snapshot, request) && (denoised == null || IsAnalyzed(denoised, request));

                _snapshot = snapshot;
                _denoised = denoised;
                _strength = strength;
                _request = request;
                _peaks = new AudioPeaks(request.SourceInTicks, perBucket, minMax, analyzed);
                return _peaks;
            }

            private static void Fill(float[] minMax, in AudioPeaksRequest request, long perBucket, int count,
                WaveformSnapshot snapshot)
            {
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
            }

            private static bool IsAnalyzed(WaveformSnapshot snapshot, in AudioPeaksRequest request) =>
                snapshot.IsComplete ||
                snapshot.ReadyTicks >= request.SourceInTicks + request.DurationTicks;
        }

        /// <summary>One capture file: its background read, and the projections built from it.</summary>
        private sealed class CaptureEntry
        {
            public CaptureEntry(Guid sourceId, string path)
            {
                SourceId = sourceId;
                Path = path;
            }

            public Guid SourceId { get; }

            public string Path { get; }

            /// <summary>Set once by the pool thread when the read finishes; null until then.</summary>
            public CursorMotion Motion;

            public CursorCache Cursor { get; } = new CursorCache();

            public KeyRunsCache Keys { get; } = new KeyRunsCache();
        }

        /// <summary>
        /// Re-buckets one recording's per-frame pointer speed to the bucket size the current zoom
        /// asked for — the peaks' <see cref="PeaksCache"/> for the cursor row. Each output bucket
        /// takes the fastest frame it covers (a flick must survive being zoomed out, just as a
        /// transient survives in a waveform), and the clicks whose press lands in the span are
        /// carried over in source ticks. The last result is kept for the unchanged-request case.
        /// </summary>
        internal sealed class CursorCache
        {
            private CursorMotion _motion;
            private CursorActivityRequest _request;
            private CursorActivity _activity;

            public CursorActivity Project(in CursorActivityRequest request, CursorMotion motion)
            {
                if (_activity != null && ReferenceEquals(motion, _motion) && _request == request)
                    return _activity;

                _motion = motion;
                _request = request;
                _activity = Build(request, motion);
                return _activity;
            }

            /// <summary>The uncached projection, for tests.</summary>
            internal static CursorActivity Build(in CursorActivityRequest request, CursorMotion motion)
            {
                var perBucket = Math.Max(1, request.TicksPerBucket);
                var count = (int)Math.Clamp((request.DurationTicks + perBucket - 1) / perBucket, 0, Int32.MaxValue / 2);
                var buckets = new float[count];

                const double ticksPerMs = TimeSpan.TicksPerMillisecond;
                var startMs = request.SourceInTicks / ticksPerMs;
                var endMs = (request.SourceInTicks + (long)count * perBucket) / ticksPerMs;

                // one forward walk over the frames in range: frame times and bucket edges are both
                // ascending, so each frame lands in exactly one bucket without a search per bucket
                var times = motion.TimesMs;
                var speed = motion.Speed;
                var bucket = 0;
                for (var f = motion.FirstFrameAtOrAfter(startMs); f < times.Count && times[f] < endMs; f++)
                {
                    var ticks = (long)Math.Round(times[f] * ticksPerMs) - request.SourceInTicks;
                    var index = (int)Math.Clamp(ticks / perBucket, 0, count - 1);
                    if (index > bucket)
                        bucket = index;
                    if (speed[f] > buckets[bucket])
                        buckets[bucket] = speed[f];
                }

                List<CursorClickSpan> clicks = null;
                var all = motion.Clicks;
                for (var c = motion.FirstClickAtOrAfter(startMs); c < all.Count && all[c].DownMs < endMs; c++)
                {
                    (clicks ??= new List<CursorClickSpan>()).Add(new CursorClickSpan(
                        (long)Math.Round(all[c].DownMs * ticksPerMs), (long)Math.Round(all[c].UpMs * ticksPerMs)));
                }

                return new CursorActivity(request.SourceInTicks, perBucket, buckets, clicks, true);
            }
        }

        /// <summary>The keystroke runs intersecting a requested span, in source ticks, segmented
        /// by the request's own pause-break and filter. Last result kept for the unchanged case.</summary>
        internal sealed class KeyRunsCache
        {
            private KeyRunsRequest _request;
            private IReadOnlyList<KeyRunSpan> _spans;
            private KeyRuns _runs;

            public KeyRuns Project(in KeyRunsRequest request, string capturePath)
            {
                var spans = InputActivity.GetKeyRuns(capturePath, Math.Max(0, request.PauseBreakMs), request.Filter);
                if (_runs != null && ReferenceEquals(spans, _spans) && _request == request)
                    return _runs;

                _spans = spans;
                _request = request;
                _runs = Build(request, spans);
                return _runs;
            }

            /// <summary>The uncached projection, for tests.</summary>
            internal static KeyRuns Build(in KeyRunsRequest request, IReadOnlyList<KeyRunSpan> spans)
            {
                const double ticksPerMs = TimeSpan.TicksPerMillisecond;
                var startMs = request.SourceInTicks / ticksPerMs;
                var endMs = (request.SourceInTicks + request.DurationTicks) / ticksPerMs;

                List<TimelineKeyRun> runs = null;
                foreach (var span in spans)
                {
                    // runs never overlap and both bounds are monotonic, so the first one starting
                    // past the span ends the walk
                    if (span.StartMs >= endMs)
                        break;
                    if (span.EndMs < startMs)
                        continue;

                    (runs ??= new List<TimelineKeyRun>()).Add(new TimelineKeyRun(
                        (long)Math.Round(span.StartMs * ticksPerMs), (long)Math.Round(span.EndMs * ticksPerMs),
                        span.KeyCount));
                }

                return runs == null ? KeyRuns.None : new KeyRuns(runs, true);
            }
        }
    }
}
