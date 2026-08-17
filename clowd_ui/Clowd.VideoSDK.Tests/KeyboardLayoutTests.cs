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
    // linger/fade visibility windows, wrapping, the run cache, and compose-level pixel checks
    // (pills draw at the anchor; keyboard tracks skip the zoom matrix).
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
            Assert.Equal("ab ⌫ Tab Left c", runs[0].FullText);
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
            Assert.Equal("VK250", KeyboardLayout.VkName(250));
        }

        // --------------------------------------------------------------------------- visibility

        private static KeyRun Run(double startMs, double endMs) =>
            KeyboardLayout.Segment(new List<InputEvent>
            {
                Kd(startMs, 65, "a"), Kd(endMs, 66, "b"),
            }, pauseBreakMs: int.MaxValue)[0];

        [Fact]
        public void Run_opacity_holds_through_linger_then_fades()
        {
            var run = Run(0, 1000);

            Assert.Equal(0, KeyboardLayout.RunOpacityAt(run, -1, 1, 300, 250));
            Assert.Equal(1, KeyboardLayout.RunOpacityAt(run, 500, 1, 300, 250));  // mid-run
            Assert.Equal(1, KeyboardLayout.RunOpacityAt(run, 1300, 1, 300, 250)); // linger edge
            Assert.Equal(0.5, KeyboardLayout.RunOpacityAt(run, 1425, 1, 300, 250), 6);
            Assert.Equal(0, KeyboardLayout.RunOpacityAt(run, 1551, 1, 300, 250));
        }

        [Fact]
        public void Zero_fade_cuts_hard_and_speed_scales_the_windows_to_project_time()
        {
            var run = Run(0, 1000);
            Assert.Equal(0, KeyboardLayout.RunOpacityAt(run, 1301, 1, 300, 0));

            // at 2x speed a 600ms source delta is 300ms of project time — still lingering
            Assert.Equal(1, KeyboardLayout.RunOpacityAt(run, 1600, 2, 300, 250));
            Assert.True(KeyboardLayout.RunOpacityAt(run, 1600, 1, 300, 250) < 1);
        }

        [Fact]
        public void Visible_rows_stack_oldest_first_and_drop_faded_runs()
        {
            var runs = KeyboardLayout.Segment(new List<InputEvent>
            {
                Kd(0, 65, "a"), Kd(100, 66, "b"),
                Kd(2000, 67, "c"), Kd(2100, 68, "d"),
            }, pauseBreakMs: 1000);
            Assert.Equal(2, runs.Count);

            // both inside their windows (long linger)
            var rows = KeyboardLayout.VisibleRowsAt(runs, 2150, 1, lingerMs: 5000, fadeMs: 0);
            Assert.Equal(2, rows.Count);
            Assert.Equal("ab", rows[0].Text);  // oldest first (top)
            Assert.Equal("cd", rows[1].Text);  // active run last (bottom)
            Assert.Equal(1, rows[1].Opacity);

            // short linger: the first run has faded out, the active one remains — and the
            // active run shows only its typed prefix
            rows = KeyboardLayout.VisibleRowsAt(runs, 2050, 1, lingerMs: 300, fadeMs: 250);
            Assert.Single(rows);
            Assert.Equal("c", rows[0].Text);

            // long after everything: nothing
            Assert.Empty(KeyboardLayout.VisibleRowsAt(runs, 60_000, 1, 300, 250));
            // before everything: nothing
            Assert.Empty(KeyboardLayout.VisibleRowsAt(runs, -50, 1, 300, 250));
        }

        // --------------------------------------------------------------------------------- wrap

        [Fact]
        public void Wrap_breaks_at_word_boundaries()
        {
            using var font = new SKFont(SKTypeface.CreateDefault(), 20);
            float word = font.MeasureText("aaaa");

            var lines = KeyboardLayout.Wrap("aaaa bbbb cccc", font, word * 2);
            Assert.True(lines.Count >= 2, $"expected a wrap, got {lines.Count} line(s)");
            foreach (var line in lines)
                Assert.True(font.MeasureText(line) <= word * 2 + 0.5f);
            Assert.Equal("aaaa bbbb cccc", string.Join(" ", lines));
        }

        [Fact]
        public void Wrap_hard_breaks_an_overlong_word()
        {
            using var font = new SKFont(SKTypeface.CreateDefault(), 20);
            float max = font.MeasureText("aaaa");

            var lines = KeyboardLayout.Wrap("aaaaaaaaaaaaaaaa", font, max);
            Assert.True(lines.Count > 1);
            foreach (var line in lines)
                Assert.True(font.MeasureText(line) <= max + 0.5f);
            Assert.Equal("aaaaaaaaaaaaaaaa", string.Concat(lines));
        }

        [Fact]
        public void Wrap_of_nothing_is_no_lines()
        {
            using var font = new SKFont(SKTypeface.CreateDefault(), 20);
            Assert.Empty(KeyboardLayout.Wrap("", font, 100));
            Assert.Empty(KeyboardLayout.Wrap(null, font, 100));
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

        private static Project NewProject() => new Project
        {
            Output = new OutputSettings { WidthPx = W, HeightPx = H, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
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

        private static byte[] Render(Project p, long timeTicks)
        {
            using var factory = new CpuSurfaceFactory();
            using var surface = factory.CreateSurface(W, H);
            FrameComposer.Compose(p, timeTicks, null, surface.Canvas, W, H);

            int rowBytes = W * 4;
            var native = Marshal.AllocHGlobal(rowBytes * H);
            try
            {
                Assert.True(factory.TryReadPixels(surface, W, H, native, rowBytes));
                var pixels = new byte[rowBytes * H];
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

            content = new KeyboardContent { SourceId = source.Id, FontSize = 10 };
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
        public void Keyboard_overlay_fades_out_after_linger_and_fade()
        {
            var p = KeyboardProject(out var content);
            content.LingerMs = 300;
            content.FadeMs = 200;

            // 100(end) + 300 + 200 = 600ms: gone at 700ms
            Assert.True(AnyInk(Render(p, (long)(0.35 * Sec)), 0, H));  // lingering
            Assert.False(AnyInk(Render(p, (long)(0.70 * Sec)), 0, H)); // faded out

            // half-way through the fade the pill is translucent, not gone
            var mid = Render(p, (long)(0.50 * Sec));
            Assert.True(AnyInk(mid, 30, 58));
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
    }
}
