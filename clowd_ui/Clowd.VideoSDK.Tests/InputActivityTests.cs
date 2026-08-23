using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Clowd.UI.VideoEditor.Timeline;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The cursor and keys rows' previews, end to end below the pixels: the SDK's per-frame
    /// pointer speed and click pairing (<see cref="InputActivity"/>), the timeline adapter's
    /// re-bucketing into source-tick requests, the zoom-dependent folding of clicks and
    /// keystroke runs into marks, and the timeline→source mapping through the hard-synced screen
    /// item. Pure math, no Avalonia runtime — Clowd.Ui exposes its internals to this project.
    /// </summary>
    public class InputActivityTests
    {
        private const long TicksPerMs = TimeSpan.TicksPerMillisecond;

        // a 1920×1080 region: diagonal ≈ 2203 px
        private const string Header =
            """{"type":"header","version":2,"region":[0,0,1920,1080],"fps_num":30,"fps_den":1,"platform":"windows"}""";

        private static InputCapture Parse(params string[] lines) =>
            InputCapture.Parse(Encoding.UTF8.GetBytes(String.Join("\n", lines)));

        private static string Frame(double t, int x, int y, int b = 0) =>
            $$"""{"type":"frame","t":{{t}},"x":{{x}},"y":{{y}},"b":{{b}},"c":"arrow"}""";

        private static string Md(double t, int btn = 1) =>
            $$"""{"type":"event","t":{{t}},"kind":"md","btn":{{btn}},"x":0,"y":0}""";

        private static string Mu(double t, int btn = 1) =>
            $$"""{"type":"event","t":{{t}},"kind":"mu","btn":{{btn}},"x":0,"y":0}""";

        private static string Kd(double t, int vk, string ch) =>
            $$"""{"type":"event","t":{{t}},"kind":"kd","vk":{{vk}},"ch":"{{ch}}"}""";

        // ------------------------------------------------------------------------- SDK: motion

        [Fact]
        public void Normalize_is_a_soft_knee_from_still_to_flick()
        {
            Assert.Equal(0f, CursorMotion.Normalize(0));
            Assert.Equal(0f, CursorMotion.Normalize(-1));
            Assert.Equal(0.5f, CursorMotion.Normalize(CursorMotion.HalfScaleDiagonalsPerSecond));

            // monotonic and bounded: a flick five times the knee is loud but not clipped
            var slow = CursorMotion.Normalize(0.1);
            var flick = CursorMotion.Normalize(5);
            Assert.InRange(slow, 0.05f, 0.15f);
            Assert.InRange(flick, 0.8f, 0.9f);
            Assert.True(CursorMotion.Normalize(1000) < 1f);
        }

        [Fact]
        public void Speed_is_the_distance_into_each_frame_over_its_gap_in_diagonals_per_second()
        {
            // 100 ms apart; the second frame moved 220.3 px ≈ a tenth of the diagonal, so
            // 1 diag/s → 0.5 after the knee; the third stayed put
            var motion = InputActivity.ComputeCursorMotion(Parse(
                Header,
                Frame(0, 0, 0),
                Frame(100, 220, 0),
                Frame(200, 220, 0)));

            Assert.Equal(3, motion.FrameCount);
            Assert.Equal(new[] { 0.0, 100.0, 200.0 }, motion.TimesMs);
            Assert.Equal(0f, motion.Speed[0]);
            Assert.InRange(motion.Speed[1], 0.49f, 0.51f);
            Assert.Equal(0f, motion.Speed[2]);
        }

        [Fact]
        public void Frames_sharing_a_timestamp_contribute_no_speed()
        {
            var motion = InputActivity.ComputeCursorMotion(Parse(
                Header,
                Frame(0, 0, 0),
                Frame(0, 500, 500),
                Frame(50, 500, 500)));

            Assert.All(motion.Speed, s => Assert.Equal(0f, s));
        }

        [Fact]
        public void Without_a_header_the_pointers_own_extent_is_the_diagonal()
        {
            // no header: the box the pointer covered is 300×400 (diagonal 500), and it crossed
            // that whole box in half a second = 2 diag/s → 2/3
            var motion = InputActivity.ComputeCursorMotion(Parse(
                Frame(0, 0, 0),
                Frame(500, 300, 400)));

            Assert.InRange(motion.Speed[1], 0.66f, 0.67f);
        }

        [Fact]
        public void Empty_capture_is_empty_motion()
        {
            Assert.Same(CursorMotion.Empty, InputActivity.ComputeCursorMotion(InputCapture.Empty));
            Assert.Same(CursorMotion.Empty, InputActivity.ComputeCursorMotion(null));
            Assert.True(CursorMotion.Empty.IsEmpty);
            Assert.Same(CursorMotion.Empty, InputActivity.GetCursorMotion(null));
            Assert.Same(CursorMotion.Empty, InputActivity.GetCursorMotion(""));
        }

        // ------------------------------------------------------------------------- SDK: clicks

        [Fact]
        public void Clicks_pair_each_buttons_down_with_its_next_up()
        {
            var motion = InputActivity.ComputeCursorMotion(Parse(
                Header,
                Frame(0, 0, 0),
                Md(10), Md(20, btn: 2), Mu(30), Mu(45, btn: 2), Md(100), Mu(400)));

            Assert.Equal(new[]
            {
                new CursorClick(10, 30, 1),
                new CursorClick(20, 45, 2),
                new CursorClick(100, 400, 1),
            }, motion.Clicks);
        }

        [Fact]
        public void A_down_the_capture_never_released_closes_on_itself()
        {
            var motion = InputActivity.ComputeCursorMotion(Parse(
                Header,
                Frame(0, 0, 0),
                Md(10), Md(50), Mu(60)));

            // the first press lost its up to a second press: it closes where it opened, the
            // second pairs normally
            Assert.Equal(new[] { new CursorClick(10, 10, 1), new CursorClick(50, 60, 1) }, motion.Clicks);
        }

        [Fact]
        public void A_stray_up_with_no_down_is_ignored()
        {
            var motion = InputActivity.ComputeCursorMotion(Parse(Header, Frame(0, 0, 0), Mu(5), Md(10), Mu(20)));
            Assert.Equal(new[] { new CursorClick(10, 20, 1) }, motion.Clicks);
        }

        [Fact]
        public void Lookups_find_the_first_frame_and_click_at_or_after_a_time()
        {
            var motion = InputActivity.ComputeCursorMotion(Parse(
                Header,
                Frame(0, 0, 0), Frame(100, 0, 0), Frame(200, 0, 0),
                Md(50), Mu(60), Md(150), Mu(160)));

            Assert.Equal(0, motion.FirstFrameAtOrAfter(-1));
            Assert.Equal(1, motion.FirstFrameAtOrAfter(100));
            Assert.Equal(2, motion.FirstFrameAtOrAfter(101));
            Assert.Equal(3, motion.FirstFrameAtOrAfter(201));

            Assert.Equal(0, motion.FirstClickAtOrAfter(0));
            Assert.Equal(1, motion.FirstClickAtOrAfter(51));
            Assert.Equal(2, motion.FirstClickAtOrAfter(151));
        }

        // ---------------------------------------------------------------------- SDK: key runs

        [Fact]
        public void Key_runs_are_the_overlays_own_segmentation_as_spans()
        {
            var path = Path.Combine(Path.GetTempPath(), $"clowd-activity-{Guid.NewGuid():N}.jsonl");
            try
            {
                File.WriteAllLines(path, new[]
                {
                    Header,
                    Kd(100, 72, "h"), Kd(150, 73, "i"),
                    // 2s gap: a new run under a 1s pause break, the same run under a 5s one
                    Kd(2150, 79, "o"), Kd(2200, 75, "k"),
                });

                var split = InputActivity.GetKeyRuns(path, 1000);
                Assert.Equal(new[]
                {
                    new KeyRunSpan(100, 150, 2, false),
                    new KeyRunSpan(2150, 2200, 2, false),
                }, split);

                var joined = InputActivity.GetKeyRuns(path, 5000);
                Assert.Equal(new[] { new KeyRunSpan(100, 2200, 4, false) }, joined);

                // cached: the same triple is the same list
                Assert.Same(split, InputActivity.GetKeyRuns(path, 1000));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Missing_capture_has_no_runs_and_no_motion()
        {
            var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.jsonl");
            Assert.Empty(InputActivity.GetKeyRuns(missing, 1000));
            Assert.Same(CursorMotion.Empty, InputActivity.GetCursorMotion(missing));
            Assert.Empty(InputActivity.GetKeyRuns(null, 1000));
        }

        // ------------------------------------------------------------------- adapter: buckets

        private static CursorMotion Motion(params (double Ms, float Speed)[] frames) =>
            new CursorMotion(frames.Select(f => f.Ms).ToArray(), frames.Select(f => f.Speed).ToArray(), null);

        [Fact]
        public void Cursor_buckets_take_the_fastest_frame_they_cover()
        {
            // 10 ms buckets over [0, 50) ms of source; frames every 4 ms
            var motion = Motion((0, 0f), (4, 0.2f), (8, 0.9f), (12, 0.1f), (16, 0.3f), (24, 0.4f), (48, 0.7f), (52, 1f));
            var activity = TimelinePreviewProvider.CursorCache.Build(
                new CursorActivityRequest(Guid.NewGuid(), 0, 50 * TicksPerMs, 10 * TicksPerMs), motion);

            Assert.True(activity.IsComplete);
            Assert.Equal(5, activity.BucketCount);
            Assert.Equal(new[] { 0.9f, 0.3f, 0.4f, 0f, 0.7f }, activity.Motion);
        }

        [Fact]
        public void Cursor_buckets_start_at_the_requested_source_in()
        {
            var motion = Motion((0, 0.9f), (100, 0.2f), (110, 0.6f), (125, 0.3f));
            var activity = TimelinePreviewProvider.CursorCache.Build(
                new CursorActivityRequest(Guid.NewGuid(), 100 * TicksPerMs, 30 * TicksPerMs, 10 * TicksPerMs), motion);

            Assert.Equal(100 * TicksPerMs, activity.StartTicks);
            Assert.Equal(new[] { 0.2f, 0.6f, 0.3f }, activity.Motion);
        }

        [Fact]
        public void Cursor_clicks_in_the_span_come_back_in_source_ticks()
        {
            var motion = new CursorMotion(new[] { 0.0 }, new[] { 0f }, new[]
            {
                new CursorClick(50, 60, 1),
                new CursorClick(150, 400, 1),   // a drag, ending past the span: still in (its down is)
                new CursorClick(500, 510, 1),   // past the span
            });
            var activity = TimelinePreviewProvider.CursorCache.Build(
                new CursorActivityRequest(Guid.NewGuid(), 100 * TicksPerMs, 200 * TicksPerMs, 10 * TicksPerMs), motion);

            Assert.Equal(new[] { new CursorClickSpan(150 * TicksPerMs, 400 * TicksPerMs) }, activity.Clicks);
        }

        [Fact]
        public void Key_runs_intersecting_the_span_come_back_in_source_ticks()
        {
            var spans = new[]
            {
                new KeyRunSpan(0, 50, 3, false),       // before
                new KeyRunSpan(80, 120, 2, false),     // straddles the start
                new KeyRunSpan(150, 150, 1, true),     // inside, a single key
                new KeyRunSpan(290, 350, 4, false),    // straddles the end
                new KeyRunSpan(400, 450, 2, false),    // after
            };
            var runs = TimelinePreviewProvider.KeyRunsCache.Build(
                new KeyRunsRequest(Guid.NewGuid(), 100 * TicksPerMs, 200 * TicksPerMs, 1000, KeystrokeFilter.None), spans);

            Assert.True(runs.IsComplete);
            Assert.Equal(new[]
            {
                new TimelineKeyRun(80 * TicksPerMs, 120 * TicksPerMs, 2),
                new TimelineKeyRun(150 * TicksPerMs, 150 * TicksPerMs, 1),
                new TimelineKeyRun(290 * TicksPerMs, 350 * TicksPerMs, 4),
            }, runs.Runs);
        }

        [Fact]
        public void Placeholders_are_flat_and_say_whether_more_is_coming()
        {
            var request = new CursorActivityRequest(Guid.NewGuid(), 0, 100 * TicksPerMs, 10 * TicksPerMs);

            var none = CursorActivity.None(request);
            Assert.True(none.IsComplete);
            Assert.Equal(10, none.BucketCount);
            Assert.All(none.Motion, v => Assert.Equal(0f, v));
            Assert.Empty(none.Clicks);

            Assert.False(CursorActivity.Pending(request).IsComplete);
            Assert.True(KeyRuns.None.IsComplete);
            Assert.False(KeyRuns.Pending.IsComplete);

            Assert.True(NullTimelinePreviewProvider.Instance.GetCursorActivity(request).IsComplete);
            Assert.Same(KeyRuns.None, NullTimelinePreviewProvider.Instance.GetKeyRuns(
                new KeyRunsRequest(Guid.NewGuid(), 0, 1, 1000, KeystrokeFilter.None)));
        }

        // ----------------------------------------------------------------------- math: marks

        [Fact]
        public void Click_marks_land_where_the_press_was_and_stretch_over_a_hold()
        {
            // 1 ms per pixel, item starts at source 1000 ms
            var marks = InputPreviewMath.ClickMarks(new[]
            {
                new CursorClickSpan(1010 * TicksPerMs, 1012 * TicksPerMs),   // a tap: min width
                new CursorClickSpan(1100 * TicksPerMs, 1160 * TicksPerMs),   // a drag: 60 px
            }, 1000 * TicksPerMs, 1.0, TicksPerMs);

            Assert.Equal(2, marks.Count);
            Assert.Equal(10, marks[0].X, 6);
            Assert.Equal(InputPreviewMath.ClickMarkMinWidth, marks[0].Width, 6);
            Assert.Equal(100, marks[1].X, 6);
            Assert.Equal(60, marks[1].Width, 6);
        }

        [Fact]
        public void Click_marks_fold_together_when_they_overlap_on_screen()
        {
            // zoomed out to 100 ms per pixel: three clicks within 20 ms are one mark
            var marks = InputPreviewMath.ClickMarks(new[]
            {
                new CursorClickSpan(0, 0),
                new CursorClickSpan(10 * TicksPerMs, 10 * TicksPerMs),
                new CursorClickSpan(20 * TicksPerMs, 20 * TicksPerMs),
                new CursorClickSpan(5000 * TicksPerMs, 5000 * TicksPerMs),
            }, 0, 1.0, 100 * TicksPerMs);

            Assert.Equal(2, marks.Count);
            Assert.Equal(3, marks[0].Count);
            Assert.Equal(1, marks[1].Count);
            Assert.Equal(50, marks[1].X, 6);
        }

        [Fact]
        public void Marks_follow_the_items_speed()
        {
            // at 2× speed a source second is half a timeline second
            var marks = InputPreviewMath.ClickMarks(new[] { new CursorClickSpan(2000 * TicksPerMs, 2000 * TicksPerMs) },
                0, 2.0, TicksPerMs);
            Assert.Equal(1000, marks[0].X, 6);
        }

        [Fact]
        public void Key_blips_span_each_run_and_fold_by_pixel_gap()
        {
            // 10 ms per pixel: runs 30 ms apart (3 px, under the 4 px merge gap) fold, the one a
            // second on stands alone
            var runs = new[]
            {
                new TimelineKeyRun(0, 100 * TicksPerMs, 5),
                new TimelineKeyRun(130 * TicksPerMs, 200 * TicksPerMs, 3),
                new TimelineKeyRun(230 * TicksPerMs, 230 * TicksPerMs, 1),
                new TimelineKeyRun(1230 * TicksPerMs, 1300 * TicksPerMs, 2),
            };

            var zoomedOut = InputPreviewMath.KeyBlips(runs, 0, 1.0, 10 * TicksPerMs);
            Assert.Equal(2, zoomedOut.Count);
            Assert.Equal(3, zoomedOut[0].Count);
            Assert.Equal(0, zoomedOut[0].X, 6);
            Assert.Equal(23 + InputPreviewMath.KeyBlipMinWidth, zoomedOut[0].Width, 6);
            Assert.Equal(1, zoomedOut[1].Count);
            Assert.Equal(123, zoomedOut[1].X, 6);
            Assert.Equal(7, zoomedOut[1].Width, 6);

            // 1 ms per pixel: the same 30 ms gap is 30 px, every run its own blip
            var zoomedIn = InputPreviewMath.KeyBlips(runs, 0, 1.0, TicksPerMs);
            Assert.Equal(4, zoomedIn.Count);
            Assert.All(zoomedIn, b => Assert.Equal(1, b.Count));
            Assert.Equal(InputPreviewMath.KeyBlipMinWidth, zoomedIn[2].Width, 6);
        }

        [Fact]
        public void Blip_height_grows_with_the_fold_and_stops_at_the_body()
        {
            Assert.Equal(InputPreviewMath.KeyBlipBaseHeight, InputPreviewMath.KeyBlipHeight(1, 22));
            Assert.Equal(InputPreviewMath.KeyBlipBaseHeight + InputPreviewMath.KeyBlipStepHeight,
                InputPreviewMath.KeyBlipHeight(2, 22));
            Assert.Equal(18, InputPreviewMath.KeyBlipHeight(50, 22));
            Assert.Equal(2, InputPreviewMath.KeyBlipHeight(50, 4));
        }

        [Fact]
        public void No_input_means_no_marks()
        {
            Assert.Empty(InputPreviewMath.ClickMarks(null, 0, 1, 1));
            Assert.Empty(InputPreviewMath.ClickMarks(Array.Empty<CursorClickSpan>(), 0, 1, 1));
            Assert.Empty(InputPreviewMath.KeyBlips(null, 0, 1, 1));
            Assert.Empty(InputPreviewMath.KeyBlips(new[] { new TimelineKeyRun(0, 1, 1) }, 0, 1, 0));
        }

        // ------------------------------------------------------------------------ timing

        private static long Sec(double s) => (long)(s * TimeSpan.TicksPerSecond);

        private static Project TimingProject(out Source source, out Track screenTrack, out Track overlayTrack)
        {
            source = new Source
            {
                Id = Guid.NewGuid(),
                Path = "rec.mp4",
                InputCapturePath = "rec.jsonl",
                Streams = new List<SourceStream>
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080 },
                    new SourceStream { Index = 1, Kind = StreamKind.Audio },
                },
            };
            screenTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0, Name = "Screen" };
            overlayTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 1, Name = "Cursor" };
            var project = new Project();
            project.Sources.Add(source);
            project.Tracks.Add(screenTrack);
            project.Tracks.Add(overlayTrack);
            return project;
        }

        [Fact]
        public void Overlay_time_runs_through_the_linked_screen_item()
        {
            var project = TimingProject(out var source, out var screenTrack, out var overlayTrack);
            var group = Guid.NewGuid();

            // the screen segment starts 4s into the recording and sits at 10s on the timeline; the
            // overlay mirrors its span and link group
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(), TrackId = screenTrack.Id, LinkGroupId = group,
                TimelineStartTicks = Sec(10), DurationTicks = Sec(5),
                Content = new MediaContent { SourceId = source.Id, StreamIndex = 0, SourceInTicks = Sec(4) },
            });
            var overlay = new Item
            {
                Id = Guid.NewGuid(), TrackId = overlayTrack.Id, LinkGroupId = group,
                TimelineStartTicks = Sec(10), DurationTicks = Sec(5),
                Content = new CursorContent { SourceId = source.Id },
            };
            project.Items.Add(overlay);

            Assert.True(OverlayTiming.TryResolve(project, overlay, source.Id, out var sourceIn, out var speed));
            Assert.Equal(Sec(4), sourceIn);
            Assert.Equal(1.0, speed);
        }

        [Fact]
        public void Overlay_prefers_its_own_link_group_and_offsets_into_a_wider_screen_item()
        {
            var project = TimingProject(out var source, out var screenTrack, out var overlayTrack);
            var mine = Guid.NewGuid();

            // another segment of the same recording overlaps too but belongs to another group
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(), TrackId = screenTrack.Id, LinkGroupId = Guid.NewGuid(),
                TimelineStartTicks = Sec(0), DurationTicks = Sec(30),
                Content = new MediaContent { SourceId = source.Id, StreamIndex = 0, SourceInTicks = Sec(100) },
            });
            // the partner starts 2s before the overlay at 2× speed: the overlay's start is 4s of
            // source past the partner's SourceIn
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(), TrackId = screenTrack.Id, LinkGroupId = mine,
                TimelineStartTicks = Sec(8), DurationTicks = Sec(10),
                Content = new MediaContent { SourceId = source.Id, StreamIndex = 0, SourceInTicks = Sec(1), Speed = 2.0 },
            });
            var overlay = new Item
            {
                Id = Guid.NewGuid(), TrackId = overlayTrack.Id, LinkGroupId = mine,
                TimelineStartTicks = Sec(10), DurationTicks = Sec(5),
                Content = new KeyboardContent { SourceId = source.Id },
            };
            project.Items.Add(overlay);

            Assert.True(OverlayTiming.TryResolve(project, overlay, source.Id, out var sourceIn, out var speed));
            Assert.Equal(Sec(5), sourceIn);
            Assert.Equal(2.0, speed);
        }

        [Fact]
        public void Overlay_ignores_audio_and_webcam_streams_and_fails_without_a_screen_item()
        {
            var project = TimingProject(out var source, out var screenTrack, out var overlayTrack);
            var audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Order = 0 };
            project.Tracks.Add(audioTrack);

            // the audio item of the same recording covers the overlay, but it is not the screen
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(), TrackId = audioTrack.Id,
                TimelineStartTicks = 0, DurationTicks = Sec(30),
                Content = new MediaContent { SourceId = source.Id, StreamIndex = 1 },
            });
            // neither is a screen item that does not overlap
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(), TrackId = screenTrack.Id,
                TimelineStartTicks = Sec(20), DurationTicks = Sec(5),
                Content = new MediaContent { SourceId = source.Id, StreamIndex = 0 },
            });
            var overlay = new Item
            {
                Id = Guid.NewGuid(), TrackId = overlayTrack.Id,
                TimelineStartTicks = Sec(10), DurationTicks = Sec(5),
                Content = new CursorContent { SourceId = source.Id },
            };
            project.Items.Add(overlay);

            Assert.False(OverlayTiming.TryResolve(project, overlay, source.Id, out var sourceIn, out var speed));
            Assert.Equal(0, sourceIn);
            Assert.Equal(1.0, speed);

            Assert.False(OverlayTiming.TryResolve(null, overlay, source.Id, out _, out _));
            Assert.False(OverlayTiming.TryResolve(project, overlay, Guid.NewGuid(), out _, out _));
        }
    }
}
