using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>One glyph/token of a keystroke run: the text it contributes (separator included,
    /// so a run's display is the plain concatenation of its tokens), the bare label the drawing
    /// uses, and the key-down time that revealed it.</summary>
    internal readonly struct KeyToken
    {
        public KeyToken(double timeMs, string text, string label, bool isCap)
        {
            TimeMs = timeMs;
            Text = text;
            Label = label;
            IsCap = isCap;
        }

        public double TimeMs { get; }

        public string Text { get; }

        /// <summary>The token without its separator — what a keycap is legended with, and what a
        /// typed character contributes to its word.</summary>
        public string Label { get; }

        /// <summary>Drawn as a keycap rather than as text: every non-printable key, and every
        /// member of a chord.</summary>
        public bool IsCap { get; }
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

        /// <summary>Time of the run's last key-down — what the linger/exit window anchors to.</summary>
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

    /// <summary>What a drawn row is made of: runs of typed text broken into words, one keycap per
    /// special key or chord member, and the "+" a chord puts between its caps.</summary>
    internal enum KeyAtomKind
    {
        Word,
        Cap,
        Plus,
    }

    /// <summary>One indivisible piece of a keyboard row — the unit the wrap moves between lines
    /// and the drawing lays out left to right.</summary>
    internal readonly struct KeyAtom
    {
        public KeyAtom(KeyAtomKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }

        public KeyAtomKind Kind { get; }

        public string Text { get; }
    }

    /// <summary>
    /// One visible row of the overlay: its atoms as typed so far, and how far through its entry
    /// and exit animations it is (linear 0..1 — the easing and the transition kind are the
    /// drawer's business, because they come from the item's model).
    /// </summary>
    internal sealed class KeyboardRow
    {
        internal KeyboardRow(IReadOnlyList<KeyAtom> atoms, bool isChord, double entryRaw, double exitRaw)
        {
            Atoms = atoms;
            IsChord = isChord;
            EntryRaw = entryRaw;
            ExitRaw = exitRaw;
        }

        public IReadOnlyList<KeyAtom> Atoms { get; }

        public bool IsChord { get; }

        /// <summary>0 the instant the row appears, 1 once its entry has finished.</summary>
        public double EntryRaw { get; }

        /// <summary>1 until the linger expires, then down to 0 as the exit plays out.</summary>
        public double ExitRaw { get; }
    }

    /// <summary>Widths the wrap needs. Fonts and keycap geometry live with the drawing, so the
    /// drawing supplies them.</summary>
    internal interface IKeyAtomMetrics
    {
        /// <summary>The atom's own drawn width.</summary>
        float Width(KeyAtom atom);

        /// <summary>The space left between two atoms that end up side by side — the typing pill's
        /// padding included, where one of the two closes or opens a pill.</summary>
        float Gap(KeyAtom left, KeyAtom right);

        /// <summary>What the atom adds at the line's outer edge: the typing pill's padding when
        /// the line begins or ends inside one, nothing for a bare keycap.</summary>
        float Edge(KeyAtom atom);
    }

    /// <summary>
    /// Which keyboard a capture's key codes came off. The recorder hands the OS's own numbering
    /// straight through — Win32 virtual-key codes from the low-level hooks, <c>CGKeyCode</c>s from
    /// the event tap — and the two spaces share no meaning whatsoever: code 13 is Enter on Windows
    /// and the W key on a Mac. Everything that turns a code into a name therefore has to be told
    /// which board it is reading, and the answer is the header's <c>platform</c> field, not the
    /// machine doing the playback: a recording made on Windows must still legend its keycaps
    /// Ctrl/Alt/Win when it is opened on a Mac, because those are the keys the user actually hit.
    /// </summary>
    internal enum KeyboardPlatform
    {
        Windows,
        MacOS,
    }

    /// <summary>
    /// The keyboard overlay's layout engine, in pure testable pieces: run segmentation from the
    /// capture's key events (precomputed once per (path, pause-break) and cached — nothing
    /// per-frame is O(events)), the per-run entry/linger/exit math, and word wrapping. Drawing
    /// lives in <c>FrameComposer</c> and <see cref="Keycap"/>.
    /// </summary>
    internal static class KeyboardLayout
    {
        // Modifier roles, not key names. The same four bits stand for Ctrl/Alt/Shift/Win on a
        // Windows capture and Control/Option/Shift/Command on a macOS one, because that is one set
        // of roles wearing two sets of legends; only the naming (ModifierNames) differs. ModFn has
        // no Windows counterpart at all — Windows' Fn is handled in the keyboard's firmware and
        // never reaches a hook.
        private const int ModCtrl = 1, ModAlt = 2, ModShift = 4, ModSuper = 8, ModFn = 16;

        // The modifiers whose presence turns a keystroke into a chord. Shift is out because it is
        // already folded into the translated character ("A", not "Shift+A"), and Fn is out for the
        // Mac version of the same argument: a laptop's arrow, function and media keys report their
        // own keycode whether or not Fn was held to reach them, so chording it would print
        // "Fn+Left" over every arrow press. Both still join a chord some other modifier opened.
        private const int ChordMask = ModCtrl | ModAlt | ModSuper;

        // ------------------------------------------------------------------------- segmentation

        /// <summary>
        /// Splits key-down events into display runs (see <see cref="KeyRun"/>). Modifier presses
        /// alone render nothing; shift never starts a chord (it is already folded into the
        /// translated character); printable keys use the captured character, everything else its
        /// key name as a keycap token (Enter, Esc, Tab, Bksp, Left, F5, …). Mouse events are
        /// ignored. <paramref name="filter"/> drops runs before they form:
        /// <see cref="KeystrokeFilter.Shortcuts"/> keeps only chords,
        /// <see cref="KeystrokeFilter.Special"/> chords plus the non-printable keys that draw as
        /// keycaps — the skipped keys neither extend a run's clock nor break one, so two special
        /// keys with only typing between them still group by their own gap.
        ///
        /// <paramref name="platform"/> is which board the codes came off (see
        /// <see cref="KeyboardPlatform"/>) — every code this method interprets, modifiers and the
        /// run-closing keys included, means something else in the other numbering. It defaults to
        /// Windows for the same reason <see cref="PlatformOf"/> does.
        /// </summary>
        internal static IReadOnlyList<KeyRun> Segment(IReadOnlyList<InputEvent> events, int pauseBreakMs,
            KeystrokeFilter filter = KeystrokeFilter.None,
            KeyboardPlatform platform = KeyboardPlatform.Windows)
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

            // a chord's members are separate caps, joined by "+" in the run's text
            void AddChordCap(double timeMs, string label) => tokens.Add(new KeyToken(
                timeMs, tokens.Count == 0 ? label : "+" + label, label, isCap: true));

            foreach (var e in events)
            {
                if (e.Kind == InputEventKind.MouseDown || e.Kind == InputEventKind.MouseUp)
                    continue;

                int mod = ModifierBit(e.Code, platform);
                if (e.Kind == InputEventKind.KeyUp)
                {
                    if (mod != 0)
                    {
                        modifiers &= ~mod;
                        // releasing the last chording modifier finishes the chord
                        if (isChord && (modifiers & ChordMask) == 0)
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

                if ((modifiers & ChordMask) != 0)
                {
                    if (!isChord)
                    {
                        Close();
                        startMs = e.TimeMs;
                        isChord = true;
                        foreach (var name in ModifierNames(modifiers, platform))
                            AddChordCap(e.TimeMs, name);
                    }
                    AddChordCap(e.TimeMs, KeyName(e.Code, platform));
                    endMs = e.TimeMs;
                    continue;
                }

                // chord state without a hook key-up row (torn capture): the next plain key
                // starts a fresh text run
                if (isChord)
                    Close();

                string ch = Printable(e.Char);
                bool special = ch == null;

                // filtered-out keys vanish outright: they never open, extend or break a run
                if (filter == KeystrokeFilter.Shortcuts || (filter == KeystrokeFilter.Special && !special))
                    continue;

                if (tokens.Count == 0)
                    startMs = e.TimeMs;
                string label = special ? KeyName(e.Code, platform) : ch;
                string text = special
                    ? (tokens.Count == 0 ? label : " " + label)
                    : (lastWasSpecial ? " " + label : label);

                tokens.Add(new KeyToken(e.TimeMs, text, label, special));
                lastWasSpecial = special;
                endMs = e.TimeMs;

                if (ClosesRun(e.Code, platform)) // Enter / Esc close the run, key included
                    Close();
            }

            Close();
            return runs;
        }

        private static int ModifierBit(int code, KeyboardPlatform platform) => platform switch
        {
            KeyboardPlatform.MacOS => code switch
            {
                56 or 60 => ModShift,
                59 or 62 => ModCtrl,
                58 or 61 => ModAlt,     // option
                54 or 55 => ModSuper,   // command
                63 => ModFn,
                // Caps Lock is deliberately NOT a modifier here even though macOS reports it as a
                // flag: the tap holds the flag set for as long as the lock is engaged, so treating
                // it as one would staple "Caps" onto the front of every chord typed for the next
                // ten minutes. As a plain key it draws one ⇪ cap at the moment the lock toggles,
                // which is what Windows' own kd/ku pair produces and what the user did.
                _ => 0,
            },
            _ => code switch
            {
                16 or 160 or 161 => ModShift,
                17 or 162 or 163 => ModCtrl,
                18 or 164 or 165 => ModAlt,
                91 or 92 => ModSuper,
                _ => 0,
            },
        };

        /// <summary>
        /// The chord's leading keycaps. One order serves both boards: Windows writes
        /// Ctrl+Alt+Shift+Win, and Apple's own menus print ⌃⌥⇧⌘ — control, option, shift, command
        /// — which is the same four roles in the same sequence. Fn brings up the rear because it
        /// never opens a chord, it only tags along in one.
        /// </summary>
        private static IEnumerable<string> ModifierNames(int modifiers, KeyboardPlatform platform)
        {
            bool mac = platform == KeyboardPlatform.MacOS;
            if ((modifiers & ModCtrl) != 0) yield return "Ctrl";
            if ((modifiers & ModAlt) != 0) yield return mac ? "Option" : "Alt";
            if ((modifiers & ModShift) != 0) yield return "Shift";
            if ((modifiers & ModSuper) != 0) yield return mac ? "Cmd" : "Win";
            if (mac && (modifiers & ModFn) != 0) yield return "Fn";
        }

        /// <summary>Whether the key ends its run with itself included — the "I have finished this
        /// thought" keys. Mac boards have two of them: Return proper, and the keypad's own Enter,
        /// which is a separate keycode rather than the Windows extended-flag variant of one.</summary>
        private static bool ClosesRun(int code, KeyboardPlatform platform) => platform switch
        {
            KeyboardPlatform.MacOS => code is 36 or 76 or 53,   // Return, keypad Enter, Esc
            _ => code is 13 or 27,                              // Enter, Esc
        };

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

        /// <summary>Display name for a key code off <paramref name="platform"/>'s board — the one
        /// entry point segmentation uses, so a code is never read in the wrong numbering by
        /// accident.</summary>
        internal static string KeyName(int code, KeyboardPlatform platform) => platform switch
        {
            KeyboardPlatform.MacOS => MacKeyName(code),
            _ => VkName(code),
        };

        /// <summary>
        /// Display name for a Windows virtual-key code. Every name is plain ASCII on purpose: the
        /// overlay draws with the platform's default typeface, and a symbol glyph it happens not
        /// to carry (⌫, ⇧, ↵ …) renders as a tofu box. Where a symbol is genuinely wanted the
        /// keycap draws it as a vector icon instead — see <see cref="Keycap"/>. Unknown codes fall
        /// back to <c>VK</c>+code, which is ugly but always legible and never empty.
        /// </summary>
        internal static string VkName(int vk) => vk switch
        {
            1 => "LMouse",
            2 => "RMouse",
            3 => "Cancel",
            4 => "MMouse",
            5 => "Mouse4",
            6 => "Mouse5",
            8 => "Bksp",
            9 => "Tab",
            12 => "Clear",
            13 => "Enter",
            19 => "Pause",
            20 => "Caps",
            21 => "Kana",
            23 => "Junja",
            24 => "Final",
            25 => "Kanji",
            27 => "Esc",
            28 => "Convert",
            29 => "NoConvert",
            30 => "Accept",
            31 => "ModeChg",
            32 => "Space",
            33 => "PgUp",
            34 => "PgDn",
            35 => "End",
            36 => "Home",
            37 => "Left",
            38 => "Up",
            39 => "Right",
            40 => "Down",
            41 => "Select",
            42 => "Print",
            43 => "Exec",
            44 => "PrtSc",
            45 => "Ins",
            46 => "Del",
            47 => "Help",
            16 or 160 or 161 => "Shift",
            17 or 162 or 163 => "Ctrl",
            18 or 164 or 165 => "Alt",
            91 or 92 => "Win",
            93 => "Menu",
            95 => "Sleep",
            >= 48 and <= 57 => ((char)vk).ToString(),   // top-row digits
            >= 65 and <= 90 => ((char)vk).ToString(),   // letters
            >= 96 and <= 105 => ((char)(vk - 48)).ToString(), // numpad digits
            106 => "*",
            107 => "+",
            108 => "Sep",
            109 => "-",
            110 => ".",
            111 => "/",
            >= 112 and <= 135 => "F" + (vk - 111),
            144 => "NumLk",
            145 => "ScrLk",
            166 => "Back",
            167 => "Fwd",
            168 => "Refresh",
            169 => "Stop",
            170 => "Search",
            171 => "Favs",
            172 => "Home",
            173 => "Mute",
            174 => "Vol-",
            175 => "Vol+",
            176 => "Next",
            177 => "Prev",
            178 => "Stop",
            179 => "Play",
            180 => "Mail",
            181 => "Media",
            182 => "App1",
            183 => "App2",
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
            223 => "OEM8",
            226 => "\\",
            229 => "Process",
            231 => "Packet",
            246 => "Attn",
            247 => "CrSel",
            248 => "ExSel",
            249 => "ErEOF",
            250 => "Play",
            251 => "Zoom",
            253 => "PA1",
            254 => "Clear",
            _ => "VK" + vk.ToString(CultureInfo.InvariantCulture),
        };

        /// <summary>
        /// Display name for a macOS <c>CGKeyCode</c>, the numbering a <c>CGEventTap</c> reports.
        /// These are positional hardware codes with no relationship to <see cref="VkName"/>'s
        /// Win32 space — 13 is W here and Enter there, 51 is Delete here and nothing there — which
        /// is exactly why the header carries the platform and why reading a Mac capture through
        /// the Win32 table produced the garbage this method replaces.
        ///
        /// Names follow the legends molded into an Apple keyboard, not the PC words for the same
        /// physical keys: Return not Enter, Delete not Bksp, Option not Alt, Cmd not Win. A Mac
        /// user watching their own recording should recognize the caps they pressed.
        ///
        /// Plain ASCII for the same reason <see cref="VkName"/> is — the overlay draws with the
        /// platform default typeface and the wanted symbols (⌘ ⌥ ⌫ ↩ ⇧ ⇪) are precisely the
        /// glyphs such a font tends to lack, so <see cref="Keycap"/> draws them as vector icons
        /// keyed off these names. Unknown codes fall back to <c>Key</c>+code, which is ugly but
        /// legible and never empty. Codes above 126 do not exist in this space at all.
        /// </summary>
        internal static string MacKeyName(int code) => code switch
        {
            // letters and the punctuation that shares their block, in Apple's scattered
            // positional order (kVK_ANSI_* in <Carbon/HIToolbox/Events.h>) — a lookup, not a
            // range, because the codes follow the 1984 Macintosh's key matrix, not the alphabet.
            0 => "A",
            1 => "S",
            2 => "D",
            3 => "F",
            4 => "H",
            5 => "G",
            6 => "Z",
            7 => "X",
            8 => "C",
            9 => "V",
            10 => "§",  // kVK_ISO_Section: the extra key an ISO board grows left of Z
            11 => "B",
            12 => "Q",
            13 => "W",
            14 => "E",
            15 => "R",
            16 => "Y",
            17 => "T",
            18 => "1",
            19 => "2",
            20 => "3",
            21 => "4",
            22 => "6",  // 6 and 5 really are transposed in the matrix
            23 => "5",
            24 => "=",
            25 => "9",
            26 => "7",
            27 => "-",
            28 => "8",
            29 => "0",
            30 => "]",
            31 => "O",
            32 => "U",
            33 => "[",
            34 => "I",
            35 => "P",
            36 => "Return",
            37 => "L",
            38 => "J",
            39 => "'",
            40 => "K",
            41 => ";",
            42 => "\\",
            43 => ",",
            44 => "/",
            45 => "N",
            46 => "M",
            47 => ".",
            48 => "Tab",
            49 => "Space",
            50 => "`",
            51 => "Delete",  // the ⌫ key; macOS calls it Delete, Windows calls it Backspace
            53 => "Esc",

            // modifiers. Left/right twins share a name — the overlay legends the key, and both
            // shifts are Shift.
            54 or 55 => "Cmd",
            56 or 60 => "Shift",
            57 => "Caps",
            58 or 61 => "Option",
            59 or 62 => "Ctrl",
            63 => "Fn",

            // keypad, media and the upper function keys, interleaved the same way the matrix is
            65 => ".",
            67 => "*",
            69 => "+",
            71 => "Clear",
            72 => "Vol+",
            73 => "Vol-",
            74 => "Mute",
            75 => "/",
            76 => "Enter",  // the keypad's own Enter, a distinct key from Return
            78 => "-",
            81 => "=",
            >= 82 and <= 89 => ((char)(code - 34)).ToString(), // keypad 0..7
            91 => "8",
            92 => "9",
            93 => "¥",  // kVK_JIS_Yen
            94 => "_",  // kVK_JIS_Underscore
            95 => ",",  // kVK_JIS_KeypadComma
            102 => "Eisu",
            104 => "Kana",
            110 => "Menu",
            114 => "Help",
            115 => "Home",
            116 => "PgUp",
            117 => "Fwd Del",  // ⌦, the other Delete — spelled out so the two cannot be confused
            119 => "End",
            121 => "PgDn",
            123 => "Left",
            124 => "Right",
            125 => "Down",
            126 => "Up",

            // F1..F20, whose codes are scattered worse than anything else in the space
            122 => "F1",
            120 => "F2",
            99 => "F3",
            118 => "F4",
            96 => "F5",
            97 => "F6",
            98 => "F7",
            100 => "F8",
            101 => "F9",
            109 => "F10",
            103 => "F11",
            111 => "F12",
            105 => "F13",
            107 => "F14",
            113 => "F15",
            106 => "F16",
            64 => "F17",
            79 => "F18",
            80 => "F19",
            90 => "F20",

            _ => "Key" + code.ToString(CultureInfo.InvariantCulture),
        };

        /// <summary>
        /// Which board a capture's codes came off, read from the header row the recorder writes.
        /// The recorder stamps <c>platform</c> precisely so a consumer can key its interpretation
        /// of <c>vk</c> off it, and the field is the recording machine's — a Windows capture
        /// opened on a Mac still legends Ctrl/Alt/Win, and a Mac capture opened on Windows still
        /// legends Cmd/Option, because the keycaps belong to whoever pressed them.
        ///
        /// A file with no platform field — a v1 capture, or one whose header row was lost — reads
        /// as Windows: input capture only ever shipped on Windows before the macOS port, so that
        /// is factually what those bytes are, and old projects keep rendering exactly as they did.
        /// </summary>
        internal static KeyboardPlatform PlatformOf(InputCaptureHeader header)
            => string.Equals(header?.Platform, "macos", StringComparison.OrdinalIgnoreCase)
                ? KeyboardPlatform.MacOS
                : KeyboardPlatform.Windows;

        // ---------------------------------------------------------------------------- row atoms

        /// <summary>
        /// The row's atoms at <paramref name="timeMs"/> — only the keys pressed by then. Typed
        /// characters gather into words (a typed space ends one; the layout puts the gap back),
        /// every special key becomes its own keycap, and a chord's caps are joined by "+".
        /// </summary>
        internal static List<KeyAtom> AtomsAt(KeyRun run, double timeMs)
        {
            var atoms = new List<KeyAtom>();
            if (run == null)
                return atoms;

            var word = new StringBuilder();
            void FlushWord()
            {
                if (word.Length == 0)
                    return;
                atoms.Add(new KeyAtom(KeyAtomKind.Word, word.ToString()));
                word.Clear();
            }

            foreach (var token in run.Tokens)
            {
                if (token.TimeMs > timeMs)
                    break;

                if (token.IsCap)
                {
                    FlushWord();
                    if (run.IsChord && atoms.Count > 0)
                        atoms.Add(new KeyAtom(KeyAtomKind.Plus, "+"));
                    atoms.Add(new KeyAtom(KeyAtomKind.Cap, token.Label));
                    continue;
                }

                foreach (var c in token.Label)
                {
                    if (c == ' ')
                        FlushWord();
                    else
                        word.Append(c);
                }
            }

            FlushWord();
            return atoms;
        }

        // --------------------------------------------------------------------------- visibility

        /// <summary>
        /// The run's animation phase at capture time <paramref name="sourceMs"/>: how far its
        /// entry has run (0 → 1) and how much of it is left (1 → 0 once the linger expires and
        /// the exit starts). Both windows are project-time milliseconds — the capture-time delta
        /// divides by <paramref name="speed"/> — so a sped-up clip does not compress them.
        /// Returns false when the run is not on screen at all.
        /// </summary>
        internal static bool RunPhaseAt(KeyRun run, double sourceMs, double speed, int lingerMs,
            int entryMs, int exitMs, out double entryRaw, out double exitRaw)
        {
            entryRaw = 0;
            exitRaw = 0;
            if (run == null || sourceMs < run.StartMs)
                return false;
            if (speed <= 0)
                speed = 1.0;

            entryRaw = entryMs <= 0 ? 1 : Math.Min(1, (sourceMs - run.StartMs) / speed / entryMs);

            double delta = (sourceMs - run.EndMs) / speed;
            if (delta <= lingerMs)
            {
                exitRaw = 1;
                return true;
            }

            if (exitMs <= 0)
                return false;

            exitRaw = 1 - (delta - lingerMs) / exitMs;
            return exitRaw > 0;
        }

        /// <summary>
        /// The rows to display at <paramref name="sourceMs"/>: every visible run's atoms as typed
        /// so far with its animation phase, oldest first (the caller stacks them upward, newest at
        /// the anchored bottom). Runs are found by binary search and walked backwards until the
        /// exit window closes — both run bounds are monotonic, so the walk stops at the first run
        /// that has finished leaving.
        /// </summary>
        internal static List<KeyboardRow> VisibleRowsAt(IReadOnlyList<KeyRun> runs, double sourceMs,
            double speed, int lingerMs, int entryMs, int exitMs)
        {
            var rows = new List<KeyboardRow>();
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
                if (!RunPhaseAt(runs[i], sourceMs, speed, lingerMs, entryMs, exitMs,
                        out double entryRaw, out double exitRaw))
                    break; // EndMs is monotonic: every earlier run has left at least as far

                var atoms = AtomsAt(runs[i], sourceMs);
                if (atoms.Count > 0)
                    rows.Add(new KeyboardRow(atoms, runs[i].IsChord, entryRaw, exitRaw));
            }

            rows.Reverse();
            return rows;
        }

        // --------------------------------------------------------------------------------- wrap

        /// <summary>Greedy wrap of one row's atoms at <paramref name="maxWidth"/> canvas px, the
        /// typing pill's own padding counted in (see <see cref="IKeyAtomMetrics.Edge"/>). Caps are
        /// indivisible; a single word wider than the line hard-breaks by character. Never returns
        /// an empty line.</summary>
        internal static List<List<KeyAtom>> WrapAtoms(IReadOnlyList<KeyAtom> atoms,
            IKeyAtomMetrics metrics, float maxWidth)
        {
            var lines = new List<List<KeyAtom>>();
            if (atoms == null || atoms.Count == 0)
                return lines;

            var current = new List<KeyAtom>();
            float body = 0;

            void Break()
            {
                if (current.Count == 0)
                    return;
                lines.Add(current);
                current = new List<KeyAtom>();
                body = 0;
            }

            void Append(KeyAtom atom, float atomWidth)
            {
                if (current.Count > 0)
                    body += metrics.Gap(current[^1], atom);
                current.Add(atom);
                body += atomWidth;
            }

            // what the line would measure with this atom on the end, edges included
            float WidthWith(KeyAtom atom, float atomWidth) => current.Count == 0
                ? 2 * metrics.Edge(atom) + atomWidth
                : metrics.Edge(current[0]) + body + metrics.Gap(current[^1], atom)
                  + atomWidth + metrics.Edge(atom);

            foreach (var atom in atoms)
            {
                float atomWidth = metrics.Width(atom);

                if (current.Count > 0 && WidthWith(atom, atomWidth) > maxWidth)
                    Break();

                if (current.Count == 0 && atom.Kind == KeyAtomKind.Word
                    && WidthWith(atom, atomWidth) > maxWidth)
                {
                    // the word alone overflows the line: break it by character, leaving room for
                    // the pill padding a chunk on a line of its own still pays for
                    float budget = maxWidth - 2 * metrics.Edge(atom);
                    foreach (var piece in SplitWord(atom.Text, metrics, budget))
                    {
                        var chunk = new KeyAtom(KeyAtomKind.Word, piece);
                        float chunkWidth = metrics.Width(chunk);
                        if (current.Count > 0 && WidthWith(chunk, chunkWidth) > maxWidth)
                            Break();
                        Append(chunk, chunkWidth);
                    }
                    continue;
                }

                Append(atom, atomWidth);
            }

            Break();
            return lines;
        }

        /// <summary>The width one laid-out line draws at: every atom, the gaps between them, and
        /// the pill padding its two ends carry.</summary>
        internal static float LineWidth(IReadOnlyList<KeyAtom> line, IKeyAtomMetrics metrics)
        {
            if (line == null || line.Count == 0)
                return 0;

            float width = metrics.Edge(line[0]) + metrics.Edge(line[^1]);
            for (int i = 0; i < line.Count; i++)
            {
                if (i > 0)
                    width += metrics.Gap(line[i - 1], line[i]);
                width += metrics.Width(line[i]);
            }
            return width;
        }

        private static IEnumerable<string> SplitWord(string word, IKeyAtomMetrics metrics, float maxWidth)
        {
            var current = new StringBuilder();
            foreach (var c in word)
            {
                current.Append(c);
                if (current.Length > 1
                    && metrics.Width(new KeyAtom(KeyAtomKind.Word, current.ToString())) > maxWidth)
                {
                    current.Length -= 1;
                    yield return current.ToString();
                    current.Clear();
                    current.Append(c);
                }
            }

            if (current.Length > 0)
                yield return current.ToString();
        }

        // -------------------------------------------------------------------------------- cache

        /// <summary>
        /// Process-wide run cache keyed by (capture path, pause-break, filter): segmentation runs
        /// once per distinct parameter triple, however many frames are composed (the ImageCache
        /// pattern — immutable after load, failures included: a missing file caches its empty run
        /// list). The keyboard platform is not part of the key and must not be: it is a property
        /// of the file the path names, so the path already decides it.
        /// </summary>
        internal static IReadOnlyList<KeyRun> GetRuns(string capturePath, int pauseBreakMs,
            KeystrokeFilter filter = KeystrokeFilter.None)
        {
            if (string.IsNullOrEmpty(capturePath))
                return Array.Empty<KeyRun>();

            string key = pauseBreakMs.ToString(CultureInfo.InvariantCulture)
                + "|" + (int)filter + "|" + capturePath;
            lock (CacheSync)
            {
                if (!Cache.TryGetValue(key, out var runs))
                {
                    var capture = InputCapture.Get(capturePath);
                    runs = Segment(capture.Events, pauseBreakMs, filter, PlatformOf(capture.Header));
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
