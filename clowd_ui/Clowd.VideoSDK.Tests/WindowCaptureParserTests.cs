using System;
using System.IO;
using System.Linq;
using System.Text;
using Clowd.VideoSDK.Composition;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The window-capture JSONL parser: header/window_info/window rows into per-window
    /// time-sorted arrays, the first-seen pick list, the hold-last lookups that make a
    /// window-following crop defined for the whole recording, the all-zero leave sentinel that
    /// is dropped at parse so an absence holds the last real rect, the occlusion pass that marks
    /// a window that spent the whole recording behind others as never visible, forward tolerance (unknown
    /// rows/fields/value shapes skipped, torn lines dropped) and the missing/corrupt degrade to
    /// <see cref="WindowCapture.Empty"/>.
    /// </summary>
    public class WindowCaptureParserTests
    {
        private const string Header =
            """{"type":"header","version":1,"region":[-100,50,1920,1080],"fps_num":30,"fps_den":1,"platform":"windows"}""";

        private static WindowCapture Parse(params string[] lines) =>
            WindowCapture.Parse(Encoding.UTF8.GetBytes(String.Join("\n", lines)));

        private static string Info(int id, string title = "README.md", string app = "Code.exe", int pid = 4212)
            => $$"""{"type":"window_info","id":{{id}},"title":"{{title}}","app":"{{app}}","pid":{{pid}}}""";

        private static string Row(double t, int id, int x, int y, int w, int h, int z = 0)
            => $$"""{"type":"window","t":{{t}},"id":{{id}},"x":{{x}},"y":{{y}},"w":{{w}},"h":{{h}},"z":{{z}}}""";

        /// <summary>The recorder's "this window left the region" row: every geometry field zero.</summary>
        private static string Gone(double t, int id) => Row(t, id, 0, 0, 0, 0);

        private static void AssertRect(WindowFrame frame, int x, int y, int w, int h)
        {
            Assert.Equal(x, frame.X);
            Assert.Equal(y, frame.Y);
            Assert.Equal(w, frame.Width);
            Assert.Equal(h, frame.Height);
        }

        // -------------------------------------------------------------------------------- header

        [Fact]
        public void Header_row_parses_completely()
        {
            var capture = Parse(Header, Info(1), Row(0, 1, 10, 20, 300, 200));

            var h = capture.Header;
            Assert.Equal(1, h.Version);
            Assert.Equal(-100, h.RegionX); // a region left of the primary monitor: the origin is signed
            Assert.Equal(50, h.RegionY);
            Assert.Equal(1920, h.RegionWidth);
            Assert.Equal(1080, h.RegionHeight);
            Assert.Equal(30, h.FpsNum);
            Assert.Equal(1, h.FpsDen);
            Assert.Equal("windows", h.Platform);
        }

        [Fact]
        public void A_file_with_no_header_still_reads_its_rows()
        {
            var capture = Parse(Info(1), Row(0, 1, 10, 20, 300, 200));

            Assert.Single(capture.Windows);
            Assert.Single(capture.FramesOf(1));
            Assert.Equal(0, capture.Header.Version); // header absent
            Assert.Null(capture.Header.Platform);
        }

        [Fact]
        public void An_unknown_version_is_read_and_never_gated_on()
        {
            // a newer recorder bumps the version; the fields it still shares parse as before
            // and the number is kept for whoever wants to look at it.
            var capture = Parse(
                """{"type":"header","version":7,"region":[0,0,640,480],"fps_num":60,"fps_den":1,"platform":"macos"}""",
                Info(1),
                Row(0, 1, 10, 20, 300, 200));

            Assert.Equal(7, capture.Header.Version);
            Assert.Equal("macos", capture.Header.Platform);
            Assert.Single(capture.Windows);
            Assert.True(capture.TryFrameAt(1, 0, out var frame));
            AssertRect(frame, 10, 20, 300, 200);
        }

        [Fact]
        public void A_header_without_a_version_reads_as_version_one()
        {
            var capture = Parse("""{"type":"header","region":[0,0,640,480],"fps_num":30,"fps_den":1,"platform":"windows"}""");

            Assert.Equal(1, capture.Header.Version);
        }

        // ---------------------------------------------------------------------------------- rows

        [Fact]
        public void Rows_are_bucketed_by_id_and_sorted_by_time()
        {
            // deliberately interleaved and out of order: a resume can step `t` back a hair
            var capture = Parse(
                Header,
                Info(1), Info(2),
                Row(33, 1, 1, 0, 100, 100),
                Row(0, 2, 2, 0, 100, 100),
                Row(0, 1, 3, 0, 100, 100),
                Row(66, 2, 4, 0, 100, 100),
                Row(32, 2, 5, 0, 100, 100));

            var one = capture.FramesOf(1);
            Assert.Equal(new[] { 0.0, 33.0 }, one.Select(f => f.TimeMs));
            Assert.Equal(new[] { 3, 1 }, one.Select(f => f.X));

            var two = capture.FramesOf(2);
            Assert.Equal(new[] { 0.0, 32.0, 66.0 }, two.Select(f => f.TimeMs));
            Assert.Equal(new[] { 2, 5, 4 }, two.Select(f => f.X));
        }

        [Fact]
        public void An_identity_row_names_its_window()
        {
            var capture = Parse(
                Header,
                Info(7, title: "Inbox", app: "chrome.exe", pid: 991),
                Row(0, 7, 0, 0, 800, 600));

            Assert.True(capture.TryGetWindow(7, out var info));
            Assert.Equal(7, info.Id);
            Assert.Equal("Inbox", info.Title);
            Assert.Equal("chrome.exe", info.App);
            Assert.Equal(991, info.Pid);
            Assert.Equal(0.0, info.FirstSeenMs);
        }

        [Fact]
        public void A_retitled_window_keeps_its_id_and_takes_the_latest_name()
        {
            var capture = Parse(
                Header,
                Info(7, title: "Untitled"),
                Row(0, 7, 0, 0, 800, 600),
                Info(7, title: "README.md"),
                Row(100, 7, 0, 0, 800, 600));

            Assert.Single(capture.Windows);
            Assert.True(capture.TryGetWindow(7, out var info));
            Assert.Equal("README.md", info.Title);
            Assert.Equal(2, capture.FramesOf(7).Count);
        }

        [Fact]
        public void A_geometry_row_with_no_identity_row_is_still_listed()
        {
            // a torn write can lose the window_info; the window still has a rect and must stay
            // pickable, so it gets a blank identity rather than vanishing.
            var capture = Parse(Header, Row(0, 5, 0, 0, 800, 600));

            var info = Assert.Single(capture.Windows);
            Assert.Equal(5, info.Id);
            Assert.Equal("", info.Title);
            Assert.Equal("", info.App);
            Assert.Equal(0, info.Pid);
            Assert.True(capture.TryGetWindow(5, out _));
        }

        [Fact]
        public void An_identity_row_with_no_geometry_is_dropped()
        {
            // announced but never entered the region: nothing to frame with, nothing to pick
            var capture = Parse(
                Header,
                Info(1), Info(2),
                Row(0, 1, 0, 0, 800, 600));

            var info = Assert.Single(capture.Windows);
            Assert.Equal(1, info.Id);
            Assert.False(capture.TryGetWindow(2, out _));
            Assert.Empty(capture.FramesOf(2));
        }

        [Fact]
        public void Windows_are_listed_in_first_seen_order()
        {
            // id 9 is met before id 3, and the identity rows arrive in the opposite order: the
            // pick list follows the geometry, not the ids or the announcement order.
            var capture = Parse(
                Header,
                Info(3), Info(9),
                Row(0, 9, 0, 0, 800, 600),
                Row(50, 3, 0, 0, 800, 600),
                Row(100, 9, 0, 0, 800, 600));

            Assert.Equal(new[] { 9, 3 }, capture.Windows.Select(w => w.Id));
            Assert.Equal(0.0, capture.Windows[0].FirstSeenMs);
            Assert.Equal(50.0, capture.Windows[1].FirstSeenMs);
        }

        [Fact]
        public void Windows_first_seen_together_are_ordered_by_id()
        {
            // two polls between video ticks share a stamp: the tie breaks on id so the list is
            // stable across reloads.
            var capture = Parse(
                Header,
                Row(0, 12, 0, 0, 800, 600),
                Row(0, 4, 0, 0, 800, 600),
                Row(0, 8, 0, 0, 800, 600));

            Assert.Equal(new[] { 4, 8, 12 }, capture.Windows.Select(w => w.Id));
        }

        // ----------------------------------------------------------------------- the leave sentinel

        [Fact]
        public void The_zero_rect_is_dropped_so_an_absence_holds_the_last_rect()
        {
            var capture = Parse(
                Header,
                Info(1),
                Row(0, 1, 10, 20, 300, 200),
                Gone(100, 1),
                Row(500, 1, 40, 50, 300, 200));

            // the sentinel is not a row at all
            Assert.Equal(2, capture.FramesOf(1).Count);
            Assert.All(capture.FramesOf(1), f => Assert.True(f.Width > 0 && f.Height > 0));

            // inside the gap the pre-sentinel rect holds
            Assert.True(capture.TryFrameAt(1, 100, out var atLeave));
            AssertRect(atLeave, 10, 20, 300, 200);
            Assert.True(capture.TryFrameAt(1, 300, out var inGap));
            AssertRect(inGap, 10, 20, 300, 200);
            Assert.True(capture.TryFrameAt(1, 499.9, out var justBefore));
            AssertRect(justBefore, 10, 20, 300, 200);
        }

        [Fact]
        public void A_window_that_re_enters_resumes_on_its_new_row()
        {
            var capture = Parse(
                Header,
                Info(1),
                Row(0, 1, 10, 20, 300, 200),
                Gone(100, 1),
                Row(500, 1, 40, 50, 320, 240));

            Assert.True(capture.TryFrameAt(1, 500, out var onReturn));
            AssertRect(onReturn, 40, 50, 320, 240);
            Assert.True(capture.TryFrameAt(1, 9_000, out var after));
            AssertRect(after, 40, 50, 320, 240);
        }

        [Fact]
        public void A_window_that_enters_leaves_and_re_enters_frames_continuously()
        {
            // two visible intervals under one id, and every time in between resolves to a rect
            // that was true at the nearest earlier poll.
            var capture = Parse(
                Header,
                Info(1),
                Row(0, 1, 1, 0, 100, 100),
                Row(50, 1, 2, 0, 100, 100),
                Gone(100, 1),
                Row(300, 1, 3, 0, 100, 100),
                Gone(400, 1),
                Row(600, 1, 4, 0, 100, 100));

            Assert.Equal(4, capture.FramesOf(1).Count);
            var expected = new[] { (0.0, 1), (49.9, 1), (50.0, 2), (200.0, 2), (300.0, 3), (500.0, 3), (600.0, 4), (1e6, 4) };
            foreach (var (t, x) in expected)
            {
                Assert.True(capture.TryFrameAt(1, t, out var frame));
                Assert.Equal(x, frame.X);
            }
        }

        [Fact]
        public void A_window_that_was_never_visible_resolves_to_nothing()
        {
            // an id whose only row is the sentinel was never on screen inside the region
            var capture = Parse(
                Header,
                Info(1), Info(2),
                Row(0, 1, 0, 0, 800, 600),
                Gone(10, 2));

            Assert.False(capture.TryFrameAt(2, 10, out _));
            Assert.Equal(-1, capture.LatestAtOrBefore(2, 10));
            Assert.Empty(capture.FramesOf(2));
            Assert.DoesNotContain(capture.Windows, w => w.Id == 2);
            Assert.False(capture.TryGetWindow(2, out _));
        }

        [Fact]
        public void A_rect_with_only_one_zero_extent_is_also_a_leave()
        {
            // the recorder never writes these, but the crop math must never see a zero extent,
            // so the guard is on either side independently.
            var capture = Parse(
                Header,
                Row(0, 1, 0, 0, 800, 600),
                Row(10, 1, 0, 0, 800, 0),
                Row(20, 1, 0, 0, 0, 600),
                Row(30, 1, 0, 0, -800, 600));

            Assert.Single(capture.FramesOf(1));
        }

        // ------------------------------------------------------------------------------- lookups

        [Fact]
        public void A_time_before_the_first_row_holds_the_first_rect()
        {
            // the window opened partway through: the framing sits where it will open, so there
            // is no pre-roll jump.
            var capture = Parse(
                Header,
                Info(1),
                Row(1000, 1, 10, 20, 300, 200),
                Row(2000, 1, 40, 50, 300, 200));

            Assert.Equal(-1, capture.LatestAtOrBefore(1, 0));
            Assert.Equal(-1, capture.LatestAtOrBefore(1, 999.9));
            Assert.True(capture.TryFrameAt(1, 0, out var atZero));
            AssertRect(atZero, 10, 20, 300, 200);
            Assert.True(capture.TryFrameAt(1, -5, out var negative));
            AssertRect(negative, 10, 20, 300, 200);
        }

        [Fact]
        public void A_time_after_the_last_row_holds_the_last_rect()
        {
            var capture = Parse(
                Header,
                Info(1),
                Row(0, 1, 10, 20, 300, 200),
                Row(1000, 1, 40, 50, 300, 200));

            Assert.Equal(1, capture.LatestAtOrBefore(1, 1000));
            Assert.Equal(1, capture.LatestAtOrBefore(1, 1e9));
            Assert.True(capture.TryFrameAt(1, 1e9, out var frame));
            AssertRect(frame, 40, 50, 300, 200);
        }

        [Fact]
        public void LatestAtOrBefore_is_inclusive_of_an_exact_stamp()
        {
            var capture = Parse(
                Header,
                Row(0, 1, 1, 0, 100, 100),
                Row(33, 1, 2, 0, 100, 100),
                Row(66, 1, 3, 0, 100, 100));

            Assert.Equal(0, capture.LatestAtOrBefore(1, 0));
            Assert.Equal(0, capture.LatestAtOrBefore(1, 32.9));
            Assert.Equal(1, capture.LatestAtOrBefore(1, 33));
            Assert.Equal(1, capture.LatestAtOrBefore(1, 65.9));
            Assert.Equal(2, capture.LatestAtOrBefore(1, 66));

            Assert.True(capture.TryFrameAt(1, 33, out var frame));
            Assert.Equal(2, frame.X);
            Assert.Equal(33.0, frame.TimeMs);
        }

        [Fact]
        public void Two_rows_sharing_a_stamp_both_survive_and_the_later_one_wins()
        {
            // two polls between video ticks share a stamp; the sort is stable enough that a
            // lookup at that stamp lands on the last of them.
            var capture = Parse(
                Header,
                Row(0, 1, 1, 0, 100, 100),
                Row(10, 1, 2, 0, 100, 100),
                Row(10, 1, 3, 0, 100, 100));

            Assert.Equal(3, capture.FramesOf(1).Count);
            Assert.Equal(2, capture.LatestAtOrBefore(1, 10));
            Assert.True(capture.TryFrameAt(1, 10, out var frame));
            Assert.Equal(3, frame.X);
        }

        [Fact]
        public void An_unknown_id_resolves_to_nothing()
        {
            var capture = Parse(Header, Info(1), Row(0, 1, 0, 0, 800, 600));

            Assert.False(capture.TryFrameAt(99, 0, out var frame));
            Assert.Equal(default, frame);
            Assert.Equal(-1, capture.LatestAtOrBefore(99, 0));
            Assert.Empty(capture.FramesOf(99));
            Assert.False(capture.TryGetWindow(99, out _));
        }

        [Fact]
        public void The_empty_capture_resolves_nothing_for_any_id()
        {
            Assert.False(WindowCapture.Empty.TryFrameAt(0, 0, out _));
            Assert.False(WindowCapture.Empty.TryFrameAt(1, 0, out _));
            Assert.Equal(-1, WindowCapture.Empty.LatestAtOrBefore(1, 0));
            Assert.Empty(WindowCapture.Empty.FramesOf(1));
            Assert.Empty(WindowCapture.Empty.Windows);
            Assert.True(WindowCapture.Empty.IsEmpty);
            Assert.Equal(0, WindowCapture.Empty.Header.Version);
        }

        [Fact]
        public void Negative_coordinates_and_oversized_extents_survive_the_parse()
        {
            // rows are never clipped to the region: a window straddling an edge is negative, one
            // larger than the region overhangs it. The consumer clamps.
            var capture = Parse(
                Header,
                Info(1),
                Row(0, 1, -250, -40, 3840, 2160));

            Assert.True(capture.TryFrameAt(1, 0, out var frame));
            AssertRect(frame, -250, -40, 3840, 2160);
        }

        [Fact]
        public void A_zero_or_negative_id_is_not_a_window()
        {
            var capture = Parse(
                Header,
                Info(0), Info(-1),
                Row(0, 0, 0, 0, 800, 600),
                Row(0, -1, 0, 0, 800, 600),
                Row(0, 1, 0, 0, 800, 600));

            var info = Assert.Single(capture.Windows);
            Assert.Equal(1, info.Id);
        }

        // ------------------------------------------------------------------------- zero windows

        [Fact]
        public void A_header_with_no_window_rows_is_an_empty_capture()
        {
            // a region no window intersected: a healthy recording that offers nothing to follow.
            // The header is kept, so this is empty by content rather than the shared instance.
            var capture = Parse(Header);

            Assert.True(capture.IsEmpty);
            Assert.Empty(capture.Windows);
            Assert.Equal(1, capture.Header.Version);
            Assert.False(capture.TryFrameAt(1, 0, out _));
        }

        [Fact]
        public void A_header_whose_windows_all_left_before_being_seen_is_empty()
        {
            var capture = Parse(Header, Info(1), Info(2), Gone(0, 1), Gone(0, 2));

            Assert.True(capture.IsEmpty);
            Assert.Empty(capture.Windows);
            Assert.False(capture.TryGetWindow(1, out _));
        }

        // -------------------------------------------------------------------------- many windows

        [Fact]
        public void A_recording_with_the_recorders_full_id_budget_parses_and_resolves_every_id()
        {
            // the recorder tracks at most 4096 ids per recording (MAX_WINDOW_IDS) and skips the
            // rest, so this is the largest file it can produce; every id must stay independently
            // addressable and the list must come out in first-seen order.
            const int count = 4096;
            var lines = new string[1 + 2 * count];
            lines[0] = Header;
            for (var i = 0; i < count; i++)
            {
                var id = count - i; // announced high to low so the sort has real work to do
                lines[1 + 2 * i] = Info(id, title: $"Window {id}", pid: id);
                lines[2 + 2 * i] = Row(i, id, i, i * 2, 100 + i, 200 + i);
            }

            var capture = Parse(lines);

            Assert.Equal(count, capture.Windows.Count);
            Assert.Equal(Enumerable.Range(0, count).Select(i => count - i), capture.Windows.Select(w => w.Id));
            Assert.Equal(Enumerable.Range(0, count).Select(i => (double)i), capture.Windows.Select(w => w.FirstSeenMs));

            for (var i = 0; i < count; i++)
            {
                var id = count - i;
                Assert.True(capture.TryGetWindow(id, out var info));
                Assert.Equal($"Window {id}", info.Title);
                Assert.True(capture.TryFrameAt(id, 1e6, out var frame));
                AssertRect(frame, i, i * 2, 100 + i, 200 + i);
            }
        }

        [Fact]
        public void Ids_beyond_the_recorders_budget_are_still_read_if_present()
        {
            // the cap is the recorder's, not the file format's: an id past it is just an id
            var capture = Parse(Header, Row(0, 4097, 0, 0, 800, 600), Row(0, 1_000_000, 0, 0, 800, 600));

            Assert.Equal(new[] { 4097, 1_000_000 }, capture.Windows.Select(w => w.Id));
        }

        // ---------------------------------------------------------------------------- occlusion

        /// <summary>A window big enough to cover the whole 1920x1080 region.</summary>
        private static string Covering(double t, int id, int z = 0) => Row(t, id, -200, -200, 3000, 2000, z);

        [Fact]
        public void Z_is_parsed_onto_every_row()
        {
            var capture = Parse(Header, Info(1), Row(0, 1, 10, 20, 300, 200, 4));

            Assert.Equal(4, capture.FramesOf(1)[0].Z);
        }

        [Fact]
        public void A_window_behind_a_coverer_for_the_whole_recording_is_not_ever_visible()
        {
            var capture = Parse(Header, Info(1), Info(2),
                Covering(0, 1),
                Row(0, 2, 100, 100, 800, 600, 1),
                Row(500, 2, 140, 100, 800, 600, 1));

            // it is still a window of this file — its geometry is intact and a stored pick on it
            // still resolves — it simply never showed
            Assert.Equal(new[] { 1, 2 }, capture.Windows.Select(w => w.Id).ToArray());
            Assert.True(capture.Windows.Single(w => w.Id == 1).EverVisible);
            Assert.False(capture.Windows.Single(w => w.Id == 2).EverVisible);
            Assert.Equal(2, capture.FramesOf(2).Count);
        }

        [Fact]
        public void A_window_covered_only_in_part_is_ever_visible()
        {
            var capture = Parse(Header, Info(1), Info(2),
                Row(0, 1, 0, 0, 1920, 1000, 0),   // 80 rows of the region left over
                Row(0, 2, 100, 100, 800, 990, 1));

            Assert.True(capture.Windows.Single(w => w.Id == 2).EverVisible);
        }

        [Fact]
        public void Two_coverers_that_only_together_cover_a_window_hide_it()
        {
            var capture = Parse(Header, Info(1), Info(2), Info(3),
                // neither half covers 3 alone; the union does, and occlusion is a union question
                Row(0, 1, 0, 0, 960, 1080, 0),
                Row(0, 2, 960, 0, 960, 1080, 1),
                Row(0, 3, 100, 100, 1000, 600, 2));

            Assert.False(capture.Windows.Single(w => w.Id == 3).EverVisible);
        }

        [Fact]
        public void A_window_uncovered_for_one_instant_is_ever_visible()
        {
            var capture = Parse(Header, Info(1), Info(2),
                Covering(0, 1),
                Row(0, 2, 100, 100, 800, 600, 1),
                // the coverer leaves the region for a single poll: a sliver of a second on screen
                // is still on screen
                Gone(500, 1),
                Covering(600, 1));

            Assert.True(capture.Windows.Single(w => w.Id == 2).EverVisible);
        }

        [Fact]
        public void A_window_that_only_ever_shows_before_the_coverer_arrives_is_ever_visible()
        {
            var capture = Parse(Header, Info(1), Info(2),
                Row(0, 2, 100, 100, 800, 600, 0),
                // the coverer's first row is its arrival; before it there is nothing in front
                Covering(500, 1),
                Row(500, 2, 100, 100, 800, 600, 1));

            Assert.True(capture.Windows.Single(w => w.Id == 2).EverVisible);
        }

        [Fact]
        public void A_raise_that_puts_a_window_in_front_makes_it_visible()
        {
            var capture = Parse(Header, Info(1), Info(2),
                Covering(0, 1),
                Row(0, 2, 100, 100, 800, 600, 1),
                // the recorder re-states both rows on a raise, with the depths swapped
                Row(500, 2, 100, 100, 800, 600, 0),
                Covering(500, 1, 1));

            Assert.True(capture.Windows.Single(w => w.Id == 2).EverVisible);
        }

        [Fact]
        public void A_window_entirely_outside_the_region_is_not_ever_visible()
        {
            var capture = Parse(Header, Info(1),
                // the recorder tracks windows that intersect the region, so this is a torn or
                // stale row rather than an everyday one; it is still not on screen
                Row(0, 1, 2000, 1200, 300, 200));

            Assert.False(capture.Windows.Single(w => w.Id == 1).EverVisible);
        }

        [Fact]
        public void Without_a_header_every_window_stays_visible()
        {
            // no region to judge coverage against: the pick list must not quietly empty itself
            var capture = Parse(Info(1), Info(2),
                Covering(0, 1),
                Row(0, 2, 100, 100, 800, 600, 1));

            Assert.All(capture.Windows, w => Assert.True(w.EverVisible));
        }

        // ----------------------------------------------------------------------- forward tolerance

        [Fact]
        public void An_unknown_row_type_is_skipped()
        {
            var capture = Parse(
                Header,
                Info(1),
                Row(0, 1, 1, 0, 100, 100),
                """{"type":"quux","t":5,"id":1,"x":99,"y":99,"w":99,"h":99}""",
                Row(10, 1, 2, 0, 100, 100));

            Assert.Equal(2, capture.FramesOf(1).Count);
            Assert.Equal(new[] { 1, 2 }, capture.FramesOf(1).Select(f => f.X));
        }

        [Fact]
        public void An_unknown_field_is_skipped()
        {
            var capture = Parse(
                Header,
                """{"type":"window_info","id":1,"title":"Inbox","app":"chrome.exe","pid":5,"monitor":{"index":1}}""",
                """{"type":"window","t":0,"id":1,"x":10,"y":20,"w":300,"h":200,"z":3,"foo":{"a":1},"bar":[1,2,3]}""");

            Assert.True(capture.TryGetWindow(1, out var info));
            Assert.Equal("Inbox", info.Title);
            Assert.True(capture.TryFrameAt(1, 0, out var frame));
            AssertRect(frame, 10, 20, 300, 200);
        }

        [Fact]
        public void A_field_of_the_wrong_type_leaves_the_row_readable()
        {
            // a reshaped "w" reads as its default of 0, which makes the row a sentinel: dropped,
            // and the rest of the file is untouched.
            var capture = Parse(
                Header,
                Info(1),
                Row(0, 1, 1, 0, 100, 100),
                """{"type":"window","t":10,"id":1,"x":2,"y":0,"w":"800","h":100,"z":0}""",
                Row(20, 1, 3, 0, 100, 100));

            Assert.Equal(new[] { 1, 3 }, capture.FramesOf(1).Select(f => f.X));
        }

        [Fact]
        public void A_reshaped_identity_field_keeps_the_rest_of_the_row()
        {
            var capture = Parse(
                Header,
                """{"type":"window_info","id":1,"title":["not","a","string"],"app":"chrome.exe","pid":"5"}""",
                Row(0, 1, 0, 0, 800, 600));

            Assert.True(capture.TryGetWindow(1, out var info));
            Assert.Equal("", info.Title);
            Assert.Equal("chrome.exe", info.App);
            Assert.Equal(0, info.Pid);
        }

        [Fact]
        public void A_short_region_array_leaves_the_region_at_zero()
        {
            var capture = Parse(
                """{"type":"header","version":1,"region":[5,6],"fps_num":30,"fps_den":1,"platform":"windows"}""",
                Row(0, 1, 0, 0, 800, 600));

            Assert.Equal(1, capture.Header.Version);
            Assert.Equal(0, capture.Header.RegionX);
            Assert.Equal(0, capture.Header.RegionWidth);
            Assert.Single(capture.Windows);
        }

        [Fact]
        public void A_torn_last_line_is_dropped_and_the_rest_kept()
        {
            var capture = Parse(
                Header,
                Info(1),
                Row(0, 1, 1, 0, 100, 100),
                """{"type":"window","t":10,"id":1,"x":2,""");

            Assert.Single(capture.FramesOf(1));
            Assert.Equal(0.0, capture.FramesOf(1)[0].TimeMs);
        }

        [Fact]
        public void Garbage_and_blank_lines_between_rows_are_skipped()
        {
            var capture = Parse(
                "this is not json",
                Header,
                "",
                "   ",
                "[1,2,3]",
                Row(0, 1, 1, 0, 100, 100) + "\r",
                Row(10, 1, 2, 0, 100, 100));

            Assert.Equal(1, capture.Header.Version);
            Assert.Equal(new[] { 1, 2 }, capture.FramesOf(1).Select(f => f.X));
        }

        [Fact]
        public void A_file_with_no_parseable_rows_is_Empty()
        {
            var capture = Parse("garbage", "more garbage");

            Assert.Same(WindowCapture.Empty, capture);
            Assert.True(capture.IsEmpty);
        }

        [Fact]
        public void An_empty_file_is_the_empty_capture()
        {
            Assert.Same(WindowCapture.Empty, Parse(""));
            Assert.Same(WindowCapture.Empty, WindowCapture.Parse(ReadOnlySpan<byte>.Empty));
        }

        // ------------------------------------------------------------------------ load and cache

        [Fact]
        public void A_missing_file_loads_as_empty()
        {
            var path = Path.Combine(Path.GetTempPath(), $"window-capture-{Guid.NewGuid():N}.jsonl");

            Assert.Same(WindowCapture.Empty, WindowCapture.Load(path));
            Assert.Same(WindowCapture.Empty, WindowCapture.Load(null));
            Assert.Same(WindowCapture.Empty, WindowCapture.Load(""));
            Assert.Same(WindowCapture.Empty, WindowCapture.Get(null));
            Assert.Same(WindowCapture.Empty, WindowCapture.Get(""));
        }

        [Fact]
        public void A_zero_byte_file_loads_as_empty()
        {
            // the recorder creates the sidecar at construction, so a take that never started
            // leaves a 0-byte file: indistinguishable from no sidecar, deliberately.
            var path = Path.Combine(Path.GetTempPath(), $"window-capture-{Guid.NewGuid():N}.jsonl");
            File.WriteAllBytes(path, Array.Empty<byte>());
            try
            {
                Assert.Same(WindowCapture.Empty, WindowCapture.Load(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Load_reads_a_real_file()
        {
            var path = Path.Combine(Path.GetTempPath(), $"window-capture-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(path, Header + "\n" + Info(1) + "\n" + Row(0, 1, 10, 20, 300, 200) + "\n");
            try
            {
                var capture = WindowCapture.Load(path);
                Assert.Equal(-100, capture.Header.RegionX);
                Assert.Single(capture.Windows);
                Assert.True(capture.TryFrameAt(1, 0, out var frame));
                AssertRect(frame, 10, 20, 300, 200);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Get_caches_the_parse_and_the_failure()
        {
            var path = Path.Combine(Path.GetTempPath(), $"window-capture-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(path, Header + "\n" + Info(1) + "\n" + Row(0, 1, 10, 20, 300, 200) + "\n");
            try
            {
                var first = WindowCapture.Get(path);
                Assert.Single(first.Windows);

                // the cache is immutable after load: a rewritten file does not change the
                // instance, and the same reference comes back.
                File.WriteAllText(path, "");
                Assert.Same(first, WindowCapture.Get(path));
            }
            finally
            {
                File.Delete(path);
            }

            var missing = Path.Combine(Path.GetTempPath(), $"window-capture-{Guid.NewGuid():N}.jsonl");
            Assert.Same(WindowCapture.Empty, WindowCapture.Get(missing));
            Assert.Same(WindowCapture.Empty, WindowCapture.Get(missing));
        }
    }
}
