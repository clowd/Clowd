using System;
using System.Collections.Generic;
using Prisc.VideoSDK.Model;
using SkiaSharp;

namespace Prisc.VideoSDK.Composition
{
    /// <summary>
    /// The keystroke overlay for keys being pressed right now rather than read back from a
    /// recording: a host feeds it key edges from a live hook, and it draws the rows with the very
    /// segmentation (<see cref="KeyboardLayout"/>) and Skia pipeline (<see cref="KeystrokeRenderer"/>)
    /// the editor's keyboard track uses, so a live chord and a recorded one look identical.
    /// <para>
    /// Key codes are the OS's own, exactly as the recorder writes them — Win32 virtual-key codes
    /// on Windows, <c>CGKeyCode</c>s on macOS (pick with the constructor) — and times are any
    /// monotonic millisecond clock, as long as <see cref="Draw"/> is given the same one. Not
    /// thread-safe: feed and draw on one thread.
    /// </para>
    /// </summary>
    public sealed class LiveKeystrokes
    {
        // A runaway buffer guard: the history is normally dropped at the first quiet moment (see
        // KeyDown), so this only trips when a key-up went missing and a key reads as held forever.
        private const int MaxEvents = 4096;

        private readonly KeyboardPlatform _platform;
        private readonly List<InputEvent> _events = new();
        private readonly HashSet<int> _held = new();
        private double _lastDownMs = double.NegativeInfinity;

        private IReadOnlyList<KeyRun> _runs = Array.Empty<KeyRun>();
        private bool _dirty;
        private int _runsPauseBreakMs = -1;
        private KeystrokeFilter _runsFilter;

        public LiveKeystrokes(bool macOS = false)
        {
            _platform = macOS ? KeyboardPlatform.MacOS : KeyboardPlatform.Windows;
        }

        /// <summary>Size, linger, new-row gap, filter and colors — the keyboard track's own
        /// settings object, so the two overlays share their defaults and validation ranges too.
        /// <see cref="KeyboardContent.FontSize"/> is in the units <see cref="Draw"/>'s scale maps
        /// to canvas pixels. <see cref="KeyboardContent.SourceId"/> is not used.</summary>
        public KeyboardContent Style { get; set; } = new KeyboardContent();

        /// <summary>How each row arrives (per row, like the keyboard track's item entry).</summary>
        public Transition Entry { get; set; }

        /// <summary>How each row leaves once its linger is up.</summary>
        public Transition Exit { get; set; }

        /// <summary>
        /// A key went down. A key already held is an auto-repeat and is dropped, as the recorder
        /// drops it, so holding Backspace draws one cap. <paramref name="character"/> is the
        /// translated character, or null for a key that types nothing (it then draws as a keycap).
        /// </summary>
        public void KeyDown(int code, string character, double timeMs)
        {
            if (!_held.Add(code))
                return;

            // with nothing held, and the last press older than both the new-row gap and the time
            // its row takes to leave, every run so far is closed and gone: forget it, so the
            // history (and the re-segmentation below) stays the size of what is on screen
            bool quiet = _held.Count == 1
                && timeMs - _lastDownMs > Math.Max(PauseBreakMs, LingerMs + KeystrokeRenderer.RowMs(Exit));
            if (quiet || _events.Count >= MaxEvents)
                _events.Clear();

            _events.Add(new InputEvent(timeMs, InputEventKind.KeyDown, code, character, 0, 0));
            _lastDownMs = timeMs;
            _dirty = true;
        }

        /// <summary>A key came up. Unmatched ups (the press predates the hook) are ignored.</summary>
        public void KeyUp(int code, double timeMs)
        {
            if (!_held.Remove(code))
                return;

            _events.Add(new InputEvent(timeMs, InputEventKind.KeyUp, code, null, 0, 0));
            _dirty = true;
        }

        /// <summary>The codes currently held, for a host that reconciles them against the OS's
        /// own modifier state (a key-up the hook never saw).</summary>
        public IReadOnlyCollection<int> HeldKeys => _held;

        /// <summary>Forgets every key, held or shown.</summary>
        public void Clear()
        {
            _events.Clear();
            _held.Clear();
            _lastDownMs = double.NegativeInfinity;
            _dirty = true;
        }

        /// <summary>
        /// When the drawing next changes: <paramref name="nowMs"/> itself while a row is animating
        /// (draw every frame), the moment the first lingering row starts leaving while every row
        /// just sits there, or null when nothing is showing and nothing will until the next key.
        /// Lets the host idle instead of redrawing an unchanging overlay.
        /// </summary>
        public double? NextChangeMs(double nowMs)
        {
            var runs = Runs();
            int entryMs = KeystrokeRenderer.RowMs(Entry), exitMs = KeystrokeRenderer.RowMs(Exit);
            int lingerMs = LingerMs;

            double? next = null;
            for (int i = runs.Count - 1; i >= 0; i--)
            {
                var run = runs[i];
                if (run.StartMs > nowMs)
                    continue;
                if (!KeyboardLayout.RunPhaseAt(run, nowMs, 1.0, lingerMs, entryMs, exitMs,
                        out double entryRaw, out double exitRaw))
                    break; // EndMs is monotonic: every earlier run has left too

                if (entryRaw < 1 || exitRaw < 1)
                    return nowMs;

                double leaves = run.EndMs + lingerMs;
                next = next is { } n ? Math.Min(n, leaves) : leaves;
            }

            return next;
        }

        /// <summary>
        /// Draws the rows showing at <paramref name="nowMs"/>. <paramref name="scale"/> maps
        /// <see cref="KeyboardContent.FontSize"/> to canvas pixels (the display's DPI scale, for a
        /// size in device-independent pixels); the anchor, the wrap width and the alignment and
        /// stacking place the block exactly as <see cref="KeystrokeRenderer.Draw"/> describes.
        /// </summary>
        public void Draw(SKCanvas canvas, double nowMs, float scale, float anchorX, float anchorY,
            float wrapWidth, KeystrokeAlignment alignment, KeystrokeStacking stacking, double opacity)
        {
            if (canvas == null)
                return;

            var rows = KeyboardLayout.VisibleRowsAt(Runs(), nowMs, 1.0, LingerMs,
                KeystrokeRenderer.RowMs(Entry), KeystrokeRenderer.RowMs(Exit));
            if (rows.Count == 0)
                return;

            var style = Style ?? new KeyboardContent();
            double size = style.FontSize > 0 ? style.FontSize : KeyboardContent.DefaultFontSize;
            KeystrokeRenderer.Draw(canvas, rows, style, Entry, Exit, (float)(size * scale),
                anchorX, anchorY, wrapWidth, alignment, stacking, opacity);
        }

        /// <summary>The runs the history segments into right now (tests).</summary>
        internal IReadOnlyList<KeyRun> CurrentRuns => Runs();

        private int LingerMs => Math.Max(0, Style?.LingerMs ?? 0);

        private int PauseBreakMs => Math.Max(0, Style?.PauseBreakMs ?? 0);

        /// <summary>The history segmented into runs, redone only after a key or a change to the
        /// settings segmentation reads — the history is the size of what is on screen, so a full
        /// re-segment is cheap, and it keeps the live runs bit-identical to a recording's.</summary>
        private IReadOnlyList<KeyRun> Runs()
        {
            var filter = Style?.Filter ?? KeystrokeFilter.None;
            if (_dirty || _runsPauseBreakMs != PauseBreakMs || _runsFilter != filter)
            {
                _runs = KeyboardLayout.Segment(_events, PauseBreakMs, filter, _platform);
                _runsPauseBreakMs = PauseBreakMs;
                _runsFilter = filter;
                _dirty = false;
            }

            return _runs;
        }
    }
}
