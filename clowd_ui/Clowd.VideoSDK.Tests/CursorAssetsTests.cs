using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.VideoSDK.Composition;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The themed cursor artwork table: every stored path must round-trip through
    /// <c>SKPath.ParseSvgPathData</c> (the store is generated SVG, so a bad edit would only surface
    /// as a silently missing cursor at compose time), every glyph must stay inside its viewBox with
    /// its hotspot on the ink, and the lookup must degrade to null rather than throw. Colourways
    /// are held to the same bar: every one a style declares must resolve to real artwork, and one
    /// it does not declare must fall back to its default rather than draw nothing.
    /// </summary>
    public class CursorAssetsTests
    {
        /// <summary>Every (style, colourway) pair the table offers — a style with one colourway
        /// contributes a single null-variant pair, which is the two-argument lookup.</summary>
        private static IEnumerable<(string Style, string Variant)> AllStyles()
        {
            foreach (var style in CursorAssets.Styles)
            {
                var variants = CursorAssets.Variants(style);
                if (variants.Count == 0)
                {
                    yield return (style, null);
                    continue;
                }
                foreach (var variant in variants)
                    yield return (style, variant.Id);
            }
        }

        public static IEnumerable<object[]> AllGlyphs()
        {
            foreach (var (style, variant) in AllStyles())
                foreach (var kind in CursorAssets.Kinds)
                    if (CursorAssets.TryGet(style, variant, kind) != null)
                        yield return new object[] { style, variant, kind };
        }

        [Fact]
        public void The_themed_styles_are_the_three_packs_native_excluded()
        {
            Assert.Equal(new[] { "vision", "point", "numix" }, CursorAssets.Styles);
            Assert.DoesNotContain(CursorAssets.NativeStyle, CursorAssets.Styles);
            Assert.Equal("vision", CursorAssets.DefaultStyle);
        }

        /// <summary>The kind keys are the recorder's own wire names, one per drawable
        /// <see cref="CursorKind"/> — a kind added to the capture without a key here would silently
        /// draw the arrow for every style forever.</summary>
        [Fact]
        public void Kinds_cover_every_drawable_cursor_kind()
        {
            var drawable = Enum.GetValues<CursorKind>()
                .Where(k => k is not (CursorKind.Custom or CursorKind.Hidden))
                .ToArray();

            Assert.Equal(drawable.Length, CursorAssets.Kinds.Count);
            foreach (var kind in drawable)
                Assert.Contains(CursorCompose.KindKey(kind), CursorAssets.Kinds);

            Assert.Null(CursorCompose.KindKey(CursorKind.Custom));
            Assert.Null(CursorCompose.KindKey(CursorKind.Hidden));
        }

        [Fact]
        public void Every_pack_covers_every_kind_in_every_colourway()
        {
            foreach (var (style, variant) in AllStyles())
                foreach (var kind in CursorAssets.Kinds)
                    Assert.NotNull(CursorAssets.TryGet(style, variant, kind));
        }

        [Fact]
        public void Each_pack_declares_a_dark_and_a_light_colourway_dark_first()
        {
            foreach (var style in CursorAssets.Styles)
            {
                Assert.Equal(new[] { "dark", "light" },
                    CursorAssets.Variants(style).Select(v => v.Id).ToArray());
                Assert.Equal(new[] { "Dark", "Light" },
                    CursorAssets.Variants(style).Select(v => v.Label).ToArray());
            }

            Assert.Empty(CursorAssets.Variants(CursorAssets.NativeStyle));
            Assert.Empty(CursorAssets.Variants("no-such-style"));
            Assert.Empty(CursorAssets.Variants(null));
        }

        [Fact]
        public void An_unknown_colourway_resolves_to_the_styles_default_rather_than_nothing()
        {
            Assert.Equal("dark", CursorAssets.ResolveVariant("vision", null));
            Assert.Equal("dark", CursorAssets.ResolveVariant("vision", "sepia"));
            Assert.Equal("light", CursorAssets.ResolveVariant("vision", "LIGHT"));
            Assert.Null(CursorAssets.ResolveVariant(null, "dark"));
            Assert.Null(CursorAssets.ResolveVariant("no-such-style", "dark"));

            // ...and the lookup follows it, so a stale project still draws a cursor
            Assert.Same(CursorAssets.TryGet("vision", "dark", CursorAssets.KindArrow),
                CursorAssets.TryGet("vision", "sepia", CursorAssets.KindArrow));
            Assert.Same(CursorAssets.TryGet("vision", "dark", CursorAssets.KindArrow),
                CursorAssets.TryGet("vision", CursorAssets.KindArrow));
        }

        /// <summary>A pack's colourways are one geometry drawn in two palettes — nothing about the
        /// shapes, hotspots or halo widths may differ between them, which is the property that
        /// lets the picker present the choice as a palette and nothing more.</summary>
        [Theory]
        [InlineData("vision")]
        [InlineData("point")]
        [InlineData("numix")]
        public void A_packs_colourways_are_one_geometry_in_two_palettes(string style)
        {
            foreach (var kind in CursorAssets.Kinds)
            {
                var dark = CursorAssets.TryGet(style, "dark", kind);
                var light = CursorAssets.TryGet(style, "light", kind);
                Assert.NotSame(dark, light);
                Assert.Equal(dark.ViewBox, light.ViewBox);
                Assert.Equal(dark.Hotspot, light.Hotspot);
                Assert.Equal(dark.Paths.Count, light.Paths.Count);

                for (int i = 0; i < dark.Paths.Count; i++)
                {
                    Assert.Equal(dark.Paths[i].PathData, light.Paths[i].PathData);
                    Assert.Equal(dark.Paths[i].StrokeWidth, light.Paths[i].StrokeWidth);
                }
            }
        }

        /// <summary>The plain pointer is where a pack's two base colours show plainest: its fill
        /// and halo trade places between the colourways. (Other kinds hold an accent that is the
        /// same in both — the deny red, the wait blue — so only this one is universal.)</summary>
        [Theory]
        [InlineData("vision")]
        [InlineData("point")]
        [InlineData("numix")]
        public void The_arrows_two_colourways_trade_the_packs_base_colours(string style)
        {
            var dark = CursorAssets.TryGet(style, "dark", CursorAssets.KindArrow);
            var light = CursorAssets.TryGet(style, "light", CursorAssets.KindArrow);

            var layer = Assert.Single(dark.Paths);
            var other = Assert.Single(light.Paths);
            Assert.Equal(layer.FillArgb, other.StrokeArgb);
            Assert.Equal(layer.StrokeArgb, other.FillArgb);
            Assert.NotEqual(layer.FillArgb, layer.StrokeArgb);
        }

        [Theory]
        [MemberData(nameof(AllGlyphs))]
        public void Every_stored_path_parses(string style, string variant, string kind)
        {
            var glyph = CursorAssets.TryGet(style, variant, kind);
            Assert.NotEmpty(glyph.Paths);

            foreach (var layer in glyph.Paths)
            {
                using var path = SKPath.ParseSvgPathData(layer.PathData);
                Assert.True(path != null, $"{style}/{variant}/{kind}: unparseable path data");
                Assert.False(path.IsEmpty, $"{style}/{variant}/{kind}: path parsed to nothing");
                Assert.True(path.Bounds.Width > 0 && path.Bounds.Height > 0,
                    $"{style}/{variant}/{kind}: degenerate layer with no area");
            }
        }

        [Theory]
        [MemberData(nameof(AllGlyphs))]
        public void Every_layer_has_a_visible_fill_and_a_consistent_halo(string style, string variant, string kind)
        {
            var glyph = CursorAssets.TryGet(style, variant, kind);
            foreach (var layer in glyph.Paths)
            {
                Assert.True(layer.Fill.Alpha > 0, $"{style}/{variant}/{kind}: invisible fill");
                // A halo is either fully specified or fully absent — a colour without a width (or
                // the reverse) would silently paint nothing.
                Assert.Equal(layer.Stroke.Alpha > 0, layer.StrokeWidth > 0);
                Assert.Equal(layer.Stroke.Alpha > 0 && layer.StrokeWidth > 0, layer.HasStroke);
                // the non-Skia view of the same two colours (the inspector's style tiles)
                Assert.Equal((uint)layer.Fill, layer.FillArgb);
                Assert.Equal((uint)layer.Stroke, layer.StrokeArgb);
            }
        }

        /// <summary>Vision draws every shape over dark and light video alike, so every layer of
        /// every glyph carries a contrast halo — the one property that keeps a cursor readable
        /// wherever it lands.</summary>
        [Theory]
        [MemberData(nameof(AllGlyphs))]
        public void Every_layer_carries_a_contrast_halo(string style, string variant, string kind)
        {
            foreach (var layer in CursorAssets.TryGet(style, variant, kind).Paths)
                Assert.True(layer.HasStroke, $"{style}/{variant}/{kind}: layer without a halo");
        }

        [Theory]
        [MemberData(nameof(AllGlyphs))]
        public void Ink_stays_inside_the_viewbox_and_the_hotspot_sits_on_it(string style, string variant, string kind)
        {
            var glyph = CursorAssets.TryGet(style, variant, kind);
            Assert.True(glyph.ViewBox > 0);

            var ink = InkOf(glyph);
            float slack = glyph.ViewBox * 0.01f;
            Assert.InRange(ink.Left, -slack, glyph.ViewBox);
            Assert.InRange(ink.Top, -slack, glyph.ViewBox);
            Assert.InRange(ink.Right, 0, glyph.ViewBox + slack);
            Assert.InRange(ink.Bottom, 0, glyph.ViewBox + slack);

            Assert.InRange(glyph.Hotspot.X, ink.Left - slack, ink.Right + slack);
            Assert.InRange(glyph.Hotspot.Y, ink.Top - slack, ink.Bottom + slack);
        }

        /// <summary>
        /// Where each Vision kind's hotspot has to be, which is what the recorded position means
        /// for that pack: a pointer-shaped cursor points with its top-left tip, the hand and its
        /// badged variant with the fingertip, and everything else is centred on the ink, which is
        /// what the OS does for the resize, crosshair and text cursors. This is Vision's shape, not
        /// a rule about packs — see the Point case below.
        /// </summary>
        [Theory]
        [InlineData("dark")]
        [InlineData("light")]
        public void Visions_hotspots_are_the_tip_the_fingertip_or_the_centre(string variant)
        {
            foreach (var kind in CursorAssets.Kinds)
            {
                var glyph = CursorAssets.TryGet("vision", variant, kind);
                var ink = InkOf(glyph);

                bool tipped = kind is CursorAssets.KindArrow or CursorAssets.KindNo
                    or CursorAssets.KindHelp or CursorAssets.KindPen or CursorAssets.KindAppStarting;
                bool fingered = kind is CursorAssets.KindHand or CursorAssets.KindPerson;

                if (tipped || fingered)
                {
                    Assert.True(glyph.Hotspot.Y <= ink.Top + ink.Height * 0.2f,
                        $"vision/{variant}/{kind}: hotspot is not near the top of the glyph");
                    if (tipped)
                        Assert.True(glyph.Hotspot.X <= ink.Left + ink.Width * 0.2f,
                            $"vision/{variant}/{kind}: hotspot is not near the tip");
                }
                else if (kind == CursorAssets.KindUpArrow)
                {
                    // "alternate select" points straight up: top edge, horizontally centred
                    Assert.True(glyph.Hotspot.Y <= ink.Top + ink.Height * 0.2f);
                    Assert.Equal(ink.MidX, glyph.Hotspot.X, 0);
                }
                else
                {
                    Assert.Equal(ink.MidX, glyph.Hotspot.X, 0);
                    Assert.Equal(ink.MidY, glyph.Hotspot.Y, 0);
                }
            }
        }

        /// <summary>Point is a dot cursor: every kind points from the dot, wherever the rest of the
        /// glyph sits. Only <c>help</c> moves it, and it moves the dot with it.</summary>
        [Theory]
        [InlineData("dark")]
        [InlineData("light")]
        public void Points_hotspots_are_always_its_dot(string variant)
        {
            foreach (var kind in CursorAssets.Kinds)
            {
                var glyph = CursorAssets.TryGet("point", variant, kind);
                var expected = kind == CursorAssets.KindHelp ? 42f : 32f;
                Assert.Equal(32f, glyph.Hotspot.X);
                Assert.Equal(expected, glyph.Hotspot.Y);
            }
        }

        /// <summary>
        /// Numix points from whatever end of the drawing does the pointing, which is not always the
        /// top-left: the pointer-shaped kinds from the tip, the hand and its badged variant from the
        /// fingertip, the upright pointer from the top of its stem — and the pen from its <i>nib</i>,
        /// which sits at the bottom left because the pen leans up and to the right. Everything else
        /// is centred on the viewBox, which is what the OS does for the resize, crosshair, text and
        /// busy cursors.
        /// </summary>
        [Theory]
        [InlineData("dark")]
        [InlineData("light")]
        public void Numixs_hotspots_are_the_tip_the_fingertip_the_nib_or_the_centre(string variant)
        {
            foreach (var kind in CursorAssets.Kinds)
            {
                var glyph = CursorAssets.TryGet("numix", variant, kind);
                var ink = InkOf(glyph);
                Assert.Equal(32f, glyph.ViewBox);

                bool tipped = kind is CursorAssets.KindArrow or CursorAssets.KindNo
                    or CursorAssets.KindHelp or CursorAssets.KindAppStarting;
                bool fingered = kind is CursorAssets.KindHand or CursorAssets.KindPerson;

                if (tipped || fingered)
                {
                    Assert.True(glyph.Hotspot.Y <= ink.Top + ink.Height * 0.2f,
                        $"numix/{variant}/{kind}: hotspot is not near the top of the glyph");
                    if (tipped)
                        Assert.True(glyph.Hotspot.X <= ink.Left + ink.Width * 0.2f,
                            $"numix/{variant}/{kind}: hotspot is not near the tip");
                }
                else if (kind == CursorAssets.KindUpArrow)
                {
                    // "alternate select" points straight up: top edge, horizontally centred
                    Assert.True(glyph.Hotspot.Y <= ink.Top + ink.Height * 0.2f);
                    Assert.Equal(ink.MidX, glyph.Hotspot.X, 0);
                }
                else if (kind == CursorAssets.KindPen)
                {
                    // the nib: bottom left, opposite the barrel
                    Assert.True(glyph.Hotspot.X <= ink.Left + ink.Width * 0.2f,
                        $"numix/{variant}/{kind}: hotspot is not at the nib");
                    Assert.True(glyph.Hotspot.Y >= ink.Bottom - ink.Height * 0.2f,
                        $"numix/{variant}/{kind}: hotspot is not at the nib");
                }
                else
                {
                    Assert.Equal(16f, glyph.Hotspot.X);
                    Assert.Equal(16f, glyph.Hotspot.Y);
                }
            }
        }

        /// <summary>Each pack's two diagonal-resize cursors are one shape and its mirror, and they
        /// lean opposite ways — the pair is generated by reflecting one source, so a mistake here
        /// would hand every NW-SE drag the NE-SW cursor.</summary>
        [Theory]
        [InlineData("vision")]
        [InlineData("point")]
        [InlineData("numix")]
        public void The_diagonal_resize_cursors_mirror_each_other(string style)
        {
            var nwse = CursorAssets.TryGet(style, "dark", CursorAssets.KindSizeNwse);
            var nesw = CursorAssets.TryGet(style, "dark", CursorAssets.KindSizeNesw);
            float mid = nwse.ViewBox / 2;

            // same extent, both centred in the viewBox
            Assert.Equal(InkOf(nwse).Width, InkOf(nesw).Width, 1);
            Assert.Equal(InkOf(nwse).Height, InkOf(nesw).Height, 1);
            Assert.Equal(mid, InkOf(nwse).MidX, 0);
            Assert.Equal(mid, InkOf(nesw).MidX, 0);

            // ...but the arms sit in opposite corners. A layer sitting on the centre (Point's dot)
            // belongs to neither diagonal and is skipped.
            foreach (var (glyph, sameSign, label) in new[]
            {
                (nwse, true, "sizenwse should run top-left to bottom-right"),
                (nesw, false, "sizenesw should run bottom-left to top-right"),
            })
            {
                var arms = LayerCentres(glyph)
                    .Where(c => Math.Abs(c.X - mid) > 1 && Math.Abs(c.Y - mid) > 1)
                    .ToArray();
                Assert.NotEmpty(arms);
                Assert.All(arms, c => Assert.Equal(sameSign, (c.X < mid) == (c.Y < mid)));
            }
        }

        /// <summary>The two kinds each pack animates in its source are stored as generated frame
        /// loops; everything else stays a static glyph. The container presents frame 0 outright,
        /// which is what keeps every structural sweep above (and the inspector's tiles) honest
        /// without asking for a time.</summary>
        [Theory]
        [MemberData(nameof(AllGlyphs))]
        public void Wait_and_appstarting_animate_and_everything_else_is_static(
            string style, string variant, string kind)
        {
            var glyph = CursorAssets.TryGet(style, variant, kind);

            if (kind is not (CursorAssets.KindWait or CursorAssets.KindAppStarting))
            {
                Assert.Null(glyph.Frames);
                Assert.Equal(0f, glyph.FrameDurationMs);
                Assert.Same(glyph, glyph.FrameAt(0));
                Assert.Same(glyph, glyph.FrameAt(12345.6));
                return;
            }

            Assert.NotNull(glyph.Frames);
            Assert.True(glyph.Frames.Count > 1);
            Assert.True(glyph.FrameDurationMs > 0);
            Assert.Same(glyph.Paths, glyph.Frames[0].Paths);

            foreach (var frame in glyph.Frames)
            {
                Assert.Null(frame.Frames); // stills, not nested animations
                Assert.Equal(glyph.ViewBox, frame.ViewBox);
                Assert.Equal(glyph.Hotspot, frame.Hotspot);
                foreach (var layer in frame.Paths)
                {
                    using var path = SKPath.ParseSvgPathData(layer.PathData);
                    Assert.False(path == null || path.IsEmpty,
                        $"{style}/{variant}/{kind}: generated frame layer does not parse");
                    Assert.True(layer.HasStroke,
                        $"{style}/{variant}/{kind}: generated frame layer without a halo");
                }
            }
        }

        /// <summary>Frame selection is a pure function of time — same time, same frame, any order,
        /// negative times included — which is the property scrubbing and render determinism ride on.</summary>
        [Fact]
        public void Frame_selection_is_deterministic_and_loops()
        {
            var glyph = CursorAssets.TryGet("vision", "dark", CursorAssets.KindWait);
            int n = glyph.Frames.Count;
            double period = n * (double)glyph.FrameDurationMs;

            Assert.Same(glyph.Frames[0], glyph.FrameAt(0));
            Assert.Same(glyph.Frames[1], glyph.FrameAt(glyph.FrameDurationMs * 1.5));
            Assert.Same(glyph.FrameAt(0), glyph.FrameAt(period));
            Assert.Same(glyph.FrameAt(123), glyph.FrameAt(123 + period * 7));
            Assert.Same(glyph.Frames[n - 1], glyph.FrameAt(-1));
        }

        [Fact]
        public void Unsupported_lookups_return_null_rather_than_throwing()
        {
            Assert.Null(CursorAssets.TryGet(null, CursorAssets.KindArrow));
            Assert.Null(CursorAssets.TryGet("vision", null));
            Assert.Null(CursorAssets.TryGet("", ""));
            Assert.Null(CursorAssets.TryGet(CursorAssets.NativeStyle, CursorAssets.KindArrow));
            Assert.Null(CursorAssets.TryGet("no-such-style", CursorAssets.KindArrow));
            Assert.Null(CursorAssets.TryGet("vision", "custom"));
            Assert.Null(CursorAssets.TryGet("vision", "dark", "custom"));
        }

        [Fact]
        public void Lookup_is_case_insensitive_and_the_table_is_shared_not_copied()
        {
            var arrow = CursorAssets.TryGet("vision", "light", "arrow");
            Assert.Same(arrow, CursorAssets.TryGet("Vision", "Light", "Arrow"));
            Assert.Same(arrow, CursorAssets.TryGet("vision", "light", "arrow"));
        }

        private static SKRect InkOf(CursorGlyph glyph)
        {
            var ink = SKRect.Empty;
            foreach (var layer in glyph.Paths)
            {
                using var path = SKPath.ParseSvgPathData(layer.PathData);
                ink = ink.IsEmpty ? path.Bounds : SKRect.Union(ink, path.Bounds);
            }
            return ink;
        }

        private static List<SKPoint> LayerCentres(CursorGlyph glyph)
        {
            var centres = new List<SKPoint>();
            foreach (var layer in glyph.Paths)
            {
                using var path = SKPath.ParseSvgPathData(layer.PathData);
                centres.Add(new SKPoint(path.Bounds.MidX, path.Bounds.MidY));
            }
            return centres;
        }
    }
}
