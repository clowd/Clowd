using System;
using System.Collections.Generic;
using System.Globalization;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>One mouse button press, from its down to the matching up (the down's own time
    /// when the capture ended before the release). Times are capture ms — the
    /// <see cref="InputCapture"/> timebase.</summary>
    public readonly record struct CursorClick(double DownMs, double UpMs, int Button);

    /// <summary>One keystroke run — the unit the keyboard overlay shows as a row — reduced to what
    /// a timeline preview draws: its span, how many keys it carries and whether it is a chord.
    /// Times are capture ms.</summary>
    public readonly record struct KeyRunSpan(double StartMs, double EndMs, int KeyCount, bool IsChord);

    /// <summary>
    /// How fast the pointer was moving, frame by frame, over one recording — the cursor row's
    /// "waveform". <see cref="Speed"/>[i] is the speed that carried the pointer INTO frame i from
    /// frame i-1 (frame 0 is 0), normalized to <c>[0, 1]</c> by <see cref="Normalize"/>; the
    /// matching <see cref="TimesMs"/> are the capture's own frame times, so a consumer buckets by
    /// walking both in step. Plus every button press in <see cref="Clicks"/>. Immutable.
    /// </summary>
    public sealed class CursorMotion
    {
        public static readonly CursorMotion Empty =
            new CursorMotion(Array.Empty<double>(), Array.Empty<float>(), Array.Empty<CursorClick>());

        /// <summary>
        /// The speed, in screen diagonals per second, that <see cref="Normalize"/> maps to one
        /// half of full scale. A diagonal a second is an unhurried sweep across the screen; a flick
        /// runs four or five times that and a careful approach to a button a tenth. Measuring in
        /// diagonals rather than pixels keeps a 4K recording and a laptop's looking alike.
        /// </summary>
        public const double HalfScaleDiagonalsPerSecond = 1.0;

        private readonly double[] _timesMs;
        private readonly float[] _speed;

        public CursorMotion(double[] timesMs, float[] speed, IReadOnlyList<CursorClick> clicks)
        {
            ArgumentNullException.ThrowIfNull(timesMs);
            ArgumentNullException.ThrowIfNull(speed);
            if (timesMs.Length != speed.Length)
                throw new ArgumentException("one speed per frame time", nameof(speed));

            _timesMs = timesMs;
            _speed = speed;
            Clicks = clicks ?? Array.Empty<CursorClick>();
        }

        /// <summary>Frame times, ascending.</summary>
        public IReadOnlyList<double> TimesMs => _timesMs;

        /// <summary>Normalized speed per frame, <c>[0, 1]</c>.</summary>
        public IReadOnlyList<float> Speed => _speed;

        /// <summary>Every button press, ascending by <see cref="CursorClick.DownMs"/>.</summary>
        public IReadOnlyList<CursorClick> Clicks { get; }

        public int FrameCount => _timesMs.Length;

        public bool IsEmpty => _timesMs.Length == 0 && Clicks.Count == 0;

        /// <summary>Index of the first frame at or after <paramref name="timeMs"/>
        /// (<see cref="FrameCount"/> when every frame is earlier).</summary>
        public int FirstFrameAtOrAfter(double timeMs)
        {
            var lo = 0;
            var hi = _timesMs.Length;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (_timesMs[mid] < timeMs)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }

        /// <summary>Index of the first click whose down is at or after <paramref name="timeMs"/>
        /// (<c>Clicks.Count</c> when every click is earlier).</summary>
        public int FirstClickAtOrAfter(double timeMs)
        {
            var lo = 0;
            var hi = Clicks.Count;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (Clicks[mid].DownMs < timeMs)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// Pointer speed to <c>[0, 1]</c>: <c>v / (v + half)</c>, a soft knee that keeps both a
        /// careful nudge and a flick visible on a row twenty pixels tall — a linear scale would
        /// have to clip the flicks or flatten everything else.
        /// </summary>
        public static float Normalize(double diagonalsPerSecond)
        {
            if (!(diagonalsPerSecond > 0))
                return 0;
            return (float)(diagonalsPerSecond / (diagonalsPerSecond + HalfScaleDiagonalsPerSecond));
        }
    }

    /// <summary>
    /// The timeline's view of a recording's input capture: the pointer's speed and clicks for
    /// the cursor row, the keystroke runs for the keys row. Built once per capture file (and, for
    /// runs, per segmentation setting) and cached process-wide like <see cref="InputCapture.Get"/>
    /// itself — immutable results, failures included: a missing file yields empty activity, never
    /// a throw. Reading the capture is the expensive part; callers that must not stall do that
    /// off their UI thread and cache the result themselves.
    /// </summary>
    public static class InputActivity
    {
        private static readonly object CacheSync = new object();

        private static readonly Dictionary<string, CursorMotion> MotionCache =
            new Dictionary<string, CursorMotion>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, IReadOnlyList<KeyRunSpan>> RunCache =
            new Dictionary<string, IReadOnlyList<KeyRunSpan>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The pointer motion of the capture at <paramref name="capturePath"/>;
        /// <see cref="CursorMotion.Empty"/> for a missing or empty file.</summary>
        public static CursorMotion GetCursorMotion(string capturePath)
        {
            if (string.IsNullOrEmpty(capturePath))
                return CursorMotion.Empty;

            lock (CacheSync)
            {
                if (MotionCache.TryGetValue(capturePath, out var cached))
                    return cached;
            }

            // the parse runs outside the lock: it can take a while on a long recording, and a
            // concurrent GetKeyRuns for the same file should not queue behind it (InputCapture.Get
            // serializes the two anyway, but a second motion request for another file need not wait).
            var motion = ComputeCursorMotion(InputCapture.Get(capturePath));
            lock (CacheSync)
            {
                if (!MotionCache.TryGetValue(capturePath, out var cached))
                    MotionCache[capturePath] = cached = motion;
                return cached;
            }
        }

        /// <summary>
        /// The keystroke runs of the capture at <paramref name="capturePath"/>, segmented exactly
        /// as the keyboard overlay shows them — same pause-break and filter (see
        /// <c>KeyboardLayout.Segment</c>) — so each span here is one row on the output.
        /// </summary>
        public static IReadOnlyList<KeyRunSpan> GetKeyRuns(string capturePath, int pauseBreakMs,
            KeystrokeFilter filter = KeystrokeFilter.None)
        {
            if (string.IsNullOrEmpty(capturePath))
                return Array.Empty<KeyRunSpan>();

            var key = pauseBreakMs.ToString(CultureInfo.InvariantCulture) + "|" + (int)filter + "|" + capturePath;
            lock (CacheSync)
            {
                if (RunCache.TryGetValue(key, out var cached))
                    return cached;
            }

            var spans = ToSpans(KeyboardLayout.GetRuns(capturePath, pauseBreakMs, filter));
            lock (CacheSync)
            {
                if (!RunCache.TryGetValue(key, out var cached))
                    RunCache[key] = cached = spans;
                return cached;
            }
        }

        /// <summary>
        /// The uncached computation behind <see cref="GetCursorMotion"/>. Speed is the distance
        /// between consecutive frames over their time gap, in screen diagonals per second (the
        /// header's region; with no header, the box the pointer actually covered), through
        /// <see cref="CursorMotion.Normalize"/>. Frames sharing a timestamp contribute no speed.
        /// Clicks pair each button's down with its next up; a down the capture never released
        /// closes on itself.
        /// </summary>
        public static CursorMotion ComputeCursorMotion(InputCapture capture)
        {
            if (capture == null || capture.IsEmpty)
                return CursorMotion.Empty;

            var frames = capture.Frames;
            var times = new double[frames.Count];
            var speed = new float[frames.Count];
            var diagonal = DiagonalOf(capture);

            for (var i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                times[i] = f.TimeMs;
                if (i == 0)
                    continue;

                var prev = frames[i - 1];
                var dtMs = f.TimeMs - prev.TimeMs;
                if (!(dtMs > 0))
                    continue;

                double dx = f.X - prev.X, dy = f.Y - prev.Y;
                var px = Math.Sqrt(dx * dx + dy * dy);
                speed[i] = CursorMotion.Normalize(px / diagonal * 1000.0 / dtMs);
            }

            return new CursorMotion(times, speed, PairClicks(capture.Events));
        }

        private static double DiagonalOf(InputCapture capture)
        {
            var header = capture.Header;
            if (header.RegionWidth > 0 && header.RegionHeight > 0)
                return Math.Sqrt((double)header.RegionWidth * header.RegionWidth + (double)header.RegionHeight * header.RegionHeight);

            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var f in capture.Frames)
            {
                minX = Math.Min(minX, f.X);
                minY = Math.Min(minY, f.Y);
                maxX = Math.Max(maxX, f.X);
                maxY = Math.Max(maxY, f.Y);
            }

            double w = maxX - minX, h = maxY - minY;
            var diagonal = Math.Sqrt(w * w + h * h);
            return diagonal > 0 ? diagonal : 1.0;
        }

        private static IReadOnlyList<CursorClick> PairClicks(IReadOnlyList<InputEvent> events)
        {
            var clicks = new List<CursorClick>();
            // index into clicks of the still-open press per button bit, or -1
            var open = new Dictionary<int, int>();

            foreach (var e in events)
            {
                if (e.Kind == InputEventKind.MouseDown)
                {
                    // a second down without an up (a lost hook event) closes the first on itself
                    if (open.TryGetValue(e.Code, out var pending) && pending >= 0)
                        clicks[pending] = clicks[pending] with { UpMs = clicks[pending].DownMs };

                    open[e.Code] = clicks.Count;
                    clicks.Add(new CursorClick(e.TimeMs, e.TimeMs, e.Code));
                }
                else if (e.Kind == InputEventKind.MouseUp)
                {
                    if (open.TryGetValue(e.Code, out var pending) && pending >= 0)
                    {
                        clicks[pending] = clicks[pending] with { UpMs = Math.Max(clicks[pending].DownMs, e.TimeMs) };
                        open[e.Code] = -1;
                    }
                }
            }

            // events are time-sorted, and each click is added at its down, so the list is sorted
            // by DownMs already
            return clicks.Count == 0 ? Array.Empty<CursorClick>() : clicks.ToArray();
        }

        internal static IReadOnlyList<KeyRunSpan> ToSpans(IReadOnlyList<KeyRun> runs)
        {
            if (runs == null || runs.Count == 0)
                return Array.Empty<KeyRunSpan>();

            var spans = new KeyRunSpan[runs.Count];
            for (var i = 0; i < runs.Count; i++)
            {
                var run = runs[i];
                spans[i] = new KeyRunSpan(run.StartMs, run.EndMs, run.Tokens.Count, run.IsChord);
            }
            return spans;
        }
    }
}
