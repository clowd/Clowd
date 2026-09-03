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
    /// following item draws with. The one assertion everything else hangs off is that the crop
    /// is grown to the ratio the item's box ALREADY has, so <c>PictureMapping.TryMap</c> lands the
    /// effective transform on exactly the dest rect it lands the stored one on — the box never
    /// moves as the window moves or resizes, in every transform state (locked, explicit height,
    /// ratio preset, a hand crop still stored). That is checked through the real mapping struct,
    /// not by re-deriving the formula. Around it: the sidecar-to-source coordinate conversion
    /// (the region's SIZE is divided out, its ORIGIN is never subtracted — the opposite of the
    /// cursor path), the slide-not-shrink clamp at the picture's edges, the whole-pixel pin on
    /// the crop origin, and every degrade of <see cref="WindowCropMath.Effective"/>, all of which
    /// must hand back the stored transform itself rather than draw nothing. One composed-pixel
    /// case closes the loop through <c>FrameComposer.Compose</c> on the CPU factory.
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

        private static CropRect Crop(WindowFrame row, Transform stored = null, WindowCaptureHeader header = null,
            double imgW = ImgW, double imgH = ImgH) =>
            WindowCropMath.CropFor(row, stored ?? new Transform(), header ?? Header(), imgW, imgH, CanvasW, CanvasH);

        /// <summary>What <see cref="WindowCropMath.Effective"/> builds: the stored transform with
        /// the resolved crop swapped in, nothing else touched.</summary>
        private static Transform Resolved(Transform stored, WindowFrame row)
        {
            var crop = Crop(row, stored);
            Assert.NotNull(crop);
            var effective = stored.Clone();
            effective.Crop = crop;
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

        /// <summary>The window shapes the box invariant is checked against: taller than the box,
        /// wider than it, hanging off a corner, and covering the whole picture.</summary>
        private static readonly WindowFrame[] Shapes =
        {
            Row(100, 100, 400, 300),
            Row(200, 500, 1200, 200),
            Row(-150, -80, 600, 400),
            Row(0, 0, 1920, 1080),
        };

        private static void AssertBoxUnmoved(Transform stored)
        {
            var expected = Map(stored).Dest;
            foreach (var row in Shapes)
                AssertSameRect(expected, Map(Resolved(stored, row)).Dest);
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
            WindowCropMath.Effective(project, media, stored, (long)(timeMs * TimeSpan.TicksPerMillisecond),
                CanvasW, CanvasH);

        // -------------------------------------------------------------------------------- shape

        [Fact]
        public void A_window_matching_the_box_ratio_crops_to_exactly_it()
        {
            // 960×540 is the box's own 16:9, so nothing is grown: the crop IS the window
            var crop = Crop(Row(480, 270, 960, 540));

            Assert.Equal(0.25, crop.Left, 1e-12);
            Assert.Equal(0.25, crop.Top, 1e-12);
            Assert.Equal(0.25, crop.Right, 1e-12);
            Assert.Equal(0.25, crop.Bottom, 1e-12);
        }

        [Fact]
        public void A_taller_window_is_widened_to_the_box_ratio_not_trimmed()
        {
            var crop = Crop(Row(760, 240, 400, 600));
            var (w, h) = Extent(crop);

            // the crop's own ratio is the box's, and the window sits wholly inside it
            Assert.Equal(ImgH / ImgW, h / w, 1e-9);
            Assert.Equal(600, h, 1e-9);
            Assert.True(crop.Left * ImgW <= 760);
            Assert.True((1 - crop.Right) * ImgW >= 760 + 400);

            // centred on the window, give or take the whole-pixel pin on the origin
            Assert.Equal(960, crop.Left * ImgW + w / 2, 0.5);
            Assert.Equal(540, crop.Top * ImgH + h / 2, 0.5);
        }

        // ------------------------------------------------------------------------- the invariant

        [Fact]
        public void The_crop_never_moves_the_drawn_box()
        {
            // the single most important assertion in the feature: the dest rect TryMap lands the
            // effective transform on is the one it lands the stored transform on, for any window
            AssertBoxUnmoved(new Transform());
            AssertBoxUnmoved(new Transform { Scale = 0.5, X = 0.3, Y = 0.6 });
        }

        [Fact]
        public void The_box_is_unmoved_with_an_explicit_height()
        {
            AssertBoxUnmoved(new Transform { Scale = 0.5, ScaleY = 0.4, X = 0.3, Y = 0.6 });
        }

        [Fact]
        public void The_box_is_unmoved_with_a_ratio_preset()
        {
            AssertBoxUnmoved(new Transform { Scale = 0.5, Aspect = 1.0, X = 0.3, Y = 0.6 });
            AssertBoxUnmoved(new Transform { Scale = 0.5, Aspect = 1.0, AspectStretch = true });
        }

        [Fact]
        public void The_box_is_unmoved_when_a_hand_crop_is_still_stored()
        {
            var stored = new Transform { Scale = 0.5, Crop = new CropRect { Left = 0.1 } };
            AssertBoxUnmoved(stored);

            // …and the editor's gizmo, which reads the STORED transform with no notion of time,
            // boxes the same ratio the resolved crop is grown to
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
            Assert.Equal(WindowCropMath.BoxAspect(stored, ImgW, ImgH, CanvasW, CanvasH), gizmo.Value, 1e-12);
        }

        [Fact]
        public void A_ratio_preset_trims_nothing()
        {
            // fill: the squared crop already has the target ratio, so the fill trim is a no-op
            // and the whole window is shown — which is why the editor can disable Fill/Stretch
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

        [Fact]
        public void An_explicit_height_decides_the_box_ratio_before_a_preset()
        {
            // the same precedence PictureMapping.TryMap uses for its dest height
            var stored = new Transform { Scale = 0.5, ScaleY = 0.4 };
            Assert.Equal(0.45, WindowCropMath.BoxAspect(stored, ImgW, ImgH, CanvasW, CanvasH), 1e-12);

            var both = new Transform { Scale = 0.5, ScaleY = 0.4, Aspect = 1.0 };
            Assert.Equal(0.45, WindowCropMath.BoxAspect(both, ImgW, ImgH, CanvasW, CanvasH), 1e-12);
        }

        // -------------------------------------------------------------------------------- edges

        [Fact]
        public void A_window_off_the_left_edge_slides_into_frame()
        {
            var inside = Crop(Row(760, 390, 400, 300));
            var offLeft = Crop(Row(-100, 390, 400, 300));

            Assert.Equal(0, offLeft.Left);
            Assert.Equal(Extent(inside).W, Extent(offLeft).W, 1e-9);
            Assert.Equal(Extent(inside).H, Extent(offLeft).H, 1e-9);
        }

        [Fact]
        public void A_window_in_a_corner_slides_rather_than_shrinks()
        {
            var inside = Crop(Row(760, 390, 400, 300));
            var corner = Crop(Row(1700, 900, 400, 300)); // past both far edges

            Assert.Equal(Extent(inside).W, Extent(corner).W, 1e-9);
            Assert.Equal(Extent(inside).H, Extent(corner).H, 1e-9);
            Assert.Equal(0, corner.Right, 1e-9);
            Assert.Equal(0, corner.Bottom, 1e-9);
            Assert.True(corner.Left > 0);
            Assert.True(corner.Top > 0);
        }

        [Fact]
        public void A_window_larger_than_the_picture_is_capped()
        {
            var crop = Crop(Row(-200, -200, 2500, 1600));

            Assert.True(crop.Left >= 0 && crop.Top >= 0 && crop.Right >= 0 && crop.Bottom >= 0);
            var (w, h) = Extent(crop);
            Assert.Equal(ImgH / ImgW, h / w, 1e-9);
            Assert.True(w <= ImgW && h <= ImgH);
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

            Assert.Null(WindowCropMath.Effective(project, media, null, 0, CanvasW, CanvasH));
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
        public void A_moving_window_changes_the_pixels_but_not_the_box()
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
