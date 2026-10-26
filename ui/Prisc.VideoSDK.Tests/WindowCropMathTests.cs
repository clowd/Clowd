using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Clowd.UI.VideoEditor;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="WindowCropMath"/>: a recorded window's rect resolved into the fractional crop a
    /// following item draws with. The assertion everything else hangs off is that the crop IS the
    /// window — not grown, not trimmed, not squared to the box — so the item shows the window's
    /// pixels and nothing beside them, and the drawn box takes the shape of whatever region that
    /// leaves. The box therefore keeps its width and its centre while the window moves, and
    /// changes height when the window is RESIZED; a stored ratio preset, stretch or explicit
    /// height is dropped rather than fought with, which is what the editor's hidden ASPECT RATIO
    /// block reflects. All of it checked through the real mapping struct, not by re-deriving the
    /// formula. Around it: the sidecar-to-source coordinate conversion (the region's SIZE is
    /// divided out, its ORIGIN is never subtracted — the opposite of the cursor path), the
    /// clip at the picture's edges, the whole-pixel pin on every crop edge, and every degrade of
    /// <see cref="WindowCropMath.Effective"/>, all of which must hand back the stored transform
    /// itself rather than draw nothing. One composed-pixel case closes the loop through
    /// <c>FrameComposer.Compose</c> on the CPU factory.
    /// </summary>
    public class WindowCropMathTests
    {
        private const long Sec = 10_000_000;
        private const double ImgW = 1920, ImgH = 1080;
        private const int CanvasW = 1920, CanvasH = 1080;

        // ------------------------------------------------------------------------------ builders

        private static WindowCaptureHeader Header(int x = 0, int y = 0, int w = 1920, int h = 1080) =>
            new WindowCaptureHeader { Version = 1, RegionX = x, RegionY = y, RegionWidth = w, RegionHeight = h };

        private static WindowFrame Row(int x, int y, int w, int h, double t = 0) =>
            new WindowFrame(t, x, y, w, h);

        private static CropRect Crop(WindowFrame row, WindowCaptureHeader header = null,
            double imgW = ImgW, double imgH = ImgH, CropRect inset = null) =>
            WindowCropMath.CropFor(row, inset, header ?? Header(), imgW, imgH);

        /// <summary>What <see cref="WindowCropMath.Effective"/> builds: the stored transform with
        /// the resolved crop swapped in and the aspect intent dropped, because the window owns the
        /// picture's shape while it is followed.</summary>
        private static Transform Resolved(Transform stored, WindowFrame row)
        {
            var crop = Crop(row, inset: stored.Crop);
            Assert.NotNull(crop);
            var effective = stored.Clone();
            effective.Crop = crop;
            effective.Aspect = null;
            effective.AspectStretch = false;
            effective.ScaleY = null;
            return effective;
        }

        private static (double W, double H) Extent(CropRect crop, double imgW = ImgW, double imgH = ImgH) =>
            ((1 - crop.Left - crop.Right) * imgW, (1 - crop.Top - crop.Bottom) * imgH);

        private static PictureMapping Map(Transform transform, double imgW = ImgW, double imgH = ImgH)
        {
            Assert.True(PictureMapping.TryMap(transform, ItemEffects.Identity, imgW, imgH,
                CanvasW, CanvasH, out var map));
            return map;
        }

        private static void AssertSameRect(SKRect expected, SKRect actual)
        {
            Assert.Equal(expected.Left, actual.Left, 1e-3);
            Assert.Equal(expected.Top, actual.Top, 1e-3);
            Assert.Equal(expected.Right, actual.Right, 1e-3);
            Assert.Equal(expected.Bottom, actual.Bottom, 1e-3);
        }

        /// <summary>The window shapes the box rules are checked against: taller than the box,
        /// wider than it, hanging off a corner, and covering the whole picture.</summary>
        private static readonly WindowFrame[] Shapes =
        {
            Row(100, 100, 400, 300),
            Row(200, 500, 1200, 200),
            Row(-150, -80, 600, 400),
            Row(0, 0, 1920, 1080),
        };

        /// <summary>The box the item is drawn in keeps the width and the centre its transform
        /// asks for, whatever the window is doing, and takes its HEIGHT from the shown region —
        /// which is the window. Checked through PictureMapping, so it is the composer's own
        /// arithmetic rather than a restatement of it.</summary>
        private static void AssertBoxFramesTheWindow(Transform stored)
        {
            var box = Map(stored).Dest;
            foreach (var row in Shapes)
            {
                var effective = Resolved(stored, row);
                var dest = Map(effective).Dest;
                var (w, h) = Extent(effective.Crop);

                // 1e-3, like AssertSameRect: SKRect is float, so the dest edges carry a few
                // ulps the double arithmetic behind them does not
                Assert.Equal(box.Width, dest.Width, 1e-3);
                Assert.Equal(box.MidX, dest.MidX, 1e-3);
                Assert.Equal(box.MidY, dest.MidY, 1e-3);
                Assert.Equal(dest.Width * (h / w), dest.Height, 1e-3);
            }
        }

        // ---------------------------------------------------------------------------- sidecars

        private const string HeaderLine =
            """{"type":"header","version":1,"region":[0,0,1920,1080],"fps_num":30,"fps_den":1,"platform":"windows"}""";

        private static string InfoLine(int id, string title = "README.md", string app = "Code.exe", int pid = 4212)
            => $$"""{"type":"window_info","id":{{id}},"title":"{{title}}","app":"{{app}}","pid":{{pid}}}""";

        private static string WindowLine(double t, int id, int x, int y, int w, int h)
            => $$"""{"type":"window","t":{{t}},"id":{{id}},"x":{{x}},"y":{{y}},"w":{{w}},"h":{{h}},"z":0}""";

        /// <summary>A real file under a GUID name: <see cref="WindowCapture.Get"/> caches by path
        /// for the life of the process, so two tests must never share one. Written without a
        /// BOM, as the recorder writes it — a BOM is not JSON and would cost the header row.</summary>
        private static string WriteSidecar(params string[] lines)
        {
            var path = Path.Combine(Path.GetTempPath(), $"clowd-wincrop-{Guid.NewGuid():N}.jsonl");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(String.Join("\n", lines)));
            return path;
        }

        /// <summary>A project holding one recording whose screen stream is 0 and webcam stream is
        /// 1, and a media item playing one of them; the item follows window 7.</summary>
        private static (Project Project, MediaContent Media, Transform Stored) Following(
            string sidecarPath, int streamIndex = 0, int windowId = 7, int screenW = 1920, int screenH = 1080)
        {
            var source = new Source
            {
                Id = Guid.NewGuid(),
                Path = @"C:\rec\video.mp4",
                WindowCapturePath = sidecarPath,
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video, Width = screenW, Height = screenH },
                    new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 640, Height = 480 },
                },
            };
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = CanvasW, HeightPx = CanvasH, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
            };
            project.Sources.Add(source);

            var media = new MediaContent { SourceId = source.Id, StreamIndex = streamIndex };
            var stored = new Transform
            {
                CropWindow = new WindowCrop { WindowId = windowId, Title = "README.md", App = "Code.exe", Pid = 4212 },
            };
            return (project, media, stored);
        }

        private static Transform Effective(Project project, MediaContent media, Transform stored, double timeMs) =>
            WindowCropMath.Effective(project, media, stored, (long)(timeMs * TimeSpan.TicksPerMillisecond));

        // -------------------------------------------------------------------------------- shape

        [Fact]
        public void A_window_crops_to_exactly_itself()
        {
            var crop = Crop(Row(480, 270, 960, 540));

            Assert.Equal(0.25, crop.Left, 1e-12);
            Assert.Equal(0.25, crop.Top, 1e-12);
            Assert.Equal(0.25, crop.Right, 1e-12);
            Assert.Equal(0.25, crop.Bottom, 1e-12);
        }

        [Fact]
        public void A_window_shaped_unlike_the_box_is_still_cropped_to_exactly_itself()
        {
            // the old rule grew this one sideways to the box's 16:9, which showed a strip of
            // desktop down either side of the window — the whole reason the rule changed
            var crop = Crop(Row(760, 240, 400, 600));
            var (w, h) = Extent(crop);

            Assert.Equal(400, w, 1e-9);
            Assert.Equal(600, h, 1e-9);
            Assert.Equal(760, crop.Left * ImgW, 1e-9);
            Assert.Equal(240, crop.Top * ImgH, 1e-9);
        }

        // ------------------------------------------------------------------------- the box rules

        [Fact]
        public void The_box_keeps_its_width_and_takes_its_height_from_the_window()
        {
            AssertBoxFramesTheWindow(new Transform());
            AssertBoxFramesTheWindow(new Transform { Scale = 0.5, X = 0.3, Y = 0.6 });
        }

        [Fact]
        public void An_explicit_height_is_dropped_while_a_window_is_followed()
        {
            // ScaleY would put the box at a ratio the crop is not, and Skia would distort the
            // window into it. The stored value is left alone — the editor just hides the tile.
            var stored = new Transform { Scale = 0.5, ScaleY = 0.4, X = 0.3, Y = 0.6 };
            AssertBoxFramesTheWindow(stored);
            Assert.Equal(0.4, stored.ScaleY);
        }

        [Fact]
        public void A_ratio_preset_is_dropped_while_a_window_is_followed()
        {
            var stored = new Transform { Scale = 0.5, Aspect = 1.0, X = 0.3, Y = 0.6 };
            AssertBoxFramesTheWindow(stored);
            AssertBoxFramesTheWindow(new Transform { Scale = 0.5, Aspect = 1.0, AspectStretch = true });
            Assert.Equal(1.0, stored.Aspect);
        }

        [Fact]
        public void A_resize_changes_the_box_but_a_move_does_not()
        {
            var stored = new Transform { Scale = 0.5, X = 0.3, Y = 0.6 };

            var first = Map(Resolved(stored, Row(100, 100, 400, 300))).Dest;
            var moved = Map(Resolved(stored, Row(900, 500, 400, 300))).Dest;
            var resized = Map(Resolved(stored, Row(100, 100, 400, 600))).Dest;

            AssertSameRect(first, moved);
            Assert.Equal(first.Width, resized.Width, 1e-3);
            Assert.Equal(first.Height * 2, resized.Height, 1e-3);
        }

        [Fact]
        public void A_stored_hand_crop_cuts_the_window_rather_than_the_picture()
        {
            var stored = new Transform { Scale = 0.5, Crop = new CropRect { Left = 0.1 } };
            AssertBoxFramesTheWindow(stored);
            Assert.Equal(0.1, stored.Crop.Left);

            // …and the editor's gizmo asks at the composed time, so it boxes the window rather
            // than the whole recording
            var (project, media, _) = Following(sidecarPath: null);
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video };
            project.Tracks.Add(track);
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                DurationTicks = 10 * Sec,
                Content = media,
                Transform = stored,
            };
            project.Items.Add(item);

            var gizmo = ItemPlacement.ContentAspect(project, item, CanvasW, CanvasH);
            Assert.NotNull(gizmo);
            // no sidecar here, so the follow degrades and the gizmo reads the stored crop
            Assert.Equal(AspectMath.DisplayAspect(stored, ImgW, ImgH).Value, gizmo.Value, 1e-12);
        }

        [Fact]
        public void The_gizmo_boxes_the_window_at_the_composed_time()
        {
            // 480×540 at t=0, twice as wide from t=3s: the box the editor draws has to follow the
            // second shape once the playhead is past it, or the handles sit off the picture
            var path = WriteSidecar(HeaderLine, InfoLine(7),
                WindowLine(0, 7, 100, 100, 480, 540),
                WindowLine(3000, 7, 100, 100, 960, 540));
            try
            {
                var (project, media, stored) = Following(path);
                stored.Scale = 0.5;
                var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video };
                project.Tracks.Add(track);
                var item = new Item
                {
                    Id = Guid.NewGuid(),
                    TrackId = track.Id,
                    DurationTicks = 10 * Sec,
                    Content = media,
                    Transform = stored,
                };
                project.Items.Add(item);

                var early = ItemPlacement.ContentAspect(project, item, CanvasW, CanvasH, 0);
                var late = ItemPlacement.ContentAspect(project, item, CanvasW, CanvasH, 5 * Sec);
                Assert.Equal(540.0 / 480, early.Value, 1e-9);
                Assert.Equal(540.0 / 960, late.Value, 1e-9);

                // and the placement it drives lands on the composer's own dest rect
                Assert.True(ItemPlacement.TryResolve(project, item, CanvasW, CanvasH, out var placed, 5 * Sec));
                var map = Map(Effective(project, media, stored, 5000));
                Assert.Equal(map.Dest.Width, placed.W, 1e-3);
                Assert.Equal(map.Dest.Height, placed.H, 1e-3);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void The_resolved_transform_carries_no_aspect_intent()
        {
            var path = WriteSidecar(HeaderLine, InfoLine(7), WindowLine(0, 7, 100, 100, 400, 300));
            try
            {
                var (project, media, stored) = Following(path);
                stored.Aspect = 1.0;
                stored.AspectStretch = true;
                stored.ScaleY = 0.4;

                var effective = Effective(project, media, stored, 0);

                Assert.Null(effective.Aspect);
                Assert.False(effective.AspectStretch);
                Assert.Null(effective.ScaleY);
                // the model keeps them, so going back to a manual crop restores the item
                Assert.Equal(1.0, stored.Aspect);
                Assert.True(stored.AspectStretch);
                Assert.Equal(0.4, stored.ScaleY);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void A_ratio_preset_trims_nothing()
        {
            // the preset is gone from the resolved transform, so SourceInsets has nothing to trim
            // with and the whole window is shown — which is why the editor hides the tiles
            var stored = new Transform { Aspect = 1.0, AspectStretch = false };
            foreach (var row in Shapes)
            {
                var effective = Resolved(stored, row);
                var (l, t, r, b) = AspectMath.SourceInsets(effective, ImgW, ImgH);
                Assert.Equal(effective.Crop.Left, l, 1e-9);
                Assert.Equal(effective.Crop.Top, t, 1e-9);
                Assert.Equal(effective.Crop.Right, r, 1e-9);
                Assert.Equal(effective.Crop.Bottom, b, 1e-9);
            }
        }

        [Fact]
        public void The_source_and_dest_rects_share_a_ratio()
        {
            // Skia stretches source into dest unconditionally; no distortion means equal scales
            var stored = new Transform { Scale = 0.5 };
            foreach (var row in new[] { Row(100, 100, 400, 300), Row(200, 500, 1200, 200), Row(700, 300, 500, 500) })
            {
                var map = Map(Resolved(stored, row));
                Assert.Equal(map.ScaleX, map.ScaleY, 1e-6);
            }
        }

        // ------------------------------------------------------------------------------- insets

        [Fact]
        public void The_stored_insets_are_fractions_of_the_window_not_the_picture()
        {
            // 10% off the top of a 300-tall window is 30px, wherever the window is and whatever
            // the recording's own size — which is what makes "cut the title bar" hold as the
            // window moves and resizes
            var inset = new CropRect { Top = 0.1 };
            var small = Crop(Row(100, 100, 400, 300), inset: inset);
            var large = Crop(Row(100, 100, 800, 600), inset: inset);

            Assert.Equal(130, small.Top * ImgH, 1e-9);
            Assert.Equal(400, Extent(small).W, 1e-9);
            Assert.Equal(270, Extent(small).H, 1e-9);

            // same fraction, twice the window: twice the cut
            Assert.Equal(160, large.Top * ImgH, 1e-9);
            Assert.Equal(540, Extent(large).H, 1e-9);
        }

        [Fact]
        public void All_four_insets_cut_from_their_own_edge()
        {
            var crop = Crop(Row(100, 100, 400, 300),
                inset: new CropRect { Left = 0.25, Top = 0.1, Right = 0.5, Bottom = 0.2 });

            Assert.Equal(200, crop.Left * ImgW, 1e-9);              // 100 + 0.25·400
            Assert.Equal(130, crop.Top * ImgH, 1e-9);               // 100 + 0.1·300
            Assert.Equal(100, Extent(crop).W, 1e-9);                // 400 − 0.25·400 − 0.5·400
            Assert.Equal(210, Extent(crop).H, 1e-9);                // 300 − 0.1·300 − 0.2·300
        }

        [Fact]
        public void An_inset_moves_with_the_window()
        {
            var inset = new CropRect { Top = 0.1 };
            var first = Crop(Row(100, 100, 400, 300), inset: inset);
            var later = Crop(Row(900, 500, 400, 300), inset: inset);

            Assert.Equal(Extent(first).W, Extent(later).W, 1e-9);
            Assert.Equal(Extent(first).H, Extent(later).H, 1e-9);
            Assert.Equal(530, later.Top * ImgH, 1e-9);
        }

        [Fact]
        public void An_inset_on_a_window_off_the_edge_still_cuts_the_window()
        {
            // the cut is measured on the window's own rect, then the clip takes what is left: a
            // title bar hanging above the region is not a reason to cut 10% off what remains
            var crop = Crop(Row(-100, -50, 400, 300), inset: new CropRect { Top = 0.1 });

            Assert.Equal(0, crop.Top, 1e-12);                       // −50 + 30 is still above 0
            Assert.Equal(300, Extent(crop).W, 1e-9);                // clipped at the left edge
            Assert.Equal(250, Extent(crop).H, 1e-9);                // −20..230
        }

        [Fact]
        public void Insets_that_leave_nothing_produce_no_crop()
        {
            Assert.Null(Crop(Row(100, 100, 400, 300), inset: new CropRect { Left = 0.6, Right = 0.6 }));
            Assert.Null(Crop(Row(100, 100, 400, 300), inset: new CropRect { Top = 1 }));
        }

        [Fact]
        public void An_inset_shapes_the_box_like_any_other_crop()
        {
            // the box takes the shown region's ratio, and the shown region is what the insets left
            var stored = new Transform { Scale = 0.5, Crop = new CropRect { Bottom = 0.5 } };
            var full = Map(Resolved(new Transform { Scale = 0.5 }, Row(100, 100, 400, 300))).Dest;
            var halved = Map(Resolved(stored, Row(100, 100, 400, 300))).Dest;

            Assert.Equal(full.Width, halved.Width, 1e-3);
            Assert.Equal(full.Height / 2, halved.Height, 1e-3);
        }

        // -------------------------------------------------------------------------------- edges

        [Fact]
        public void A_window_off_the_left_edge_is_clipped_not_slid()
        {
            // only the part of the window that was inside the region has pixels; sliding the rect
            // back in would keep its size by framing desktop the window was never over
            var offLeft = Crop(Row(-100, 390, 400, 300));

            Assert.Equal(0, offLeft.Left);
            Assert.Equal(300, Extent(offLeft).W, 1e-9);
            Assert.Equal(300, Extent(offLeft).H, 1e-9);
        }

        [Fact]
        public void A_window_past_the_far_edges_is_clipped_there_too()
        {
            var corner = Crop(Row(1700, 900, 400, 300)); // past both far edges

            Assert.Equal(0, corner.Right, 1e-9);
            Assert.Equal(0, corner.Bottom, 1e-9);
            Assert.Equal(220, Extent(corner).W, 1e-9);
            Assert.Equal(180, Extent(corner).H, 1e-9);
        }

        [Fact]
        public void A_window_larger_than_the_picture_shows_the_whole_picture()
        {
            var crop = Crop(Row(-200, -200, 2500, 1600));

            Assert.Equal(0, crop.Left, 1e-12);
            Assert.Equal(0, crop.Top, 1e-12);
            Assert.Equal(0, crop.Right, 1e-12);
            Assert.Equal(0, crop.Bottom, 1e-12);
        }

        [Fact]
        public void A_window_covering_the_whole_region_shows_the_whole_frame()
        {
            var crop = Crop(Row(0, 0, 1920, 1080));

            Assert.Equal(0, crop.Left, 1e-12);
            Assert.Equal(0, crop.Top, 1e-12);
            Assert.Equal(0, crop.Right, 1e-12);
            Assert.Equal(0, crop.Bottom, 1e-12);
        }

        [Fact]
        public void A_window_that_would_leave_nothing_still_draws_the_stored_crop()
        {
            // the row is still a rect (the parser never emits a zero one), so a follow still resolves
            var (project, media, stored) = Following(WriteSidecar(HeaderLine, InfoLine(7), WindowLine(0, 7, 1, 1, 1, 1)));
            try
            {
                var effective = Effective(project, media, stored, 0);
                Assert.NotSame(stored, effective);
                Assert.True(PictureMapping.TryMap(effective, ItemEffects.Identity, ImgW, ImgH,
                    CanvasW, CanvasH, out _));
            }
            finally
            {
                File.Delete(project.Sources[0].WindowCapturePath);
            }
        }

        // -------------------------------------------------------------------------- coordinates

        [Fact]
        public void A_capped_video_scales_the_rows_by_the_header()
        {
            // a 1920×1080 region encoded at 1280×720: every row edge is carried across at 2/3
            var crop = Crop(Row(600, 270, 960, 540), imgW: 1280, imgH: 720);

            Assert.Equal(400.0 / 1280, crop.Left, 1e-12);
            Assert.Equal(180.0 / 720, crop.Top, 1e-12);
            Assert.Equal(240.0 / 1280, crop.Right, 1e-12);
            Assert.Equal(180.0 / 720, crop.Bottom, 1e-12);
        }

        [Fact]
        public void The_region_origin_is_never_subtracted()
        {
            // rows are region-relative already; only the input-capture sidecar carries absolutes
            var row = Row(300, 200, 800, 450);
            var atOrigin = Crop(row, header: Header(0, 0));
            var offset = Crop(row, header: Header(-100, 50));

            Assert.Equal(atOrigin.Left, offset.Left);
            Assert.Equal(atOrigin.Top, offset.Top);
            Assert.Equal(atOrigin.Right, offset.Right);
            Assert.Equal(atOrigin.Bottom, offset.Bottom);
        }

        [Fact]
        public void A_header_without_a_region_reads_the_rows_as_source_pixels()
        {
            var lost = new WindowCaptureHeader();
            var crop = Crop(Row(480, 270, 960, 540), header: lost);

            Assert.Equal(0.25, crop.Left, 1e-12);
            Assert.Equal(0.25, crop.Right, 1e-12);
        }

        [Fact]
        public void The_crop_origin_is_pinned_to_whole_source_pixels()
        {
            // 2/3 scale lands the window's edges between pixels; the origin is rounded onto one,
            // the extent is left alone so the box ratio does not breathe
            var crop = Crop(Row(481, 271, 960, 540), imgW: 1280, imgH: 720);

            double left = crop.Left * 1280, top = crop.Top * 720;
            Assert.Equal(Math.Round(left), left, 1e-9);
            Assert.Equal(Math.Round(top), top, 1e-9);
            Assert.Equal(321, left, 1e-9);
            Assert.Equal(181, top, 1e-9);

            var (w, h) = Extent(crop, 1280, 720);
            Assert.Equal(640, w, 1e-9);
            Assert.Equal(360, h, 1e-9);
        }

        [Fact]
        public void A_degenerate_row_produces_no_crop()
        {
            Assert.Null(Crop(Row(100, 100, 0, 300)));
            Assert.Null(Crop(Row(100, 100, 400, 0)));
            Assert.Null(Crop(Row(100, 100, 400, 300), imgW: 0, imgH: 1080));
            // wholly outside the picture: the clip leaves nothing, and the caller draws the
            // stored crop rather than nothing at all
            Assert.Null(Crop(Row(2200, 100, 400, 300)));
        }

        // ----------------------------------------------------------------------------- degrades

        [Fact]
        public void No_follow_returns_the_stored_transform_itself()
        {
            var (project, media, _) = Following(sidecarPath: null);

            var plain = new Transform();
            Assert.Same(plain, Effective(project, media, plain, 500));

            var unpicked = new Transform { CropWindow = new WindowCrop { WindowId = 0 } };
            Assert.Same(unpicked, Effective(project, media, unpicked, 500));

            Assert.Null(WindowCropMath.Effective(project, media, null, 0));
        }

        [Fact]
        public void A_follow_without_a_sidecar_draws_the_stored_crop()
        {
            var (project, media, stored) = Following(sidecarPath: null);
            Assert.Same(stored, Effective(project, media, stored, 500));

            // a path that no longer resolves is the same degrade, cached after the first probe
            var gone = Path.Combine(Path.GetTempPath(), $"clowd-wincrop-missing-{Guid.NewGuid():N}.jsonl");
            var (project2, media2, stored2) = Following(gone);
            Assert.Same(stored2, Effective(project2, media2, stored2, 500));
            Assert.Same(stored2, Effective(project2, media2, stored2, 500));
        }

        [Fact]
        public void A_follow_on_a_webcam_stream_is_ignored()
        {
            var path = WriteSidecar(HeaderLine, InfoLine(7), WindowLine(0, 7, 480, 270, 960, 540));
            try
            {
                var (project, media, stored) = Following(path, streamIndex: 1);
                Assert.Same(stored, Effective(project, media, stored, 500));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void A_follow_whose_window_is_missing_draws_the_stored_crop()
        {
            var path = WriteSidecar(HeaderLine, InfoLine(7), WindowLine(0, 7, 480, 270, 960, 540));
            try
            {
                var (project, media, stored) = Following(path, windowId: 99);
                Assert.Same(stored, Effective(project, media, stored, 500));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void A_follow_on_an_unknown_source_draws_the_stored_crop()
        {
            var (project, _, stored) = Following(sidecarPath: null);
            var orphan = new MediaContent { SourceId = Guid.NewGuid(), StreamIndex = 0 };
            Assert.Same(stored, Effective(project, orphan, stored, 500));
        }

        [Fact]
        public void A_resolved_follow_never_writes_the_stored_transform()
        {
            var path = WriteSidecar(HeaderLine, InfoLine(7), WindowLine(0, 7, 480, 270, 960, 540));
            try
            {
                var (project, media, stored) = Following(path);
                stored.Crop = new CropRect { Left = 0.1 };

                var effective = Effective(project, media, stored, 500);

                Assert.NotSame(stored, effective);
                Assert.NotSame(stored.Crop, effective.Crop);
                Assert.Equal(0.1, stored.Crop.Left);
                Assert.Equal(0, stored.Crop.Right);
                Assert.NotNull(effective.CropWindow);
                Assert.Equal(7, effective.CropWindow.WindowId);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void A_gone_window_holds_its_last_rect()
        {
            var before = Row(100, 100, 400, 300);
            var last = Row(500, 200, 400, 300);
            var after = Row(800, 300, 400, 300);
            var path = WriteSidecar(
                HeaderLine,
                InfoLine(7),
                WindowLine(0, 7, before.X, before.Y, before.Width, before.Height),
                WindowLine(1000, 7, last.X, last.Y, last.Width, last.Height),
                WindowLine(2000, 7, 0, 0, 0, 0),            // minimized: the leave sentinel
                WindowLine(4000, 7, after.X, after.Y, after.Width, after.Height));
            try
            {
                var (project, media, stored) = Following(path);

                AssertCrop(Crop(before), Effective(project, media, stored, 500).Crop);
                AssertCrop(Crop(last), Effective(project, media, stored, 1500).Crop);
                AssertCrop(Crop(last), Effective(project, media, stored, 3000).Crop);   // inside the gap
                AssertCrop(Crop(after), Effective(project, media, stored, 5000).Crop);
                AssertCrop(Crop(before), Effective(project, media, stored, -100).Crop); // held backwards
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void AssertCrop(CropRect expected, CropRect actual)
        {
            Assert.NotNull(actual);
            Assert.Equal(expected.Left, actual.Left, 1e-12);
            Assert.Equal(expected.Top, actual.Top, 1e-12);
            Assert.Equal(expected.Right, actual.Right, 1e-12);
            Assert.Equal(expected.Bottom, actual.Bottom, 1e-12);
        }

        [Fact]
        public void A_capped_recording_resolves_against_the_stream_not_the_region()
        {
            // the probe says 1280×720 for a 1920×1080 region: the same 2/3 CropFor applies
            var path = WriteSidecar(HeaderLine, InfoLine(7), WindowLine(0, 7, 600, 270, 960, 540));
            try
            {
                var (project, media, stored) = Following(path, screenW: 1280, screenH: 720);
                var crop = Effective(project, media, stored, 500).Crop;
                Assert.Equal(400.0 / 1280, crop.Left, 1e-12);
                Assert.Equal(240.0 / 1280, crop.Right, 1e-12);
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ---------------------------------------------------------------------------- composed

        private const int PixW = 64, PixH = 64;

        private sealed class StillFrameSource : IFrameSource, IDisposable
        {
            private readonly SKImage _image;

            public StillFrameSource(SKImage image)
            {
                _image = image;
            }

            public bool TryGetFrame(Guid sourceId, int streamIndex, long sourceTimeTicks, out FrameRef frame)
            {
                frame = new FrameRef(_image, sourceTimeTicks);
                return true;
            }

            public void Dispose() => _image.Dispose();
        }

        private static byte[] Render(Project p, long timeTicks, IFrameSource frames)
        {
            using var factory = new CpuSurfaceFactory();
            using var surface = factory.CreateSurface(PixW, PixH);
            FrameComposer.Compose(p, timeTicks, frames, surface.Canvas, PixW, PixH);

            int rowBytes = PixW * 4;
            var native = Marshal.AllocHGlobal(rowBytes * PixH);
            try
            {
                Assert.True(factory.TryReadPixels(surface, PixW, PixH, native, rowBytes));
                var pixels = new byte[rowBytes * PixH];
                Marshal.Copy(native, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        private static (byte B, byte G, byte R) Px(byte[] bgra, int x, int y)
        {
            int i = y * PixW * 4 + x * 4;
            return (bgra[i], bgra[i + 1], bgra[i + 2]);
        }

        /// <summary>The bounding box of everything drawn over the black canvas.</summary>
        private static (int L, int T, int R, int B) Painted(byte[] bgra)
        {
            int l = PixW, t = PixH, r = -1, b = -1;
            for (int y = 0; y < PixH; y++)
            {
                for (int x = 0; x < PixW; x++)
                {
                    var (pb, pg, pr) = Px(bgra, x, y);
                    if (pb + pg + pr <= 24)
                        continue;
                    l = Math.Min(l, x);
                    t = Math.Min(t, y);
                    r = Math.Max(r, x);
                    b = Math.Max(b, y);
                }
            }
            return (l, t, r, b);
        }

        [Fact]
        public void A_moving_window_changes_the_pixels_but_not_the_box() // it keeps its shape
        {
            // a 192×108 recording, red on the left half and green on the right; the followed
            // window sits in the red half at t=0 and in the green half from t=3s
            const int srcW = 192, srcH = 108;
            var path = WriteSidecar(
                """{"type":"header","version":1,"region":[0,0,192,108],"fps_num":30,"fps_den":1,"platform":"windows"}""",
                InfoLine(7),
                WindowLine(0, 7, 16, 36, 64, 36),
                WindowLine(3000, 7, 112, 36, 64, 36));

            using var surface = SKSurface.Create(new SKImageInfo(srcW, srcH, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var red = new SKPaint { Color = SKColors.Red })
            using (var green = new SKPaint { Color = SKColors.Lime })
            {
                surface.Canvas.DrawRect(SKRect.Create(0, 0, srcW / 2f, srcH), red);
                surface.Canvas.DrawRect(SKRect.Create(srcW / 2f, 0, srcW / 2f, srcH), green);
            }
            using var frames = new StillFrameSource(surface.Snapshot());

            try
            {
                var (project, media, stored) = Following(path, screenW: srcW, screenH: srcH);
                stored.Scale = 0.5;
                var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video };
                project.Tracks.Add(track);
                project.Items.Add(new Item
                {
                    Id = Guid.NewGuid(),
                    TrackId = track.Id,
                    DurationTicks = 10 * Sec,
                    Content = media,
                    Transform = stored,
                });

                var early = Render(project, 0, frames);
                var late = Render(project, 5 * Sec, frames);

                // 0.5 of a 64-wide canvas at 16:9 is a 32×18 box centred on (32,32)
                Assert.Equal((16, 23, 47, 40), Painted(early));
                Assert.Equal(Painted(early), Painted(late));

                var (b0, g0, r0) = Px(early, PixW / 2, PixH / 2);
                Assert.True(r0 > 200 && g0 < 30 && b0 < 30, "expected red inside the box at t=0");
                var (b1, g1, r1) = Px(late, PixW / 2, PixH / 2);
                Assert.True(g1 > 200 && r1 < 30 && b1 < 30, "expected green inside the box at t=5s");

                // the model was never written to
                Assert.Null(stored.Crop);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
