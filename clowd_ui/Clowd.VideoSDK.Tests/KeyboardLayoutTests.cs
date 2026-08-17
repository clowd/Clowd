using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // The keyboard overlay's layout engine: run segmentation (pause break, Enter/Esc, chords),
    // the atoms a row draws as (words, keycaps, chord pluses), the entry/linger/exit windows,
    // wrapping, the run cache, and compose-level pixel checks (pills draw at the anchor, keycaps
    // are two layers deep, rows animate one at a time, keyboard tracks skip the zoom matrix).
    public class KeyboardLayoutTests
    {
        private const long Sec = 10_000_000;
        private const int W = 64, H = 64;

        // ------------------------------------------------------------------------ event builders

        private static InputEvent Kd(double t, int vk, string ch = null) =>
            new InputEvent(t, InputEventKind.KeyDown, vk, ch, 0, 0);

        private static InputEvent Ku(double t, int vk) =>
            new InputEvent(t, InputEventKind.KeyUp, vk, null, 0, 0);

        /// <summary>Types a string as kd events, 50ms apart, letters carrying their character.</summary>
        private static void Type(List<InputEvent> events, ref double t, string text)
        {
            foreach (var c in text)
            {
                int vk = char.IsLetter(c) ? char.ToUpperInvariant(c) : c == ' ' ? 32 : c;
                events.Add(Kd(t, vk, c.ToString()));
                t += 50;
            }
        }

        // ------------------------------------------------------------------------- segmentation

        [Fact]
        public void Spec_example_segments_into_three_runs()
        {
            // "Hello World [enter] This is a keyboard track [ctrl+K+Y]" → 3 runs
            var events = new List<InputEvent>();
            double t = 0;
            Type(events, ref t, "Hello World");
            events.Add(Kd(t, 13)); // Enter, no ch
            t += 500;
            Type(events, ref t, "This is a keyboard track");
            events.Add(Kd(t, 162)); // LCtrl down
            t += 50;
            events.Add(Kd(t, 75)); // K
            t += 50;
            events.Add(Kd(t, 89)); // Y
            t += 50;
            events.Add(Ku(t, 162));

            var runs = KeyboardLayout.Segment(events, pauseBreakMs: 1000);

            Assert.Equal(3, runs.Count);
            Assert.Equal("Hello World Enter", runs[0].FullText);
            Assert.Equal("This is a keyboard track", runs[1].FullText);
            Assert.Equal("Ctrl+K+Y", runs[2].FullText);
            Assert.True(runs[2].IsChord);
            Assert.False(runs[0].IsChord);
        }

        [Fact]
        public void Pause_gap_splits_runs()
        {
            var runs = KeyboardLayout.Segment(new List<InputEvent>
            {
                Kd(0, 65, "a"),
                Kd(1500, 66, "b"), // 1500ms gap > 1000
            }, pauseBreakMs: 1000);

            Assert.Equal(2, runs.Count);
            Assert.Equal("a", runs[0].FullText);
            Assert.Equal("b", runs[1].FullText);

            // a gap at exactly the threshold does NOT split
            var joined = KeyboardLayout.Segment(new List<InputEvent>
            {
                Kd(0, 65, "a"),
                Kd(1000, 66, "b"),
            }, pauseBreakMs: 1000);
            Assert.Single(joined);
            Assert.Equal("ab", joined[0].FullText);
        }

        [Fact]
        public void Enter_and_esc_close_the_run_with_the_key_included()
        {
            var runs = KeyboardLayout.Segment(new List<InputEvent>
            {
                Kd(0, 65, "a"),
                Kd(50, 27), // Esc
                Kd(100, 66, "b"),
            }, pauseBreakMs: 1000);

            Assert.Equal(2, runs.Count);
            Assert.Equal("a Esc", runs[0].FullText);
            Assert.Equal("b", runs[1].FullText);
        }

        [Fact]
        public void Special_keys_render_as_tokens()
        {
            var runs = KeyboardLayout.Segment(new List<InputEvent>
            {
                Kd(0, 65, "a"),
                Kd(50, 66, "b"),
                Kd(100, 8),  // Backspace
                Kd(150, 9),  // Tab
                Kd(200, 37), // Left arrow
                Kd(250, 67, "c"),
            }, pauseBreakMs: 1000);

            Assert.Single(runs);
            Assert.Equal("ab Bksp Tab Left c", runs[0].FullText);
        }

        [Fact]
        public void Shortcuts_filter_keeps_only_chords()
        {
            var events = new List<InputEvent>();
            double t = 0;
            Type(events, ref t, "hello");
            events.Add(Kd(t, 27)); // Esc — special, but not a chord
            t += 50;
            events.Add(Kd(t, 162)); // Ctrl down
            t += 50;
            events.Add(Kd(t, 67));
            t += 50;
            events.Add(Ku(t, 162));

            var runs = KeyboardLayout.Segment(events, pauseBreakMs: 1000, KeystrokeFilter.Shortcuts);

            var run = Assert.Single(runs);
            Assert.True(run.IsChord);
            Assert.Equal("Ctrl+C", run.FullText);
        }

        [Fact]
        public void Special_filter_keeps_keycaps_and_chords_and_drops_typing()
        {
            // the typing between the two special keys neither joins the run nor breaks it — the
            // specials group by their own gap
            var events = new List<InputEvent>();
            double t = 0;
            Type(events, ref t, "abc");
            events.Add(Kd(t, 8)); // Bksp
            t += 50;
            Type(events, ref t, "de");
            events.Add(Kd(t, 27)); // Esc closes the run, key included
            t += 50;
            events.Add(Kd(t, 162)); // Ctrl down
            t += 50;
            events.Add(Kd(t, 75)); // K
            t += 50;
            events.Add(Ku(t, 162));

            var runs = KeyboardLayout.Segment(events, pauseBreakMs: 1000, KeystrokeFilter.Special);

            Assert.Equal(2, runs.Count);
            Assert.Equal("Bksp Esc", runs[0].FullText);
            Assert.False(runs[0].IsChord);
            Assert.Equal("Ctrl+K", runs[1].FullText);
            Assert.True(runs[1].IsChord);
        }

        [Fact]
        public void Filtered_runs_are_cached_per_filter()
        {
            string path = Path.Combine(Path.GetTempPath(), $"kbfilter-{Guid.NewGuid():N}.jsonl");
            File.WriteAllLines(path, new[]
            {
                """{"type":"header","version":2,"region":[0,0,1920,1080],"fps_num":30,"fps_den":1,"platform":"windows"}""",
                """{"type":"event","t":0,"kind":"kd","vk":65,"ch":"a"}""",
                """{"type":"event","t":50,"kind":"kd","vk":27}""",
            });
            try
            {
                var all = KeyboardLayout.GetRuns(path, 1000);
                var special = KeyboardLayout.GetRuns(path, 1000, KeystrokeFilter.Special);
                Assert.NotSame(all, special);
                Assert.Same(special, KeyboardLayout.GetRuns(path, 1000, KeystrokeFilter.Special));
                Assert.Equal("a Esc", Assert.Single(all).FullText);
                Assert.Equal("Esc", Assert.Single(special).FullText);
                Assert.Empty(KeyboardLayout.GetRuns(path, 1000, KeystrokeFilter.Shortcuts));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Shift_alone_never_chords()
        {
            var runs = KeyboardLayout.Segment(new List<InputEvent>
            {
                Kd(0, 160),      // LShift down
                Kd(50, 65, "A"), // translated char already uppercase
                Ku(100, 160),
            }, pauseBreakMs: 1000);

            Assert.Single(runs);
            Assert.Equal("A", runs[0].FullText);
            Assert.False(runs[0].IsChord);
        }

        [Fact]
        public void Chord_includes_shift_when_a_real_modifier_is_down()
        {
            var runs = KeyboardLayout.Segment(new List<InputEvent>
            {
                Kd(0, 162),  // Ctrl
                Kd(20, 160), // Shift
                Kd(50, 80),  // P
                Ku(80, 160),
                Ku(100, 162),
            }, pauseBreakMs: 1000);

            Assert.Single(runs);
            Assert.Equal("Ctrl+Shift+P", runs[0].FullText);
        }

        [Fact]
        public void Chord_closes_on_modifier_release_and_typing_resumes_in_a_new_run()
        {
            var events = new List<InputEvent>
            {
                Kd(0, 65, "a"),
                Kd(100, 162),
                Kd(150, 75), // Ctrl+K ends the text run
                Ku(200, 162),
                Kd(300, 66, "b"),
            };
            var runs = KeyboardLayout.Segment(events, pauseBreakMs: 1000);

            Assert.Equal(3, runs.Count);
            Assert.Equal("a", runs[0].FullText);
            Assert.Equal("Ctrl+K", runs[1].FullText);
            Assert.Equal("b", runs[2].FullText);
        }

        [Fact]
        public void Modifier_presses_alone_render_nothing()
        {
            var runs = KeyboardLayout.Segment(new List<InputEvent>
            {
                Kd(0, 162), Ku(100, 162), Kd(200, 164), Ku(300, 164), Kd(400, 91), Ku(500, 91),
            }, pauseBreakMs: 1000);
            Assert.Empty(runs);
        }

        [Fact]
        public void Mouse_events_are_ignored()
        {
            var runs = KeyboardLayout.Segment(new List<InputEvent>
            {
                Kd(0, 65, "a"),
                new InputEvent(50, InputEventKind.MouseDown, 1, null, 5, 5),
                new InputEvent(60, InputEventKind.MouseUp, 1, null, 5, 5),
                Kd(100, 66, "b"),
            }, pauseBreakMs: 1000);

            Assert.Single(runs);
            Assert.Equal("ab", runs[0].FullText);
        }

        [Fact]
        public void TextAt_shows_only_the_keys_typed_so_far()
        {
            var runs = KeyboardLayout.Segment(new List<InputEvent>
            {
                Kd(0, 65, "a"), Kd(100, 66, "b"), Kd(200, 67, "c"),
            }, pauseBreakMs: 1000);

            Assert.Single(runs);
            Assert.Equal("", runs[0].TextAt(-1));
            Assert.Equal("a", runs[0].TextAt(50));
            Assert.Equal("ab", runs[0].TextAt(100));
            Assert.Equal("abc", runs[0].TextAt(9999));
            Assert.Equal(0, runs[0].StartMs);
            Assert.Equal(200, runs[0].EndMs);
        }

        [Fact]
        public void Vk_names_cover_the_common_keys()
        {
            Assert.Equal("Left", KeyboardLayout.VkName(37));
            Assert.Equal("F1", KeyboardLayout.VkName(112));
            Assert.Equal("F24", KeyboardLayout.VkName(135));
            Assert.Equal("A", KeyboardLayout.VkName(65));
            Assert.Equal("7", KeyboardLayout.VkName(55));
            Assert.Equal("0", KeyboardLayout.VkName(96)); // numpad
            Assert.Equal("\\", KeyboardLayout.VkName(220));
            Assert.Equal("Space", KeyboardLayout.VkName(32));
            Assert.Equal("Bksp", KeyboardLayout.VkName(8));
            Assert.Equal("VK232", KeyboardLayout.VkName(232)); // unassigned
        }

        /// <summary>
        /// No tofu, ever. The overlay draws with the platform's default typeface, so every label a
        /// virtual key can produce has to be a string that typeface actually has glyphs for — the
        /// reason the symbol keys (⌫ ⇧ ↵) are spelled out and drawn as vector icons instead.
        /// </summary>
        [Fact]
        public void Every_vk_label_draws_real_glyphs_in_the_default_typeface()
        {
            using var typeface = SKTypeface.CreateDefault();
            using var font = new SKFont(typeface, 28);

            for (int vk = 1; vk <= 255; vk++)
            {
                var label = KeyboardLayout.VkName(vk);
                Assert.False(string.IsNullOrWhiteSpace(label), $"VK {vk} has no label");
                Assert.True(font.ContainsGlyphs(label), $"VK {vk} label '{label}' has a missing glyph");
                Assert.All(font.GetGlyphs(label), g => Assert.NotEqual(0, g)); // 0 == .notdef
                Assert.True(font.MeasureText(label) > 0, $"VK {vk} label '{label}' measures empty");
            }
        }

        // ---------------------------------------------------------------------------- row atoms

        private static KeyRun OneRun(params InputEvent[] events) =>
            KeyboardLayout.Segment(events, pauseBreakMs: int.MaxValue)[0];

        [Fact]
        public void Typing_atoms_are_words_split_on_the_typed_spaces()
        {
            var events = new List<InputEvent>();
            double t = 0;
            Type(events, ref t, "Hello World");

            var atoms = KeyboardLayout.AtomsAt(OneRun(events.ToArray()), double.MaxValue);

            Assert.Equal(2, atoms.Count);
            Assert.All(atoms, a => Assert.Equal(KeyAtomKind.Word, a.Kind));
            Assert.Equal("Hello", atoms[0].Text);
            Assert.Equal("World", atoms[1].Text);
        }

        [Fact]
        public void Special_keys_inside_a_typing_run_become_keycaps()
        {
            var run = OneRun(Kd(0, 65, "a"), Kd(50, 66, "b"), Kd(100, 8), Kd(150, 67, "c"));
            var atoms = KeyboardLayout.AtomsAt(run, double.MaxValue);

            Assert.Equal(3, atoms.Count);
            Assert.Equal(new[] { KeyAtomKind.Word, KeyAtomKind.Cap, KeyAtomKind.Word },
                atoms.Select(a => a.Kind));
            Assert.Equal("Bksp", atoms[1].Text);
        }

        /// <summary>A chord is N keycaps with a "+" between them — never one long string.</summary>
        [Fact]
        public void A_chord_is_one_keycap_per_member_joined_by_pluses()
        {
            var run = OneRun(Kd(0, 162), Kd(20, 75), Kd(40, 74), Ku(60, 162));
            var atoms = KeyboardLayout.AtomsAt(run, double.MaxValue);

            Assert.Equal(new[]
            {
                KeyAtomKind.Cap, KeyAtomKind.Plus, KeyAtomKind.Cap, KeyAtomKind.Plus, KeyAtomKind.Cap,
            }, atoms.Select(a => a.Kind));
            Assert.Equal(new[] { "Ctrl", "K", "J" },
                atoms.Where(a => a.Kind == KeyAtomKind.Cap).Select(a => a.Text));
        }

        [Fact]
        public void Atoms_only_hold_the_keys_pressed_so_far()
        {
            var run = OneRun(Kd(0, 65, "a"), Kd(100, 8), Kd(200, 66, "b"));

            Assert.Empty(KeyboardLayout.AtomsAt(run, -1));
            Assert.Single(KeyboardLayout.AtomsAt(run, 0));
            Assert.Equal(2, KeyboardLayout.AtomsAt(run, 100).Count);
            Assert.Equal(3, KeyboardLayout.AtomsAt(run, 999).Count);
        }

        // --------------------------------------------------------------------------- visibility

        private static KeyRun Run(double startMs, double endMs) =>
            OneRun(Kd(startMs, 65, "a"), Kd(endMs, 66, "b"));

        private static double ExitAt(KeyRun run, double sourceMs, double speed, int lingerMs, int exitMs)
        {
            KeyboardLayout.RunPhaseAt(run, sourceMs, speed, lingerMs, 0, exitMs, out _, out var exit);
            return exit;
        }

        [Fact]
        public void A_row_holds_through_its_linger_then_plays_its_exit()
        {
            var run = Run(0, 1000);

            Assert.False(KeyboardLayout.RunPhaseAt(run, -1, 1, 300, 0, 250, out _, out _));
            Assert.Equal(1, ExitAt(run, 500, 1, 300, 250));  // mid-run
            Assert.Equal(1, ExitAt(run, 1300, 1, 300, 250)); // linger edge
            Assert.Equal(0.5, ExitAt(run, 1425, 1, 300, 250), 6);
            Assert.False(KeyboardLayout.RunPhaseAt(run, 1551, 1, 300, 0, 250, out _, out _));
        }

        [Fact]
        public void A_row_enters_over_the_entry_window_from_its_first_key()
        {
            var run = Run(0, 1000);

            Assert.True(KeyboardLayout.RunPhaseAt(run, 0, 1, 300, 400, 250, out var entry, out _));
            Assert.Equal(0, entry);
            KeyboardLayout.RunPhaseAt(run, 200, 1, 300, 400, 250, out entry, out _);
            Assert.Equal(0.5, entry, 6);
            KeyboardLayout.RunPhaseAt(run, 900, 1, 300, 400, 250, out entry, out _);
            Assert.Equal(1, entry);

            // no entry animation at all: the row is simply there
            KeyboardLayout.RunPhaseAt(run, 0, 1, 300, 0, 250, out entry, out _);
            Assert.Equal(1, entry);
        }

        [Fact]
        public void Zero_exit_cuts_hard_and_speed_scales_the_windows_to_project_time()
        {
            var run = Run(0, 1000);
            Assert.False(KeyboardLayout.RunPhaseAt(run, 1301, 1, 300, 0, 0, out _, out _));

            // at 2x speed a 600ms source delta is 300ms of project time — still lingering
            Assert.Equal(1, ExitAt(run, 1600, 2, 300, 250));
            Assert.True(ExitAt(run, 1600, 1, 300, 250) < 1);
        }

        [Fact]
        public void Visible_rows_stack_oldest_first_and_drop_finished_runs()
        {
            var runs = KeyboardLayout.Segment(new List<InputEvent>
            {
                Kd(0, 65, "a"), Kd(100, 66, "b"),
                Kd(2000, 67, "c"), Kd(2100, 68, "d"),
            }, pauseBreakMs: 1000);
            Assert.Equal(2, runs.Count);

            // both inside their windows (long linger)
            var rows = KeyboardLayout.VisibleRowsAt(runs, 2150, 1, lingerMs: 5000, entryMs: 0, exitMs: 0);
            Assert.Equal(2, rows.Count);
            Assert.Equal("ab", rows[0].Atoms.Single().Text);  // oldest first (top)
            Assert.Equal("cd", rows[1].Atoms.Single().Text);  // active run last (bottom)
            Assert.Equal(1, rows[1].ExitRaw);

            // short linger: the first run has left, the active one remains — and the active run
            // shows only its typed prefix
            rows = KeyboardLayout.VisibleRowsAt(runs, 2050, 1, 300, 0, 250);
            Assert.Single(rows);
            Assert.Equal("c", rows[0].Atoms.Single().Text);

            // long after everything: nothing
            Assert.Empty(KeyboardLayout.VisibleRowsAt(runs, 60_000, 1, 300, 0, 250));
            // before everything: nothing
            Assert.Empty(KeyboardLayout.VisibleRowsAt(runs, -50, 1, 300, 0, 250));
        }

        // --------------------------------------------------------------------------------- wrap

        /// <summary>Wrap arithmetic without a font: a character is 10 wide, a keycap 30, a plus 8,
        /// any two neighbours sit 5 apart, and a run of words pays 5 of pill padding at each end
        /// of the line it starts or finishes.</summary>
        private sealed class FakeMetrics : IKeyAtomMetrics
        {
            public float Width(KeyAtom atom) => atom.Kind switch
            {
                KeyAtomKind.Cap => 30,
                KeyAtomKind.Plus => 8,
                _ => atom.Text.Length * 10,
            };

            public float Gap(KeyAtom left, KeyAtom right) => 5;

            public float Edge(KeyAtom atom) => atom.Kind == KeyAtomKind.Word ? 5 : 0;
        }

        private static KeyAtom Word(string text) => new KeyAtom(KeyAtomKind.Word, text);

        private static KeyAtom Cap(string text) => new KeyAtom(KeyAtomKind.Cap, text);

        [Fact]
        public void Wrap_breaks_between_atoms()
        {
            var atoms = new[] { Word("aaaa"), Word("bbbb"), Word("cccc") };

            // "aaaa bbbb" measures 5 + 40 + 5 + 40 + 5 = 95, exactly the box
            var lines = KeyboardLayout.WrapAtoms(atoms, new FakeMetrics(), 95);

            Assert.Equal(2, lines.Count);
            Assert.Equal(new[] { "aaaa", "bbbb" }, lines[0].Select(a => a.Text));
            Assert.Equal(new[] { "cccc" }, lines[1].Select(a => a.Text));
        }

        [Fact]
        public void Wrap_hard_breaks_an_overlong_word()
        {
            var metrics = new FakeMetrics();
            var lines = KeyboardLayout.WrapAtoms(new[] { Word("aaaaaaaa") }, metrics, 35);

            Assert.True(lines.Count > 1);
            // the pill padding a chunk still pays for is inside the budget, not on top of it
            Assert.All(lines, line => Assert.True(KeyboardLayout.LineWidth(line, metrics) <= 35));
            Assert.Equal("aaaaaaaa", string.Concat(lines.SelectMany(l => l).Select(a => a.Text)));
        }

        [Fact]
        public void Wrap_never_splits_a_keycap()
        {
            // the cap does not fit beside the word and still comes out whole, on its own row
            var lines = KeyboardLayout.WrapAtoms(new[] { Word("aa"), Cap("Enter") }, new FakeMetrics(), 35);

            Assert.Equal(2, lines.Count);
            Assert.Equal("aa", lines[0].Single().Text);
            Assert.Equal("Enter", lines[1].Single().Text);
        }

        /// <summary>The pill's own padding is part of what the wrap measures — a bare keycap
        /// carries none, so a line ending on one is that much narrower.</summary>
        [Fact]
        public void Line_width_counts_the_pill_padding_only_where_a_pill_is()
        {
            var metrics = new FakeMetrics();

            Assert.Equal(30, KeyboardLayout.LineWidth(new[] { Cap("Esc") }, metrics));
            Assert.Equal(30, KeyboardLayout.LineWidth(new[] { Word("aa") }, metrics)); // 5 + 20 + 5
            Assert.Equal(0, KeyboardLayout.LineWidth(Array.Empty<KeyAtom>(), metrics));
        }

        [Fact]
        public void Wrap_of_nothing_is_no_lines()
        {
            Assert.Empty(KeyboardLayout.WrapAtoms(Array.Empty<KeyAtom>(), new FakeMetrics(), 100));
            Assert.Empty(KeyboardLayout.WrapAtoms(null, new FakeMetrics(), 100));
        }

        [Fact]
        public void Line_width_counts_the_gaps_between_atoms()
        {
            var line = new[] { Word("aa"), Cap("Esc") }; // 5 + 20 + 5 + 30 + 0
            Assert.Equal(60, KeyboardLayout.LineWidth(line, new FakeMetrics()));
        }

        // -------------------------------------------------------------------------------- cache

        [Fact]
        public void Run_cache_is_keyed_by_path_and_pause_break()
        {
            string path = WriteCapture(
                "{\"type\":\"event\",\"t\":0,\"kind\":\"kd\",\"vk\":65,\"ch\":\"a\"}",
                "{\"type\":\"event\",\"t\":1500,\"kind\":\"kd\",\"vk\":66,\"ch\":\"b\"}");

            var first = KeyboardLayout.GetRuns(path, 1000);
            Assert.Same(first, KeyboardLayout.GetRuns(path, 1000)); // cached instance
            Assert.Equal(2, first.Count);

            var wide = KeyboardLayout.GetRuns(path, 2000); // different pause: different runs
            Assert.NotSame(first, wide);
            Assert.Single(wide);

            Assert.Empty(KeyboardLayout.GetRuns(
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.jsonl"), 1000));
            Assert.Empty(KeyboardLayout.GetRuns(null, 1000));
        }

        // ------------------------------------------------------------------------ compose pixels

        private static string WriteCapture(params string[] lines)
        {
            string path = Path.Combine(Path.GetTempPath(), $"clowd-keys-test-{Guid.NewGuid():N}.jsonl");
            File.WriteAllLines(path, lines);
            return path;
        }

        private static Project NewProject(int width = W, int height = H) => new Project
        {
            Output = new OutputSettings
            {
                WidthPx = width, HeightPx = height, FpsNum = 30, FpsDen = 1, SampleRate = 48000,
            },
        };

        private static Track AddTrack(Project p, TrackKind kind, int order)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = kind, Order = order };
            p.Tracks.Add(track);
            return track;
        }

        private static Item AddItem(Project p, Track track, ItemContent content,
            long start = 0, long duration = 10 * Sec)
        {
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = start,
                DurationTicks = duration,
                Content = content,
                LinkGroupId = Guid.NewGuid(),
            };
            p.Items.Add(item);
            return item;
        }

        private static byte[] Render(Project p, long timeTicks) => RenderAt(p, timeTicks, W, H);

        private static byte[] RenderAt(Project p, long timeTicks, int width, int height)
        {
            using var factory = new CpuSurfaceFactory();
            using var surface = factory.CreateSurface(width, height);
            FrameComposer.Compose(p, timeTicks, null, surface.Canvas, width, height);

            int rowBytes = width * 4;
            var native = Marshal.AllocHGlobal(rowBytes * height);
            try
            {
                Assert.True(factory.TryReadPixels(surface, width, height, native, rowBytes));
                var pixels = new byte[rowBytes * height];
                Marshal.Copy(native, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        private static bool AnyInk(byte[] bgra, int y0, int y1, int x0 = 0, int x1 = W)
        {
            for (int y = Math.Max(0, y0); y < Math.Min(H, y1); y++)
            {
                for (int x = Math.Max(0, x0); x < Math.Min(W, x1); x++)
                {
                    int i = y * W * 4 + x * 4;
                    if (bgra[i] != 0 || bgra[i + 1] != 0 || bgra[i + 2] != 0)
                        return true;
                }
            }
            return false;
        }

        /// <summary>A project with only a keyboard track (time falls back to the item's own
        /// span): two keys at 0/100ms.</summary>
        private static Project KeyboardProject(out KeyboardContent content)
        {
            string capture = WriteCapture(
                "{\"type\":\"event\",\"t\":0,\"kind\":\"kd\",\"vk\":72,\"ch\":\"H\"}",
                "{\"type\":\"event\",\"t\":100,\"kind\":\"kd\",\"vk\":73,\"ch\":\"i\"}");

            var p = NewProject();
            var source = new Source { Id = Guid.NewGuid(), Path = "recording.mp4", InputCapturePath = capture };
            p.Sources.Add(source);

            content = new KeyboardContent { SourceId = source.Id, FontSize = 10, LingerMs = 300 };
            var item = AddItem(p, AddTrack(p, TrackKind.Video, 0), content);
            item.Transform = new Transform { X = 0.5, Y = 0.9, Scale = 1.0 };
            return p;
        }

        [Fact]
        public void Keyboard_item_draws_pills_above_the_anchor()
        {
            var p = KeyboardProject(out _);
            var px = Render(p, (long)(0.2 * Sec)); // 200ms: both keys typed, within linger

            Assert.True(AnyInk(px, 30, 58), "no pill ink above the anchored bottom");
            Assert.False(AnyInk(px, 0, 20), "ink far above the block");
        }

        [Fact]
        public void A_row_leaves_with_the_items_exit_transition()
        {
            var p = KeyboardProject(out var content);
            content.LingerMs = 300;
            var item = p.Items.Single();
            item.Exit = new Transition
            {
                Kind = TransitionKind.Fade,
                DurationTicks = 200 * TimeSpan.TicksPerMillisecond,
                Easing = TransitionEasing.Linear,
            };

            // 100(end) + 300 linger + 200 exit = 600ms: gone at 700ms
            Assert.True(AnyInk(Render(p, (long)(0.35 * Sec)), 0, H));  // lingering
            Assert.True(AnyInk(Render(p, (long)(0.50 * Sec)), 30, 58)); // half-way out, translucent
            Assert.False(AnyInk(Render(p, (long)(0.70 * Sec)), 0, H)); // gone

            // no exit transition at all: the row cuts out the instant its linger expires
            item.Exit = null;
            Assert.True(AnyInk(Render(p, (long)(0.39 * Sec)), 0, H));
            Assert.False(AnyInk(Render(p, (long)(0.41 * Sec)), 0, H));
        }

        [Fact]
        public void Keyboard_track_skips_the_zoom_matrix()
        {
            var p = KeyboardProject(out _);

            // a small centred solid to witness the zoom, beneath a zoom row covering everything.
            // Focus (0,0) makes the zoom a pure 2x scale from the origin: the solid's [24,40)
            // square moves to [48,80), and a zoomed keyboard block (bottom y≈57) would land
            // entirely below the canvas.
            var solid = AddItem(p, AddTrack(p, TrackKind.Video, -1), new SolidContent { Color = "#FFFF0000" });
            solid.Transform = new Transform { Scale = 0.25 };

            AddItem(p, AddTrack(p, TrackKind.Effect, 10),
                new ZoomContent { Zoom = 2.0, FocusX = 0.0, FocusY = 0.0 });

            var px = Render(p, (long)(0.2 * Sec));

            // the solid doubled away from the origin: (50,50) red now, (30,30) no longer
            int i = 50 * W * 4 + 50 * 4;
            Assert.True(px[i + 2] > 200 && px[i] < 50, "solid was not zoomed");
            int j = 30 * W * 4 + 30 * 4;
            Assert.True(px[j + 2] < 50, "solid still at its unzoomed position");

            // the keyboard pill still hugs its unzoomed anchor rows on the canvas' left half,
            // where the zoomed solid never reaches; a zoomed pill would be off-canvas entirely
            Assert.True(AnyInk(px, 38, 58, 0, 46), "keyboard pill left its unzoomed anchor");
        }

        [Fact]
        public void Keyboard_item_with_missing_capture_draws_nothing_and_does_not_throw()
        {
            var p = NewProject();
            var source = new Source
            {
                Id = Guid.NewGuid(),
                Path = "recording.mp4",
                InputCapturePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.jsonl"),
            };
            p.Sources.Add(source);
            AddItem(p, AddTrack(p, TrackKind.Video, 0), new KeyboardContent { SourceId = source.Id });

            Assert.False(AnyInk(Render(p, 1 * Sec), 0, H));
        }

        [Fact]
        public void Keyboard_time_rides_the_linked_screen_item()
        {
            // screen item trimmed: SourceIn = 2s, so at project t=0.2s the capture clock reads
            // 2.2s — keys at 2.0/2.1s are visible, keys at 0ms long faded
            string capture = WriteCapture(
                "{\"type\":\"event\",\"t\":2000,\"kind\":\"kd\",\"vk\":72,\"ch\":\"H\"}",
                "{\"type\":\"event\",\"t\":2100,\"kind\":\"kd\",\"vk\":73,\"ch\":\"i\"}");

            var p = NewProject();
            var source = new Source { Id = Guid.NewGuid(), Path = "recording.mp4", InputCapturePath = capture };
            source.Streams.Add(new SourceStream { Index = 0, Kind = StreamKind.Video, Width = W, Height = H });
            p.Sources.Add(source);

            var group = Guid.NewGuid();
            var screen = AddItem(p, AddTrack(p, TrackKind.Video, 0),
                new MediaContent { SourceId = source.Id, StreamIndex = 0, SourceInTicks = 2 * Sec });
            screen.LinkGroupId = group;

            var keys = AddItem(p, AddTrack(p, TrackKind.Video, 1),
                new KeyboardContent { SourceId = source.Id, FontSize = 10 });
            keys.LinkGroupId = group;
            keys.Transform = new Transform { X = 0.5, Y = 0.9, Scale = 1.0 };

            // without the screen item's clock this instant would show nothing (item-relative
            // time is 200ms, far before the 2000ms keys)
            Assert.True(AnyInk(Render(p, (long)(0.2 * Sec)), 30, 58));
        }

        // --------------------------------------------------------------- keycaps and pill livery

        private const int BigW = 480, BigH = 260;

        /// <summary>A big canvas carrying one keyboard item, with the pill in an opaque colour so
        /// its extent is measurable. <paramref name="white"/> lays a white sheet underneath —
        /// what the keycap's black seat needs to be visible at all.</summary>
        private static Project BigKeyboardProject(string capture, out Item item,
            out KeyboardContent content, bool white = false)
        {
            var p = NewProject(BigW, BigH);
            var source = new Source { Id = Guid.NewGuid(), Path = "recording.mp4", InputCapturePath = capture };
            p.Sources.Add(source);

            if (white)
                AddItem(p, AddTrack(p, TrackKind.Video, -1), new SolidContent { Color = "#FFFFFFFF" });

            content = new KeyboardContent
            {
                SourceId = source.Id,
                FontSize = 44,
                LingerMs = 5000,
                PauseBreakMs = 10_000,
                TextColor = 0xFFFFFFFF,
                BackgroundColor = 0xFFFF0000, // opaque red, so the pill is unmistakable
            };
            item = AddItem(p, AddTrack(p, TrackKind.Video, 0), content);
            item.Transform = new Transform { X = 0.5, Y = 0.75, Scale = 1.0 };
            return p;
        }

        private static (int Left, int Top, int Right, int Bottom)? Bounds(
            byte[] bgra, int width, int height, Func<byte, byte, byte, bool> match)
        {
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    if (!match(bgra[i], bgra[i + 1], bgra[i + 2]))
                        continue;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
            return right < 0 ? null : (left, top, right, bottom);
        }

        /// <summary>
        /// The typed text sits in the middle of its pill: the gap above the ink and the gap below
        /// it agree. Measured off pixels, because the bug this replaces was a baseline computed
        /// from padding rather than from the font's own ascent/descent.
        /// </summary>
        [Fact]
        public void Pill_text_is_vertically_centred_in_its_pill()
        {
            var capture = WriteCapture("{\"type\":\"event\",\"t\":0,\"kind\":\"kd\",\"vk\":72,\"ch\":\"H\"}");
            var p = BigKeyboardProject(capture, out _, out var content);

            var px = RenderAt(p, (long)(0.1 * Sec), BigW, BigH);

            var pill = Bounds(px, BigW, BigH, (b, g, r) => r > 60);            // red pill + white text
            var ink = Bounds(px, BigW, BigH, (b, g, r) => b > 180 && g > 180); // white text only
            Assert.NotNull(pill);
            Assert.NotNull(ink);

            double above = ink.Value.Top - pill.Value.Top;
            double below = pill.Value.Bottom - ink.Value.Bottom;
            Assert.True(Math.Abs(above - below) <= 0.15 * content.FontSize,
                $"text is not centred: {above}px above, {below}px below");
        }

        /// <summary>The pill and its text take their colours from the item — alpha included, so a
        /// translucent fill is just a fill whose alpha the user chose.</summary>
        [Fact]
        public void Pill_and_text_take_the_items_colours()
        {
            var capture = WriteCapture("{\"type\":\"event\",\"t\":0,\"kind\":\"kd\",\"vk\":72,\"ch\":\"H\"}");
            var p = BigKeyboardProject(capture, out _, out var content);
            content.BackgroundColor = 0xFF0000FF; // opaque blue
            content.TextColor = 0xFF00FF00;       // green

            var px = RenderAt(p, (long)(0.1 * Sec), BigW, BigH);

            Assert.NotNull(Bounds(px, BigW, BigH, (b, g, r) => b > 200 && r < 60 && g < 60));
            Assert.NotNull(Bounds(px, BigW, BigH, (b, g, r) => g > 200 && r < 60 && b < 60));

            // half-transparent fill halves the drawn blue over the black canvas
            content.BackgroundColor = 0x800000FF;
            px = RenderAt(p, (long)(0.1 * Sec), BigW, BigH);
            Assert.Null(Bounds(px, BigW, BigH, (b, g, r) => b > 200 && r < 60 && g < 60));
            Assert.NotNull(Bounds(px, BigW, BigH, (b, g, r) => b is > 100 and < 180 && r < 60 && g < 60));
        }

        /// <summary>A special key is a keycap, not text: a dark face with a black seat that sticks
        /// out below it — and no red typing pill anywhere, because the line is all keycap.</summary>
        [Fact]
        public void A_special_key_draws_a_two_layer_keycap()
        {
            var capture = WriteCapture("{\"type\":\"event\",\"t\":0,\"kind\":\"kd\",\"vk\":13}"); // Enter
            var p = BigKeyboardProject(capture, out _, out _, white: true);

            var px = RenderAt(p, (long)(0.1 * Sec), BigW, BigH);

            var face = Bounds(px, BigW, BigH, (b, g, r) => b == 0x3D && g == 0x3D && r == 0x3D);
            Assert.NotNull(face);

            // down the middle of the cap: the grey face, then the black seat peeking out under it,
            // then the white sheet again. (Edges are anti-aliased, so only the solid runs count.)
            int x = (face.Value.Left + face.Value.Right) / 2;
            int lastFace = -1, blackRows = 0;
            for (int y = 0; y < BigH; y++)
            {
                int i = (y * BigW + x) * 4;
                if (px[i] == 0x3D && px[i + 1] == 0x3D && px[i + 2] == 0x3D)
                    lastFace = y;
                else if (lastFace >= 0 && px[i] < 0x10 && px[i + 1] < 0x10 && px[i + 2] < 0x10)
                    blackRows++;
            }

            Assert.True(lastFace >= 0);
            Assert.True(blackRows >= 2,
                $"the 3D seat does not stick out under the cap face ({blackRows} black rows)");

            // no typing pill: the line is all keycap
            Assert.Null(Bounds(px, BigW, BigH, (b, g, r) => r > 100 && g < 60 && b < 60));
        }

        /// <summary>Counts the horizontal runs of pixels matching <paramref name="match"/> along
        /// row <paramref name="y"/> — how many separate rects of a colour a line drew.</summary>
        private static int RunsAcross(byte[] bgra, int y, Func<byte, byte, byte, bool> match)
        {
            int runs = 0;
            bool inside = false;
            for (int x = 0; x < BigW; x++)
            {
                int i = (y * BigW + x) * 4;
                bool hit = match(bgra[i], bgra[i + 1], bgra[i + 2]);
                if (hit && !inside)
                    runs++;
                inside = hit;
            }
            return runs;
        }

        private static bool IsPill(byte b, byte g, byte r) => r > 150 && g < 60 && b < 60;

        private static bool IsFace(byte b, byte g, byte r) =>
            b is > 0x30 and < 0x4A && g == b && r == b;

        /// <summary>
        /// A keycap never sits inside a pill. Typing interrupted by Backspace and resumed draws
        /// pill · keycap · pill — two separate rounded rects with the cap standing free between
        /// them, not one pill with a cap inlaid.
        /// </summary>
        [Fact]
        public void A_keycap_between_words_splits_the_pill_in_two()
        {
            var capture = WriteCapture(
                "{\"type\":\"event\",\"t\":0,\"kind\":\"kd\",\"vk\":83,\"ch\":\"s\"}",
                "{\"type\":\"event\",\"t\":20,\"kind\":\"kd\",\"vk\":79,\"ch\":\"o\"}",
                "{\"type\":\"event\",\"t\":40,\"kind\":\"kd\",\"vk\":8}", // Bksp
                "{\"type\":\"event\",\"t\":60,\"kind\":\"kd\",\"vk\":69,\"ch\":\"e\"}");
            var p = BigKeyboardProject(capture, out _, out _);

            var px = RenderAt(p, (long)(0.1 * Sec), BigW, BigH);
            var pill = Bounds(px, BigW, BigH, IsPill);
            var face = Bounds(px, BigW, BigH, IsFace);
            Assert.NotNull(pill);
            Assert.NotNull(face);

            // sampled just inside the pills' top edge, clear of the glyphs
            int y = pill.Value.Top + 2;
            Assert.Equal(2, RunsAcross(px, y, IsPill));

            // …and the keycap sits in the space between them
            Assert.True(face.Value.Left > pill.Value.Left && face.Value.Right < pill.Value.Right,
                "the keycap is not between the two pills");
        }

        /// <summary>A keycap at the very start or end of a row grows no empty pill beside it —
        /// the padding is the pill's, and there is no pill there.</summary>
        [Fact]
        public void A_keycap_at_the_row_edge_has_no_pill_beside_it()
        {
            var capture = WriteCapture(
                "{\"type\":\"event\",\"t\":0,\"kind\":\"kd\",\"vk\":9}", // Tab, first
                "{\"type\":\"event\",\"t\":20,\"kind\":\"kd\",\"vk\":72,\"ch\":\"h\"}",
                "{\"type\":\"event\",\"t\":40,\"kind\":\"kd\",\"vk\":73,\"ch\":\"i\"}",
                "{\"type\":\"event\",\"t\":60,\"kind\":\"kd\",\"vk\":46}"); // Del, last
            var p = BigKeyboardProject(capture, out _, out _);

            var px = RenderAt(p, (long)(0.1 * Sec), BigW, BigH);
            var pill = Bounds(px, BigW, BigH, IsPill);
            var face = Bounds(px, BigW, BigH, IsFace);
            Assert.NotNull(pill);
            Assert.NotNull(face);

            // exactly one pill, and it is bracketed by the two caps rather than wrapping them
            Assert.Equal(1, RunsAcross(px, pill.Value.Top + 2, IsPill));
            Assert.True(face.Value.Left < pill.Value.Left, "the leading keycap is inside a pill");
            Assert.True(face.Value.Right > pill.Value.Right, "the trailing keycap is inside a pill");
        }

        /// <summary>A chord's members are separate caps: three keys draw three faces across the
        /// row, not one long pill.</summary>
        [Fact]
        public void A_chord_draws_one_keycap_per_member()
        {
            var capture = WriteCapture(
                "{\"type\":\"event\",\"t\":0,\"kind\":\"kd\",\"vk\":162}",
                "{\"type\":\"event\",\"t\":20,\"kind\":\"kd\",\"vk\":75}",
                "{\"type\":\"event\",\"t\":40,\"kind\":\"kd\",\"vk\":74}",
                "{\"type\":\"event\",\"t\":60,\"kind\":\"ku\",\"vk\":162}");
            var p = BigKeyboardProject(capture, out _, out _, white: true);

            var px = RenderAt(p, (long)(0.1 * Sec), BigW, BigH);
            var face = Bounds(px, BigW, BigH, (b, g, r) => b is > 0x30 and < 0x4A && g == b && r == b);
            Assert.NotNull(face);

            // three faces means three runs of grey across the top of the caps — sampled above the
            // legends and icons, which are white and would otherwise break each face in two
            int y = face.Value.Top + 3;
            int runs = 0;
            bool inside = false;
            for (int x = 0; x < BigW; x++)
            {
                int i = (y * BigW + x) * 4;
                bool grey = px[i] is > 0x20 and < 0x4A && px[i] == px[i + 1] && px[i + 1] == px[i + 2];
                if (grey && !inside)
                    runs++;
                inside = grey;
            }

            Assert.Equal(3, runs);
        }

        // ------------------------------------------------------------------- per-row animation

        /// <summary>A sliding entry belongs to the ROW: the newest row is drawn below its settled
        /// slot while it arrives, and the whole block is not faded as one.</summary>
        [Fact]
        public void A_new_row_slides_up_into_place()
        {
            var capture = WriteCapture("{\"type\":\"event\",\"t\":0,\"kind\":\"kd\",\"vk\":72,\"ch\":\"H\"}");
            var p = BigKeyboardProject(capture, out var item, out _);
            item.Entry = new Transition
            {
                Kind = TransitionKind.SlideUp,
                DurationTicks = 400 * TimeSpan.TicksPerMillisecond,
                Easing = TransitionEasing.Linear,
            };

            var arriving = RenderAt(p, (long)(0.1 * Sec), BigW, BigH); // 100ms of 400
            var settled = RenderAt(p, (long)(0.6 * Sec), BigW, BigH);

            var moving = Bounds(arriving, BigW, BigH, (b, g, r) => r > 60);
            var home = Bounds(settled, BigW, BigH, (b, g, r) => r > 60);
            Assert.NotNull(moving);
            Assert.NotNull(home);

            Assert.True(moving.Value.Bottom > home.Value.Bottom,
                "the arriving row was already in its settled slot");
            // …and lands on the anchor (Bounds reports the last drawn row, the edge is one past it)
            Assert.InRange(home.Value.Bottom + 1, 0.75 * BigH - 2, 0.75 * BigH + 2);
        }

        /// <summary>
        /// The item's transitions are spent on the rows, not on the block: a row half-way through
        /// a 400ms fade entry draws at half alpha, not a quarter. (Applying the whole-item
        /// transition as well would multiply the two.)
        /// </summary>
        [Fact]
        public void The_block_does_not_animate_on_top_of_its_rows()
        {
            var capture = WriteCapture("{\"type\":\"event\",\"t\":0,\"kind\":\"kd\",\"vk\":72,\"ch\":\"H\"}");
            var p = BigKeyboardProject(capture, out var item, out _);
            item.Entry = new Transition
            {
                Kind = TransitionKind.Fade,
                DurationTicks = 400 * TimeSpan.TicksPerMillisecond,
                Easing = TransitionEasing.Linear,
            };

            var px = RenderAt(p, (long)(0.2 * Sec), BigW, BigH); // exactly half-way through

            var pill = Bounds(px, BigW, BigH, (b, g, r) => r > 40);
            Assert.NotNull(pill);

            // a pill pixel well inside the rounded rect, clear of the glyph
            int x = pill.Value.Left + 4;
            int y = (pill.Value.Top + pill.Value.Bottom) / 2;
            byte red = px[(y * BigW + x) * 4 + 2];
            Assert.InRange(red, 100, 160); // 0.5 · 255, not 0.25 · 255
        }
    }
}
