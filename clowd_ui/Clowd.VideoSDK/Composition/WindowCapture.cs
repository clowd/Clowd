using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>One window's rect at one instant, in the sidecar's canvas pixels, relative to the
    /// capture region's top-left. Deliberately NOT clipped to the region: a window straddling an
    /// edge reports a negative <see cref="X"/>/<see cref="Y"/>, and one larger than the region
    /// reports an extent past it. The consumer clamps.</summary>
    public readonly struct WindowFrame
    {
        public WindowFrame(double timeMs, int x, int y, int width, int height, int z = 0)
        {
            TimeMs = timeMs;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Z = z;
        }

        /// <summary>Milliseconds from the recording's first frame, minus paused time — the same
        /// clock, with the same origin, as <see cref="InputFrame.TimeMs"/> (the recorder maps both
        /// through one function and arms both in the same libobs tick). Not strictly increasing:
        /// two polls between video ticks share a stamp, and a resume can step it back a hair.</summary>
        public double TimeMs { get; }

        public int X { get; }

        public int Y { get; }

        public int Width { get; }

        public int Height { get; }

        /// <summary>The window's depth at this instant: 0 is the topmost window, and the recorder
        /// numbers the tracked windows contiguously from there, re-stating every row whose depth
        /// moved. Only the occlusion pass reads it; the crop math does not care what is in
        /// front.</summary>
        public int Z { get; }
    }

    /// <summary>One window's identity, from the sidecar's <c>window_info</c> rows. The latest row
    /// for an id wins: the recorder re-states these whenever a title or application name changes,
    /// and re-announces every tracked window after a failed write.</summary>
    public sealed class WindowInfo
    {
        public int Id { get; init; }

        /// <summary>The window title. Empty is normal on macOS without the Screen Recording
        /// permission, where the recorder deliberately does not backfill it from <see cref="App"/>
        /// so the permission failure stays visible rather than hiding.</summary>
        public string Title { get; init; } = "";

        /// <summary>Windows: the owning executable's file name. macOS: the application's display
        /// name. Empty when the owner could not be resolved.</summary>
        public string App { get; init; } = "";

        public int Pid { get; init; }

        /// <summary>The time of this window's first visible rect — the list's sort key, so the
        /// editor offers windows in the order the recording met them.</summary>
        public double FirstSeenMs { get; init; }

        /// <summary>True when this window was actually on screen — not merely inside the region —
        /// at some sampled instant: some part of it was not covered by a window above it. False
        /// means it spent the whole recording completely behind other windows and contributed no
        /// pixel to the video. Always true when the file's header is lost, since the region size
        /// occlusion is judged against is then unknown.</summary>
        public bool EverVisible { get; init; }
    }

    /// <summary>The sidecar's header row. <see cref="Version"/> 0 means the file carried no
    /// parseable header, which is also what a 0-byte file reads as.</summary>
    public sealed class WindowCaptureHeader
    {
        public int Version { get; init; }

        /// <summary>The capture region's origin in canvas pixels. Present ONLY so a rect can be
        /// put back on the desktop. Window rows are already region-relative, so a consumer mapping
        /// one onto the encoded frame must NOT subtract this. (The input-capture sidecar is the
        /// other way round — its rows are virtual-desktop absolute and the cursor path does
        /// subtract its origin at <c>FrameComposer.DrawDefaultCursorOverlay</c>. Confusing the two
        /// is the single easiest bug to write here.)</summary>
        public int RegionX { get; init; }

        public int RegionY { get; init; }

        /// <summary>The base canvas the rows are measured against. Equal to the encoded video's
        /// dimensions unless the recorder was given a max width or height, which Clowd never
        /// passes; divide by it anyway, since the canvas is also force-rounded to an even
        /// size.</summary>
        public int RegionWidth { get; init; }

        public int RegionHeight { get; init; }

        public int FpsNum { get; init; }

        public int FpsDen { get; init; }

        /// <summary><c>"windows"</c> or <c>"macos"</c>; null on a lost header.</summary>
        public string Platform { get; init; }
    }

    /// <summary>
    /// A recording's parsed window-capture sidecar (see <c>Source.WindowCapturePath</c>): the
    /// JSONL file the recorder writes alongside the input-capture one, holding the live geometry
    /// of every window that intersected the capture region, loaded into immutable per-window
    /// time-sorted arrays with binary-search lookups. Parsing is forward-tolerant — unknown row
    /// types, unknown fields and malformed lines are skipped — and a missing or unreadable file
    /// loads as <see cref="Empty"/>, never a throw: a window-following crop is a convenience, it
    /// must not block a project.
    ///
    /// Instances are immutable after load; <see cref="Get"/> is the process-wide cache the
    /// composer uses, the same pattern as <see cref="InputCapture.Get"/>.
    /// </summary>
    public sealed class WindowCapture
    {
        /// <summary>The no-data instance: what a missing, empty or corrupt file loads as.</summary>
        public static readonly WindowCapture Empty =
            new WindowCapture(new WindowCaptureHeader(), Array.Empty<WindowInfo>(),
                new Dictionary<int, WindowInfo>(), new Dictionary<int, WindowFrame[]>());

        private static readonly WindowFrame[] NoFrames = Array.Empty<WindowFrame>();

        private readonly WindowInfo[] _windows;
        private readonly Dictionary<int, WindowInfo> _infos;
        private readonly Dictionary<int, WindowFrame[]> _frames;

        private WindowCapture(WindowCaptureHeader header, WindowInfo[] windows,
            Dictionary<int, WindowInfo> infos, Dictionary<int, WindowFrame[]> frames)
        {
            Header = header;
            _windows = windows;
            _infos = infos;
            _frames = frames;
        }

        public WindowCaptureHeader Header { get; }

        /// <summary>Every window that ever held geometry inside the region, ordered by when it
        /// was first seen and then by id. This is the editor's pick list; the composer never needs
        /// it. Nothing is dropped here — the recorder skips only its own process, so Clowd's own
        /// windows ARE in this list, and dropping them is a fact about this application rather
        /// than about the file format. Windows that were on screen only in the geometric sense
        /// carry <see cref="WindowInfo.EverVisible"/> false rather than going missing, so a
        /// project that already follows one still resolves it.</summary>
        public IReadOnlyList<WindowInfo> Windows => _windows;

        /// <summary>True when the file yielded no window at all: the missing/corrupt degrade, and
        /// equally a healthy recording of a region no window intersected.</summary>
        public bool IsEmpty => _windows.Length == 0;

        public bool TryGetWindow(int windowId, out WindowInfo info)
            => _infos.TryGetValue(windowId, out info);

        // ------------------------------------------------------------------------------- lookups

        /// <summary>This window's rows, time-sorted, or an empty list for an unknown id.</summary>
        public IReadOnlyList<WindowFrame> FramesOf(int windowId)
            => _frames.TryGetValue(windowId, out var frames) ? frames : NoFrames;

        /// <summary>The index into <see cref="FramesOf"/> of the latest row at or before
        /// <paramref name="timeMs"/>, or -1 when the time precedes every row.</summary>
        public int LatestAtOrBefore(int windowId, double timeMs)
        {
            if (!_frames.TryGetValue(windowId, out var frames))
                return -1;

            var lo = 0;
            var hi = frames.Length - 1;
            var best = -1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (frames[mid].TimeMs <= timeMs)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return best;
        }

        /// <summary>
        /// The rect to frame this window with at <paramref name="timeMs"/>: the row in force, i.e.
        /// the latest at or before the time, and the first row when the time precedes them all.
        /// Defined for the whole recording whenever the window ever held geometry, and false only
        /// for an id this file never carried — the one case a caller must have a policy for.
        /// Deliberately raw: the recorder's own numbers reach the crop with nothing in between.
        ///
        /// Holding forwards and backwards is deliberate. A window that has not opened yet, or one
        /// that is minimized, must leave the framing exactly where it was: collapsing the crop
        /// would push the insets past 1, <c>PictureMapping.TryMap</c> would return false, and the
        /// item, its surround AND its cursor overlay would all vanish for those frames.
        /// </summary>
        public bool TryFrameAt(int windowId, double timeMs, out WindowFrame frame)
        {
            if (!_frames.TryGetValue(windowId, out var frames) || frames.Length == 0)
            {
                frame = default;
                return false;
            }

            var index = LatestAtOrBefore(windowId, timeMs);
            frame = frames[index < 0 ? 0 : index];
            return true;
        }

        // ------------------------------------------------------------------------------- loading

        /// <summary>Process-wide load cache, keyed by path; caches the failure too, so a bad path
        /// costs one probe rather than one per composed frame. Deliberately unbounded, like
        /// <see cref="InputCapture.Get"/>: it holds at most the sidecars of open projects.</summary>
        public static WindowCapture Get(string path)
        {
            if (string.IsNullOrEmpty(path))
                return Empty;

            lock (CacheSync)
            {
                if (Cache.TryGetValue(path, out var cached))
                    return cached;

                var loaded = Load(path);
                Cache[path] = loaded;
                return loaded;
            }
        }

        private static readonly object CacheSync = new object();
        private static readonly Dictionary<string, WindowCapture> Cache
            = new Dictionary<string, WindowCapture>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Uncached load. Never throws: a missing or unreadable file returns
        /// <see cref="Empty"/>, and rows that fail to parse are skipped individually.</summary>
        public static WindowCapture Load(string path)
        {
            byte[] bytes;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return Empty;
                bytes = File.ReadAllBytes(path);
            }
            catch
            {
                return Empty;
            }

            return Parse(bytes);
        }

        /// <summary>Parses window-capture JSONL from memory — <see cref="Load"/>'s body, split out
        /// so tests can feed synthetic bytes.</summary>
        public static WindowCapture Parse(ReadOnlySpan<byte> bytes)
        {
            WindowCaptureHeader header = null;
            var infos = new Dictionary<int, WindowInfo>();
            var rows = new Dictionary<int, List<WindowFrame>>();

            while (!bytes.IsEmpty)
            {
                var newline = bytes.IndexOf((byte)'\n');
                var line = newline < 0 ? bytes : bytes.Slice(0, newline);
                bytes = newline < 0 ? ReadOnlySpan<byte>.Empty : bytes.Slice(newline + 1);

                line = line.TrimEnd((byte)'\r');
                if (line.IsEmpty)
                    continue;

                try
                {
                    ParseLine(line, ref header, infos, rows);
                }
                catch (JsonException)
                {
                    // a torn last line (recorder killed mid-write) or garbage row: skip it,
                    // keep what parsed.
                }
                catch (InvalidOperationException)
                {
                    // a field with an unexpected JSON type — same forward tolerance.
                }
                catch (FormatException)
                {
                    // a numeric field that does not fit the requested representation.
                }
            }

            // rows are written in time order, but `t` is not strictly monotonic — two polls
            // between video ticks share a stamp, and a resume can step it back a hair — so sort
            // per window so the binary searches hold.
            foreach (var list in rows.Values)
                list.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));

            // `rows` still holds the recorder's zero-rect "left the region" sentinels, which the
            // occlusion pass needs so a window that closed stops covering the ones behind it. The
            // lookup arrays drop them: that is what makes hold-last mean "hold the last real rect
            // through an absence", and it keeps a zero-extent rect out of the crop math.
            var frames = new Dictionary<int, WindowFrame[]>(rows.Count);
            foreach (var (id, list) in rows)
            {
                var real = list.FindAll(f => f.Width > 0 && f.Height > 0);
                if (real.Count > 0)
                    frames[id] = real.ToArray();
            }

            var everVisible = ComputeEverVisible(header, rows, frames);

            // the pick list is the set of windows that were ever actually on screen: an identity
            // row with no geometry (a window the recorder announced but that never entered the
            // region) is dropped, and geometry with no identity row — reachable through a torn
            // write — gets a blank one so it stays pickable.
            var windows = new List<WindowInfo>(frames.Count);
            var known = new Dictionary<int, WindowInfo>(frames.Count);
            foreach (var (id, arr) in frames)
            {
                var info = infos.TryGetValue(id, out var announced)
                    ? new WindowInfo
                    {
                        Id = id,
                        Title = announced.Title,
                        App = announced.App,
                        Pid = announced.Pid,
                        FirstSeenMs = arr[0].TimeMs,
                        EverVisible = everVisible.Contains(id),
                    }
                    : new WindowInfo
                    {
                        Id = id, Title = "", App = "", Pid = 0, FirstSeenMs = arr[0].TimeMs,
                        EverVisible = everVisible.Contains(id),
                    };
                windows.Add(info);
                known[id] = info;
            }

            var windowArray = windows.ToArray();
            Array.Sort(windowArray, (a, b) =>
            {
                int byTime = a.FirstSeenMs.CompareTo(b.FirstSeenMs);
                return byTime != 0 ? byTime : a.Id.CompareTo(b.Id);
            });

            if (header == null && windowArray.Length == 0)
                return Empty;

            return new WindowCapture(header ?? new WindowCaptureHeader(), windowArray, known, frames);
        }

        // -------------------------------------------------------------------------- occlusion

        /// <summary>
        /// The ids a viewer could actually see at some point: at one sampled instant, part of the
        /// window's rect inside the capture region was not covered by a window above it. A window
        /// that failed this spent the whole recording completely behind other windows, so it put no
        /// pixel in the video and framing a crop on it would only show whatever was covering it.
        ///
        /// Judged off the recorder's z — 0 is topmost, contiguous per sample — re-evaluated at
        /// every distinct row time, holding each window's last row between its own rows because the
        /// recorder writes a row only when something moved. A zero-rect sentinel means the window
        /// left the region, so it covers nothing from then on. Without a header there is no region
        /// to judge against and every window passes.
        /// </summary>
        private static HashSet<int> ComputeEverVisible(WindowCaptureHeader header,
            Dictionary<int, List<WindowFrame>> rows, Dictionary<int, WindowFrame[]> frames)
        {
            var visible = new HashSet<int>();
            var regionWidth = header?.RegionWidth ?? 0;
            var regionHeight = header?.RegionHeight ?? 0;
            if (regionWidth <= 0 || regionHeight <= 0)
            {
                foreach (var id in frames.Keys)
                    visible.Add(id);
                return visible;
            }

            var ids = new int[rows.Count];
            rows.Keys.CopyTo(ids, 0);
            Array.Sort(ids);

            var times = new List<double>();
            foreach (var list in rows.Values)
            {
                foreach (var frame in list)
                    times.Add(frame.TimeMs);
            }
            times.Sort();

            // -1 is "this window has no row yet", which is not the same as a sentinel: it has
            // never been in the region, so it neither shows nor covers.
            var cursor = new int[ids.Length];
            for (var i = 0; i < cursor.Length; i++)
                cursor[i] = -1;

            var region = new RectI(0, 0, regionWidth, regionHeight);
            var live = new List<(int Z, int Id, RectI Rect)>(ids.Length);
            var above = new List<RectI>(ids.Length);
            var pending = new List<RectI>();
            var next = new List<RectI>();

            for (var ti = 0; ti < times.Count; ti++)
            {
                if (ti > 0 && times[ti] == times[ti - 1])
                    continue;
                var time = times[ti];

                live.Clear();
                for (var i = 0; i < ids.Length; i++)
                {
                    var list = rows[ids[i]];
                    var index = cursor[i];
                    while (index + 1 < list.Count && list[index + 1].TimeMs <= time)
                        index++;
                    cursor[i] = index;
                    if (index < 0)
                        continue;

                    var frame = list[index];
                    if (frame.Width <= 0 || frame.Height <= 0)
                        continue;

                    var rect = Intersect(new RectI(frame.X, frame.Y, frame.Width, frame.Height), region);
                    if (rect.W > 0 && rect.H > 0)
                        live.Add((frame.Z, ids[i], rect));
                }

                // topmost first, so a window's occluders are exactly the entries before it. A tie
                // (a torn row, or a z the recorder had not restated yet) falls back to the id so
                // the order stays total and no two windows can occlude each other.
                live.Sort((a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.Id.CompareTo(b.Id));

                for (var i = 0; i < live.Count; i++)
                {
                    if (visible.Contains(live[i].Id))
                        continue;

                    above.Clear();
                    for (var j = 0; j < i; j++)
                        above.Add(live[j].Rect);
                    if (!IsCovered(live[i].Rect, above, pending, next))
                        visible.Add(live[i].Id);
                }

                if (visible.Count >= ids.Length)
                    break;
            }

            // sentinel-only windows have no lookup array, so keep the two sets in step.
            visible.IntersectWith(frames.Keys);
            return visible;
        }

        /// <summary>Whether <paramref name="target"/> lies entirely inside the union of
        /// <paramref name="occluders"/>: subtract each occluder in turn, keeping what is left as
        /// disjoint rectangles, and covered means nothing survives. Exact rather than sampled — a
        /// one-pixel sliver is the difference between a window being on screen and not — and the
        /// two scratch lists are the caller's so a long recording does not allocate per row.
        /// </summary>
        private static bool IsCovered(RectI target, List<RectI> occluders, List<RectI> pending,
            List<RectI> next)
        {
            pending.Clear();
            pending.Add(target);
            foreach (var occluder in occluders)
            {
                next.Clear();
                foreach (var rect in pending)
                    Subtract(rect, occluder, next);

                pending.Clear();
                pending.AddRange(next);
                if (pending.Count == 0)
                    return true;
            }

            return pending.Count == 0;
        }

        /// <summary>Appends the parts of <paramref name="rect"/> that <paramref name="cut"/> does
        /// not cover — up to four strips, the full-width bands above and below the overlap and the
        /// two side pieces beside it — to <paramref name="into"/>.</summary>
        private static void Subtract(RectI rect, RectI cut, List<RectI> into)
        {
            var overlap = Intersect(rect, cut);
            if (overlap.W <= 0 || overlap.H <= 0)
            {
                into.Add(rect);
                return;
            }

            if (overlap.Y > rect.Y)
                into.Add(new RectI(rect.X, rect.Y, rect.W, overlap.Y - rect.Y));
            if (overlap.Y + overlap.H < rect.Y + rect.H)
                into.Add(new RectI(rect.X, overlap.Y + overlap.H, rect.W,
                    rect.Y + rect.H - (overlap.Y + overlap.H)));
            if (overlap.X > rect.X)
                into.Add(new RectI(rect.X, overlap.Y, overlap.X - rect.X, overlap.H));
            if (overlap.X + overlap.W < rect.X + rect.W)
                into.Add(new RectI(overlap.X + overlap.W, overlap.Y,
                    rect.X + rect.W - (overlap.X + overlap.W), overlap.H));
        }

        private static RectI Intersect(RectI a, RectI b)
        {
            var x = Math.Max(a.X, b.X);
            var y = Math.Max(a.Y, b.Y);
            var right = Math.Min(a.X + a.W, b.X + b.W);
            var bottom = Math.Min(a.Y + a.H, b.Y + b.H);
            return new RectI(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
        }

        /// <summary>An integer rectangle in region-relative pixels, for the occlusion pass only.
        /// Deliberately not one of the drawing types: nothing in this file is a Skia concept and it
        /// stays that way.</summary>
        private readonly struct RectI
        {
            public RectI(int x, int y, int w, int h)
            {
                X = x;
                Y = y;
                W = w;
                H = h;
            }

            public int X { get; }

            public int Y { get; }

            public int W { get; }

            public int H { get; }
        }

        private static void ParseLine(ReadOnlySpan<byte> line, ref WindowCaptureHeader header,
            Dictionary<int, WindowInfo> infos, Dictionary<int, List<WindowFrame>> rows)
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return;

            string type = null, platform = null, title = null, app = null;
            double t = 0;
            int id = 0, x = 0, y = 0, w = 0, h = 0, z = 0, pid = 0;
            int version = 0, fpsNum = 0, fpsDen = 0;
            int[] region = null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                // each branch fully consumes its value: unknown properties — and known ones
                // carrying an unexpected value shape — are skipped, not fatal.
                if (reader.ValueTextEquals("type"))
                    { if (Next(ref reader, JsonTokenType.String)) type = reader.GetString(); }
                else if (reader.ValueTextEquals("t"))
                    { if (Next(ref reader, JsonTokenType.Number)) t = reader.GetDouble(); }
                else if (reader.ValueTextEquals("id"))
                    { if (Next(ref reader, JsonTokenType.Number)) id = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("x"))
                    { if (Next(ref reader, JsonTokenType.Number)) x = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("y"))
                    { if (Next(ref reader, JsonTokenType.Number)) y = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("w"))
                    { if (Next(ref reader, JsonTokenType.Number)) w = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("h"))
                    { if (Next(ref reader, JsonTokenType.Number)) h = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("z"))
                    { if (Next(ref reader, JsonTokenType.Number)) z = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("title"))
                    { if (Next(ref reader, JsonTokenType.String)) title = reader.GetString(); }
                else if (reader.ValueTextEquals("app"))
                    { if (Next(ref reader, JsonTokenType.String)) app = reader.GetString(); }
                else if (reader.ValueTextEquals("pid"))
                    { if (Next(ref reader, JsonTokenType.Number)) pid = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("version"))
                    { if (Next(ref reader, JsonTokenType.Number)) version = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("fps_num"))
                    { if (Next(ref reader, JsonTokenType.Number)) fpsNum = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("fps_den"))
                    { if (Next(ref reader, JsonTokenType.Number)) fpsDen = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("platform"))
                    { if (Next(ref reader, JsonTokenType.String)) platform = reader.GetString(); }
                else if (reader.ValueTextEquals("region"))
                    { if (Next(ref reader, JsonTokenType.StartArray)) region = ReadIntArrayRaw(ref reader); }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            switch (type)
            {
                case "header":
                    // last header wins, which cannot happen in a well-formed file anyway.
                    header = new WindowCaptureHeader
                    {
                        Version = version > 0 ? version : 1,
                        RegionX = region is { Length: >= 4 } ? region[0] : 0,
                        RegionY = region is { Length: >= 4 } ? region[1] : 0,
                        RegionWidth = region is { Length: >= 4 } ? region[2] : 0,
                        RegionHeight = region is { Length: >= 4 } ? region[3] : 0,
                        FpsNum = fpsNum,
                        FpsDen = fpsDen,
                        Platform = platform,
                    };
                    break;

                case "window_info":
                    // latest wins: a retitle re-states the row under the same id, and the
                    // re-announcement after a failed write simply overwrites.
                    if (id >= 1)
                        infos[id] = new WindowInfo { Id = id, Title = title ?? "", App = app ?? "", Pid = pid };
                    break;

                case "window":
                    // the all-zero rect is the recorder's "this window left the region" sentinel
                    // and carries no geometry. It is kept in the raw rows because the occlusion
                    // pass has to see a window stop covering things, and dropped in Parse before
                    // anything else can read it.
                    if (id >= 1)
                    {
                        if (!rows.TryGetValue(id, out var list))
                            rows[id] = list = new List<WindowFrame>();
                        list.Add(new WindowFrame(t, x, y, w, h, z));
                    }
                    break;

                    // an unknown row type from a newer recorder is skipped.
            }
        }

        /// <summary>Advances onto the property's value and reports whether it is the expected
        /// token type; a mismatch (a newer recorder changed a field's shape) skips the value so
        /// the rest of the row still parses.</summary>
        private static bool Next(ref Utf8JsonReader reader, JsonTokenType expected)
        {
            if (!reader.Read())
                return false;
            if (reader.TokenType == expected)
                return true;

            reader.Skip();
            return false;
        }

        private static int[] ReadIntArrayRaw(ref Utf8JsonReader reader)
        {
            var values = new List<int>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.Number)
                    values.Add((int)reader.GetDouble());
                else
                    reader.Skip();
            }
            return values.ToArray();
        }
    }
}
