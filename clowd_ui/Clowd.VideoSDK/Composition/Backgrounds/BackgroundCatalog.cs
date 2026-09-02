using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// One theme of a background style: the picker's two names for it and, for the loader, the
    /// artwork it draws and the recoloring applied to it. A theme either has its own file (the
    /// four Big Sur, two Monterey and ten Gradient drawings) or shares its style's one file and
    /// differs from the others by a palette (the six generative styles) — the picker cannot tell
    /// which, and does not need to.
    /// </summary>
    public sealed class BackgroundTheme
    {
        internal BackgroundTheme(string id, string label, string asset, CursorPackPalette palette)
        {
            Id = id;
            Label = label;
            Asset = asset;
            Palette = palette;
        }

        /// <summary>The wire value stored in <c>BackgroundContent.Theme</c>; null for the single
        /// unnamed colorway of a style that offers no themes.</summary>
        public string Id { get; }

        /// <summary>The picker's display name. Plain words — the source files' own titles are
        /// never used as display copy.</summary>
        public string Label { get; }

        /// <summary>The artwork's path under <c>Composition/Backgrounds/Art/</c> with <c>/</c>
        /// separators, e.g. <c>big-sur/teal.svg</c> or <c>moving-blob/source.svg</c>. Every asset
        /// is an SVG parsed into a live scene; nothing in the library ships as a raster.</summary>
        internal string Asset { get; }

        /// <summary>The recoloring that turns the stored file's colors into this theme's, or null
        /// to draw the file as authored (every authored-theme style, and the generative styles'
        /// <c>source</c> theme).</summary>
        internal CursorPackPalette Palette { get; }
    }

    /// <summary>
    /// One background style: a piece of artwork (or a family of them) as the picker and the
    /// composer see it — its names, whether and how long it loops, and the themes it offers.
    /// Immutable and shared process-wide.
    /// </summary>
    public sealed class BackgroundStyle
    {
        internal BackgroundStyle(string id, string label, double periodSeconds, BackgroundTheme[] specs)
        {
            Id = id;
            Label = label;
            PeriodSeconds = periodSeconds;
            PeriodTicks = (long)(periodSeconds * TimeSpan.TicksPerSecond);
            Specs = specs;
            // A style with one colorway has nothing to pick, so the picker sees no themes and the
            // stored theme stays null (the CursorAssets.Variants contract) — and a style that
            // later gains a second colorway becomes pickable by adding a row, not by a lookup change.
            Themes = specs.Length >= 2 ? Array.AsReadOnly(specs) : Array.Empty<BackgroundTheme>();
        }

        /// <summary>The wire value stored in <c>BackgroundContent.Style</c>.</summary>
        public string Id { get; }

        /// <summary>The picker's display name.</summary>
        public string Label { get; }

        /// <summary>How long one loop of an animated style runs, in seconds; 0 for a static style.
        /// Declared here rather than read off the file so <see cref="IsAnimated"/> never forces a
        /// parse (the preview's repaint gate asks before any art is loaded); a test holds it to the
        /// <c>dur</c> the file's animations actually carry.</summary>
        public double PeriodSeconds { get; }

        /// <summary><see cref="PeriodSeconds"/> in 100ns ticks — the modulus of the phase clock,
        /// so it is kept as the exact integer the composer divides by (600_000_000 for a 60 s
        /// loop) rather than recomputed from a double at every frame.</summary>
        public long PeriodTicks { get; }

        /// <summary>True when the art moves with time; the phase handed to the scene is always 0
        /// otherwise.</summary>
        public bool IsAnimated => PeriodSeconds > 0;

        /// <summary>The themes the picker shows, in picker order — empty when the style offers
        /// nothing to pick, in which case the stored theme is null.</summary>
        public IReadOnlyList<BackgroundTheme> Themes { get; }

        /// <summary>Every drawable colorway, one per (asset, palette) row, including the single
        /// unnamed one of a style with no themes. <see cref="Themes"/> is this list when it is
        /// worth picking from; the loader always resolves through this one.</summary>
        internal BackgroundTheme[] Specs { get; }
    }

    /// <summary>
    /// The library of wallpapers a <c>BackgroundContent</c> may name: every style, every theme,
    /// and how the ids a project stores resolve to something drawable. All data, in static
    /// tables at the bottom of the file, in the manner of <see cref="CursorAssets"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two kinds of style live here. An <b>authored-theme</b> style (Big Sur, Monterey, Gradient,
    /// Explode) ships one file per theme and draws it as-is. A <b>generative</b> style (the six
    /// Haikei-derived drawings) ships one file and offers it in its own colors (<c>source</c>,
    /// the default, so a project written before palettes existed is pixel-identical) plus one
    /// theme per entry of the shared <see cref="Palettes"/> table, recolored through a
    /// <see cref="CursorPackPalette"/> the way the cursor packs are. Each generative row states
    /// which of the file's own colors plays which role in a palette, so adding a palette is one
    /// line here and it appears under all six styles; nothing in the draw path ever looks at a
    /// palette id.
    /// </para>
    /// <para>
    /// Ids are wire contract and resolve leniently: an unknown style is the default style, an
    /// unknown theme is its style's first — the <see cref="CursorAssets.ResolveVariant"/> rule —
    /// so a project that names an id this build has retired (or one a newer build added) still
    /// opens and draws something rather than nothing.
    /// </para>
    /// </remarks>
    public static class BackgroundCatalog
    {
        /// <summary>The style <c>BackgroundContent.Style</c> defaults to, and what an unknown
        /// style id resolves to.</summary>
        public const string DefaultStyle = "big-sur";

        /// <summary>Every style, in picker order (the order of <c>BackgroundContent.Styles</c>).</summary>
        public static IReadOnlyList<BackgroundStyle> Styles { get; }

        private static readonly Dictionary<string, BackgroundStyle> ById;

        // Built in a static constructor rather than in field initializers: the table is assembled
        // from the data arrays at the bottom of the file, and C# runs field initializers in
        // textual order, so an initializer up here would read them as null. The constructor body
        // runs after every initializer regardless of where the arrays sit.
        static BackgroundCatalog()
        {
            Styles = Array.AsReadOnly(BuildStyles());
            ById = BuildIndex();
        }

        /// <summary>The style for an id, or null when there is none (case-insensitive). Callers
        /// that want something drawable for any input go through <see cref="ResolveStyle"/>.</summary>
        public static BackgroundStyle Find(string styleId)
            => styleId != null && ById.TryGetValue(styleId, out var style) ? style : null;

        /// <summary>The style id a stored value actually draws: itself when known, else
        /// <see cref="DefaultStyle"/>.</summary>
        public static string ResolveStyle(string styleId)
            => Find(styleId)?.Id ?? DefaultStyle;

        /// <summary>
        /// The theme id a (style, theme) pair actually draws: the stored one when the style offers
        /// it, else the style's first — an unrecognized theme degrades to the style's default the
        /// way an unrecognized style degrades to the default style. Null when the style has no
        /// themes to choose between. An unknown style is resolved first, so the answer is always a
        /// theme of the style that will be drawn.
        /// </summary>
        public static string ResolveTheme(string styleId, string themeId)
        {
            var style = Find(styleId) ?? Find(DefaultStyle);
            var specs = style.Specs;
            if (specs.Length <= 1)
                return null;
            foreach (var candidate in specs)
            {
                if (string.Equals(candidate.Id, themeId, StringComparison.OrdinalIgnoreCase))
                    return candidate.Id;
            }
            return specs[0].Id;
        }

        /// <summary>The theme a (style, theme) pair resolves to, with its asset and palette, for
        /// the loader — never null, by the same rules as <see cref="ResolveTheme"/>.</summary>
        internal static BackgroundTheme ResolveSpec(string styleId, string themeId)
        {
            var style = Find(styleId) ?? Find(DefaultStyle);
            var resolved = ResolveTheme(style.Id, themeId);
            foreach (var spec in style.Specs)
            {
                if (string.Equals(spec.Id, resolved, StringComparison.OrdinalIgnoreCase))
                    return spec;
            }
            return style.Specs[0];
        }

        /// <summary>
        /// True when composing the project at different instants can give different pictures on
        /// account of a wallpaper: some item on a non-hidden video track is a
        /// <c>BackgroundContent</c> whose (resolved) style loops. This is the editor preview's
        /// repaint gate — it otherwise redraws only when a decoded frame arrives, which an
        /// animated wallpaper over a stretch with no footage never does — and it lives here so the
        /// gate applies exactly the resolve rule the composer draws by.
        /// </summary>
        public static bool ProjectHasAnimatedBackground(Project project)
        {
            if (project?.Items == null)
                return false;

            HashSet<Guid> visibleVideoTracks = null;
            foreach (var item in project.Items)
            {
                if (item.Content is not BackgroundContent background)
                    continue;
                if (!(Find(ResolveStyle(background.Style))?.IsAnimated ?? false))
                    continue;

                if (visibleVideoTracks == null)
                {
                    visibleVideoTracks = new HashSet<Guid>();
                    if (project.Tracks != null)
                    {
                        foreach (var track in project.Tracks)
                        {
                            if (track.Kind == TrackKind.Video && !track.Hidden)
                                visibleVideoTracks.Add(track.Id);
                        }
                    }
                }
                if (visibleVideoTracks.Contains(item.TrackId))
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------ palettes

        /// <summary>The seven roles a generative style's colors are cast in: a ground and a ramp
        /// that runs from the lightest, most forward layer (<see cref="Ramp0"/>) to the deepest
        /// (<see cref="Ramp5"/>). Seven because Layered Waves, the deepest style, stacks a ground
        /// and six waves; a shallower style uses the subset its row names.</summary>
        internal enum Swatch
        {
            Background = 0,
            Ramp0,
            Ramp1,
            Ramp2,
            Ramp3,
            Ramp4,
            Ramp5,
        }

        /// <summary>One named palette: seven <c>0xRRGGBB</c> values indexed by
        /// <see cref="Swatch"/>.</summary>
        private sealed record Palette(string Id, string Label, params uint[] Rgb);

        /// <summary>The palettes every generative style offers, in picker order after
        /// <c>source</c>. Each ramp is monotonic in lightness from Ramp0 to Ramp5, which is what
        /// lets one table serve a six-deep wave stack, a four-deep step stack and a two-color
        /// poster alike. Adding a palette is adding a row; a test holds every row to exactly
        /// seven values.</summary>
        private static readonly Palette[] Palettes =
        {
            //           id          label          background  ramp0     ramp1     ramp2     ramp3     ramp4     ramp5
            new Palette("midnight", "Midnight", 0x050B1E, 0x7FF3E8, 0x4FD8E0, 0x2FB6DA, 0x1E8ECC, 0x1466B4, 0x0D3F8C),
            new Palette("ember",    "Ember",    0x1A0A08, 0xFFD166, 0xFCA13F, 0xF2743B, 0xDE4B3C, 0xB62F3E, 0x7E1D36),
            new Palette("forest",   "Forest",   0x06150F, 0xA8E6A1, 0x6FD08C, 0x3EB489, 0x219A83, 0x14776E, 0x0B5450),
            new Palette("grape",    "Grape",    0x140021, 0xE9A8FF, 0xC46BF5, 0x9B3FE8, 0x7A25CE, 0x5A16A6, 0x3C0B78),
            new Palette("mono",     "Mono",     0x0B0B0D, 0xF2F3F5, 0xCBCDD3, 0x9EA1AA, 0x74777F, 0x4C4F57, 0x2A2C32),
            new Palette("blush",    "Blush",    0x1B0A12, 0xFFD9C7, 0xFFAF9B, 0xFA7268, 0xE85467, 0xC62368, 0x8E1352),
        };

        /// <summary>The count every <see cref="Palettes"/> row must have; exposed for the test
        /// that enforces it.</summary>
        internal static int SwatchCount => Enum.GetValues(typeof(Swatch)).Length;

        /// <summary>Every palette's (id, label, values) for the tests' structural sweeps.</summary>
        internal static IEnumerable<(string Id, string Label, uint[] Rgb)> PaletteRows
            => Palettes.Select(p => (p.Id, p.Label, p.Rgb));

        // ------------------------------------------------------------------------ generative styles

        /// <summary>A generative style as the table sees it: its one file, its loop length, and
        /// the slot map — which of the file's own colors (as <c>0xRRGGBB</c>, matched
        /// case-insensitively against the file's mixed-case hex) plays which palette role. Explicit
        /// pairs rather than a scrape of the file in document order, so the unreferenced
        /// <c>&lt;defs&gt;</c> gradients in Explode and Moving Corners, the missing ground rect in
        /// Stacked Waves, and the ground color Breathing Field reuses on two of its circles (to
        /// carve holes in the blurred mass) all need no special handling: a color not in the map
        /// passes through, and a color in it is recolored wherever it appears.</summary>
        private sealed record GenerativeStyle(string Id, string Label, string Asset, double PeriodSeconds,
            params (uint Source, Swatch Role)[] Slots);

        private static readonly GenerativeStyle[] GenerativeStyles =
        {
            new GenerativeStyle("layered-waves", "Layered Waves", "layered-waves/source.svg", 0,
                (0x001220, Swatch.Background), (0xFA7268, Swatch.Ramp0), (0xF16367, Swatch.Ramp1),
                (0xE85467, Swatch.Ramp2), (0xDE4467, Swatch.Ramp3), (0xD23467, Swatch.Ramp4),
                (0xC62368, Swatch.Ramp5)),
            // No ground rect in this file: its first wave covers the canvas, so every slot is a
            // ramp entry.
            new GenerativeStyle("stacked-waves", "Stacked Waves", "stacked-waves/source.svg", 0,
                (0x6198FF, Swatch.Ramp0), (0x4385FF, Swatch.Ramp1), (0x2071FF, Swatch.Ramp2),
                (0x0061F1, Swatch.Ramp3), (0x0056D6, Swatch.Ramp4), (0x004CBB, Swatch.Ramp5)),
            // The middle four ramp entries rather than the full spread: the source's four purples
            // are tonally tight, and the full spread reads harsh against every palette where the
            // middle four stay as calm as the original.
            new GenerativeStyle("layered-steps", "Layered Steps", "layered-steps/source.svg", 0,
                (0x140021, Swatch.Background), (0x9900FF, Swatch.Ramp1), (0x8200D9, Swatch.Ramp2),
                (0x6C00B4, Swatch.Ramp3), (0x560090, Swatch.Ramp4)),
            // Ramp1/Ramp3 rather than Background/Ramp0: the blob's field is a bright poster pink,
            // not a ground, and casting it as the palette's ground turns a bold two-tone poster
            // into a dark blob and loses the artwork.
            new GenerativeStyle("moving-blob", "Moving Blob", "moving-blob/source.svg", 60,
                (0xFF0066, Swatch.Ramp1), (0xBB004B, Swatch.Ramp3)),
            new GenerativeStyle("moving-corners", "Moving Corners", "moving-corners/source.svg", 60,
                (0x001220, Swatch.Background), (0xFBAE3C, Swatch.Ramp0)),
            new GenerativeStyle("breathing-field", "Breathing Field", "breathing-field/source.svg", 90,
                (0x6600FF, Swatch.Background), (0x00CC99, Swatch.Ramp0)),
        };

        // ------------------------------------------------------------------------ the table

        /// <summary>A theme drawn from its own file, as authored.</summary>
        private static BackgroundTheme Authored(string id, string label, string asset)
            => new BackgroundTheme(id, label, asset, palette: null);

        /// <summary>A generative style's colorways: the file as authored first (so a null theme
        /// keeps every pre-palette project pixel-identical), then one theme per palette, each a
        /// recoloring of the file's slots into that palette's swatches.</summary>
        private static BackgroundTheme[] GenerativeSpecs(GenerativeStyle style)
        {
            var specs = new BackgroundTheme[Palettes.Length + 1];
            specs[0] = Authored("source", "Original", style.Asset);
            for (int i = 0; i < Palettes.Length; i++)
            {
                var palette = Palettes[i];
                var pairs = style.Slots.Select(s => (s.Source, palette.Rgb[(int)s.Role])).ToArray();
                specs[i + 1] = new BackgroundTheme(palette.Id, palette.Label, style.Asset, new CursorPackPalette(pairs));
            }
            return specs;
        }

        /// <summary>The styles in picker order: the authored-theme four, then the generative six
        /// — static ones first, the three loops last so the picker groups them. This order is the
        /// one <c>BackgroundContent.Styles</c> spells out literally.</summary>
        private static BackgroundStyle[] BuildStyles()
        {
            var generative = GenerativeStyles.ToDictionary(g => g.Id, StringComparer.Ordinal);
            BackgroundStyle Generative(string id)
            {
                var style = generative[id];
                return new BackgroundStyle(style.Id, style.Label, style.PeriodSeconds, GenerativeSpecs(style));
            }

            return new[]
            {
                // Big Sur is a gradient reconstruction of the macOS mesh: a dozen gradient-filled
                // paths per theme, no filter and no clip, so all four themes together are 17 KB
                // and cost one trivial parse. The mesh itself cannot be shipped (SVG2
                // meshgradient, which only Inkscape renders) and tessellating it ran to 925 KB
                // of flat quads per theme under seven blur layers, which is what this replaces.
                new BackgroundStyle("big-sur", "Big Sur", 0, new[]
                {
                    Authored("default", "Default", "big-sur/default.svg"),
                    Authored("teal", "Teal", "big-sur/teal.svg"),
                    Authored("violet", "Violet", "big-sur/violet.svg"),
                    Authored("amber", "Amber", "big-sur/amber.svg"),
                }),
                new BackgroundStyle("monterey", "Monterey", 0, new[]
                {
                    Authored("light", "Light", "monterey/light.svg"),
                    Authored("dark", "Dark", "monterey/dark.svg"),
                }),
                // File order, which is also a sensible menu order: warm to cool to dark.
                new BackgroundStyle("gradient", "Gradient", 0, new[]
                {
                    Authored("sunrise", "Sunrise", "gradient/sunrise.svg"),
                    Authored("sunset", "Sunset", "gradient/sunset.svg"),
                    Authored("glacier", "Glacier", "gradient/glacier.svg"),
                    Authored("periwinkle", "Periwinkle", "gradient/periwinkle.svg"),
                    Authored("magenta-wedge", "Magenta Wedge", "gradient/magenta-wedge.svg"),
                    Authored("abyss", "Abyss", "gradient/abyss.svg"),
                    Authored("peach-cream", "Peach Cream", "gradient/peach-cream.svg"),
                    Authored("indigo-dusk", "Indigo Dusk", "gradient/indigo-dusk.svg"),
                    Authored("orchid", "Orchid", "gradient/orchid.svg"),
                    Authored("midnight-bloom", "Midnight Bloom", "gradient/midnight-bloom.svg"),
                }),
                // One colorway and nothing to pick, so its single spec is unnamed and the stored
                // theme is always null.
                new BackgroundStyle("explode", "Explode", 0, new[]
                {
                    Authored(null, null, "explode/explode.svg"),
                }),
                Generative("layered-waves"),
                Generative("stacked-waves"),
                Generative("layered-steps"),
                Generative("moving-blob"),
                Generative("moving-corners"),
                Generative("breathing-field"),
            };
        }

        private static Dictionary<string, BackgroundStyle> BuildIndex()
        {
            var index = new Dictionary<string, BackgroundStyle>(StringComparer.OrdinalIgnoreCase);
            foreach (var style in Styles)
                index.Add(style.Id, style);
            return index;
        }
    }
}
