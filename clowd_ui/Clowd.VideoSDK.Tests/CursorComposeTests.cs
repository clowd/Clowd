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
    // resolution fallbacks, the default native-sprite overlay and its suppression by a cursor
    // track, sprite placement/mask exactness, themed glyph/click-animation pixels. Mirrors
    // ComposeTests' CPU-factory pattern.
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

        /// <summary>A recording source: screen stream 0 (64x64) with an input-capture sidecar.</summary>
        private static Source AddCaptureSource(Project p, string capturePath)
        {
            var source = new Source
            {
                Id = Guid.NewGuid(),
                Path = "recording.mp4",
                InputCapturePath = capturePath,
            };
            source.Streams.Add(new SourceStream { Index = 0, Kind = StreamKind.Video, Width = W, Height = H });
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
            "{\"type\":\"header\",\"version\":2,\"region\":[0,0,64,64],\"fps_num\":30,\"fps_den\":1," +
            "\"platform\":\"windows\",\"monitors\":[{\"x\":0,\"y\":0,\"w\":64,\"h\":64,\"scale\":1.0}]}";

        private static string Frame(double t, int x, int y, string kind = "arrow", int buttons = 0, int ci = -1) =>
            $"{{\"type\":\"frame\",\"t\":{t},\"x\":{x},\"y\":{y},\"b\":{buttons},\"c\":\"{kind}\"" +
            (ci >= 0 ? $",\"ci\":{ci}" : "") + "}";

        private static string MouseEvent(string kind, double t, int x, int y) =>
            $"{{\"type\":\"event\",\"t\":{t},\"kind\":\"{kind}\",\"btn\":1,\"x\":{x},\"y\":{y}}}";

        /// <summary>PNG-encodes a solid <paramref name="color"/> square — a sprite fixture's bmp or
        /// mask plane (Transparent = a plane with no ink), byte-exact the way the recorder writes
        /// them.</summary>
        private static byte[] SpritePng(int size, SKColor color)
        {
            using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(color);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        /// <summary>A <c>cursor_image</c> row exactly as the recorder emits it — base64 PNG planes,
        /// <c>mask</c> omitted for a plain alpha cursor.</summary>
        private static string CursorImage(int id, int size, byte[] bmp, byte[] mask = null,
            string kind = "arrow", int hotx = 0, int hoty = 0) =>
            $"{{\"type\":\"cursor_image\",\"id\":{id},\"kind\":\"{kind}\",\"w\":{size},\"h\":{size}," +
            $"\"hotx\":{hotx},\"hoty\":{hoty},\"bmp\":\"{Convert.ToBase64String(bmp)}\"" +
            (mask == null ? "" : $",\"mask\":\"{Convert.ToBase64String(mask)}\"") + "}";

        /// <summary>Per-stream still frames: stream 0 = the screen, stream 1 = the webcam.</summary>
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

            /// <summary>A screen frame with structure in it — what the press warp needs to have
            /// any visible effect. Left of <paramref name="splitX"/> is blue, the rest red.</summary>
            public MultiStreamSource SetSplit(int streamIndex, int size, int splitX)
            {
                using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
                surface.Canvas.Clear(Blue);
                using var paint = new SKPaint { Color = Red };
                surface.Canvas.DrawRect(new SKRect(splitX, 0, size, size), paint);
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
        public void ResolveGlyph_returns_the_styles_own_artwork_for_every_kind()
        {
            foreach (var kind in Enum.GetValues<CursorKind>())
            {
                if (kind is CursorKind.Custom or CursorKind.Hidden)
                    continue;
                Assert.Same(CursorAssets.TryGet("vision", "dark", CursorCompose.KindKey(kind)),
                    CursorCompose.ResolveGlyph("vision", kind));
            }
        }

        [Fact]
        public void ResolveGlyph_draws_the_asked_for_colourway()
        {
            Assert.Same(CursorAssets.TryGet("vision", "light", "hand"),
                CursorCompose.ResolveGlyph("vision", "light", CursorKind.Hand));

            // an unrecognised colourway is the style's default, not a miss
            Assert.Same(CursorAssets.TryGet("vision", "dark", "hand"),
                CursorCompose.ResolveGlyph("vision", "sepia", CursorKind.Hand));
            Assert.Same(CursorAssets.TryGet("vision", "dark", "hand"),
                CursorCompose.ResolveGlyph("vision", null, CursorKind.Hand));
        }

        [Fact]
        public void ResolveGlyph_unsupported_kinds_fall_back_to_the_styles_arrow()
        {
            // Custom is an application's own cursor: no pack can have artwork for it
            Assert.Same(CursorAssets.TryGet("vision", "arrow"),
                CursorCompose.ResolveGlyph("vision", CursorKind.Custom));
            Assert.Same(CursorAssets.TryGet("vision", "light", "arrow"),
                CursorCompose.ResolveGlyph("vision", "light", CursorKind.Custom));
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
            Assert.Null(CursorCompose.ResolveGlyph("vision", CursorKind.Hidden));
            Assert.Null(CursorCompose.ResolveGlyph("vision", "light", CursorKind.Hidden));
        }

        // ------------------------------------------------------------------------- header math

        [Fact]
        public void Region_and_monitor_scale_lookups()
        {
            var header = new InputCaptureHeader
            {
                Version = 2,
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
        public void Default_overlay_draws_the_sprite_at_the_captured_position()
        {
            var p = NewProject();
            string capture = WriteCapture(Header,
                CursorImage(1, 8, SpritePng(8, Red)),
                Frame(0, 32, 32, ci: 1));
            var source = AddCaptureSource(p, capture);
            AddItem(p, AddVideoTrack(p), new MediaContent { SourceId = source.Id, StreamIndex = 0 });

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            var px = Render(p, 5 * Sec, frames);

            AssertColor(Px(px, 34, 34), 0, 0, 255); // sprite ink inside [32,40)
            AssertColor(Px(px, 8, 8), 255, 0, 0);   // screen blue elsewhere
        }

        [Fact]
        public void The_hotspot_pins_the_sprite_to_the_captured_position()
        {
            // hotspot (4,4) on an 8px sprite: the frame position (32,32) is the sprite's centre,
            // so its ink covers [28,36) rather than [32,40)
            var p = NewProject();
            string capture = WriteCapture(Header,
                CursorImage(1, 8, SpritePng(8, Red), hotx: 4, hoty: 4),
                Frame(0, 32, 32, ci: 1));
            var source = AddCaptureSource(p, capture);
            AddItem(p, AddVideoTrack(p), new MediaContent { SourceId = source.Id, StreamIndex = 0 });

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            var px = Render(p, 5 * Sec, frames);

            AssertColor(Px(px, 30, 30), 0, 0, 255); // inside the offset rect
            AssertColor(Px(px, 26, 26), 255, 0, 0); // before its left/top edge
            AssertColor(Px(px, 38, 38), 255, 0, 0); // past where the unoffset rect would reach
        }

        [Fact]
        public void A_white_mask_inverts_the_pixels_beneath_exactly()
        {
            // an inverting cursor: empty bmp, all-white XOR plane — Difference against white is
            // 1 − d per channel, exactly
            var p = NewProject();
            string capture = WriteCapture(Header,
                CursorImage(1, 8, SpritePng(8, SKColors.Transparent), mask: SpritePng(8, SKColors.White)),
                Frame(0, 32, 32, ci: 1));
            var source = AddCaptureSource(p, capture);
            AddItem(p, AddVideoTrack(p), new MediaContent { SourceId = source.Id, StreamIndex = 0 });

            using var blue = new MultiStreamSource().Set(0, Blue, 64);
            var px = Render(p, 5 * Sec, blue);
            AssertColor(Px(px, 34, 34), 0, 255, 255); // |blue − white| = yellow
            AssertColor(Px(px, 8, 8), 255, 0, 0);     // outside the sprite: untouched

            // mid-gray 128 inverts to 127 — the off-by-one is the arithmetic's own (255 − 128)
            using var gray = new MultiStreamSource().Set(0, new SKColor(128, 128, 128), 64);
            AssertColor(Px(Render(p, 5 * Sec, gray), 34, 34), 127, 127, 127, tolerance: 2);
        }

        [Fact]
        public void A_black_mask_pixel_leaves_the_pixels_beneath_unchanged()
        {
            // the preserved no-op cell of the XOR plane: Difference against black is d itself
            var p = NewProject();
            string capture = WriteCapture(Header,
                CursorImage(1, 8, SpritePng(8, SKColors.Transparent), mask: SpritePng(8, SKColors.Black)),
                Frame(0, 32, 32, ci: 1));
            var source = AddCaptureSource(p, capture);
            AddItem(p, AddVideoTrack(p), new MediaContent { SourceId = source.Id, StreamIndex = 0 });

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            AssertColor(Px(Render(p, 5 * Sec, frames), 34, 34), 255, 0, 0);
        }

        [Fact]
        public void Default_overlay_skips_hidden_cursor_and_positions_outside_the_region()
        {
            byte[] bmp = SpritePng(8, Red);
            var hiddenCapture = WriteCapture(Header,
                CursorImage(1, 8, bmp),
                Frame(0, 32, 32, kind: "hidden"));
            var p1 = NewProject();
            var s1 = AddCaptureSource(p1, hiddenCapture);
            AddItem(p1, AddVideoTrack(p1), new MediaContent { SourceId = s1.Id, StreamIndex = 0 });
            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            AssertColor(Px(Render(p1, 5 * Sec, frames), 32, 32), 255, 0, 0);

            var outsideCapture = WriteCapture(Header,
                CursorImage(1, 8, bmp),
                Frame(0, 200, 32, ci: 1));
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

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            var px = Render(p, 5 * Sec, frames); // must not throw
            AssertColor(Px(px, 32, 32), 255, 0, 0);
        }

        [Fact]
        public void Default_overlay_draws_nothing_when_the_frame_carries_no_sprite()
        {
            // a v1 file or a degraded capture: frames without ci reference nothing to draw
            var p = NewProject();
            string capture = WriteCapture(Header, Frame(0, 32, 32));
            var source = AddCaptureSource(p, capture);
            AddItem(p, AddVideoTrack(p), new MediaContent { SourceId = source.Id, StreamIndex = 0 });

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            AssertColor(Px(Render(p, 5 * Sec, frames), 32, 32), 255, 0, 0);
        }

        [Fact]
        public void Default_overlay_is_suppressed_while_a_cursor_item_is_active()
        {
            var p = NewProject();
            string capture = WriteCapture(Header,
                CursorImage(1, 8, SpritePng(8, Red)),
                Frame(0, 32, 32, ci: 1));
            var source = AddCaptureSource(p, capture);
            var screenTrack = AddVideoTrack(p, order: 0);
            AddItem(p, screenTrack, new MediaContent { SourceId = source.Id, StreamIndex = 0 });

            // a native cursor item on a hidden row composes nothing itself but still owns the
            // cursor, so a bare screen pixel at the hotspot proves the default overlay stood
            // down — this is exactly how the row's eye toggle hides the cursor
            var cursorTrack = AddVideoTrack(p, order: 1);
            cursorTrack.Hidden = true;
            var cursorItem = AddItem(p, cursorTrack,
                new CursorContent { SourceId = source.Id, Style = "native" },
                linkGroup: Guid.NewGuid());

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            AssertColor(Px(Render(p, 5 * Sec, frames), 34, 34), 255, 0, 0);

            // an inactive cursor item (span elsewhere) suppresses nothing
            cursorItem.TimelineStartTicks = 8 * Sec;
            cursorItem.DurationTicks = 1 * Sec;
            AssertColor(Px(Render(p, 5 * Sec, frames), 34, 34), 0, 0, 255);
        }

        [Fact]
        public void A_native_item_without_sprites_still_suppresses_the_overlay()
        {
            // frames with ci absent: the native-style item draws nothing of its own, and the
            // default overlay must stand down all the same — the cursor track owns the cursor
            var p = NewProject();
            string capture = WriteCapture(Header, Frame(0, 32, 32));
            var source = AddCaptureSource(p, capture);
            var group = Guid.NewGuid();
            AddItem(p, AddVideoTrack(p, order: 0),
                new MediaContent { SourceId = source.Id, StreamIndex = 0 }, linkGroup: group);
            AddItem(p, AddVideoTrack(p, order: 1),
                new CursorContent { SourceId = source.Id, Style = "native" }, linkGroup: group);

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            var px = Render(p, 5 * Sec, frames);
            for (int y = 0; y < H; y += 8)
            {
                for (int x = 0; x < W; x += 8)
                    AssertColor(Px(px, x, y), 255, 0, 0);
            }
        }

        [Fact]
        public void Default_overlay_belongs_to_the_screen_stream_only()
        {
            // a webcam item (video stream 1 of the same source) must not composite the sprite
            var p = NewProject();
            string capture = WriteCapture(Header,
                CursorImage(1, 8, SpritePng(8, Red)),
                Frame(0, 32, 32, ci: 1));
            var source = AddCaptureSource(p, capture);
            source.Streams.Add(new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 64, Height = 64 });
            AddItem(p, AddVideoTrack(p), new MediaContent { SourceId = source.Id, StreamIndex = 1 });

            using var frames = new MultiStreamSource().Set(1, Green, 64);
            AssertColor(Px(Render(p, 5 * Sec, frames), 34, 34), 0, 255, 0);

            Assert.True(FrameComposer.IsScreenStream(source, 0));
            Assert.False(FrameComposer.IsScreenStream(source, 1)); // the webcam
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
                new CursorContent { SourceId = source.Id, Style = style },
                linkGroup: group);
            return (p, source, cursor);
        }

        [Fact]
        public void Native_style_item_draws_the_recorded_sprite()
        {
            string capture = WriteCapture(Header,
                CursorImage(1, 8, SpritePng(8, Red)),
                Frame(0, 32, 32, ci: 1));
            var (p, _, _) = CursorProject(capture, "native");

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            var px = Render(p, 5 * Sec, frames);
            AssertColor(Px(px, 34, 34), 0, 0, 255);
            AssertColor(Px(px, 8, 8), 255, 0, 0);
        }

        [Fact]
        public void None_style_item_draws_no_cursor_at_all()
        {
            // a recorded sprite is on the frame, but "none" hides the cursor outright
            string capture = WriteCapture(Header,
                CursorImage(1, 8, SpritePng(8, Red)),
                Frame(0, 32, 32, ci: 1));
            var (p, _, _) = CursorProject(capture, "none");

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            var px = Render(p, 5 * Sec, frames);
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                    AssertColor(Px(px, x, y), 255, 0, 0);
            }
        }

        [Fact]
        public void Size_scales_the_native_sprite()
        {
            string capture = WriteCapture(Header,
                CursorImage(1, 8, SpritePng(8, Red)),
                Frame(0, 32, 32, ci: 1));
            var (p, _, cursor) = CursorProject(capture, "native");

            using var frames = new MultiStreamSource().Set(0, Blue, 64);

            // at 1x the 8px sprite covers [32,40): a point 12px out is bare screen
            AssertColor(Px(Render(p, 5 * Sec, frames), 44, 44), 255, 0, 0);

            // at 2x it covers [32,48) and reaches the same point
            ((CursorContent)cursor.Content).Size = 2.0;
            AssertColor(Px(Render(p, 5 * Sec, frames), 44, 44), 0, 0, 255);
        }

        [Fact]
        public void Native_maps_through_the_screen_items_crop()
        {
            // left half of the screen cropped away (ScaleY pinned so the mapping is 2x horizontal):
            // a cursor captured at x=48 must land at canvas x=32, its sprite stretched to 16px wide
            string capture = WriteCapture(Header,
                CursorImage(1, 8, SpritePng(8, Red)),
                Frame(0, 48, 32, ci: 1));
            var (p, _, _) = CursorProject(capture, "native");
            var screen = p.Items[0];
            screen.Transform.Crop = new CropRect { Left = 0.5 };
            screen.Transform.ScaleY = 1.0;

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            var px = Render(p, 5 * Sec, frames);
            AssertColor(Px(px, 40, 34), 0, 0, 255); // inside the crop-mapped [32,48) rect
            AssertColor(Px(px, 52, 34), 255, 0, 0); // where the un-mapped position would be
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
            var (p, _, _) = CursorProject(capture, "vision");

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
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
            var (p, _, _) = CursorProject(capture, "vision");
            var screen = p.Items[0];
            screen.Transform.Crop = new CropRect { Left = 0.5 };
            screen.Transform.ScaleY = 1.0;

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            var px = Render(p, 5 * Sec, frames);
            Assert.True(AnyInkNear(px, 32, 32, 48, 48), "no glyph ink at the crop-mapped position");
            Assert.False(AnyInkNear(px, 48, 0, 64, 16), "ink where the un-mapped position would be");
        }

        [Fact]
        public void Themed_style_hidden_cursor_draws_nothing()
        {
            string capture = WriteCapture(Header, Frame(0, 32, 32, kind: "hidden"));
            var (p, _, _) = CursorProject(capture, "vision");

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            var px = Render(p, 5 * Sec, frames);
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                    AssertColor(Px(px, x, y), 255, 0, 0);
            }
        }

        /// <summary>Every effect draws the glyph twice — once through a decoration-only filter, once
        /// plainly on top (see <c>SurroundMath</c>) — so the one thing every kind must still do
        /// is leave the glyph itself visible.</summary>
        [Theory]
        [InlineData(SurroundKind.Shadow)]
        [InlineData(SurroundKind.Glow)]
        [InlineData(SurroundKind.Outline)]
        public void An_effect_still_draws_the_glyph(SurroundKind kind)
        {
            string capture = WriteCapture(Header, Frame(0, 32, 32));
            var (p, _, cursor) = CursorProject(capture, "vision");
            cursor.Surround = Surround.Create(kind, cursor: true);

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
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
            string capture = WriteCapture(Header,
                CursorImage(1, 8, SpritePng(8, Red)),
                Frame(0, 16, 16, ci: 1));
            var p = NewProject();
            var source = AddCaptureSource(p, capture);
            var group = Guid.NewGuid();
            AddItem(p, AddVideoTrack(p, order: 0),
                new MediaContent { SourceId = source.Id, StreamIndex = 0 }, linkGroup: group);

            var zoomTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Order = 1 };
            p.Tracks.Add(zoomTrack);
            AddItem(p, zoomTrack, new ZoomContent { Zoom = 2.0, FocusX = 0.5, FocusY = 0.5 });

            AddItem(p, AddVideoTrack(p, order: 2),
                new CursorContent { SourceId = source.Id, Style = "native" },
                linkGroup: group);

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            var px = Render(p, 5 * Sec, frames);

            // 2x zoom about the centre maps the hotspot (16,16) → (0,0): the sprite must land
            // at the canvas corner with the zoomed pixels, not stay at the unzoomed (16,16).
            AssertColor(Px(px, 2, 2), 0, 0, 255);
            AssertColor(Px(px, 24, 24), 255, 0, 0);
        }

        [Fact]
        public void Cursor_item_without_a_screen_item_draws_nothing()
        {
            string capture = WriteCapture(Header,
                CursorImage(1, 8, SpritePng(8, Red)),
                Frame(0, 32, 32, ci: 1));
            var p = NewProject();
            var source = AddCaptureSource(p, capture);
            AddItem(p, AddVideoTrack(p),
                new CursorContent { SourceId = source.Id, Style = "vision" },
                linkGroup: Guid.NewGuid());

            using var frames = new MultiStreamSource();
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
            var (p, _, cursor) = CursorProject(capture, "vision");
            ((CursorContent)cursor.Content).ClickAnimation = "ripple";

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
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
            var (p, _, cursor) = CursorProject(capture, "vision");
            ((CursorContent)cursor.Content).ClickAnimation = "ripple";

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            var px = Render(p, (long)(1.2 * Sec), frames);
            AssertColor(Px(px, 30, 20), 255, 0, 0, tolerance: 3);
            AssertColor(Px(px, 20, 20), 255, 0, 0, tolerance: 3);
        }

        [Fact]
        public void Held_button_draws_a_dot_that_follows_the_cursor()
        {
            // native with no sprites in the capture draws no cursor of its own, so every pixel
            // below is the highlight's. Buttons are held from 1000ms, and the pointer drags
            // (20,20) → (40,40).
            string capture = WriteCapture(Header,
                Frame(0, 20, 20),
                Frame(1000, 20, 20, buttons: 1),
                Frame(1200, 40, 40, buttons: 1),
                MouseEvent("md", 1000, 20, 20));
            var (p, _, cursor) = CursorProject(capture, "native");
            var content = (CursorContent)cursor.Content;
            content.ClickAnimation = "ripple";

            using var frames = new MultiStreamSource().Set(0, Blue, 64);

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
            var (p, _, cursor) = CursorProject(capture, "vision");
            ((CursorContent)cursor.Content).ClickAnimation = "pulse";

            using var frames = new MultiStreamSource().Set(0, Blue, 64);

            // early (progress 0.125): radius ≈ 36 — the point 30px out is covered
            var early = Render(p, (long)(1.05 * Sec), frames);
            Assert.True(Px(early, 20, 50).R > 60, "early pulse should cover a wide dot");

            // late (progress 0.875): radius ≈ 14 — the same point is bare again
            var late = Render(p, (long)(1.35 * Sec), frames);
            AssertColor(Px(late, 20, 50), 255, 0, 0, tolerance: 3);
        }

        // -------------------------------------------------------------------------------- ring

        [Fact]
        public void Ring_rests_on_the_pointer_with_a_translucent_fill()
        {
            // no clicks anywhere: unlike the burst animations the ring is always on screen,
            // resting at 18 DIP around the pointer
            string capture = WriteCapture(Header, Frame(0, 32, 32));
            var (p, _, cursor) = CursorProject(capture, "none");
            ((CursorContent)cursor.Content).ClickAnimation = "ring";

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            var px = Render(p, 5 * Sec, frames);

            // the outline (stroke centre at radius 18) is the colour, near enough opaque
            Assert.True(Px(px, 50, 32).R > 150, "no ring outline at the resting radius");

            // inside it the default 15% fill barely tints the screen
            var centre = Px(px, 32, 32);
            Assert.InRange(centre.R, 20, 80);
            Assert.True(centre.B > 150, "the fill should stay translucent");

            // and past the ring the screen is untouched
            AssertColor(Px(px, 56, 32), 255, 0, 0, tolerance: 3);
        }

        [Fact]
        public void Ring_closes_while_the_button_is_held()
        {
            // held since long before the composed instant: fully engaged at 0.65x → radius ~11.7
            string capture = WriteCapture(Header,
                Frame(0, 32, 32, buttons: 1),
                MouseEvent("md", 0, 32, 32));
            var (p, _, cursor) = CursorProject(capture, "none");
            ((CursorContent)cursor.Content).ClickAnimation = "ring";

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            var px = Render(p, 5 * Sec, frames);

            Assert.True(Px(px, 44, 32).R > 150, "no ring outline at the held radius");
            AssertColor(Px(px, 50, 32), 255, 0, 0, tolerance: 3); // the resting radius is bare
        }

        [Fact]
        public void Ring_fill_opacity_dials_the_inner_disc_only()
        {
            string capture = WriteCapture(Header, Frame(0, 32, 32));
            var (p, _, cursor) = CursorProject(capture, "none");
            var content = (CursorContent)cursor.Content;
            content.ClickAnimation = "ring";

            using var frames = new MultiStreamSource().Set(0, Blue, 64);

            content.FillOpacity = 0;
            AssertColor(Px(Render(p, 5 * Sec, frames), 32, 32), 255, 0, 0, tolerance: 3);

            content.FillOpacity = 0.8;
            Assert.True(Px(Render(p, 5 * Sec, frames), 32, 32).R > 140,
                "a strong fill should tint the disc strongly");

            // a hand-edited NaN draws no fill rather than poisoning the alpha
            content.FillOpacity = Double.NaN;
            AssertColor(Px(Render(p, 5 * Sec, frames), 32, 32), 255, 0, 0, tolerance: 3);
        }

        [Fact]
        public void Ring_radius_scales_with_click_size()
        {
            string capture = WriteCapture(Header, Frame(0, 32, 32));
            var (p, _, cursor) = CursorProject(capture, "none");
            var content = (CursorContent)cursor.Content;
            content.ClickAnimation = "ring";
            content.ClickSize = 0.5; // resting radius 9

            using var frames = new MultiStreamSource().Set(0, Blue, 64);
            var px = Render(p, 5 * Sec, frames);
            Assert.True(Px(px, 41, 32).R > 150, "no ring outline at the halved radius");
            AssertColor(Px(px, 50, 32), 255, 0, 0, tolerance: 3);
        }

        // ------------------------------------------------------------------------------- press

        [Fact]
        public void Press_stretches_the_screen_pixels_toward_the_held_pointer()
        {
            // the screen splits blue|red at x=40 and the pointer holds at (24,32). Fully engaged,
            // the pixel at (39,32) — 15px out — samples ~1.22x further along its ray (x≈42.3),
            // across the split: blue turns red exactly where the paper is being dragged inward.
            string capture = WriteCapture(Header,
                Frame(0, 24, 32, buttons: 1),
                MouseEvent("md", 0, 24, 32));
            var (p, _, cursor) = CursorProject(capture, "none");
            ((CursorContent)cursor.Content).ClickAnimation = "pressure";

            using var frames = new MultiStreamSource().SetSplit(0, 64, splitX: 40);
            var held = Render(p, 5 * Sec, frames);
            Assert.True(Px(held, 39, 32).R > 100, "the held press did not pull the split inward");

            // the warp draws nothing of its own: a pixel whose stretched sample is still blue
            // stays blue, and the pointer itself is not painted over
            Assert.True(Px(held, 10, 32).B > 150, "the warp should only move pixels, not tint them");

            // no buttons, no events: the same instant composes the plain split
            string idleCapture = WriteCapture(Header, Frame(0, 24, 32));
            var (idleProject, _, idleCursor) = CursorProject(idleCapture, "none");
            ((CursorContent)idleCursor.Content).ClickAnimation = "pressure";
            AssertColor(Px(Render(idleProject, 5 * Sec, frames), 39, 32), 255, 0, 0, tolerance: 3);
        }

        [Fact]
        public void Press_relaxes_back_out_after_the_release()
        {
            string capture = WriteCapture(Header,
                Frame(0, 24, 32, buttons: 1),
                Frame(1000, 24, 32),
                MouseEvent("md", 0, 24, 32),
                MouseEvent("mu", 1000, 24, 32));
            var (p, _, cursor) = CursorProject(capture, "none");
            ((CursorContent)cursor.Content).ClickAnimation = "pressure";

            using var frames = new MultiStreamSource().SetSplit(0, 64, splitX: 40);

            // 50ms after the release the warp is still relaxing — the split is still pulled in
            Assert.True(Px(Render(p, (long)(1.05 * Sec), frames), 39, 32).R > 100,
                "the release should ease out, not snap");

            // 400ms after it (past the 260ms release) the screen is exactly itself again
            AssertColor(Px(Render(p, (long)(1.4 * Sec), frames), 39, 32), 255, 0, 0, tolerance: 3);
        }
    }
}
