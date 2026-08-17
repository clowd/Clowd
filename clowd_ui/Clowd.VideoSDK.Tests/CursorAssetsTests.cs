using System;
using System.Collections.Generic;
using Clowd.VideoSDK.Composition;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The themed cursor artwork table: every stored path must round-trip through
    /// <c>SKPath.ParseSvgPathData</c> (the store is hand-normalised SVG, so a typo would only
    /// surface as a silently missing cursor at compose time), every glyph must stay inside its
    /// viewBox with its hotspot on the ink, and the lookup must degrade to null rather than throw
    /// for the styles/kinds icons8 has no artwork for.
    /// </summary>
    public class CursorAssetsTests
    {
        private static readonly string[] Kinds =
        {
            CursorAssets.KindArrow, CursorAssets.KindHand, CursorAssets.KindIBeam,
        };

        public static IEnumerable<object[]> AllGlyphs()
        {
            foreach (var style in CursorAssets.Styles)
                foreach (var kind in Kinds)
                    if (CursorAssets.TryGet(style, kind) != null)
                        yield return new object[] { style, kind };
        }

        [Fact]
        public void Styles_are_the_seven_themed_styles_native_excluded()
        {
            Assert.Equal(
                new[] { "ios-glyph", "material", "fluent", "plumpy", "softteal", "papercut", "doodle" },
                CursorAssets.Styles);
            Assert.DoesNotContain(CursorAssets.NativeStyle, CursorAssets.Styles);
            Assert.Contains(CursorAssets.DefaultStyle, CursorAssets.Styles);
        }

        [Fact]
        public void Every_style_has_an_arrow_the_universal_fallback()
        {
            foreach (var style in CursorAssets.Styles)
                Assert.NotNull(CursorAssets.TryGet(style, CursorAssets.KindArrow));
        }

        [Theory]
        [MemberData(nameof(AllGlyphs))]
        public void Every_stored_path_parses(string style, string kind)
        {
            var glyph = CursorAssets.TryGet(style, kind);
            Assert.NotEmpty(glyph.Paths);

            foreach (var layer in glyph.Paths)
            {
                using var path = SKPath.ParseSvgPathData(layer.PathData);
                Assert.True(path != null, $"{style}/{kind}: unparseable path data");
                Assert.False(path.IsEmpty, $"{style}/{kind}: path parsed to nothing");
                Assert.True(path.Bounds.Width > 0 && path.Bounds.Height > 0,
                    $"{style}/{kind}: degenerate layer with no area");
            }
        }

        [Theory]
        [MemberData(nameof(AllGlyphs))]
        public void Every_layer_has_a_visible_fill_and_a_consistent_halo(string style, string kind)
        {
            var glyph = CursorAssets.TryGet(style, kind);
            foreach (var layer in glyph.Paths)
            {
                Assert.True(layer.Fill.Alpha > 0, $"{style}/{kind}: invisible fill");
                // A halo is either fully specified or fully absent — a colour without a width (or
                // the reverse) would silently paint nothing.
                Assert.Equal(layer.Stroke.Alpha > 0, layer.StrokeWidth > 0);
                Assert.Equal(layer.Stroke.Alpha > 0 && layer.StrokeWidth > 0, layer.HasStroke);
            }
        }

        [Theory]
        [MemberData(nameof(AllGlyphs))]
        public void Ink_stays_inside_the_viewbox_and_the_hotspot_sits_on_it(string style, string kind)
        {
            var glyph = CursorAssets.TryGet(style, kind);
            Assert.True(glyph.ViewBox > 0);

            var ink = SKRect.Empty;
            foreach (var layer in glyph.Paths)
            {
                using var path = SKPath.ParseSvgPathData(layer.PathData);
                ink = ink.IsEmpty ? path.Bounds : SKRect.Union(ink, path.Bounds);
            }

            float slack = glyph.ViewBox * 0.01f;
            Assert.InRange(ink.Left, -slack, glyph.ViewBox);
            Assert.InRange(ink.Top, -slack, glyph.ViewBox);
            Assert.InRange(ink.Right, 0, glyph.ViewBox + slack);
            Assert.InRange(ink.Bottom, 0, glyph.ViewBox + slack);

            Assert.InRange(glyph.Hotspot.X, ink.Left - slack, ink.Right + slack);
            Assert.InRange(glyph.Hotspot.Y, ink.Top - slack, ink.Bottom + slack);
        }

        [Theory]
        [MemberData(nameof(AllGlyphs))]
        public void Arrow_hotspot_is_the_tip_and_ibeam_hotspot_is_the_centre(string style, string kind)
        {
            var glyph = CursorAssets.TryGet(style, kind);

            var ink = SKRect.Empty;
            foreach (var layer in glyph.Paths)
            {
                using var path = SKPath.ParseSvgPathData(layer.PathData);
                ink = ink.IsEmpty ? path.Bounds : SKRect.Union(ink, path.Bounds);
            }

            if (kind == CursorAssets.KindArrow || kind == CursorAssets.KindHand)
            {
                // Tip / fingertip: in the top fifth of the ink, and for the arrow also the left fifth.
                Assert.True(glyph.Hotspot.Y <= ink.Top + ink.Height * 0.2f,
                    $"{style}/{kind}: hotspot is not near the top of the glyph");
                if (kind == CursorAssets.KindArrow)
                    Assert.True(glyph.Hotspot.X <= ink.Left + ink.Width * 0.2f,
                        $"{style}/arrow: hotspot is not near the tip");
            }
            else
            {
                Assert.Equal(ink.MidX, glyph.Hotspot.X, 0);
                Assert.Equal(ink.MidY, glyph.Hotspot.Y, 0);
            }
        }

        [Fact]
        public void Unsupported_lookups_return_null_rather_than_throwing()
        {
            Assert.Null(CursorAssets.TryGet(null, CursorAssets.KindArrow));
            Assert.Null(CursorAssets.TryGet("ios-glyph", null));
            Assert.Null(CursorAssets.TryGet("", ""));
            Assert.Null(CursorAssets.TryGet(CursorAssets.NativeStyle, CursorAssets.KindArrow));
            Assert.Null(CursorAssets.TryGet("no-such-style", CursorAssets.KindArrow));
            Assert.Null(CursorAssets.TryGet("ios-glyph", "wait"));
            Assert.Null(CursorAssets.TryGet("ios-glyph", "custom"));

            // The documented gaps: icons8 has no I-beam in these families, so the caller falls
            // back to the style's arrow.
            foreach (var style in new[] { "softteal", "papercut", "doodle" })
            {
                Assert.Null(CursorAssets.TryGet(style, CursorAssets.KindIBeam));
                Assert.NotNull(CursorAssets.TryGet(style, CursorAssets.KindArrow));
                Assert.NotNull(CursorAssets.TryGet(style, CursorAssets.KindHand));
            }
        }

        [Fact]
        public void Lookup_is_case_insensitive_and_the_table_is_shared_not_copied()
        {
            var lower = CursorAssets.TryGet("ios-glyph", "arrow");
            Assert.Same(lower, CursorAssets.TryGet("IOS-Glyph", "Arrow"));
            Assert.Same(lower, CursorAssets.TryGet("ios-glyph", "arrow"));
        }

        [Fact]
        public void Monochrome_styles_carry_a_white_halo_and_ink_styles_do_not()
        {
            foreach (var style in new[] { "ios-glyph", "material", "fluent", "plumpy", "softteal" })
                foreach (var kind in Kinds)
                {
                    var glyph = CursorAssets.TryGet(style, kind);
                    if (glyph == null)
                        continue;
                    foreach (var layer in glyph.Paths)
                    {
                        Assert.True(layer.HasStroke, $"{style}/{kind}: monochrome layer without a halo");
                        Assert.Equal(SKColors.White, layer.Stroke);
                    }
                }

            foreach (var style in new[] { "papercut", "doodle" })
                foreach (var kind in Kinds)
                {
                    var glyph = CursorAssets.TryGet(style, kind);
                    if (glyph == null)
                        continue;
                    foreach (var layer in glyph.Paths)
                        Assert.False(layer.HasStroke, $"{style}/{kind}: ink style should carry no halo");
                }
        }
    }
}
