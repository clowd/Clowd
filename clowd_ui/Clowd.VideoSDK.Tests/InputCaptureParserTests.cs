using System;
using System.IO;
using System.Linq;
using System.Text;
using Clowd.VideoSDK.Composition;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The input-capture JSONL parser: header/frame/event/cursor_image rows into sorted arrays
    /// and the sprite table, the binary-search lookups, forward tolerance (unknown rows/fields/
    /// value shapes skipped, torn lines dropped) and the missing/corrupt degrade to
    /// <see cref="InputCapture.Empty"/>.
    /// </summary>
    public class InputCaptureParserTests
    {
        private const string Header =
            """{"type":"header","version":2,"region":[100,200,1920,1080],"fps_num":30,"fps_den":1,"platform":"windows","monitors":[{"x":0,"y":0,"w":2560,"h":1440,"scale":1.5},{"x":2560,"y":0,"w":1920,"h":1080,"scale":1.0}]}""";

        private static InputCapture Parse(params string[] lines) =>
            InputCapture.Parse(Encoding.UTF8.GetBytes(String.Join("\n", lines)));

        // -------------------------------------------------------------------------------- header

        [Fact]
        public void Header_row_parses_completely()
        {
            var capture = Parse(Header);

            var h = capture.Header;
            Assert.Equal(2, h.Version);
            Assert.Equal(100, h.RegionX);
            Assert.Equal(200, h.RegionY);
            Assert.Equal(1920, h.RegionWidth);
            Assert.Equal(1080, h.RegionHeight);
            Assert.Equal(30, h.FpsNum);
            Assert.Equal(1, h.FpsDen);
            Assert.Equal("windows", h.Platform);
            Assert.Equal(2, h.Monitors.Count);
            Assert.Equal(2560, h.Monitors[0].Width);
            Assert.Equal(1.5, h.Monitors[0].Scale);
            Assert.Equal(2560, h.Monitors[1].X);
            Assert.Equal(1.0, h.Monitors[1].Scale);
        }

        // -------------------------------------------------------------------------------- frames

        [Fact]
        public void Frame_rows_parse_and_sort()
        {
            var capture = Parse(
                Header,
                """{"type":"frame","t":33.3,"x":110,"y":210,"b":0,"c":"arrow"}""",
                """{"type":"frame","t":0.0,"x":100,"y":200,"b":1,"k":[17,75],"c":"hand"}""");

            Assert.Equal(2, capture.Frames.Count);
            Assert.False(capture.IsEmpty);

            var first = capture.Frames[0];
            Assert.Equal(0.0, first.TimeMs);
            Assert.Equal(100, first.X);
            Assert.Equal(200, first.Y);
            Assert.Equal(1, first.Buttons);
            Assert.Equal(new[] { 17, 75 }, first.Keys);
            Assert.Equal(CursorKind.Hand, first.Cursor);

            var second = capture.Frames[1];
            Assert.Equal(33.3, second.TimeMs);
            Assert.Empty(second.Keys); // omitted k = no keys down
            Assert.Equal(CursorKind.Arrow, second.Cursor);
        }

        [Theory]
        [InlineData("arrow", CursorKind.Arrow)]
        [InlineData("ibeam", CursorKind.IBeam)]
        [InlineData("wait", CursorKind.Wait)]
        [InlineData("cross", CursorKind.Cross)]
        [InlineData("uparrow", CursorKind.UpArrow)]
        [InlineData("sizenwse", CursorKind.SizeNWSE)]
        [InlineData("sizenesw", CursorKind.SizeNESW)]
        [InlineData("sizewe", CursorKind.SizeWE)]
        [InlineData("sizens", CursorKind.SizeNS)]
        [InlineData("sizeall", CursorKind.SizeAll)]
        [InlineData("no", CursorKind.No)]
        [InlineData("hand", CursorKind.Hand)]
        [InlineData("appstarting", CursorKind.AppStarting)]
        [InlineData("help", CursorKind.Help)]
        [InlineData("pen", CursorKind.Pen)]
        [InlineData("person", CursorKind.Person)]
        [InlineData("custom", CursorKind.Custom)]
        [InlineData("hidden", CursorKind.Hidden)]
        [InlineData("some-future-kind", CursorKind.Custom)]
        [InlineData(null, CursorKind.Custom)]
        public void Cursor_kinds_map_and_unknowns_degrade_to_custom(string wire, CursorKind expected)
        {
            Assert.Equal(expected, InputCapture.ParseCursorKind(wire));
        }

        // -------------------------------------------------------------------------------- events

        [Fact]
        public void Event_rows_parse_all_four_kinds()
        {
            var capture = Parse(
                Header,
                """{"type":"event","t":10.5,"kind":"kd","vk":75,"ch":"k"}""",
                """{"type":"event","t":80.0,"kind":"ku","vk":75}""",
                """{"type":"event","t":120.0,"kind":"md","btn":1,"x":300,"y":400}""",
                """{"type":"event","t":150.5,"kind":"mu","btn":1,"x":301,"y":401}""");

            Assert.Equal(4, capture.Events.Count);

            var kd = capture.Events[0];
            Assert.Equal(InputEventKind.KeyDown, kd.Kind);
            Assert.Equal(75, kd.Code);
            Assert.Equal("k", kd.Char);

            var ku = capture.Events[1];
            Assert.Equal(InputEventKind.KeyUp, ku.Kind);
            Assert.Equal(75, ku.Code);
            Assert.Null(ku.Char);

            var md = capture.Events[2];
            Assert.Equal(InputEventKind.MouseDown, md.Kind);
            Assert.Equal(1, md.Code);
            Assert.Equal(300, md.X);
            Assert.Equal(400, md.Y);

            var mu = capture.Events[3];
            Assert.Equal(InputEventKind.MouseUp, mu.Kind);
            Assert.Equal(150.5, mu.TimeMs);
        }

        // ------------------------------------------------------------------------ cursor sprites

        /// <summary>A <c>cursor_image</c> row over arbitrary plane bytes — the parser stores the
        /// decoded base64 verbatim and never inspects the PNG, so fixtures need none.</summary>
        private static string SpriteRow(int id, byte[] bmp, string mask = "absent",
            string kind = "arrow", int w = 32, int h = 32, int hotx = 2, int hoty = 3)
        {
            var maskJson = mask switch
            {
                "absent" => "",
                null => ",\"mask\":null",
                _ => $",\"mask\":\"{mask}\"",
            };
            return $"{{\"type\":\"cursor_image\",\"id\":{id},\"kind\":\"{kind}\",\"w\":{w},\"h\":{h}," +
                   $"\"hotx\":{hotx},\"hoty\":{hoty},\"bmp\":\"{Convert.ToBase64String(bmp)}\"{maskJson}}}";
        }

        [Fact]
        public void A_cursor_image_row_round_trips_with_its_mask()
        {
            byte[] bmp = { 1, 2, 3, 4 };
            byte[] mask = { 9, 8, 7 };
            var capture = Parse(
                Header,
                SpriteRow(1, bmp, Convert.ToBase64String(mask), kind: "ibeam", w: 24, h: 40, hotx: 11, hoty: 19),
                """{"type":"frame","t":0,"x":1,"y":2,"c":"ibeam","ci":1}""");

            Assert.True(capture.TryGetSprite(1, out var sprite));
            Assert.Equal(CursorKind.IBeam, sprite.Kind);
            Assert.Equal(24, sprite.Width);
            Assert.Equal(40, sprite.Height);
            Assert.Equal(11, sprite.HotX);
            Assert.Equal(19, sprite.HotY);
            Assert.Equal(bmp, sprite.Bmp);
            Assert.Equal(mask, sprite.Mask);
        }

        [Fact]
        public void A_null_or_absent_mask_is_a_plain_alpha_sprite()
        {
            var capture = Parse(
                Header,
                SpriteRow(1, new byte[] { 1 }, mask: null),
                SpriteRow(2, new byte[] { 2 }));

            Assert.True(capture.TryGetSprite(1, out var withNull));
            Assert.Null(withNull.Mask);
            Assert.True(capture.TryGetSprite(2, out var withAbsent));
            Assert.Null(withAbsent.Mask);
        }

        [Fact]
        public void Frames_reference_sprites_by_ci_and_default_to_none()
        {
            var capture = Parse(
                Header,
                SpriteRow(3, new byte[] { 1, 2 }),
                """{"type":"frame","t":0,"x":1,"y":2,"c":"arrow","ci":3}""",
                """{"type":"frame","t":33,"x":1,"y":2,"c":"arrow"}""");

            Assert.Equal(3, capture.Frames[0].SpriteId);
            Assert.True(capture.TryGetSprite(capture.Frames[0].SpriteId, out _));

            // ci omitted (hidden/degraded) — and -1 never resolves to a sprite
            Assert.Equal(-1, capture.Frames[1].SpriteId);
            Assert.False(capture.TryGetSprite(-1, out _));
            Assert.False(capture.TryGetSprite(99, out _)); // an id the file never defined
        }

        [Fact]
        public void A_bad_base64_sprite_is_dropped_and_the_rest_kept()
        {
            var capture = Parse(
                Header,
                """{"type":"cursor_image","id":1,"kind":"arrow","w":8,"h":8,"hotx":0,"hoty":0,"bmp":"not base64!!"}""",
                SpriteRow(2, new byte[] { 5 }),
                """{"type":"frame","t":0,"x":1,"y":2,"c":"arrow","ci":1}""");

            Assert.False(capture.TryGetSprite(1, out _)); // the torn sprite is lost…
            Assert.True(capture.TryGetSprite(2, out _));  // …its neighbours are not
            Assert.Single(capture.Frames);                // and the frame still references id 1
            Assert.Equal(1, capture.Frames[0].SpriteId);  // — the lookup just misses
        }

        [Fact]
        public void A_duplicate_sprite_id_keeps_the_last_definition()
        {
            var capture = Parse(
                Header,
                SpriteRow(1, new byte[] { 1 }),
                SpriteRow(1, new byte[] { 2 }));

            Assert.True(capture.TryGetSprite(1, out var sprite));
            Assert.Equal(new byte[] { 2 }, sprite.Bmp);
        }

        [Fact]
        public void A_v1_file_yields_no_sprites()
        {
            var capture = Parse(
                """{"type":"header","version":1,"region":[0,0,64,64],"fps_num":30,"fps_den":1,"platform":"windows"}""",
                """{"type":"frame","t":0,"x":1,"y":2,"c":"arrow"}""");

            Assert.Equal(-1, capture.Frames[0].SpriteId);
            Assert.False(capture.TryGetPreviewSprite(out _));
        }

        [Fact]
        public void The_preview_sprite_prefers_a_maskless_arrow()
        {
            // masked arrow (needs pixels beneath), maskless ibeam, maskless arrow: the tile
            // wants the last — then the first maskless shape, then anything at all
            var capture = Parse(
                Header,
                SpriteRow(1, new byte[] { 1 }, Convert.ToBase64String(new byte[] { 9 })),
                SpriteRow(2, new byte[] { 2 }, kind: "ibeam"),
                SpriteRow(3, new byte[] { 3 }));

            Assert.True(capture.TryGetPreviewSprite(out var sprite));
            Assert.Equal(new byte[] { 3 }, sprite.Bmp);

            var noArrow = Parse(
                Header,
                SpriteRow(1, new byte[] { 1 }, Convert.ToBase64String(new byte[] { 9 })),
                SpriteRow(2, new byte[] { 2 }, kind: "ibeam"));
            Assert.True(noArrow.TryGetPreviewSprite(out sprite));
            Assert.Equal(new byte[] { 2 }, sprite.Bmp);

            var maskedOnly = Parse(
                Header,
                SpriteRow(1, new byte[] { 1 }, Convert.ToBase64String(new byte[] { 9 })));
            Assert.True(maskedOnly.TryGetPreviewSprite(out sprite));
            Assert.Equal(new byte[] { 1 }, sprite.Bmp);
        }

        // ------------------------------------------------------------------------------- lookups

        [Fact]
        public void FrameAt_returns_the_latest_at_or_before()
        {
            var capture = Parse(
                Header,
                """{"type":"frame","t":0,"x":1,"y":0,"c":"arrow"}""",
                """{"type":"frame","t":33,"x":2,"y":0,"c":"arrow"}""",
                """{"type":"frame","t":66,"x":3,"y":0,"c":"arrow"}""");

            Assert.Null(capture.FrameAt(-0.1)); // before the first frame
            Assert.Equal(1, capture.FrameAt(0).Value.X);
            Assert.Equal(1, capture.FrameAt(32.9).Value.X);
            Assert.Equal(2, capture.FrameAt(33).Value.X);
            Assert.Equal(3, capture.FrameAt(66).Value.X);
            Assert.Equal(3, capture.FrameAt(10_000).Value.X); // past the end holds the last
        }

        [Fact]
        public void FrameAt_holds_the_last_frame_across_a_gap()
        {
            // pause-adjusted times are contiguous but not dense: a VFR stall (or simply sparse
            // sampling) leaves a gap, and any time inside it resolves to the frame before it.
            var capture = Parse(
                Header,
                """{"type":"frame","t":100,"x":10,"y":0,"c":"arrow"}""",
                """{"type":"frame","t":5000,"x":20,"y":0,"c":"arrow"}""");

            Assert.Equal(10, capture.FrameAt(2500).Value.X);
            Assert.Equal(10, capture.FrameAt(4999.9).Value.X);
            Assert.Equal(20, capture.FrameAt(5000).Value.X);
        }

        [Fact]
        public void EventsBetween_is_a_half_open_range()
        {
            var capture = Parse(
                Header,
                """{"type":"event","t":10,"kind":"md","btn":1,"x":0,"y":0}""",
                """{"type":"event","t":20,"kind":"mu","btn":1,"x":0,"y":0}""",
                """{"type":"event","t":30,"kind":"md","btn":2,"x":0,"y":0}""");

            Assert.Equal(3, capture.EventsBetween(10, 30.1).Count);
            Assert.Equal(2, capture.EventsBetween(10, 30).Count); // end exclusive
            Assert.Single(capture.EventsBetween(15, 25));
            Assert.Equal(0, capture.EventsBetween(21, 29).Count);
            Assert.Equal(0, capture.EventsBetween(100, 200).Count);
            Assert.Equal(0, capture.EventsBetween(30, 10).Count); // inverted range

            var slice = capture.EventsBetween(15, 35);
            Assert.Equal(new[] { 20.0, 30.0 }, slice.Select(e => e.TimeMs));
        }

        [Fact]
        public void Events_sort_even_when_hook_rows_land_out_of_order()
        {
            var capture = Parse(
                Header,
                """{"type":"event","t":50,"kind":"kd","vk":65}""",
                """{"type":"event","t":49.5,"kind":"md","btn":1,"x":0,"y":0}""");

            Assert.Equal(49.5, capture.Events[0].TimeMs);
            Assert.Equal(50.0, capture.Events[1].TimeMs);
        }

        // ----------------------------------------------------------------------- forward tolerance

        [Fact]
        public void Unknown_rows_fields_and_value_shapes_are_skipped()
        {
            var capture = Parse(
                Header,
                """{"type":"gesture","t":5,"fingers":3}""",
                """{"type":"frame","t":10,"x":1,"y":2,"c":"arrow","pressure":0.5,"extra":{"nested":[1,2]}}""",
                """{"type":"event","t":20,"kind":"wheel","delta":120}""",
                """{"type":"frame","t":30,"x":3,"y":4,"c":"arrow","b":{"weird":"shape"}}""");

            // the unknown row type and unknown event kind vanish; the frames keep parsing, the
            // reshaped "b" field alone falls back to its default.
            Assert.Equal(2, capture.Frames.Count);
            Assert.Empty(capture.Events);
            Assert.Equal(1, capture.Frames[0].X);
            Assert.Equal(3, capture.Frames[1].X);
            Assert.Equal(0, capture.Frames[1].Buttons);
        }

        [Fact]
        public void A_torn_last_line_is_dropped_and_the_rest_kept()
        {
            var capture = Parse(
                Header,
                """{"type":"frame","t":10,"x":1,"y":2,"c":"arrow"}""",
                """{"type":"frame","t":20,"x":3,""");

            Assert.Single(capture.Frames);
            Assert.Equal(10, capture.Frames[0].TimeMs);
        }

        [Fact]
        public void Garbage_between_rows_is_skipped()
        {
            var capture = Parse(
                "this is not json",
                Header,
                "",
                """{"type":"frame","t":10,"x":1,"y":2,"c":"arrow"}""");

            Assert.Equal(2, capture.Header.Version);
            Assert.Single(capture.Frames);
        }

        [Fact]
        public void A_file_with_no_parseable_rows_is_Empty()
        {
            var capture = Parse("garbage", "more garbage");

            Assert.Same(InputCapture.Empty, capture);
            Assert.True(capture.IsEmpty);
            Assert.Null(capture.FrameAt(0));
            Assert.Equal(0, capture.EventsBetween(0, 1000).Count);
        }

        [Fact]
        public void Frames_without_a_header_are_still_kept()
        {
            var capture = Parse("""{"type":"frame","t":10,"x":1,"y":2,"c":"arrow"}""");

            Assert.Single(capture.Frames);
            Assert.Equal(0, capture.Header.Version); // header absent
        }

        // ------------------------------------------------------------------------ load and cache

        [Fact]
        public void Load_of_a_missing_file_is_Empty_and_never_throws()
        {
            var path = Path.Combine(Path.GetTempPath(), $"input-capture-{Guid.NewGuid():N}.jsonl");

            Assert.Same(InputCapture.Empty, InputCapture.Load(path));
            Assert.Same(InputCapture.Empty, InputCapture.Load(null));
            Assert.Same(InputCapture.Empty, InputCapture.Load(""));
        }

        [Fact]
        public void Get_caches_per_path()
        {
            var path = Path.Combine(Path.GetTempPath(), $"input-capture-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(path,
                Header + "\n" + """{"type":"frame","t":10,"x":1,"y":2,"c":"arrow"}""" + "\n");
            try
            {
                var first = InputCapture.Get(path);
                Assert.Single(first.Frames);

                // the cache is immutable after load: a rewritten file does not change the
                // instance, and the same reference comes back.
                File.WriteAllText(path, "");
                Assert.Same(first, InputCapture.Get(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Get_caches_the_missing_probe_too()
        {
            var path = Path.Combine(Path.GetTempPath(), $"input-capture-{Guid.NewGuid():N}.jsonl");

            Assert.Same(InputCapture.Empty, InputCapture.Get(path));
            Assert.Same(InputCapture.Empty, InputCapture.Get(path));
        }
    }
}
