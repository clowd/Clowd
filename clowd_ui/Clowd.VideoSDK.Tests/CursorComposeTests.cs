using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // Cursor overlay drawing: the shared picture mapping (crop/aspect respected), glyph
    // resolution fallbacks, the default native-box overlay and its suppression by a cursor
    // track, themed glyph/click-animation pixels. Mirrors ComposeTests' CPU-factory pattern.
    public class CursorComposeTests
    {
        private const long Sec = 10_000_000;
        private const int W = 64, H = 64;

        // ---------------------------------------------------------------------------- builders

        private static Project NewProject() => new Project
        {
            Output = new OutputSettings { WidthPx = W, HeightPx = H, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
        };

        private static Track AddVideoTrack(Project p, int order = 0)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = order };
            p.Tracks.Add(track);
            return track;
        }

        private static Item AddItem(Project p, Track track, ItemContent content,
            long start = 0, long duration = 10 * Sec, Guid? linkGroup = null)
        {
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = start,
                DurationTicks = duration,
                Content = content,
                LinkGroupId = linkGroup,
            };
            p.Items.Add(item);
            return item;
        }

        /// <summary>A recording source: screen stream 0 (64x64), cursor box stream 1.</summary>
        private static Source AddCaptureSource(Project p, string capturePath)
        {
            var source = new Source
            {
                Id = Guid.NewGuid(),
                Path = "recording.mp4",
                InputCapturePath = capturePath,
                CursorStreamIndex = 1,
            };
            source.Streams.Add(new SourceStream { Index = 0, Kind = StreamKind.Video, Width = W, Height = H });
            source.Streams.Add(new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 512, Height = 512 });
            p.Sources.Add(source);
            return source;
        }

        private static string WriteCapture(params string[] lines)
        {
            string path = Path.Combine(Path.GetTempPath(), $"clowd-cursor-compose-{Guid.NewGuid():N}.jsonl");
            File.WriteAllLines(path, lines);
            return path;
        }

        private const string Header =
            "{\"type\":\"header\",\"version\":1,\"region\":[0,0,64,64],\"fps_num\":30,\"fps_den\":1," +
            "\"platform\":\"windows\",\"monitors\":[{\"x\":0,\"y\":0,\"w\":64,\"h\":64,\"scale\":1.0}]}";

        private static string Frame(double t, int x, int y, string kind = "arrow", int buttons = 0) =>
            $"{{\"type\":\"frame\",\"t\":{t},\"x\":{x},\"y\":{y},\"b\":{buttons},\"c\":\"{kind}\"}}";

        private static string MouseEvent(string kind, double t, int x, int y) =>
            $"{{\"type\":\"event\",\"t\":{t},\"kind\":\"{kind}\",\"btn\":1,\"x\":{x},\"y\":{y}}}";

        /// <summary>Per-stream still frames: stream 0 = the screen, stream 1 = the cursor box.</summary>
        private sealed class MultiStreamSource : IFrameSource, IDisposable
        {
            private readonly Dictionary<int, SKImage> _streams = new Dictionary<int, SKImage>();

            public MultiStreamSource Set(int streamIndex, SKColor color, int size)
            {
                using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
                surface.Canvas.Clear(color);
                _streams[streamIndex] = surface.Snapshot();
                return this;
            }

            public bool TryGetFrame(Guid sourceId, int streamIndex, long sourceTimeTicks, out FrameRef frame)
            {
                if (_streams.TryGetValue(streamIndex, out var image))
                {
                    frame = new FrameRef(image, sourceTimeTicks);
                    return true;
                }
                frame = default;
                return false;
            }

            public void Dispose()
            {
                foreach (var image in _streams.Values)
                    image.Dispose();
            }
        }

        private static byte[] Render(Project p, long timeTicks, IFrameSource frames)
        {
            using var factory = new CpuSurfaceFactory();
            using var surface = factory.CreateSurface(W, H);
            FrameComposer.Compose(p, timeTicks, frames, surface.Canvas, W, H);

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

        private static (byte B, byte G, byte R, byte A) Px(byte[] bgra, int x, int y)
        {
            int i = y * W * 4 + x * 4;
            return (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
        }

        private static void AssertColor((byte B, byte G, byte R, byte A) actual,
            byte b, byte g, byte r, int tolerance = 1)
        {
            Assert.InRange(actual.B, Math.Max(0, b - tolerance), Math.Min(255, b + tolerance));
            Assert.InRange(actual.G, Math.Max(0, g - tolerance), Math.Min(255, g + tolerance));
            Assert.InRange(actual.R, Math.Max(0, r - tolerance), Math.Min(255, r + tolerance));
        }

        private static readonly SKColor Blue = new SKColor(0, 0, 255);
        private static readonly SKColor Red = new SKColor(255, 0, 0);
        private static readonly SKColor Green = new SKColor(0, 255, 0);

        // ----------------------------------------------------------------------- picture mapping

        [Fact]
        public void Mapping_full_frame_is_identity()
        {
            Assert.True(PictureMapping.TryMap(new Transform(), ItemEffects.Identity, W, H, W, H, out var map));
            Assert.Equal(1.0, map.ScaleX, 6);
            Assert.Equal(1.0, map.ScaleY, 6);
            Assert.Equal(new SKPoint(10, 20), map.Map(10, 20));
        }

        [Fact]
        public void Mapping_scaled_item_offsets_and_scales_points()
        {
            var transform = new Transform { Scale = 0.5 }; // 32x32 centred on 64x64
            Assert.True(PictureMapping.TryMap(transform, ItemEffects.Identity, W, H, W, H, out var map));
            Assert.Equal(0.5, map.ScaleX, 6);
            Assert.Equal(new SKPoint(16, 16), map.Map(0, 0));
            Assert.Equal(new SKPoint(48, 48), map.Map(64, 64));
        }

        [Fact]
        public void Mapping_crop_maps_the_shown_region_onto_the_dest()
        {
            // left half cropped away; ScaleY pins the dest to the square canvas so the surviving
            // 32px-wide region stretches over 64 canvas px (ScaleX = 2)
            var transform = new Transform { Crop = new CropRect { Left = 0.5 }, ScaleY = 1.0 };
            Assert.True(PictureMapping.TryMap(transform, ItemEffects.Identity, W, H, W, H, out var map));
            Assert.Equal(2.0, map.ScaleX, 6);
            Assert.Equal(new SKPoint(0, 0), map.Map(32, 0));   // region's left edge → canvas left
            Assert.Equal(new SKPoint(32, 32), map.Map(48, 32)); // region centre → canvas centre
        }

        [Fact]
        public void Mapping_cropped_to_nothing_fails()
        {
            var transform = new Transform { Crop = new CropRect { Left = 0.6, Right = 0.6 } };
            Assert.False(PictureMapping.TryMap(transform, ItemEffects.Identity, W, H, W, H, out _));
        }

        [Fact]
        public void Mapping_no_picture_fails()
        {
            Assert.False(PictureMapping.TryMap(new Transform(), ItemEffects.Identity, 0, 0, W, H, out _));
        }

        // ---------------------------------------------------------------------- glyph resolution

        [Fact]
        public void ResolveGlyph_returns_the_styles_own_artwork()
        {
            Assert.Same(CursorAssets.TryGet("ios-glyph", "arrow"),
                CursorCompose.ResolveGlyph("ios-glyph", CursorKind.Arrow));
            Assert.Same(CursorAssets.TryGet("material", "hand"),
                CursorCompose.ResolveGlyph("material", CursorKind.Hand));
            Assert.Same(CursorAssets.TryGet("fluent", "ibeam"),
                CursorCompose.ResolveGlyph("fluent", CursorKind.IBeam));
        }

        [Fact]
        public void ResolveGlyph_unsupported_kinds_fall_back_to_the_styles_arrow()
        {
            var arrow = CursorAssets.TryGet("ios-glyph", "arrow");
            Assert.Same(arrow, CursorCompose.ResolveGlyph("ios-glyph", CursorKind.Wait));
            Assert.Same(arrow, CursorCompose.ResolveGlyph("ios-glyph", CursorKind.SizeAll));
            Assert.Same(arrow, CursorCompose.ResolveGlyph("ios-glyph", CursorKind.Custom));

            // softteal has no ibeam artwork — the documented gap degrades to its arrow
            Assert.Same(CursorAssets.TryGet("softteal", "arrow"),
                CursorCompose.ResolveGlyph("softteal", CursorKind.IBeam));
        }

        [Fact]
        public void ResolveGlyph_unknown_style_falls_back_to_the_default_style()
        {
            Assert.Same(CursorAssets.TryGet(CursorAssets.DefaultStyle, "hand"),
                CursorCompose.ResolveGlyph("no-such-style", CursorKind.Hand));
            Assert.Same(CursorAssets.TryGet(CursorAssets.DefaultStyle, "arrow"),
                CursorCompose.ResolveGlyph(null, CursorKind.Arrow));
        }

        [Fact]
        public void ResolveGlyph_hidden_draws_nothing()
        {
            Assert.Null(CursorCompose.ResolveGlyph("ios-glyph", CursorKind.Hidden));
        }

        // ------------------------------------------------------------------------- header math

        [Fact]
        public void Region_and_monitor_scale_lookups()
        {
            var header = new InputCaptureHeader
            {
                Version = 1,
                RegionX = 10,
                RegionY = 20,
                RegionWidth = 100,
                RegionHeight = 50,
                Monitors = new[]
                {
                    new InputCaptureMonitor(0, 0, 60, 70, 1.0),
                    new InputCaptureMonitor(60, 0, 60, 70, 1.5),
                },
            };

            Assert.True(CursorCompose.IsInsideRegion(header, 10, 20));
            Assert.True(CursorCompose.IsInsideRegion(header, 109, 69));
            Assert.False(CursorCompose.IsInsideRegion(header, 110, 20)); // exclusive right edge
            Assert.False(CursorCompose.IsInsideRegion(header, 9, 20));
            Assert.False(CursorCompose.IsInsideRegion(null, 10, 20));
            Assert.False(CursorCompose.IsInsideRegion(new InputCaptureHeader(), 0, 0)); // no region

            Assert.Equal(1.0, CursorCompose.MonitorScaleAt(header, 30, 30));
            Assert.Equal(1.5, CursorCompose.MonitorScaleAt(header, 80, 30));
            Assert.Equal(1.0, CursorCompose.MonitorScaleAt(header, 500, 500)); // off every monitor: first
            Assert.Equal(1.0, CursorCompose.MonitorScaleAt(new InputCaptureHeader(), 0, 0));
        }

        // ---------------------------------------------------------------- default native overlay

        [Fact]
        public void Default_overlay_draws_the_box_at_the_captured_position()
        {
            var p = NewProject();
            string capture = WriteCapture(Header, Frame(0, 32, 32));
            var source = AddCaptureSource(p, capture);
            AddItem(p, AddVideoTrack(p), new MediaContent { SourceId = source.Id, StreamIndex = 0 });

            using var frames = new MultiStreamSource().Set(0, Blue, 64).Set(1, Red, 16);
            var px = Render(p, 5 * Sec, frames);

            AssertColor(Px(px, 32, 32), 0, 0, 255); // 16px red box centred on the hotspot
            AssertColor(Px(px, 8, 8), 255, 0, 0);   // screen blue elsewhere
        }

        [Fact]
        public void Default_overlay_skips_hidden_cursor_and_positions_outside_the_region()
        {
            var hiddenCapture = WriteCapture(Header, Frame(0, 32, 32, kind: "hidden"));
            var p1 = NewProject();
            var s1 = AddCaptureSource(p1, hiddenCapture);
            AddItem(p1, AddVideoTrack(p1), new MediaContent { SourceId = s1.Id, StreamIndex = 0 });
            using var frames = new MultiStreamSource().Set(0, Blue, 64).Set(1, Red, 16);
            AssertColor(Px(Render(p1, 5 * Sec, frames), 32, 32), 255, 0, 0);

            var outsideCapture = WriteCapture(Header, Frame(0, 200, 32));
            var p2 = NewProject();
            var s2 = AddCaptureSource(p2, outsideCapture);
            AddItem(p2, AddVideoTrack(p2), new MediaContent { SourceId = s2.Id, StreamIndex = 0 });
            var px = Render(p2, 5 * Sec, frames);
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                    AssertColor(Px(px, x, y), 255, 0, 0);
            }
        }

        [Fact]
        public void Default_overlay_degrades_to_nothing_on_a_missing_capture_file()
        {
            var p = NewProject();
            var source = AddCaptureSource(p,
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.jsonl"));
            AddItem(p, AddVideoTrack(p), new MediaContent { SourceId = source.Id, StreamIndex = 0 });

            using var frames = new MultiStreamSource().Set(0, Blue, 64).Set(1, Red, 16);
            var px = Render(p, 5 * Sec, frames); // must not throw
            AssertColor(Px(px, 32, 32), 255, 0, 0);
        }

        [Fact]
        public void Default_overlay_is_suppressed_while_a_cursor_item_is_active()
        {
            var p = NewProject();
            string capture = WriteCapture(Header, Frame(0, 32, 32));
            var source = AddCaptureSource(p, capture);
            var screenTrack = AddVideoTrack(p, order: 0);
            AddItem(p, screenTrack, new MediaContent { SourceId = source.Id, StreamIndex = 0 });

            // a native-style cursor item with no box stream draws nothing itself, so a blue
            // hotspot pixel proves the default overlay stood down
            var cursorTrack = AddVideoTrack(p, order: 1);
            var cursorItem = AddItem(p, cursorTrack,
                new CursorContent { SourceId = source.Id, StreamIndex = -1, Style = "native" },
                linkGroup: Guid.NewGuid());

            using var frames = new MultiStreamSource().Set(0, Blue, 64).Set(1, Red, 16);
            AssertColor(Px(Render(p, 5 * Sec, frames), 32, 32), 255, 0, 0);

            // an inactive cursor item (span elsewhere) suppresses nothing
            cursorItem.TimelineStartTicks = 8 * Sec;
            cursorItem.DurationTicks = 1 * Sec;
            AssertColor(Px(Render(p, 5 * Sec, frames), 32, 32), 0, 0, 255);
        }

        [Fact]
        public void Default_overlay_belongs_to_the_screen_stream_only()
        {
            // a webcam item (video stream 2 of the same source) must not composite the box
            var p = NewProject();
            string capture = WriteCapture(Header, Frame(0, 32, 32));
            var source = AddCaptureSource(p, capture);
            source.Streams.Add(new SourceStream { Index = 2, Kind = StreamKind.Video, Width = 64, Height = 64 });
            AddItem(p, AddVideoTrack(p), new MediaContent { SourceId = source.Id, StreamIndex = 2 });

            using var frames = new MultiStreamSource().Set(1, Red, 16).Set(2, Green, 64);
            AssertColor(Px(Render(p, 5 * Sec, frames), 32, 32), 0, 255, 0);

            Assert.True(FrameComposer.IsScreenStream(source, 0));
            Assert.False(FrameComposer.IsScreenStream(source, 1)); // the box stream itself
            Assert.False(FrameComposer.IsScreenStream(source, 2)); // the webcam
        }

        // ------------------------------------------------------------------- cursor track items

        private static (Project Project, Source Source, Item Cursor) CursorProject(
            string capture, string style)
        {
            var p = NewProject();
            var source = AddCaptureSource(p, capture);
            var group = Guid.NewGuid();
            AddItem(p, AddVideoTrack(p, order: 0),
                new MediaContent { SourceId = source.Id, StreamIndex = 0 }, linkGroup: group);
            var cursor = AddItem(p, AddVideoTrack(p, order: 1),
                new CursorContent { SourceId = source.Id, StreamIndex = 1, Style = style },
                linkGroup: group);
            return (p, source, cursor);
        }

        [Fact]
        public void Native_style_item_draws_its_own_box_stream()
        {
            string capture = WriteCapture(Header, Frame(0, 32, 32));
            var (p, _, _) = CursorProject(capture, "native");

            using var frames = new MultiStreamSource().Set(0, Blue, 64).Set(1, Red, 16);
            var px = Render(p, 5 * Sec, frames);
            AssertColor(Px(px, 32, 32), 0, 0, 255);
            AssertColor(Px(px, 8, 8), 255, 0, 0);
        }

        private static bool AnyInkNear(byte[] px, int x0, int y0, int x1, int y1)
        {
            for (int y = Math.Max(0, y0); y < Math.Min(H, y1); y++)
            {
                for (int x = Math.Max(0, x0); x < Math.Min(W, x1); x++)
                {
                    var c = Px(px, x, y);
                    if (Math.Abs(c.B - 255) > 60 || c.R > 60 || c.G > 60)
                        return true;
                }
            }
            return false;
        }

        [Fact]
        public void Themed_style_item_draws_a_glyph_at_the_captured_position()
        {
            string capture = WriteCapture(Header, Frame(0, 32, 32));
            var (p, _, _) = CursorProject(capture, "ios-glyph");

            using var frames = new MultiStreamSource().Set(0, Blue, 64).Set(1, Red, 16);
            var px = Render(p, 5 * Sec, frames);

            // black-and-white arrow ink below/right of the hotspot; none far away from it
            Assert.True(AnyInkNear(px, 32, 32, 48, 48), "no glyph ink near the hotspot");
            Assert.False(AnyInkNear(px, 0, 0, 16, 16), "ink far from the hotspot");
        }

        [Fact]
        public void Themed_style_maps_through_the_screen_items_crop()
        {
            // left half of the screen cropped away (ScaleY pinned so the mapping is 2x horizontal):
            // a cursor captured at x=48 must land at canvas x=32
            string capture = WriteCapture(Header, Frame(0, 48, 32));
            var (p, _, _) = CursorProject(capture, "ios-glyph");
            var screen = p.Items[0];
            screen.Transform.Crop = new CropRect { Left = 0.5 };
            screen.Transform.ScaleY = 1.0;

            using var frames = new MultiStreamSource().Set(0, Blue, 64).Set(1, Red, 16);
            var px = Render(p, 5 * Sec, frames);
            Assert.True(AnyInkNear(px, 32, 32, 48, 48), "no glyph ink at the crop-mapped position");
            Assert.False(AnyInkNear(px, 48, 0, 64, 16), "ink where the un-mapped position would be");
        }

        [Fact]
        public void Themed_style_hidden_cursor_draws_nothing()
        {
            string capture = WriteCapture(Header, Frame(0, 32, 32, kind: "hidden"));
            var (p, _, _) = CursorProject(capture, "ios-glyph");

            using var frames = new MultiStreamSource().Set(0, Blue, 64).Set(1, Red, 16);
            var px = Render(p, 5 * Sec, frames);
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                    AssertColor(Px(px, x, y), 255, 0, 0);
            }
        }

        [Fact]
        public void Drop_shadow_still_draws_the_glyph()
        {
            string capture = WriteCapture(Header, Frame(0, 32, 32));
            var (p, _, cursor) = CursorProject(capture, "material");
            ((CursorContent)cursor.Content).DropShadow = true;

            using var frames = new MultiStreamSource().Set(0, Blue, 64).Set(1, Red, 16);
            var px = Render(p, 5 * Sec, frames);
            Assert.True(AnyInkNear(px, 32, 32, 48, 48));
        }

        [Fact]
        public void Cursor_rides_the_screens_zoom_when_an_effect_row_sits_between()
        {
            // zoom row (order 1) between the screen row (0) and the cursor row (2): the cursor
            // borrows all of its geometry from the screen item, so it must borrow the screen
            // row's zoom matrix too — evaluated at its own order it would stay unzoomed and
            // detach from the pixels it annotates.
            string capture = WriteCapture(Header, Frame(0, 16, 16));
            var p = NewProject();
            var source = AddCaptureSource(p, capture);
            var group = Guid.NewGuid();
            AddItem(p, AddVideoTrack(p, order: 0),
                new MediaContent { SourceId = source.Id, StreamIndex = 0 }, linkGroup: group);

            var zoomTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Order = 1 };
            p.Tracks.Add(zoomTrack);
            AddItem(p, zoomTrack, new ZoomContent { Zoom = 2.0, FocusX = 0.5, FocusY = 0.5 });

            AddItem(p, AddVideoTrack(p, order: 2),
                new CursorContent { SourceId = source.Id, StreamIndex = 1, Style = "native" },
                linkGroup: group);

            using var frames = new MultiStreamSource().Set(0, Blue, 64).Set(1, Red, 16);
            var px = Render(p, 5 * Sec, frames);

            // 2x zoom about the centre maps the hotspot (16,16) → (0,0): the red box must land
            // at the canvas corner with the zoomed pixels, not stay at the unzoomed (16,16).
            AssertColor(Px(px, 2, 2), 0, 0, 255);
            AssertColor(Px(px, 24, 24), 255, 0, 0);
        }

        [Fact]
        public void Cursor_item_without_a_screen_item_draws_nothing()
        {
            string capture = WriteCapture(Header, Frame(0, 32, 32));
            var p = NewProject();
            var source = AddCaptureSource(p, capture);
            AddItem(p, AddVideoTrack(p),
                new CursorContent { SourceId = source.Id, StreamIndex = 1, Style = "ios-glyph" },
                linkGroup: Guid.NewGuid());

            using var frames = new MultiStreamSource().Set(1, Red, 16);
            var px = Render(p, 5 * Sec, frames); // must not throw; black canvas
            for (int y = 0; y < H; y += 8)
            {
                for (int x = 0; x < W; x += 8)
                    AssertColor(Px(px, x, y), 0, 0, 0);
            }
        }

        // ----------------------------------------------------------------------- click highlight

        [Fact]
        public void Ripple_draws_a_fading_circle_at_the_release_position()
        {
            // mu at t=1000ms, pos (20,20); composed at 1200ms → progress 0.5, radius 25px,
            // opacity 0.425 red over blue
            string capture = WriteCapture(Header, Frame(0, 32, 32),
                MouseEvent("mu", 1000, 20, 20));
            var (p, _, cursor) = CursorProject(capture, "ios-glyph");
            ((CursorContent)cursor.Content).ClickAnimation = "ripple";

            using var frames = new MultiStreamSource().Set(0, Blue, 64).Set(1, Red, 16);
            var px = Render(p, (long)(1.2 * Sec), frames);

            var inside = Px(px, 30, 20); // 10px from centre, well inside radius 25
            Assert.True(inside.R > 60, $"expected red tint inside the ripple, got R={inside.R}");
            AssertColor(Px(px, 56, 8), 255, 0, 0, tolerance: 3); // outside the ripple: pure blue

            // long after the click the animation is over
            var later = Render(p, (long)(2.0 * Sec), frames);
            AssertColor(Px(later, 30, 20), 255, 0, 0, tolerance: 3);
        }

        [Fact]
        public void Press_alone_draws_no_animation()
        {
            // the press only pins the held dot to the pointer (and this capture's frames report no
            // button held); the animation belongs to the release, which never comes here
            string capture = WriteCapture(Header, Frame(0, 32, 32),
                MouseEvent("md", 1000, 20, 20));
            var (p, _, cursor) = CursorProject(capture, "ios-glyph");
            ((CursorContent)cursor.Content).ClickAnimation = "ripple";

            using var frames = new MultiStreamSource().Set(0, Blue, 64).Set(1, Red, 16);
            var px = Render(p, (long)(1.2 * Sec), frames);
            AssertColor(Px(px, 30, 20), 255, 0, 0, tolerance: 3);
            AssertColor(Px(px, 20, 20), 255, 0, 0, tolerance: 3);
        }

        [Fact]
        public void Held_button_draws_a_dot_that_follows_the_cursor()
        {
            // native with no box stream draws no cursor of its own, so every pixel below is the
            // highlight's. Buttons are held from 1000ms, and the pointer drags (20,20) → (40,40).
            string capture = WriteCapture(Header,
                Frame(0, 20, 20),
                Frame(1000, 20, 20, buttons: 1),
                Frame(1200, 40, 40, buttons: 1),
                MouseEvent("md", 1000, 20, 20));
            var (p, _, cursor) = CursorProject(capture, "native");
            var content = (CursorContent)cursor.Content;
            content.StreamIndex = -1;
            content.ClickAnimation = "ripple";

            using var frames = new MultiStreamSource().Set(0, Blue, 64).Set(1, Red, 16);

            var atPress = Render(p, (long)(1.05 * Sec), frames);
            Assert.True(Px(atPress, 20, 20).R > 60, "no held dot at the press position");
            AssertColor(Px(atPress, 40, 40), 255, 0, 0, tolerance: 3);

            // mid-drag the dot has moved with the pointer and left the press position bare —
            // it is not an animation anchored where the press landed
            var midDrag = Render(p, (long)(1.25 * Sec), frames);
            Assert.True(Px(midDrag, 40, 40).R > 60, "the held dot did not follow the cursor");
            AssertColor(Px(midDrag, 20, 20), 255, 0, 0, tolerance: 3);

            // radius 10 DIP: 12px out is already outside it
            AssertColor(Px(midDrag, 52, 40), 255, 0, 0, tolerance: 3);

            // …and none of it draws without a highlight to draw
            content.ClickAnimation = "none";
            AssertColor(Px(Render(p, (long)(1.25 * Sec), frames), 40, 40), 255, 0, 0, tolerance: 3);
        }

        [Fact]
        public void Release_explodes_the_highlight_where_the_button_came_up()
        {
            string capture = WriteCapture(Header,
                Frame(0, 20, 20),
                Frame(1000, 40, 40, buttons: 1),
                Frame(1500, 40, 40),
                MouseEvent("md", 900, 20, 20),
                MouseEvent("mu", 1400, 40, 40));
            var (p, _, cursor) = CursorProject(capture, "native");
            var content = (CursorContent)cursor.Content;
            content.StreamIndex = -1;
            content.ClickAnimation = "ripple";

            using var frames = new MultiStreamSource().Set(0, Blue, 64);

            // 200ms after the release → progress 0.5, radius 25px about (40,40)
            var px = Render(p, (long)(1.6 * Sec), frames);
            Assert.True(Px(px, 60, 40).R > 60, "the release did not fire the animation");
            AssertColor(Px(px, 8, 40), 255, 0, 0, tolerance: 3); // 32px out: past the ripple
        }

        [Fact]
        public void Hold_size_scales_the_held_dot_only()
        {
            string capture = WriteCapture(Header,
                Frame(0, 32, 32, buttons: 1),
                MouseEvent("md", 0, 32, 32));
            var (p, _, cursor) = CursorProject(capture, "native");
            var content = (CursorContent)cursor.Content;
            content.StreamIndex = -1;
            content.ClickAnimation = "ripple";

            using var frames = new MultiStreamSource().Set(0, Blue, 64);

            // default: radius 10, so 16px out is bare
            AssertColor(Px(Render(p, 1 * Sec, frames), 48, 32), 255, 0, 0, tolerance: 3);

            // doubled: radius 20 reaches it
            content.HoldSize = 2.0;
            Assert.True(Px(Render(p, 1 * Sec, frames), 48, 32).R > 60,
                "hold size did not widen the held dot");

            // and shrinking it pulls the dot back inside 6px
            content.HoldSize = 0.5;
            AssertColor(Px(Render(p, 1 * Sec, frames), 40, 32), 255, 0, 0, tolerance: 3);
        }

        [Fact]
        public void Click_size_scales_the_release_animation()
        {
            // mu at 1000ms; composed at 1200ms → progress 0.5, radius 25 by default
            string capture = WriteCapture(Header, Frame(0, 32, 32),
                MouseEvent("mu", 1000, 32, 32));
            var (p, _, cursor) = CursorProject(capture, "native");
            var content = (CursorContent)cursor.Content;
            content.StreamIndex = -1;
            content.ClickAnimation = "ripple";

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            long at = (long)(1.2 * Sec);

            // half size: radius 12.5, so a point 20px out is outside it…
            content.ClickSize = 0.5;
            AssertColor(Px(Render(p, at, frames), 52, 32), 255, 0, 0, tolerance: 3);

            // …and at full size it is inside
            content.ClickSize = 1.0;
            Assert.True(Px(Render(p, at, frames), 52, 32).R > 60,
                "click size did not scale the release animation");
        }

        [Fact]
        public void Animation_speed_shortens_the_release_animation()
        {
            string capture = WriteCapture(Header, Frame(0, 32, 32),
                MouseEvent("mu", 1000, 32, 32));
            var (p, _, cursor) = CursorProject(capture, "native");
            var content = (CursorContent)cursor.Content;
            content.StreamIndex = -1;
            content.ClickAnimation = "ripple";

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            long early = (long)(1.2 * Sec); // 200ms after the release
            long late = (long)(1.5 * Sec);  // 500ms after it, past the stock 400ms

            // 1x: still running at 200ms, over by 500ms
            Assert.True(Px(Render(p, early, frames), 32, 32).R > 60, "the animation should still be running");
            AssertColor(Px(Render(p, late, frames), 32, 32), 255, 0, 0, tolerance: 3);

            // 2x: 200ms long, so it is already over at 200ms
            content.AnimationSpeed = 2.0;
            AssertColor(Px(Render(p, early, frames), 32, 32), 255, 0, 0, tolerance: 3);

            // half speed: 800ms long, so it is still going where 1x had finished
            content.AnimationSpeed = 0.5;
            Assert.True(Px(Render(p, late, frames), 32, 32).R > 60,
                "half speed should still be animating after the stock duration");
        }

        [Fact]
        public void Highlight_factors_survive_a_hand_edited_project()
        {
            // the model rejects these, but a file carrying them must still draw something sane
            // rather than nothing at all (a zero speed would divide the clock away)
            string capture = WriteCapture(Header,
                Frame(0, 32, 32, buttons: 1),
                Frame(1100, 32, 32),
                MouseEvent("mu", 1000, 32, 32));
            var (p, _, cursor) = CursorProject(capture, "native");
            var content = (CursorContent)cursor.Content;
            content.StreamIndex = -1;
            content.ClickAnimation = "ripple";
            content.HoldSize = 0;
            content.ClickSize = Double.NaN;
            content.AnimationSpeed = 0;

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            Assert.True(Px(Render(p, (long)(0.5 * Sec), frames), 32, 32).R > 60,
                "a clamped hold size should still draw the held dot");
            Assert.True(Px(Render(p, (long)(1.2 * Sec), frames), 32, 32).R > 60,
                "a clamped speed should still run the animation");
        }

        [Fact]
        public void Pulse_draws_a_shrinking_dot()
        {
            string capture = WriteCapture(Header, Frame(0, 32, 32),
                MouseEvent("mu", 1000, 20, 20));
            var (p, _, cursor) = CursorProject(capture, "ios-glyph");
            ((CursorContent)cursor.Content).ClickAnimation = "pulse";

            using var frames = new MultiStreamSource().Set(0, Blue, 64).Set(1, Red, 16);

            // early (progress 0.125): radius ≈ 36 — the point 30px out is covered
            var early = Render(p, (long)(1.05 * Sec), frames);
            Assert.True(Px(early, 20, 50).R > 60, "early pulse should cover a wide dot");

            // late (progress 0.875): radius ≈ 14 — the same point is bare again
            var late = Render(p, (long)(1.35 * Sec), frames);
            AssertColor(Px(late, 20, 50), 255, 0, 0, tolerance: 3);
        }
    }
}
