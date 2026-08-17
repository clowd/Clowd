using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>One glyph/token of a keystroke run: the text it contributes (separator included,
    /// so a run's display is the plain concatenation of its tokens) and the key-down time that
    /// revealed it.</summary>
    internal readonly struct KeyToken
    {
        public KeyToken(double timeMs, string text)
        {
            TimeMs = timeMs;
            Text = text;
        }

        public double TimeMs { get; }

        public string Text { get; }
    }

    /// <summary>
    /// One run of consecutive keystrokes — the unit the keyboard overlay displays as a row (or
    /// several, once wrapped). A run ends on a typing gap, on Enter/Esc (included, then closed),
    /// or when a chord starts; a chord (non-shift modifier + keys, rendered <c>Ctrl+K+Y</c>) is
    /// its own run, closed when the modifiers release. <see cref="EndMs"/> is the last key-down's
    /// time — runs never overlap, so both bounds are monotonic across a capture's run list.
    /// </summary>
    internal sealed class KeyRun
    {
        internal KeyRun(double startMs, double endMs, bool isChord, KeyToken[] tokens)
        {
            StartMs = startMs;
            EndMs = endMs;
            IsChord = isChord;
            Tokens = Array.AsReadOnly(tokens);
        }

        public double StartMs { get; }

        /// <summary>Time of the run's last key-down — what the linger/fade window anchors to.</summary>
        public double EndMs { get; }

        public bool IsChord { get; }

        public IReadOnlyList<KeyToken> Tokens { get; }

        /// <summary>The run's full display text.</summary>
        public string FullText => TextAt(double.MaxValue);

        /// <summary>The display text at <paramref name="timeMs"/>: only the tokens already typed
        /// — keys appear as they are pressed.</summary>
        public string TextAt(double timeMs)
        {
            var sb = new StringBuilder();
            foreach (var token in Tokens)
            {
                if (token.TimeMs > timeMs)
                    break;
                sb.Append(token.Text);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// The keyboard overlay's layout engine, in pure testable pieces: run segmentation from the
    /// capture's key events (precomputed once per (path, pause-break) and cached — nothing
    /// per-frame is O(events)), the per-run visibility/fade math, and word wrapping. Drawing
    /// lives in <c>FrameComposer</c>.
    /// </summary>
    internal static class KeyboardLayout
    {
        private const int ModCtrl = 1, ModAlt = 2, ModShift = 4, ModWin = 8;
        private const int NonShiftMask = ModCtrl | ModAlt | ModWin;

        // ------------------------------------------------------------------------- segmentation

        /// <summary>
        /// Splits key-down events into display runs (see <see cref="KeyRun"/>). Modifier presses
        /// alone render nothing; shift never starts a chord (it is already folded into the
        /// translated character); printable keys use the captured character, everything else its
        /// VK name as a spaced token (Enter, Esc, Tab, ⌫, Left, F5, …). Mouse events are
        /// ignored.
        /// </summary>
        internal static IReadOnlyList<KeyRun> Segment(IReadOnlyList<InputEvent> events, int pauseBreakMs)
        {
            var runs = new List<KeyRun>();
            if (events == null || events.Count == 0)
                return runs;

            var tokens = new List<KeyToken>();
            double startMs = 0, endMs = 0;
            bool isChord = false, lastWasSpecial = false;
            int modifiers = 0;

            void Close()
            {
                if (tokens.Count > 0)
                    runs.Add(new KeyRun(startMs, endMs, isChord, tokens.ToArray()));
                tokens.Clear();
                isChord = false;
                lastWasSpecial = false;
            }

            foreach (var e in events)
            {
                if (e.Kind == InputEventKind.MouseDown || e.Kind == InputEventKind.MouseUp)
                    continue;

                int mod = ModifierBit(e.Code);
                if (e.Kind == InputEventKind.KeyUp)
                {
                    if (mod != 0)
                    {
                        modifiers &= ~mod;
                        // releasing the last non-shift modifier finishes the chord
                        if (isChord && (modifiers & NonShiftMask) == 0)
                            Close();
                    }
                    continue;
                }

                // key down. Bare modifiers only arm the chord state.
                if (mod != 0)
                {
                    modifiers |= mod;
                    continue;
                }

                if (tokens.Count > 0 && e.TimeMs - endMs > pauseBreakMs)
                    Close();

                if ((modifiers & NonShiftMask) != 0)
                {
                    if (!isChord)
                    {
                        Close();
                        startMs = e.TimeMs;
                        isChord = true;
                        tokens.Add(new KeyToken(e.TimeMs, ModifierNames(modifiers) + "+" + VkName(e.Code)));
                    }
                    else
                    {
                        tokens.Add(new KeyToken(e.TimeMs, "+" + VkName(e.Code)));
                    }
                    endMs = e.TimeMs;
                    continue;
                }

                // chord state without a hook key-up row (torn capture): the next plain key
                // starts a fresh text run
                if (isChord)
                    Close();

                if (tokens.Count == 0)
                    startMs = e.TimeMs;

                string ch = Printable(e.Char);
                string text;
                bool special = ch == null;
                if (special)
                    text = tokens.Count == 0 ? VkName(e.Code) : " " + VkName(e.Code);
                else
                    text = lastWasSpecial ? " " + ch : ch;

                tokens.Add(new KeyToken(e.TimeMs, text));
                lastWasSpecial = special;
                endMs = e.TimeMs;

                if (e.Code == 13 || e.Code == 27) // Enter / Esc close the run, key included
                    Close();
            }

            Close();
            return runs;
        }

        private static int ModifierBit(int vk) => vk switch
        {
            16 or 160 or 161 => ModShift,
            17 or 162 or 163 => ModCtrl,
            18 or 164 or 165 => ModAlt,
            91 or 92 => ModWin,
            _ => 0,
        };

        private static string ModifierNames(int modifiers)
        {
            var sb = new StringBuilder();
            void Add(string name)
            {
                if (sb.Length > 0)
                    sb.Append('+');
                sb.Append(name);
            }

            if ((modifiers & ModCtrl) != 0) Add("Ctrl");
            if ((modifiers & ModAlt) != 0) Add("Alt");
            if ((modifiers & ModShift) != 0) Add("Shift");
            if ((modifiers & ModWin) != 0) Add("Win");
            return sb.ToString();
        }

        /// <summary>The captured character when every code point is printable, else null (the key
        /// then renders as its VK name).</summary>
        private static string Printable(string ch)
        {
            if (string.IsNullOrEmpty(ch))
                return null;
            foreach (var c in ch)
            {
                if (c < 0x20 || c == 0x7f)
                    return null;
            }
            return ch;
        }

        /// <summary>Display name for a Windows virtual-key code.</summary>
        internal static string VkName(int vk) => vk switch
        {
            8 => "⌫", // ⌫
            9 => "Tab",
            13 => "Enter",
            20 => "Caps",
            27 => "Esc",
            32 => "Space",
            33 => "PgUp",
            34 => "PgDn",
            35 => "End",
            36 => "Home",
            37 => "Left",
            38 => "Up",
            39 => "Right",
            40 => "Down",
            45 => "Ins",
            46 => "Del",
            16 or 160 or 161 => "Shift",
            17 or 162 or 163 => "Ctrl",
            18 or 164 or 165 => "Alt",
            91 or 92 => "Win",
            >= 48 and <= 57 => ((char)vk).ToString(),   // top-row digits
            >= 65 and <= 90 => ((char)vk).ToString(),   // letters
            >= 96 and <= 105 => ((char)(vk - 48)).ToString(), // numpad digits
            >= 112 and <= 135 => "F" + (vk - 111),
            186 => ";",
            187 => "=",
            188 => ",",
            189 => "-",
            190 => ".",
            191 => "/",
            192 => "`",
            219 => "[",
            220 => "\\",
            221 => "]",
            222 => "'",
            _ => "VK" + vk,
        };

        // --------------------------------------------------------------------------- visibility

        /// <summary>
        /// The run's opacity at capture time <paramref name="sourceMs"/>: 1 from its first key
        /// until <see cref="KeyRun.EndMs"/> + linger, then a linear fade over the fade window, 0
        /// outside. The linger/fade windows are project-time milliseconds — the capture-time delta
        /// divides by <paramref name="speed"/> — so a sped-up clip does not compress them.
        /// </summary>
        internal static double RunOpacityAt(KeyRun run, double sourceMs, double speed,
            int lingerMs, int fadeMs)
        {
            if (sourceMs < run.StartMs)
                return 0;
            if (speed <= 0)
                speed = 1.0;

            double delta = (sourceMs - run.EndMs) / speed;
            if (delta <= lingerMs)
                return 1;
            if (fadeMs <= 0)
                return 0;
            double remaining = 1 - (delta - lingerMs) / fadeMs;
            return remaining <= 0 ? 0 : remaining;
        }

        /// <summary>
        /// The rows to display at <paramref name="sourceMs"/>: every visible run's typed-so-far
        /// text with its opacity, oldest first (the caller stacks them upward, newest at the
        /// anchored bottom). Runs are found by binary search and walked backwards until the fade
        /// window closes — both run bounds are monotonic, so the walk stops at the first fully
        /// faded run.
        /// </summary>
        internal static List<(string Text, double Opacity)> VisibleRowsAt(
            IReadOnlyList<KeyRun> runs, double sourceMs, double speed, int lingerMs, int fadeMs)
        {
            var rows = new List<(string, double)>();
            if (runs == null || runs.Count == 0)
                return rows;

            // last run that has started by sourceMs
            int lo = 0, hi = runs.Count - 1, last = -1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (runs[mid].StartMs <= sourceMs)
                {
                    last = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            for (int i = last; i >= 0; i--)
            {
                double opacity = RunOpacityAt(runs[i], sourceMs, speed, lingerMs, fadeMs);
                if (opacity <= 0)
                    break; // EndMs is monotonic: every earlier run is at least as faded
                string text = runs[i].TextAt(sourceMs);
                if (text.Length > 0)
                    rows.Add((text, opacity));
            }

            rows.Reverse();
            return rows;
        }

        // --------------------------------------------------------------------------------- wrap

        /// <summary>Greedy word wrap of one run's text at <paramref name="maxWidth"/> canvas px;
        /// a single word wider than the line hard-breaks by character. Never returns an empty
        /// line.</summary>
        internal static IReadOnlyList<string> Wrap(string text, SKFont font, float maxWidth)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text))
                return lines;
            if (maxWidth <= 0)
            {
                lines.Add(text);
                return lines;
            }

            var current = new StringBuilder();
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (font.MeasureText(candidate) <= maxWidth)
                {
                    current.Clear();
                    current.Append(candidate);
                    continue;
                }

                if (current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }

                if (font.MeasureText(word) <= maxWidth)
                {
                    current.Append(word);
                    continue;
                }

                // the word alone overflows the line: break by character
                foreach (var c in word)
                {
                    current.Append(c);
                    if (current.Length > 1 && font.MeasureText(current.ToString()) > maxWidth)
                    {
                        current.Length -= 1;
                        lines.Add(current.ToString());
                        current.Clear();
                        current.Append(c);
                    }
                }
            }

            if (current.Length > 0)
                lines.Add(current.ToString());
            return lines;
        }

        // -------------------------------------------------------------------------------- cache

        /// <summary>
        /// Process-wide run cache keyed by (capture path, pause-break): segmentation runs once per
        /// distinct parameter pair, however many frames are composed (the ImageCache pattern —
        /// immutable after load, failures included: a missing file caches its empty run list).
        /// </summary>
        internal static IReadOnlyList<KeyRun> GetRuns(string capturePath, int pauseBreakMs)
        {
            if (string.IsNullOrEmpty(capturePath))
                return Array.Empty<KeyRun>();

            string key = pauseBreakMs.ToString(CultureInfo.InvariantCulture) + "|" + capturePath;
            lock (CacheSync)
            {
                if (!Cache.TryGetValue(key, out var runs))
                {
                    runs = Segment(InputCapture.Get(capturePath).Events, pauseBreakMs);
                    Cache[key] = runs;
                }
                return runs;
            }
        }

        private static readonly object CacheSync = new object();
        private static readonly Dictionary<string, IReadOnlyList<KeyRun>> Cache
            = new Dictionary<string, IReadOnlyList<KeyRun>>(StringComparer.OrdinalIgnoreCase);
    }
}
