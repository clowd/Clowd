using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>The cursor shape a capture frame reports. Mirrors the recorder's classification
    /// of the live HCURSOR against the stock <c>IDC_*</c> cursors; the wire strings are the
    /// lower-case member names.</summary>
    public enum CursorKind
    {
        Arrow,
        IBeam,
        Wait,
        Cross,
        UpArrow,
        SizeNWSE,
        SizeNESW,
        SizeWE,
        SizeNS,
        SizeAll,
        No,
        Hand,
        AppStarting,
        Help,
        Pen,
        Person,

        /// <summary>An HCURSOR matching no stock cursor — also what an unrecognized wire string
        /// parses to, so a newer recorder's kinds degrade to the drawing fallback rather than
        /// failing the row.</summary>
        Custom,

        /// <summary>CURSORINFO reported the cursor not showing; nothing is drawn.</summary>
        Hidden,
    }

    public enum InputEventKind
    {
        KeyDown,
        KeyUp,
        MouseDown,
        MouseUp,
    }

    /// <summary>One per-frame sample of the full input state: cursor hotspot position (physical
    /// px, virtual-desktop coords — the header's region space), held buttons, held keys and the
    /// cursor shape. Times are ms from the first encoded frame, pause-adjusted — the same
    /// timebase as the recording's PTS.</summary>
    public readonly struct InputFrame
    {
        public InputFrame(double timeMs, int x, int y, int buttons, IReadOnlyList<int> keys, CursorKind cursor,
            int spriteId = -1)
        {
            TimeMs = timeMs;
            X = x;
            Y = y;
            Buttons = buttons;
            Keys = keys;
            Cursor = cursor;
            SpriteId = spriteId;
        }

        public double TimeMs { get; }

        public int X { get; }

        public int Y { get; }

        /// <summary>Held mouse buttons: L=1 R=2 M=4 X1=8 X2=16.</summary>
        public int Buttons { get; }

        /// <summary>VK codes currently down, sorted. Never null.</summary>
        public IReadOnlyList<int> Keys { get; }

        public CursorKind Cursor { get; }

        /// <summary>Id of the native cursor sprite live on this frame
        /// (<see cref="InputCapture.TryGetSprite"/>), or -1 when none was captured — a hidden
        /// cursor, a degraded capture (cache cap, oversized skip) or a v1 file.</summary>
        public int SpriteId { get; }
    }

    /// <summary>
    /// One rasterized native cursor shape from a <c>cursor_image</c> row: the recorder's
    /// PNG-encoded snapshot of the live cursor, referenced by frame rows through
    /// <see cref="InputFrame.SpriteId"/>. <see cref="Mask"/> is the XOR plane of an inverting
    /// cursor — white pixels screen-invert, black pixels are a preserved no-op, transparent pixels
    /// do not apply (<see cref="Bmp"/> owns them) — or null for a plain alpha cursor.
    /// </summary>
    public sealed class CursorSprite
    {
        public CursorSprite(CursorKind kind, int width, int height, int hotX, int hotY,
            byte[] bmp, byte[] mask)
        {
            Kind = kind;
            Width = width;
            Height = height;
            HotX = hotX;
            HotY = hotY;
            Bmp = bmp;
            Mask = mask;
        }

        /// <summary>The shape's classified kind, recorded on the sprite row itself so sprite-level
        /// consumers (the inspector's native preview tile) need not scan frame rows for a
        /// reference.</summary>
        public CursorKind Kind { get; }

        /// <summary>Sprite width in native physical pixels.</summary>
        public int Width { get; }

        public int Height { get; }

        /// <summary>Hotspot within the sprite, in sprite pixels — the point the frame row's
        /// position pins to the screen.</summary>
        public int HotX { get; }

        public int HotY { get; }

        /// <summary>The colour/alpha pixels, PNG-encoded (straight alpha).</summary>
        public byte[] Bmp { get; }

        /// <summary>The XOR plane, PNG-encoded, or null when the cursor carries none.</summary>
        public byte[] Mask { get; }

        private readonly object _decodeSync = new object();
        private SKImage _bmpImage;
        private SKImage _maskImage;
        private bool _bmpDecoded;
        private bool _maskDecoded;

        /// <summary>The decoded <see cref="Bmp"/>, decoded once on first use and shared by every
        /// draw thereafter (an SKImage is immutable — callers must not dispose it). Null when the
        /// PNG fails to decode; the failure is cached too, so a corrupt sprite costs one probe,
        /// not one per composed frame.</summary>
        public SKImage GetBmpImage()
        {
            lock (_decodeSync)
            {
                if (!_bmpDecoded)
                {
                    _bmpDecoded = true;
                    _bmpImage = SKImage.FromEncodedData(Bmp);
                }
                return _bmpImage;
            }
        }

        /// <summary>The decoded <see cref="Mask"/>; same contract as <see cref="GetBmpImage"/>,
        /// and null when the sprite has no mask at all.</summary>
        public SKImage GetMaskImage()
        {
            lock (_decodeSync)
            {
                if (!_maskDecoded)
                {
                    _maskDecoded = true;
                    _maskImage = Mask == null ? null : SKImage.FromEncodedData(Mask);
                }
                return _maskImage;
            }
        }
    }

    /// <summary>One sub-frame-precise input edge from the recorder's low-level hooks, on the same
    /// timebase as the frames.</summary>
    public readonly struct InputEvent
    {
        public InputEvent(double timeMs, InputEventKind kind, int code, string ch, int x, int y)
        {
            TimeMs = timeMs;
            Kind = kind;
            Code = code;
            Char = ch;
            X = x;
            Y = y;
        }

        public double TimeMs { get; }

        public InputEventKind Kind { get; }

        /// <summary>The VK code for key events; the button bitmask value (1,2,4,8,16) for mouse
        /// events.</summary>
        public int Code { get; }

        /// <summary>Best-effort translated character for key-downs, or null for
        /// control/untranslatable keys and all other event kinds.</summary>
        public string Char { get; }

        /// <summary>Cursor position for mouse events; 0 for key events.</summary>
        public int X { get; }

        public int Y { get; }
    }

    /// <summary>One monitor from the capture header, physical px virtual-desktop coords.</summary>
    public readonly struct InputCaptureMonitor
    {
        public InputCaptureMonitor(int x, int y, int width, int height, double scale)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Scale = scale;
        }

        public int X { get; }

        public int Y { get; }

        public int Width { get; }

        public int Height { get; }

        /// <summary>The monitor's DPI scale (1.5 = 150%) — what sizes a themed cursor glyph.</summary>
        public double Scale { get; }
    }

    /// <summary>The capture file's header row. <see cref="Version"/> 0 means the file carried no
    /// (parseable) header — coordinates then have no region to map against, and drawing degrades
    /// to nothing.</summary>
    public sealed class InputCaptureHeader
    {
        public int Version { get; init; }

        /// <summary>Recording region in physical px, virtual-desktop coords — the space every
        /// frame/event coordinate is in.</summary>
        public int RegionX { get; init; }

        public int RegionY { get; init; }

        public int RegionWidth { get; init; }

        public int RegionHeight { get; init; }

        public int FpsNum { get; init; }

        public int FpsDen { get; init; }

        public string Platform { get; init; }

        public IReadOnlyList<InputCaptureMonitor> Monitors { get; init; } = Array.Empty<InputCaptureMonitor>();
    }

    /// <summary>
    /// A recording's parsed input-capture sidecar (see <c>Source.InputCapturePath</c>): the JSONL
    /// file the recorder writes, loaded into immutable time-sorted arrays with binary-search
    /// lookups. Parsing is forward-tolerant — unknown row types, unknown fields and malformed
    /// lines are skipped — and a missing or unreadable file loads as <see cref="Empty"/>, never a
    /// throw: input data is decoration, it must not block a project.
    ///
    /// Instances are immutable after load; <see cref="Get"/> is the process-wide cache the
    /// composer uses (same pattern as the frame composer's image cache — one parse per path,
    /// however many frames are composed).
    /// </summary>
    public sealed class InputCapture
    {
        /// <summary>The no-data instance: what a missing/corrupt file loads as, and what lookups
        /// on nothing return against.</summary>
        public static readonly InputCapture Empty =
            new InputCapture(new InputCaptureHeader(), Array.Empty<InputFrame>(), Array.Empty<InputEvent>(),
                new Dictionary<int, CursorSprite>());

        private readonly InputFrame[] _frames;
        private readonly InputEvent[] _events;
        private readonly Dictionary<int, CursorSprite> _sprites;
        private readonly bool[] _inactive;

        private InputCapture(InputCaptureHeader header, InputFrame[] frames, InputEvent[] events,
            Dictionary<int, CursorSprite> sprites)
        {
            Header = header;
            _frames = frames;
            _events = events;
            _sprites = sprites;
            _inactive = ComputeInactive(frames);
        }

        /// <summary>How far (physical px, Euclidean) the pointer may drift from where it hid
        /// before the debounce latch reads it as deliberate movement — sensor jitter and a
        /// nudged desk should not resurrect a typing-hidden cursor.</summary>
        internal const int DebounceMovePx = 3;

        /// <summary>
        /// The debounce latch behind <see cref="IsInactiveAt"/>, one forward pass at load. Windows
        /// hides the cursor while the user types and flashes it back on every pause, so the raw
        /// Hidden/visible transitions flicker; the latch turns them into "hidden until the pointer
        /// actually does something": a Hidden frame sets it (and anchors it at that position), and
        /// it clears only when the pointer moves more than <see cref="DebounceMovePx"/> from the
        /// anchor — cumulative, so a slow drift still counts — or a button changes against the
        /// previous frame.
        /// </summary>
        private static bool[] ComputeInactive(InputFrame[] frames)
        {
            var inactive = new bool[frames.Length];
            bool latched = false;
            int anchorX = 0, anchorY = 0;
            for (int i = 0; i < frames.Length; i++)
            {
                ref readonly var f = ref frames[i];
                if (f.Cursor == CursorKind.Hidden)
                {
                    latched = true;
                    anchorX = f.X;
                    anchorY = f.Y;
                }
                else if (latched)
                {
                    long dx = f.X - anchorX, dy = f.Y - anchorY;
                    if (dx * dx + dy * dy > DebounceMovePx * DebounceMovePx
                        || (i > 0 && f.Buttons != frames[i - 1].Buttons))
                        latched = false;
                }
                inactive[i] = latched;
            }
            return inactive;
        }

        public InputCaptureHeader Header { get; }

        /// <summary>All frame rows, sorted by time.</summary>
        public IReadOnlyList<InputFrame> Frames => _frames;

        /// <summary>All event rows, sorted by time.</summary>
        public IReadOnlyList<InputEvent> Events => _events;

        /// <summary>True when the file yielded no rows at all — the missing/corrupt degrade.</summary>
        public bool IsEmpty => _frames.Length == 0 && _events.Length == 0;

        /// <summary>Looks up a native cursor sprite by the id a frame row's
        /// <see cref="InputFrame.SpriteId"/> carries. False for -1 and for ids the file never
        /// defined (a torn <c>cursor_image</c> row — the frame degrades to no sprite).</summary>
        public bool TryGetSprite(int id, out CursorSprite sprite)
            => _sprites.TryGetValue(id, out sprite);

        /// <summary>
        /// A representative sprite for a static preview (the inspector's native style tile):
        /// prefers a mask-less <see cref="CursorKind.Arrow"/> — the shape a user pictures as "the
        /// cursor", drawable without the underlying pixels a mask needs — then the first mask-less
        /// sprite, then the first sprite at all, each in id (emission) order. False when the
        /// capture carries none (v1 files, degraded capture).
        /// </summary>
        public bool TryGetPreviewSprite(out CursorSprite sprite)
        {
            sprite = null;
            if (_sprites.Count == 0)
                return false;

            var ids = new int[_sprites.Count];
            _sprites.Keys.CopyTo(ids, 0);
            Array.Sort(ids);

            CursorSprite first = null, maskless = null;
            foreach (var id in ids)
            {
                var candidate = _sprites[id];
                first ??= candidate;
                if (candidate.Mask == null)
                {
                    if (candidate.Kind == CursorKind.Arrow)
                    {
                        sprite = candidate;
                        return true;
                    }
                    maskless ??= candidate;
                }
            }

            sprite = maskless ?? first;
            return true;
        }

        // ------------------------------------------------------------------------------- lookups

        /// <summary>The latest frame at or before <paramref name="timeMs"/>, or null before the
        /// first frame (and on <see cref="Empty"/>). A time gap — VFR, or simply between samples —
        /// holds the last frame, which is what keeps the cursor continuous across a recording
        /// pause (pause-adjusted times are contiguous, but nothing guarantees density).</summary>
        public InputFrame? FrameAt(double timeMs)
        {
            var index = LatestAtOrBefore(timeMs);
            return index < 0 ? null : _frames[index];
        }

        /// <summary>Whether the frame at <paramref name="index"/> (into <see cref="Frames"/>) sits
        /// in a debounced-hidden stretch: the cursor went Hidden at some earlier frame and has
        /// since neither moved more than <see cref="DebounceMovePx"/> from where it hid nor
        /// pressed/released a button, even if CURSORINFO briefly reports it showing again
        /// (Windows flashes the typing-hidden cursor back on every pause). True for every
        /// genuinely Hidden frame too, so a debouncing caller need only test this.</summary>
        public bool IsInactiveAt(int index)
            => index >= 0 && index < _inactive.Length && _inactive[index];

        /// <summary>The index into <see cref="Frames"/> of the latest frame at or before
        /// <paramref name="timeMs"/>, or -1. The index form of <see cref="FrameAt"/> for callers
        /// that walk neighbours.</summary>
        public int LatestAtOrBefore(double timeMs)
        {
            var lo = 0;
            var hi = _frames.Length - 1;
            var best = -1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (_frames[mid].TimeMs <= timeMs)
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

        /// <summary>The events in the half-open time range
        /// [<paramref name="startMs"/>, <paramref name="endMs"/>), as a zero-copy slice of
        /// <see cref="Events"/>. Empty when the range covers nothing.</summary>
        public ArraySegment<InputEvent> EventsBetween(double startMs, double endMs)
        {
            if (_events.Length == 0 || !(endMs > startMs))
                return ArraySegment<InputEvent>.Empty;

            var start = FirstEventAtOrAfter(startMs);
            var end = FirstEventAtOrAfter(endMs);
            return start >= end
                ? ArraySegment<InputEvent>.Empty
                : new ArraySegment<InputEvent>(_events, start, end - start);
        }

        /// <summary>The index of the first event at or after <paramref name="timeMs"/>
        /// (<c>Events.Count</c> when every event is earlier).</summary>
        public int FirstEventAtOrAfter(double timeMs)
        {
            var lo = 0;
            var hi = _events.Length;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (_events[mid].TimeMs < timeMs)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }

        // ------------------------------------------------------------------------------- loading

        /// <summary>
        /// Process-wide load cache, keyed by path. Loads (or returns <see cref="Empty"/> for)
        /// the file once per path — including caching the failure, so a bad path costs one probe,
        /// not one per composed frame. Deliberately unbounded: it holds at most the capture files
        /// of open projects.
        /// </summary>
        public static InputCapture Get(string path)
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
        private static readonly Dictionary<string, InputCapture> Cache
            = new Dictionary<string, InputCapture>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Uncached parse. Never throws: a missing or unreadable file returns
        /// <see cref="Empty"/>, and rows that fail to parse are skipped individually — everything
        /// readable is kept.</summary>
        public static InputCapture Load(string path)
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

        /// <summary>Parses capture JSONL from memory — <see cref="Load"/>'s body, split out so
        /// tests can feed synthetic bytes.</summary>
        public static InputCapture Parse(ReadOnlySpan<byte> bytes)
        {
            InputCaptureHeader header = null;
            var frames = new List<InputFrame>();
            var events = new List<InputEvent>();
            var sprites = new Dictionary<int, CursorSprite>();

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
                    ParseLine(line, ref header, frames, events, sprites);
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

            // rows are written in time order, but frame rows and hook-thread event rows
            // interleave on separate clocks close enough to cross — sort so the binary
            // searches hold.
            var frameArray = frames.ToArray();
            var eventArray = events.ToArray();
            Array.Sort(frameArray, (a, b) => a.TimeMs.CompareTo(b.TimeMs));
            Array.Sort(eventArray, (a, b) => a.TimeMs.CompareTo(b.TimeMs));

            if (header == null && frameArray.Length == 0 && eventArray.Length == 0 && sprites.Count == 0)
                return Empty;

            return new InputCapture(header ?? new InputCaptureHeader(), frameArray, eventArray, sprites);
        }

        private static void ParseLine(ReadOnlySpan<byte> line, ref InputCaptureHeader header,
            List<InputFrame> frames, List<InputEvent> events, Dictionary<int, CursorSprite> sprites)
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return;

            string type = null, platform = null, kind = null, ch = null, cursor = null;
            string bmp = null, mask = null;
            double t = 0;
            int x = 0, y = 0, buttons = 0, vk = 0, btn = 0;
            int ci = -1, id = -1, w = 0, h = 0, hotx = 0, hoty = 0;
            int version = 0, fpsNum = 0, fpsDen = 0;
            int[] region = null;
            IReadOnlyList<int> keys = Array.Empty<int>();
            List<InputCaptureMonitor> monitors = null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                // each branch fully consumes its value: unknown properties — and known ones
                // carrying an unexpected value shape — are skipped, not fatal.
                if (reader.ValueTextEquals("type"))
                    { if (Next(ref reader, JsonTokenType.String)) type = reader.GetString(); }
                else if (reader.ValueTextEquals("t"))
                    { if (Next(ref reader, JsonTokenType.Number)) t = reader.GetDouble(); }
                else if (reader.ValueTextEquals("x"))
                    { if (Next(ref reader, JsonTokenType.Number)) x = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("y"))
                    { if (Next(ref reader, JsonTokenType.Number)) y = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("b"))
                    { if (Next(ref reader, JsonTokenType.Number)) buttons = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("k"))
                    { if (Next(ref reader, JsonTokenType.StartArray)) keys = ReadIntArray(ref reader); }
                else if (reader.ValueTextEquals("c"))
                    { if (Next(ref reader, JsonTokenType.String)) cursor = reader.GetString(); }
                else if (reader.ValueTextEquals("kind"))
                    { if (Next(ref reader, JsonTokenType.String)) kind = reader.GetString(); }
                else if (reader.ValueTextEquals("vk"))
                    { if (Next(ref reader, JsonTokenType.Number)) vk = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("btn"))
                    { if (Next(ref reader, JsonTokenType.Number)) btn = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("ch"))
                    { if (Next(ref reader, JsonTokenType.String)) ch = reader.GetString(); }
                else if (reader.ValueTextEquals("ci"))
                    { if (Next(ref reader, JsonTokenType.Number)) ci = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("id"))
                    { if (Next(ref reader, JsonTokenType.Number)) id = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("w"))
                    { if (Next(ref reader, JsonTokenType.Number)) w = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("h"))
                    { if (Next(ref reader, JsonTokenType.Number)) h = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("hotx"))
                    { if (Next(ref reader, JsonTokenType.Number)) hotx = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("hoty"))
                    { if (Next(ref reader, JsonTokenType.Number)) hoty = (int)reader.GetDouble(); }
                else if (reader.ValueTextEquals("bmp"))
                    { if (Next(ref reader, JsonTokenType.String)) bmp = reader.GetString(); }
                else if (reader.ValueTextEquals("mask"))
                    // a JSON null (no XOR plane) fails the type check and leaves mask null —
                    // exactly the absent-field reading.
                    { if (Next(ref reader, JsonTokenType.String)) mask = reader.GetString(); }
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
                else if (reader.ValueTextEquals("monitors"))
                    { if (Next(ref reader, JsonTokenType.StartArray)) monitors = ReadMonitors(ref reader); }
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
                    header = new InputCaptureHeader
                    {
                        Version = version > 0 ? version : 1,
                        RegionX = region is { Length: >= 4 } ? region[0] : 0,
                        RegionY = region is { Length: >= 4 } ? region[1] : 0,
                        RegionWidth = region is { Length: >= 4 } ? region[2] : 0,
                        RegionHeight = region is { Length: >= 4 } ? region[3] : 0,
                        FpsNum = fpsNum,
                        FpsDen = fpsDen,
                        Platform = platform,
                        Monitors = (IReadOnlyList<InputCaptureMonitor>)monitors ?? Array.Empty<InputCaptureMonitor>(),
                    };
                    break;

                case "frame":
                    frames.Add(new InputFrame(t, x, y, buttons, keys, ParseCursorKind(cursor), ci));
                    break;

                case "cursor_image":
                    // Convert.FromBase64String throws FormatException on garbage, which the
                    // caller's per-line tolerance catches — the sprite is lost, the file survives.
                    // A duplicate id (malformed file) keeps the last definition.
                    if (id >= 0 && bmp != null)
                        sprites[id] = new CursorSprite(ParseCursorKind(kind), w, h, hotx, hoty,
                            Convert.FromBase64String(bmp),
                            mask == null ? null : Convert.FromBase64String(mask));
                    break;

                case "event":
                    switch (kind)
                    {
                        case "kd":
                            events.Add(new InputEvent(t, InputEventKind.KeyDown, vk, ch, 0, 0));
                            break;
                        case "ku":
                            events.Add(new InputEvent(t, InputEventKind.KeyUp, vk, null, 0, 0));
                            break;
                        case "md":
                            events.Add(new InputEvent(t, InputEventKind.MouseDown, btn, null, x, y));
                            break;
                        case "mu":
                            events.Add(new InputEvent(t, InputEventKind.MouseUp, btn, null, x, y));
                            break;
                            // an unknown event kind from a newer recorder is skipped.
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

        private static IReadOnlyList<int> ReadIntArray(ref Utf8JsonReader reader)
        {
            var values = ReadIntArrayRaw(ref reader);
            return values.Length == 0 ? Array.Empty<int>() : values;
        }

        private static List<InputCaptureMonitor> ReadMonitors(ref Utf8JsonReader reader)
        {
            var monitors = new List<InputCaptureMonitor>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                int x = 0, y = 0, w = 0, h = 0;
                double scale = 1.0;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("x"))
                        { if (Next(ref reader, JsonTokenType.Number)) x = (int)reader.GetDouble(); }
                    else if (reader.ValueTextEquals("y"))
                        { if (Next(ref reader, JsonTokenType.Number)) y = (int)reader.GetDouble(); }
                    else if (reader.ValueTextEquals("w"))
                        { if (Next(ref reader, JsonTokenType.Number)) w = (int)reader.GetDouble(); }
                    else if (reader.ValueTextEquals("h"))
                        { if (Next(ref reader, JsonTokenType.Number)) h = (int)reader.GetDouble(); }
                    else if (reader.ValueTextEquals("scale"))
                        { if (Next(ref reader, JsonTokenType.Number)) scale = reader.GetDouble(); }
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }
                monitors.Add(new InputCaptureMonitor(x, y, w, h, scale));
            }
            return monitors;
        }

        /// <summary>Wire string → <see cref="CursorKind"/>. Unrecognized (or absent) strings map
        /// to <see cref="CursorKind.Custom"/>, which draws as the theme's arrow.</summary>
        public static CursorKind ParseCursorKind(string value) => value switch
        {
            "arrow" => CursorKind.Arrow,
            "ibeam" => CursorKind.IBeam,
            "wait" => CursorKind.Wait,
            "cross" => CursorKind.Cross,
            "uparrow" => CursorKind.UpArrow,
            "sizenwse" => CursorKind.SizeNWSE,
            "sizenesw" => CursorKind.SizeNESW,
            "sizewe" => CursorKind.SizeWE,
            "sizens" => CursorKind.SizeNS,
            "sizeall" => CursorKind.SizeAll,
            "no" => CursorKind.No,
            "hand" => CursorKind.Hand,
            "appstarting" => CursorKind.AppStarting,
            "help" => CursorKind.Help,
            "pen" => CursorKind.Pen,
            "person" => CursorKind.Person,
            "hidden" => CursorKind.Hidden,
            _ => CursorKind.Custom,
        };
    }
}
