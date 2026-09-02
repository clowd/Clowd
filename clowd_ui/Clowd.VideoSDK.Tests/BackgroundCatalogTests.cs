using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The background library as a whole: every catalog row names an embedded file that loads,
    /// every SVG is understood by the reader but for the deliberate omissions, the declared
    /// loop periods agree with the files, the shipped animations close their loops, and the id
    /// resolution degrades the way the cursor styles' does.
    /// </summary>
    public class BackgroundCatalogTests
    {
        private static readonly SKRect[] KnownViewBoxes =
        {
            SKRect.Create(0, 0, 1600, 1000),   // gradient
            SKRect.Create(0, 0, 2000, 1125),   // monterey
            SKRect.Create(0, 0, 2560, 1440),   // big sur
            SKRect.Create(0, 0, 900, 600),     // the haikei-derived eight
        };

        /// <summary>Every (style, spec) row, including the unnamed one of a style with no themes.</summary>
        public static IEnumerable<object[]> AllSpecs()
            => BackgroundCatalog.Styles.SelectMany(s => s.Specs.Select(t => new object[] { s.Id, t.Id }));

        /// <summary>Every distinct SVG file, with the style that owns it.</summary>
        public static IEnumerable<object[]> AllSvgAssets()
            => BackgroundCatalog.Styles.SelectMany(s => s.Specs.Select(t => t.Asset))
                .Where(a => a.EndsWith(".svg", StringComparison.Ordinal))
                .Distinct()
                .Select(a => new object[] { a });

        private static BackgroundTheme SpecOf(string style, string theme)
            => BackgroundCatalog.Find(style).Specs.Single(s => s.Id == theme);

        // ------------------------------------------------------------------------ the model

        [Fact]
        public void Model_style_list_equals_catalog_order()
        {
            Assert.Equal(BackgroundCatalog.Styles.Select(s => s.Id).ToArray(), BackgroundContent.Styles.ToArray());
            Assert.Equal(BackgroundCatalog.DefaultStyle, new BackgroundContent().Style);
        }

        [Fact]
        public void Labels_are_plain_words()
        {
            foreach (var style in BackgroundCatalog.Styles)
            {
                Assert.DoesNotContain('—', style.Label);
                Assert.DoesNotContain('–', style.Label);
                foreach (var theme in style.Specs.Where(t => t.Label != null))
                {
                    Assert.DoesNotContain('—', theme.Label);
                    Assert.DoesNotContain('–', theme.Label);
                }
            }
        }

        // ---------------------------------------------------------------------- the assets

        [Theory]
        [MemberData(nameof(AllSpecs))]
        public void Every_asset_is_embedded(string style, string theme)
        {
            var spec = SpecOf(style, theme);
            Assert.True(BackgroundAssets.ResourceExists(spec.Asset),
                $"{style}/{theme}: {BackgroundAssets.ResourceNameOf(spec.Asset)} is not in the manifest");
        }

        [Fact]
        public void Manifest_names_fold_both_separators()
        {
            Assert.Equal(BackgroundAssets.ResourceRoot + "big-sur.teal.svg", BackgroundAssets.ResourceNameOf("big-sur/teal.svg"));
            Assert.Equal(BackgroundAssets.ResourceRoot + "big-sur.teal.svg", BackgroundAssets.ResourceNameOf("big-sur\\teal.svg"));
        }

        [Theory]
        [MemberData(nameof(AllSpecs))]
        public void Every_spec_loads_into_a_scene(string style, string theme)
        {
            var scene = BackgroundRenderer.GetScene(style, theme);
            Assert.NotNull(scene);
            Assert.Contains(scene.ViewBox, KnownViewBoxes);
            Assert.Same(scene, BackgroundRenderer.GetScene(style, theme));

            var declared = BackgroundCatalog.Find(style);
            Assert.Equal(declared.IsAnimated, scene.IsAnimated);
            Assert.Equal(declared.PeriodTicks, scene.PeriodTicks);
        }

        /// <summary>
        /// The reader must understand every shipped file completely except for one deliberate
        /// omission: the Gradient files' <c>feTurbulence</c> grain rect. Anything else in the
        /// skip list is a wallpaper drawn half. Monterey's three <c>&lt;use&gt;</c> backdrop-blur
        /// layers used to be a second omission; they are read now, and this is what keeps them
        /// read.
        /// </summary>
        [Theory]
        [MemberData(nameof(AllSvgAssets))]
        public void Every_svg_parses_with_only_the_deliberate_skips(string asset)
        {
            var root = BackgroundAssets.TryReadXml(asset);
            Assert.NotNull(root);
            var scene = BackgroundSvgReader.Read(root);

            var unexpected = scene.Skipped
                .Where(s => !(s.StartsWith("rect: filter #grain ", StringComparison.Ordinal) && s.Contains("feTurbulence")))
                .ToArray();
            Assert.True(unexpected.Length == 0, asset + " skipped: " + string.Join(" | ", unexpected));

            // And the omission lands exactly where the survey says it is.
            int grain = scene.Skipped.Count(s => s.StartsWith("rect: filter #grain ", StringComparison.Ordinal));
            Assert.Equal(asset.StartsWith("gradient/", StringComparison.Ordinal) ? 1 : 0, grain);
        }

        [Theory]
        [MemberData(nameof(AllSvgAssets))]
        public void Declared_period_matches_the_file(string asset)
        {
            var style = BackgroundCatalog.Styles.Single(s => s.Specs.Any(t => t.Asset == asset));
            var root = BackgroundAssets.TryReadXml(asset);
            var animations = root.Descendants().Where(e => e.Name.LocalName is "animate" or "animateTransform").ToArray();

            if (!style.IsAnimated)
            {
                Assert.Empty(animations);
                return;
            }

            Assert.NotEmpty(animations);
            foreach (var animation in animations)
                Assert.Equal(style.PeriodTicks, SmilTrack.Ticks((string)animation.Attribute("dur")));
        }

        /// <summary>The sampler has no wraparound branch, so this is what makes the loops
        /// seamless: the last keyTime is exactly 1 and the first and last values are the same
        /// text, on every animation of every shipped file.</summary>
        [Theory]
        [MemberData(nameof(AllSvgAssets))]
        public void Every_animation_closes_its_loop(string asset)
        {
            var root = BackgroundAssets.TryReadXml(asset);
            foreach (var animation in root.Descendants().Where(e => e.Name.LocalName is "animate" or "animateTransform"))
            {
                Assert.Equal("indefinite", (string)animation.Attribute("repeatCount"));
                Assert.Equal("linear", (string)animation.Attribute("calcMode"));
                var keyTimes = ((string)animation.Attribute("keyTimes")).Split(';');
                var values = ((string)animation.Attribute("values")).Split(';');
                Assert.Equal(keyTimes.Length, values.Length);
                Assert.Equal(0f, float.Parse(keyTimes[0], System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(1f, float.Parse(keyTimes[^1], System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(values[0].Trim(), values[^1].Trim());
            }
        }

        // -------------------------------------------------------------------------- palettes

        [Fact]
        public void Every_palette_row_has_one_value_per_swatch()
        {
            var rows = BackgroundCatalog.PaletteRows.ToArray();
            Assert.Equal(6, rows.Length);
            foreach (var (id, label, rgb) in rows)
            {
                Assert.Equal(BackgroundCatalog.SwatchCount, rgb.Length);
                Assert.False(string.IsNullOrEmpty(id));
                Assert.False(string.IsNullOrEmpty(label));
                Assert.All(rgb, v => Assert.True(v <= 0xFFFFFF));
            }
        }

        [Fact]
        public void Generative_styles_offer_source_then_every_palette()
        {
            var paletteIds = BackgroundCatalog.PaletteRows.Select(p => p.Id).ToArray();
            foreach (var id in new[] { "layered-waves", "stacked-waves", "layered-steps", "moving-blob", "moving-corners", "breathing-field" })
            {
                var style = BackgroundCatalog.Find(id);
                Assert.Equal(new[] { "source" }.Concat(paletteIds).ToArray(), style.Themes.Select(t => t.Id).ToArray());
                Assert.Null(style.Specs[0].Palette);
                Assert.All(style.Specs.Skip(1), t => Assert.NotNull(t.Palette));
            }
        }

        [Theory]
        [InlineData("layered-waves")]
        [InlineData("stacked-waves")]
        [InlineData("layered-steps")]
        [InlineData("moving-blob")]
        [InlineData("moving-corners")]
        [InlineData("breathing-field")]
        public void Each_palette_theme_renders_differently_from_source(string styleId)
        {
            var style = BackgroundCatalog.Find(styleId);
            var source = BackgroundRendererTests.RenderPixels(styleId, "source", 0);
            var seen = new List<byte[]> { source };
            foreach (var theme in style.Themes.Skip(1))
            {
                var pixels = BackgroundRendererTests.RenderPixels(styleId, theme.Id, 0);
                foreach (var other in seen)
                    Assert.False(pixels.AsSpan().SequenceEqual(other), $"{styleId}/{theme.Id} draws the same pixels as an earlier theme");
                seen.Add(pixels);
            }
        }

        [Fact]
        public void Recoloring_keeps_alpha_and_passes_unmapped_colors_through()
        {
            var palette = new CursorPackPalette((0xFF0066, 0x123456));
            Assert.Equal(0x80123456u, palette.Resolve(0x80FF0066));
            Assert.Equal(0x80ABCDEFu, palette.Resolve(0x80ABCDEF));
        }

        // -------------------------------------------------------------------------- resolving

        [Theory]
        [InlineData(null, "big-sur")]
        [InlineData("", "big-sur")]
        [InlineData("no-such-style", "big-sur")]
        [InlineData("MONTEREY", "monterey")]
        [InlineData("moving-blob", "moving-blob")]
        public void Unknown_style_ids_fall_back_to_the_default(string stored, string resolved)
        {
            Assert.Equal(resolved, BackgroundCatalog.ResolveStyle(stored));
        }

        [Theory]
        [InlineData("big-sur", null, "default")]
        [InlineData("big-sur", "no-such-theme", "default")]
        [InlineData("big-sur", "Violet", "violet")]
        [InlineData("explode", null, null)]
        [InlineData("explode", "anything", null)]
        [InlineData("moving-blob", null, "source")]
        [InlineData("moving-blob", "ember", "ember")]
        [InlineData("no-such-style", "teal", "teal")]         // the style resolves first
        [InlineData("no-such-style", "ember", "default")]     // ...so a theme of another style is unknown
        public void Unknown_theme_ids_fall_back_to_the_first(string style, string stored, string resolved)
        {
            Assert.Equal(resolved, BackgroundCatalog.ResolveTheme(style, stored));
        }

        [Fact]
        public void GetScene_resolves_unknown_ids_to_the_defaults()
        {
            Assert.Same(BackgroundRenderer.GetScene("big-sur", "default"), BackgroundRenderer.GetScene("no-such-style", null));
            Assert.Same(BackgroundRenderer.GetScene("big-sur", "default"), BackgroundRenderer.GetScene(null, "no-such-theme"));
            Assert.Same(BackgroundRenderer.GetScene("gradient", "sunrise"), BackgroundRenderer.GetScene("GRADIENT", "SUNRISE"));
            Assert.Same(BackgroundRenderer.GetScene("gradient", "sunrise"), BackgroundRenderer.GetScene("gradient", "not-a-theme"));
            Assert.NotSame(BackgroundRenderer.GetScene("gradient", "sunrise"), BackgroundRenderer.GetScene("gradient", "abyss"));
        }

        [Fact]
        public void ProjectHasAnimatedBackground_applies_the_resolve_rule()
        {
            var project = new Project();
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0 };
            project.Tracks.Add(track);
            var item = new Item { Id = Guid.NewGuid(), TrackId = track.Id, DurationTicks = 1, Content = new BackgroundContent { Style = "MOVING-BLOB" } };
            project.Items.Add(item);
            Assert.True(BackgroundCatalog.ProjectHasAnimatedBackground(project));

            track.Hidden = true;
            Assert.False(BackgroundCatalog.ProjectHasAnimatedBackground(project));
            track.Hidden = false;

            ((BackgroundContent)item.Content).Style = "not-a-style"; // resolves to big-sur, a still
            Assert.False(BackgroundCatalog.ProjectHasAnimatedBackground(project));
        }

        [Fact]
        public void Only_monterey_dark_needs_isolation()
        {
            foreach (var style in BackgroundCatalog.Styles)
            {
                foreach (var spec in style.Specs)
                {
                    bool expected = style.Id == "monterey" && spec.Id == "dark";
                    Assert.Equal(expected, BackgroundRenderer.GetScene(style.Id, spec.Id).NeedsIsolation);
                }
            }
        }
    }
}
