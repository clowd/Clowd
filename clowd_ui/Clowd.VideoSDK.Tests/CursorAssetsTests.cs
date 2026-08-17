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
        public void Vision_is_the_one_themed_style_native_excluded()
        {
            Assert.Equal(new[] { "vision" }, CursorAssets.Styles);
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
        public void Vision_covers_every_kind_in_both_colourways()
        {
            foreach (var (style, variant) in AllStyles())
                foreach (var kind in CursorAssets.Kinds)
                    Assert.NotNull(CursorAssets.TryGet(style, variant, kind));
        }

        [Fact]
        public void Only_vision_declares_colourways_and_dark_is_its_default()
        {
            Assert.Equal(new[] { "dark", "light" },
                CursorAssets.Variants("vision").Select(v => v.Id).ToArray());
            Assert.Equal(new[] { "Dark", "Light" },
                CursorAssets.Variants("vision").Select(v => v.Label).ToArray());

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

        [Fact]
        public void The_two_colourways_are_one_geometry_with_the_palette_swapped()
        {
            const uint ink = 0xFF0C1E35, paper = 0xFFFFFFFF;

            foreach (var kind in CursorAssets.Kinds)
            {
                var dark = CursorAssets.TryGet("vision", "dark", kind);
                var light = CursorAssets.TryGet("vision", "light", kind);
                Assert.NotSame(dark, light);
                Assert.Equal(dark.ViewBox, light.ViewBox);
                Assert.Equal(dark.Hotspot, light.Hotspot);
                Assert.Equal(dark.Paths.Count, light.Paths.Count);

                for (int i = 0; i < dark.Paths.Count; i++)
                {
                    var a = dark.Paths[i];
                    var b = light.Paths[i];
                    Assert.Equal(a.PathData, b.PathData);
                    Assert.Equal(a.StrokeWidth, b.StrokeWidth);

                    // A layer drawn in the pack's two base colours trades them between the
                    // colourways; one drawn in an accent (the deny red, the wait cyan, the busy
                    // pointer's white body) is the same colour in both, deliberately.
                    foreach (var (x, y) in new[] { (a.FillArgb, b.FillArgb), (a.StrokeArgb, b.StrokeArgb) })
                    {
                        bool swapped = (x == ink && y == paper) || (x == paper && y == ink);
                        Assert.True(swapped || x == y,
                            $"vision/{kind}: layer {i} colour {x:X8}/{y:X8} neither swaps nor holds");
                    }
                }
            }
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
        /// Where each kind's hotspot has to be, which is what the recorded position means. A
        /// pointer-shaped cursor points with its top-left tip; the hand and its badged variant with
        /// the fingertip; everything else is centred on the ink, which is what the OS does for the
        /// resize, crosshair and text cursors.
        /// </summary>
        [Theory]
        [MemberData(nameof(AllGlyphs))]
        public void The_hotspot_is_where_the_kind_points_from(string style, string variant, string kind)
        {
            var glyph = CursorAssets.TryGet(style, variant, kind);
            var ink = InkOf(glyph);

            bool tipped = kind is CursorAssets.KindArrow or CursorAssets.KindNo
                or CursorAssets.KindHelp or CursorAssets.KindPen or CursorAssets.KindAppStarting;
            bool fingered = kind is CursorAssets.KindHand or CursorAssets.KindPerson;

            if (tipped || fingered)
            {
                Assert.True(glyph.Hotspot.Y <= ink.Top + ink.Height * 0.2f,
                    $"{style}/{variant}/{kind}: hotspot is not near the top of the glyph");
                if (tipped)
                    Assert.True(glyph.Hotspot.X <= ink.Left + ink.Width * 0.2f,
                        $"{style}/{variant}/{kind}: hotspot is not near the tip");
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

        /// <summary>The two diagonal-resize cursors are one shape and its mirror, and they lean
        /// opposite ways — the pair is generated by reflecting one source, so a mistake here would
        /// hand every NW-SE drag the NE-SW cursor.</summary>
        [Fact]
        public void The_diagonal_resize_cursors_mirror_each_other()
        {
            var nwse = InkOf(CursorAssets.TryGet("vision", "dark", CursorAssets.KindSizeNwse));
            var nesw = InkOf(CursorAssets.TryGet("vision", "dark", CursorAssets.KindSizeNesw));

            // same extent, both centred in the viewBox
            Assert.Equal(nwse.Width, nesw.Width, 1);
            Assert.Equal(nwse.Height, nesw.Height, 1);
            Assert.Equal(64f, nwse.MidX, 0);
            Assert.Equal(64f, nesw.MidX, 0);

            // ...but the ink sits in the opposite corners: compare where each layer's own centre is
            var nwseLayers = LayerCentres("sizenwse");
            var neswLayers = LayerCentres("sizenesw");
            Assert.All(nwseLayers, c => Assert.True((c.X < 64) == (c.Y < 64),
                "sizenwse should run top-left to bottom-right"));
            Assert.All(neswLayers, c => Assert.True((c.X < 64) != (c.Y < 64),
                "sizenesw should run bottom-left to top-right"));
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

        private static List<SKPoint> LayerCentres(string kind)
        {
            var centres = new List<SKPoint>();
            foreach (var layer in CursorAssets.TryGet("vision", "dark", kind).Paths)
            {
                using var path = SKPath.ParseSvgPathData(layer.PathData);
                centres.Add(new SKPoint(path.Bounds.MidX, path.Bounds.MidY));
            }
            return centres;
        }
    }
}
