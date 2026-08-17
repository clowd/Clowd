using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// One filled/stroked shape of a themed cursor glyph. <see cref="PathData"/> is SVG path
    /// syntax accepted by <c>SKPath.ParseSvgPathData</c> and is expressed in the owning
    /// <see cref="CursorGlyph.ViewBox"/> coordinate space, with holes spelled as reversed contours
    /// (the nonzero fill rule, which is both SVG's default and Skia's). Layers are painted in list
    /// order, each one's halo immediately under its own fill.
    /// </summary>
    public sealed class CursorGlyphPath
    {
        internal CursorGlyphPath(string pathData, uint fill, uint stroke, float strokeWidth)
        {
            PathData = pathData;
            FillArgb = fill;
            StrokeArgb = stroke;
            Fill = new SKColor(fill);
            Stroke = new SKColor(stroke);
            StrokeWidth = strokeWidth;
        }

        /// <summary>SVG path data, in viewBox units.</summary>
        public string PathData { get; }

        /// <summary>Fill colour (ARGB). Never transparent — every stored layer is a real fill.</summary>
        public SKColor Fill { get; }

        /// <summary>Contrast-halo colour (ARGB), or transparent when the layer has no halo.
        /// The halo is a *centred* stroke, so a caller must paint the stroke first and this
        /// layer's fill on top, otherwise the halo eats half of the layer's ink.</summary>
        public SKColor Stroke { get; }

        /// <summary>Halo width in viewBox units (0 when <see cref="Stroke"/> is transparent).</summary>
        public float StrokeWidth { get; }

        /// <summary><see cref="Fill"/> as packed ARGB, and <see cref="Stroke"/> likewise — for the
        /// non-Skia consumer (the inspector's style tiles draw the same layers through Avalonia).</summary>
        public uint FillArgb { get; }

        public uint StrokeArgb { get; }

        /// <summary>True when this layer wants a contrast halo painted behind its fill.</summary>
        public bool HasStroke => Stroke.Alpha > 0 && StrokeWidth > 0;
    }

    /// <summary>
    /// A themed cursor shape: a square viewBox, the hotspot inside it, and the layers to paint.
    /// Immutable and shared process-wide — a caller may cache parsed <c>SKPath</c>s keyed by the
    /// instance, but must not mutate anything reachable from here.
    /// </summary>
    /// <remarks>
    /// An <b>animated</b> cursor is a loop of these: the table stores a container glyph whose
    /// <see cref="Frames"/> are the individual stills, each a plain static glyph. The container
    /// adopts its first frame's geometry outright, so a consumer that never asks for time (the
    /// inspector's style tiles, the tests' structural sweeps) sees frame 0 and needs no special
    /// case; a consumer drawing at a moment in time calls <see cref="FrameAt"/>. Frame selection
    /// is a pure function of the time it is handed — the caller decides whose clock that is
    /// (the compositor passes <i>project</i> time, so a spinner turns at one speed through
    /// speed-warped clips, and preview, scrub and render all agree by construction).
    /// </remarks>
    public sealed class CursorGlyph
    {
        internal CursorGlyph(float viewBox, float hotspotX, float hotspotY, CursorGlyphPath[] paths)
        {
            ViewBox = viewBox;
            Hotspot = new SKPoint(hotspotX, hotspotY);
            Paths = Array.AsReadOnly(paths);
        }

        /// <summary>An animated cursor: a looping frame list, each frame a static glyph sharing
        /// the same viewBox and hotspot. The container itself presents frame 0's geometry.</summary>
        internal CursorGlyph(float frameDurationMs, CursorGlyph[] frames)
        {
            if (frames == null || frames.Length == 0)
                throw new ArgumentException("an animated glyph needs at least one frame", nameof(frames));
            if (frameDurationMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(frameDurationMs));
            foreach (var frame in frames)
            {
                if (frame.Frames != null)
                    throw new ArgumentException("frames cannot themselves be animated", nameof(frames));
                if (frame.ViewBox != frames[0].ViewBox || frame.Hotspot != frames[0].Hotspot)
                    throw new ArgumentException("frames must share one viewBox and hotspot", nameof(frames));
            }

            ViewBox = frames[0].ViewBox;
            Hotspot = frames[0].Hotspot;
            Paths = frames[0].Paths;
            Frames = Array.AsReadOnly(frames);
            FrameDurationMs = frameDurationMs;
        }

        /// <summary>Side of the (always square) source viewBox. Draw scale for a target glyph of
        /// <c>px</c> pixels is <c>px / ViewBox</c>.</summary>
        public float ViewBox { get; }

        /// <summary>The point of the glyph that sits on the sampled cursor position, in viewBox
        /// units — the pack's own hotspot, off its <c>.cur</c> headers.</summary>
        public SKPoint Hotspot { get; }

        /// <summary>Layers, bottom-first. For an animated glyph these are frame 0's layers.</summary>
        public IReadOnlyList<CursorGlyphPath> Paths { get; }

        /// <summary>The animation's stills in loop order, or null for a static glyph.</summary>
        public IReadOnlyList<CursorGlyph> Frames { get; }

        /// <summary>How long each frame shows, in milliseconds (0 for a static glyph).</summary>
        public float FrameDurationMs { get; }

        /// <summary>
        /// The still to draw at <paramref name="timeMs"/>: the glyph itself when static, else
        /// the covering frame of the loop. Deterministic — the same time always yields the same
        /// frame, whatever order times are asked in, which is what keeps scrubbing, preview and
        /// render pixel-identical. Negative times are valid (the loop extends both ways).
        /// </summary>
        public CursorGlyph FrameAt(double timeMs)
        {
            if (Frames == null)
                return this;
            int idx = (int)Math.Floor(timeMs / FrameDurationMs) % Frames.Count;
            if (idx < 0)
                idx += Frames.Count;
            return Frames[idx];
        }
    }

    /// <summary>
    /// One colourway of a cursor style. A style that ships more than one (see
    /// <see cref="CursorAssets.Variants"/>) draws the same geometry in each — only the palette
    /// differs — so <c>CursorContent.Variant</c> selects between them without changing anything
    /// else about the glyph.
    /// </summary>
    public sealed class CursorVariant
    {
        internal CursorVariant(string id, string label)
        {
            Id = id;
            Label = label;
        }

        /// <summary>The wire value stored in <c>CursorContent.Variant</c>.</summary>
        public string Id { get; }

        /// <summary>The picker's display name.</summary>
        public string Label { get; }
    }

    /// <summary>
    /// Static vector artwork for the themed cursor styles of <c>CursorContent</c> — the styles
    /// other than <c>native</c>, which draws the recorded 512×512 cursor box instead and never
    /// consults this table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A style covers a <b>kind</b> (see <see cref="Kinds"/> — the shapes the recorder reports, one
    /// key per drawable <c>CursorKind</c>) in one or more <b>colourways</b>: one geometry, several
    /// palettes, declared in <see cref="Variants"/> and selected by <c>CursorContent.Variant</c>.
    /// A style that declares no colourway has exactly one, unnamed, and stores a null variant.
    /// </para>
    /// <para>
    /// Seven styles live here, from two families that are built quite differently.
    /// </para>
    /// <para>
    /// <b>The iDarques packs</b> — <c>vision</c> (Vision Cursor, a conventional pointer set on a
    /// 128-unit viewBox) and <c>point</c> (Point.er, a minimal set built from one dot and one
    /// triangular cap on a 64-unit one) — each cover every drawable kind in a dark and a light
    /// colourway, and their artwork is the path constants below. Neither is traced: both are read
    /// out of the packs' own Photoshop sources, whose files carry every cursor as a shape layer and
    /// whose vector masks are that path data. Each constant is named after the PSD layer it came
    /// from.
    /// </para>
    /// <para>
    /// <b>The SVG-sourced packs</b> — <c>bibata</c>, <c>breezex</c>, <c>macos</c>, <c>fuchsia</c>
    /// and <c>neon</c> — carry no artwork here at all. Their SVG sources ship as embedded resources
    /// under <c>Composition/CursorPacks</c> and <see cref="CursorPackLoader"/> reads them at table
    /// build, so a pack is re-synced by re-copying files rather than by transcribing anything. Each
    /// colourway is a recolouring of one stored drawing (see <see cref="CursorPackPalette"/>): the
    /// four ful1e5 packs author against placeholder colours and declare the real ones per theme,
    /// while <c>neon</c> ships one folder per colour, of which one is stored and the rest are its
    /// palettes. <c>bibata</c> is the one style whose colourways are not purely a palette — its
    /// <c>R</c> and <c>S</c> halves are the pack's rounded and sharp edge sets, which is two
    /// geometries — and only its left-hand cursors are carried, the pack's <c>-Right</c> mirrors
    /// being a separate set for a different hand.
    /// </para>
    /// <para>
    /// Faithfulness notes for the iDarques packs, all of them forced by what this table can hold
    /// (flat fills and one centred contrast stroke per layer):
    /// </para>
    /// <list type="bullet">
    /// <item>Both stroke their shapes <i>outside</i> the path. A centred stroke painted under the
    /// layer's own fill is the same picture at double the authored width, which is where their halo
    /// widths come from (Vision authors 8/6/4, Point 3). The caller must use round joins for this to
    /// hold at a corner — see <c>CursorCompose.PaintGlyph</c>.</item>
    /// <item>Photoshop marks a hole with a path operation and leaves the winding alone; the same
    /// hole is spelled here as a reversed contour, which is what the nonzero fill rule wants.</item>
    /// <item>Each pack's <c>wait</c>/<c>appstarting</c> are its two animated cursors, gradient-based
    /// in the source. Each is stored as a looping frame list (<see cref="CursorGlyph.Frames"/>),
    /// generated at table build from the static artwork rather than pasted per frame: Vision's
    /// rotating gradient becomes a paper sector orbiting the busy ring and a pointer whose fill
    /// pulses towards the accent; Point's colour cycle is the source's own — the dot lerping between
    /// the colourway's base colour and its link blue. (The SVG-sourced packs need none of this: the
    /// ful1e5 four draw every frame, and Neon declares its animation in the file, which the loader
    /// samples — either way the loader hands back a finished loop.)</item>
    /// <item>Both <c>help</c> badges are raster in the source — live text in DIN Round Pro, a pixel
    /// layer — so those two glyphs are traced from the PSDs' own rasterised layers.</item>
    /// <item>Only one of the two diagonal-resize cursors is authored per PSD (Vision's Black source
    /// has the NESW one, Point's the NWSE one). Each pack's pair is one geometry mirrored about the
    /// viewBox midline, so the two diagonals always match.</item>
    /// <item>Point's White source parks its dot at the <c>help</c> composition's lower position and
    /// omits the <c>vert</c>/<c>dgn</c> groups; geometry therefore comes from one file per pack,
    /// with only the palette read per colourway.</item>
    /// </list>
    /// <para>
    /// Every hotspot is the pack's own: the iDarques ones off their <c>.cur</c> headers scaled to the
    /// viewBox — which is why Vision points from a tip and Point from its dot — the ful1e5 ones off
    /// each pack's <c>configs/x.build.toml</c>, and Neon's, which ships no config, off the point
    /// each of its drawings is built around. All are quoted against a 256-unit box.
    /// </para>
    /// </remarks>
    public static class CursorAssets
    {
        /// <summary>The style <c>CursorContent.Style</c> defaults to.</summary>
        public const string DefaultStyle = VisionStyle;

        /// <summary>The style that draws the recorded cursor sprites; never present in this table.</summary>
        public const string NativeStyle = "native";

        /// <summary>The style that draws no cursor at all; never present in this table.</summary>
        public const string NoneStyle = "none";

        private const string VisionStyle = "vision";

        private const string PointStyle = "point";

        private const string BibataStyle = "bibata";

        private const string BreezeXStyle = "breezex";

        private const string MacOsStyle = "macos";

        private const string FuchsiaStyle = "fuchsia";

        private const string NeonStyle = "neon";

        // Kind keys: the input-capture wire names, one per drawable CursorKind. `Custom` has none
        // (it falls back to the arrow) and `Hidden` draws nothing.
        public const string KindArrow = "arrow";
        public const string KindIBeam = "ibeam";
        public const string KindWait = "wait";
        public const string KindCross = "cross";
        public const string KindUpArrow = "uparrow";
        public const string KindSizeNwse = "sizenwse";
        public const string KindSizeNesw = "sizenesw";
        public const string KindSizeWe = "sizewe";
        public const string KindSizeNs = "sizens";
        public const string KindSizeAll = "sizeall";
        public const string KindNo = "no";
        public const string KindHand = "hand";
        public const string KindAppStarting = "appstarting";
        public const string KindHelp = "help";
        public const string KindPen = "pen";
        public const string KindPerson = "person";

        /// <summary>Every kind a style may carry artwork for. A style is free to cover only some;
        /// the caller falls back to <see cref="KindArrow"/>, which every style must have.</summary>
        public static IReadOnlyList<string> Kinds { get; } = Array.AsReadOnly(new[]
        {
            KindArrow, KindIBeam, KindWait, KindCross, KindUpArrow, KindSizeNwse, KindSizeNesw,
            KindSizeWe, KindSizeNs, KindSizeAll, KindNo, KindHand, KindAppStarting, KindHelp,
            KindPen, KindPerson,
        });

        /// <summary>The themed styles, in picker order. Excludes <see cref="NativeStyle"/>.</summary>
        public static IReadOnlyList<string> Styles { get; } = Array.AsReadOnly(new[]
        {
            VisionStyle, PointStyle, BibataStyle, BreezeXStyle, MacOsStyle, FuchsiaStyle,
            NeonStyle,
        });

        /// <summary>
        /// The colourways a style offers, in picker order, or empty for a style with only one —
        /// whose stored variant is then null, which is also what a project written before
        /// colourways existed holds, so nothing needs migrating.
        /// </summary>
        public static IReadOnlyList<CursorVariant> Variants(string style)
            => style != null && VariantTable.TryGetValue(style, out var variants)
                ? variants
                : Array.Empty<CursorVariant>();

        /// <summary>
        /// The variant id a (style, variant) pair actually resolves to: the stored one when the
        /// style offers it, else the style's first — an unrecognised colourway degrades to the
        /// style's default the way an unrecognised style degrades to the default style. Null when
        /// the style has no colourways to choose between (or is not a themed style at all).
        /// </summary>
        public static string ResolveVariant(string style, string variant)
        {
            var variants = Variants(style);
            if (variants.Count == 0)
                return null;
            foreach (var candidate in variants)
            {
                if (string.Equals(candidate.Id, variant, StringComparison.OrdinalIgnoreCase))
                    return candidate.Id;
            }
            return variants[0].Id;
        }

        /// <summary>The glyph for a (style, kind) pair in the style's default colourway; see the
        /// three-argument overload.</summary>
        public static CursorGlyph TryGet(string style, string kind) => TryGet(style, null, kind);

        /// <summary>
        /// The glyph for a (style, colourway, kind) triple, or null when it has no artwork — an
        /// unknown or <c>native</c> style, an unknown kind, or a kind the style does not cover. A
        /// caller that gets null for a kind should retry with <see cref="KindArrow"/>. An
        /// unrecognised <paramref name="variant"/> is not a miss: it resolves to the style's
        /// default (see <see cref="ResolveVariant"/>).
        /// </summary>
        public static CursorGlyph TryGet(string style, string variant, string kind)
        {
            if (string.IsNullOrEmpty(style) || string.IsNullOrEmpty(kind))
                return null;
            return Table.TryGetValue(Key(style, ResolveVariant(style, variant), kind), out var glyph)
                ? glyph
                : null;
        }

        /// <summary>The table key: a style with one colourway is keyed without a variant segment,
        /// so giving a style colourways later is a table change and not a lookup change.</summary>
        private static string Key(string style, string variant, string kind)
            => variant == null ? style + "/" + kind : style + "/" + variant + "/" + kind;

        // ------------------------------------------------------------------------ ful1e5 packs

        /// <summary>One colourway of an SVG-sourced pack: the picker's two names for it, the
        /// artwork folder it draws (only <see cref="BibataStyle"/> has more than one), and the
        /// recolouring that turns that folder's stored colours into this theme's.</summary>
        private sealed class PackTheme
        {
            internal PackTheme(string id, string label, string folder,
                params (uint From, uint To)[] palette)
            {
                Id = id;
                Label = label;
                Folder = folder;
                Palette = new CursorPackPalette(palette);
            }

            internal string Id { get; }

            internal string Label { get; }

            internal string Folder { get; }

            internal CursorPackPalette Palette { get; }
        }

        /// <summary>A ful1e5 pack as this table sees it: its colourways, how long each animation
        /// frame shows, and the hotspots its build config overrides — everything else sits at the
        /// config's own fallback, the centre of the 256-unit box.</summary>
        private sealed class Pack
        {
            internal Pack(string style, float frameMs, (string Kind, float X, float Y)[] hotspots,
                PackTheme[] themes, float periodMs = 0)
            {
                Style = style;
                FrameMs = frameMs;
                PeriodMs = periodMs;
                Themes = themes;
                Hotspots = new Dictionary<string, SKPoint>(StringComparer.OrdinalIgnoreCase);
                foreach (var (kind, x, y) in hotspots)
                    Hotspots[kind] = new SKPoint(x, y);
            }

            internal string Style { get; }

            internal float FrameMs { get; }

            /// <summary>How long one loop of a pack whose animation has to be sampled runs, in
            /// milliseconds; zero for a pack that draws its frames and needs no sampling.</summary>
            internal float PeriodMs { get; }

            internal PackTheme[] Themes { get; }

            internal Dictionary<string, SKPoint> Hotspots { get; }

            /// <summary>The pack's hotspot for a kind, in its own 256-unit space.</summary>
            internal SKPoint HotspotOf(string kind)
                => Hotspots.TryGetValue(kind, out var point) ? point : new SKPoint(128f, 128f);
        }

        // A ful1e5 theme names the real colour of each placeholder its pack draws with; these three
        // spell which placeholder is being named. A pack whose themes never name an accent simply
        // omits it — the accent placeholder only ever appears in Bibata's artwork.
        private static (uint, uint) Body(uint colour) => (0x00FF00, colour);

        private static (uint, uint) Outline(uint colour) => (0x0000FF, colour);

        private static (uint, uint) Accent(uint colour) => (0xFF0000, colour);

        // Neon states its colours outright rather than through placeholders, so a theme is keyed on
        // the stored folder's own four: the neon line, the pale core inside it, and the contrasting
        // pair the help badge is drawn in.
        private static (uint, uint) NeonLine(uint colour) => (0x3B6EFF, colour);

        private static (uint, uint) NeonCore(uint colour) => (0xABC1FF, colour);

        private static (uint, uint) NeonBadge(uint colour) => (0x45FF83, colour);

        private static (uint, uint) NeonBadgeCore(uint colour) => (0xAFFFCA, colour);

        // The packs' own colours, off each repository's render.json (and, for Neon, off the eight
        // folders it ships).
        private static readonly Pack[] Packs =
        {
            // Bibata is the one pack with two geometries: `-r` is its Modern (rounded edge) set and
            // `-s` its Original (sharp edge) one, and each of the three palettes is offered on both.
            new Pack(BibataStyle, 40f, new (string, float, float)[]
            {
                (KindArrow, 55f, 17f), (KindAppStarting, 55f, 17f), (KindUpArrow, 127f, 17f),
                (KindHand, 114f, 18f), (KindPerson, 114f, 18f), (KindPen, 46f, 211f),
                (KindHelp, 42f, 86f),
            }, new[]
            {
                new PackTheme("amber-r", "Amber R", "bibata-r", Body(0xFF8300), Outline(0xFFFFFF), Accent(0x001524)),
                new PackTheme("amber-s", "Amber S", "bibata-s", Body(0xFF8300), Outline(0xFFFFFF), Accent(0x001524)),
                new PackTheme("classic-r", "Classic R", "bibata-r", Body(0x000000), Outline(0xFFFFFF), Accent(0x000000)),
                new PackTheme("classic-s", "Classic S", "bibata-s", Body(0x000000), Outline(0xFFFFFF), Accent(0x000000)),
                new PackTheme("ice-r", "Ice R", "bibata-r", Body(0xFFFFFF), Outline(0x000000), Accent(0xFFFFFF)),
                new PackTheme("ice-s", "Ice S", "bibata-s", Body(0xFFFFFF), Outline(0x000000), Accent(0xFFFFFF)),
            }),
            new Pack(BreezeXStyle, 10f, new (string, float, float)[]
            {
                (KindArrow, 69f, 30f), (KindAppStarting, 69f, 30f), (KindNo, 69f, 30f),
                (KindHelp, 69f, 30f), (KindUpArrow, 128f, 30f), (KindHand, 117f, 36f),
                (KindPerson, 117f, 36f), (KindPen, 40f, 210f), (KindSizeAll, 141f, 79f),
                (KindSizeNwse, 127f, 124f), (KindSizeNesw, 126f, 125f), (KindSizeWe, 127f, 125f),
                (KindSizeNs, 126f, 125f),
            }, new[]
            {
                new PackTheme("dark", "Dark", "breezex", Body(0x4D4D4D), Outline(0xFFFFFF)),
                new PackTheme("black", "Black", "breezex", Body(0x000000), Outline(0xFFFFFF)),
                new PackTheme("light", "Light", "breezex", Body(0xFFFFFF), Outline(0x4D4D4D)),
            }),
            new Pack(MacOsStyle, 20f, new (string, float, float)[]
            {
                (KindArrow, 80f, 38f), (KindAppStarting, 56f, 17f), (KindNo, 56f, 17f),
                (KindUpArrow, 128f, 34f), (KindHand, 92f, 53f), (KindPerson, 92f, 53f),
                (KindPen, 37f, 218f), (KindHelp, 128f, 169f), (KindIBeam, 129f, 136f),
                (KindSizeAll, 139f, 86f),
            }, new[]
            {
                new PackTheme("black", "Black", "macos", Body(0x000000), Outline(0xFFFFFF)),
                new PackTheme("white", "White", "macos", Body(0xFFFFFF), Outline(0x000000)),
            }),
            new Pack(FuchsiaStyle, 35f, new (string, float, float)[]
            {
                (KindArrow, 33f, 33f), (KindAppStarting, 32f, 32f), (KindNo, 33f, 33f),
                (KindHelp, 33f, 33f), (KindHand, 33f, 33f), (KindPerson, 33f, 33f),
                (KindUpArrow, 128f, 18f), (KindPen, 55f, 203f),
            }, new[]
            {
                new PackTheme("fuchsia", "Fuchsia", "fuchsia", Body(0xE11C79), Outline(0xFFFFFF)),
                new PackTheme("pop", "Pop", "fuchsia", Body(0xF8B572), Outline(0xFFFFFF)),
                new PackTheme("red", "Red", "fuchsia", Body(0xFF0000), Outline(0xFFFFFF)),
                new PackTheme("amber", "Amber", "fuchsia", Body(0xFFA400), Outline(0xFFFFFF)),
            }),
            // Neon draws every cursor as a stack of strokes of one path — a wide dim glow, a
            // brighter bloom, a pale core — and animates its two busy cursors in the file, which is
            // why it declares a loop length: 6.4 s sampled every 100 ms, long enough to hold whole
            // numbers of both the 1.6 s pulse and (stretched a touch) the 6 s colour cycle.
            // Its hotspots ship with no config, so they are read off the drawings — the pointer's
            // tip, the fingertip, the pen's nib, the centre for everything else — and, like every
            // pack here, quoted against a 256-unit box: Neon draws on 32, so each is the point in
            // its own units times eight (the arrow's tip at (8.5, 6) is (68, 48) below).
            new Pack(NeonStyle, 100f, new (string, float, float)[]
            {
                (KindArrow, 68f, 48f), (KindHelp, 68f, 48f), (KindAppStarting, 68f, 48f),
                (KindUpArrow, 128f, 43f), (KindHand, 113.5f, 44.4f), (KindPerson, 89.4f, 32f),
                (KindPen, 40.2f, 190.2f),
            }, new[]
            {
                new PackTheme("blue", "Blue", "neon", NeonLine(0x3B6EFF), NeonCore(0xABC1FF),
                    NeonBadge(0x45FF83), NeonBadgeCore(0xAFFFCA)),
                new PackTheme("cyan", "Cyan", "neon", NeonLine(0x36FFFF), NeonCore(0xA9FFFF),
                    NeonBadge(0x45FFE0), NeonBadgeCore(0xAFFFF2)),
                new PackTheme("green", "Green", "neon", NeonLine(0x49FF3B), NeonCore(0xB1FFAB),
                    NeonBadge(0x4545FF), NeonBadgeCore(0xAFAFFF)),
                new PackTheme("yellow", "Yellow", "neon", NeonLine(0xFCFF3B), NeonCore(0xFEFFAB),
                    NeonBadge(0xFF45FF), NeonBadgeCore(0xFFAFFF)),
                new PackTheme("orange", "Orange", "neon", NeonLine(0xFFA53B), NeonCore(0xFFD8AB),
                    NeonBadge(0xFF45A2), NeonBadgeCore(0xFFAFD7)),
                new PackTheme("red", "Red", "neon", NeonLine(0xFF3B3B), NeonCore(0xFFABAB),
                    NeonBadge(0xFF4545), NeonBadgeCore(0xFFAFAF)),
                new PackTheme("pink", "Pink", "neon", NeonLine(0xFF3BBC), NeonCore(0xFFABE2),
                    NeonBadge(0xFFC145), NeonBadgeCore(0xFFE4AF)),
                new PackTheme("purple", "Purple", "neon", NeonLine(0xAE3BFF), NeonCore(0xDCABFF),
                    NeonBadge(0xA2FF45), NeonBadgeCore(0xD7FFAF)),
            }, periodMs: 6400f),
        };

        private static readonly Dictionary<string, CursorVariant[]> VariantTable = BuildVariantTable();

        private static Dictionary<string, CursorVariant[]> BuildVariantTable()
        {
            var table = new Dictionary<string, CursorVariant[]>(StringComparer.OrdinalIgnoreCase)
            {
                // Dark first: an ink glyph with a paper halo is the one that reads on arbitrary
                // video without being chosen, so it is what an unset variant draws.
                [VisionStyle] = new[]
                {
                    new CursorVariant("dark", "Dark"),
                    new CursorVariant("light", "Light"),
                },
                [PointStyle] = new[]
                {
                    new CursorVariant("dark", "Dark"),
                    new CursorVariant("light", "Light"),
                },
            };

            // A ful1e5 pack's colourways are its themes, in the order its own README lists them.
            foreach (var pack in Packs)
            {
                var variants = new CursorVariant[pack.Themes.Length];
                for (int i = 0; i < pack.Themes.Length; i++)
                    variants[i] = new CursorVariant(pack.Themes[i].Id, pack.Themes[i].Label);
                table[pack.Style] = variants;
            }
            return table;
        }

        private static readonly Dictionary<string, CursorGlyph> Table = BuildTable();

        private static CursorGlyphPath P(string pathData, uint fill, uint stroke, float strokeWidth)
            => new CursorGlyphPath(pathData, fill, stroke, strokeWidth);

        private static CursorGlyph G(float viewBox, float hotspotX, float hotspotY, params CursorGlyphPath[] paths)
            => new CursorGlyph(viewBox, hotspotX, hotspotY, paths);

        private static CursorGlyph A(float frameDurationMs, CursorGlyph[] frames)
            => new CursorGlyph(frameDurationMs, frames);

        /// <summary>An annular sector (a slice of a ring) as SVG path data, generated through
        /// <c>SKPath</c> so the stored layer is a plain path like every authored one. Angles are
        /// Skia's: degrees clockwise from 3 o'clock (y grows downward).</summary>
        private static string RingSector(float centre, float outerRadius, float innerRadius,
            float startDeg, float sweepDeg)
        {
            using var path = new SKPath();
            var outer = SKRect.Create(centre - outerRadius, centre - outerRadius,
                outerRadius * 2, outerRadius * 2);
            var inner = SKRect.Create(centre - innerRadius, centre - innerRadius,
                innerRadius * 2, innerRadius * 2);
            path.AddArc(outer, startDeg, sweepDeg);
            path.ArcTo(inner, startDeg + sweepDeg, -sweepDeg, false);
            path.Close();
            return path.ToSvgPathData();
        }

        /// <summary>Per-channel ARGB interpolation, <paramref name="t"/> ∈ [0, 1].</summary>
        private static uint Lerp(uint from, uint to, float t)
        {
            uint Channel(int shift)
            {
                int a = (int)((from >> shift) & 0xFF);
                int b = (int)((to >> shift) & 0xFF);
                return (uint)Math.Clamp((int)Math.Round(a + (b - a) * (double)t), 0, 255);
            }

            return Channel(24) << 24 | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
        }

        // ---------------------------------------------------------------------- vision palette

        /// <summary>Vision's two base colours. They trade places between the colourways: the dark
        /// one fills in <see cref="Ink"/> and haloes in <see cref="Paper"/>, the light one the
        /// other way round.</summary>
        private const uint Ink = 0xFF0C1E35;

        private const uint Paper = 0xFFFFFFFF;

        /// <summary>The "no entry" badge's red, and the accent the two animated cursors are built
        /// on. Both are the same in every colourway — the source authors them that way.</summary>
        private const uint Deny = 0xFFCF0000;

        private const uint Spin = 0xFF50CAFF;

        /// <summary>The pack's 8-, 6- and 4-unit outside strokes as the centred widths that draw
        /// the same picture.</summary>
        private const float Halo = 16f;

        private const float HaloMid = 12f;

        private const float HaloThin = 8f;

        // ----------------------------------------------------------------------- point palette

        /// <summary>Point's ink. Its paper is the shared <see cref="Paper"/>, and the two trade
        /// places between its colourways exactly as Vision's do.</summary>
        private const uint PointInk = 0xFF2F303A;

        /// <summary>The blue Point builds its link cursor and its wait ring on, and the red pair
        /// its "no entry" dot — the same in every colourway, as the source authors them.</summary>
        private const uint PointLink = 0xFF3DA6FF;

        private const uint PointDeny = 0xFFD50000;

        private const uint PointDenyEdge = 0xFF720000;

        /// <summary>Point's 3-unit outside stroke as the centred width that draws the same
        /// picture, on its smaller 64-unit viewBox.</summary>
        private const float PointHalo = 6f;

        // ------------------------------------------------------------------ animation generation

        /// <summary>Frames per animation loop and how long each shows: 18 × 60 ms ≈ one revolution
        /// (or one colour cycle) every 1.08 s, the cadence a Windows <c>.ani</c> spinner runs at.</summary>
        private const int SpinFrames = 18;

        private const float SpinFrameMs = 60f;

        /// <summary>Sweep of the orbiting highlight sector, degrees.</summary>
        private const float SpinSweep = 100f;

        // --------------------------------------------------------------------- vision artwork
        // Generated from the pack's PSD vector masks; see the class remarks. Each constant is
        // named for the Photoshop layer it came from.

        private const string VisionPointer =
            "M40 65C37.737 66.694 34.882 67.303 32 67C26 65.667 20 64.333 14 63" +
            "C12.481 62.769 11.077 62.085 10 61C8.43 59.419 7.752 57.193 8 55C8 41 8 27 8 13" +
            "C7.826 11.354 8.627 9.786 10 9C11.227 8.297 12.748 8.308 14 9" +
            "C25.333 17.667 36.667 26.333 48 35C48.925 36.194 49.605 37.549 50 39" +
            "C50.534 40.962 50.527 43.026 50 45C47.333 51 44.667 57 42 63" +
            "C41.425 63.763 40.757 64.433 40 65Z";

        private const string VisionText =
            "M64.139 25.839C64.139 25.839 64.396 25.839 64.396 25.839" +
            "C66.432 25.839 68.083 27.49 68.083 29.527C68.083 29.527 68.083 98.661 68.083 98.661" +
            "C68.083 100.697 66.432 102.348 64.396 102.348" +
            "C64.396 102.348 64.139 102.348 64.139 102.348" +
            "C62.103 102.348 60.452 100.697 60.452 98.661" +
            "C60.452 98.661 60.452 29.527 60.452 29.527C60.452 27.49 62.103 25.839 64.139 25.839Z";

        private const string VisionLink =
            "M15 42C13.598 41.882 10.661 41.97 9 44C7.406 45.948 7.233 49.13 9 52" +
            "C12.522 57.798 16.525 63.157 21 68C24.915 72.236 28.497 75.195 32.064 77.169" +
            "C38.44 80.697 45.665 82.325 53 82C56.792 81.769 60.302 80.452 63 78" +
            "C64.625 76.523 65.883 74.695 67 72C69.282 66.497 68.708 61.39 69 54" +
            "C69.134 50.602 69.352 47.114 67 44C65.561 42.094 63.485 40.773 61 40" +
            "C56.199 39.18 51.108 38.419 47 38C43.925 37.686 39.636 37.333 37 34" +
            "C35.338 31.898 34.74 29.104 35 26C35 22 35 18 35 14C35 10.708 32.292 8 29 8" +
            "C25.708 8 23 10.708 23 14C23 21.333 23 28.667 23 36C23 40 23 44 23 48" +
            "C21.404 44.811 18.511 42.296 15 42Z";

        private const string VisionAlternate =
            "M64 8C62.427 8 60.944 8.742 60 10C54.667 18 49.333 26 44 34" +
            "C43.864 34.652 43.852 35.327 44 36C44.271 37.228 45.149 38.286 46 39" +
            "C49.797 42.186 59.965 42 64 42C68.035 42 78.203 42.186 82 39" +
            "C82.851 38.286 83.729 37.228 84 36C84.148 35.327 84.136 34.652 84 34" +
            "C78.667 26 73.333 18 68 10C67.056 8.742 65.573 8 64 8Z";

        private const string VisionBusy =
            "M64 33.75C80.707 33.75 94.25 47.293 94.25 64C94.25 80.707 80.707 94.25 64 94.25" +
            "C47.293 94.25 33.75 80.707 33.75 64C33.75 47.293 47.293 33.75 64 33.75ZM64 44.062" +
            "C52.989 44.062 44.062 52.989 44.062 64C44.062 75.011 52.989 83.938 64 83.938" +
            "C75.011 83.938 83.938 75.011 83.938 64C83.938 52.989 75.011 44.062 64 44.062Z";

        private const string VisionWork =
            "M40 65C37.737 66.694 34.882 67.303 32 67C26 65.667 20 64.333 14 63" +
            "C12.481 62.769 11.077 62.085 10 61C8.43 59.419 7.752 57.193 8 55C8 41 8 27 8 13" +
            "C7.826 11.354 8.627 9.786 10 9C11.227 8.297 12.748 8.308 14 9" +
            "C25.333 17.667 36.667 26.333 48 35C48.925 36.194 49.605 37.549 50 39" +
            "C50.534 40.962 50.527 43.026 50 45C47.333 51 44.667 57 42 63" +
            "C41.425 63.763 40.757 64.433 40 65Z";

        private const string VisionCrossTop =
            "M64.062 30.333C65.614 30.314 67.068 29.571 68 28.333" +
            "C70.667 24.333 73.333 20.333 76 16.333C76.181 15.653 76.204 14.975 76 14.333" +
            "C74.917 10.92 68.082 10.388 66 10.333C65.656 10.324 64 10.333 64 10.333" +
            "C64 10.333 62.344 10.324 62 10.333C59.918 10.388 53.083 10.92 52 14.333" +
            "C51.796 14.975 51.819 15.653 52 16.333C54.667 20.333 57.333 24.333 60 28.333" +
            "C60.958 29.605 62.466 30.353 64.062 30.333Z";

        private const string VisionCrossBottom =
            "M63.938 97.667C62.386 97.686 60.932 98.429 60 99.667" +
            "C57.333 103.667 54.667 107.667 52 111.667C51.819 112.347 51.796 113.025 52 113.667" +
            "C53.083 117.08 59.918 117.612 62 117.667C62.344 117.676 64 117.667 64 117.667" +
            "C64 117.667 65.656 117.676 66 117.667C68.082 117.612 74.917 117.08 76 113.667" +
            "C76.204 113.025 76.181 112.347 76 111.667C73.333 107.667 70.667 103.667 68 99.667" +
            "C67.042 98.395 65.534 97.647 63.938 97.667Z";

        private const string VisionCrossRight =
            "M97.667 64.062C97.686 65.614 98.429 67.068 99.667 68" +
            "C103.667 70.667 107.667 73.333 111.667 76C112.347 76.181 113.025 76.204 113.667 76" +
            "C117.08 74.917 117.612 68.082 117.667 66C117.676 65.656 117.667 64 117.667 64" +
            "C117.667 64 117.676 62.344 117.667 62C117.612 59.918 117.08 53.083 113.667 52" +
            "C113.025 51.796 112.347 51.819 111.667 52C107.667 54.667 103.667 57.333 99.667 60" +
            "C98.395 60.958 97.647 62.466 97.667 64.062Z";

        private const string VisionCrossLeft =
            "M30.333 63.938C30.314 62.386 29.571 60.932 28.333 60" +
            "C24.333 57.333 20.333 54.667 16.333 52C15.653 51.819 14.975 51.796 14.333 52" +
            "C10.92 53.083 10.388 59.918 10.333 62C10.324 62.344 10.333 64 10.333 64" +
            "C10.333 64 10.324 65.656 10.333 66C10.388 68.082 10.92 74.917 14.333 76" +
            "C14.975 76.204 15.653 76.181 16.333 76C20.333 73.333 24.333 70.667 28.333 68" +
            "C29.605 67.042 30.353 65.534 30.333 63.938Z";

        private const string VisionCapTop =
            "M63.938 9.667C62.386 9.686 60.932 10.429 60 11.667" +
            "C57.333 15.667 54.667 19.667 52 23.667C51.819 24.347 51.796 25.025 52 25.667" +
            "C53.083 29.08 59.918 29.612 62 29.667C62.344 29.676 64 29.667 64 29.667" +
            "C64 29.667 65.656 29.676 66 29.667C68.082 29.612 74.917 29.08 76 25.667" +
            "C76.204 25.025 76.181 24.347 76 23.667C73.333 19.667 70.667 15.667 68 11.667" +
            "C67.042 10.395 65.534 9.647 63.938 9.667Z";

        private const string VisionCapBottom =
            "M64.062 118.333C65.614 118.314 67.068 117.571 68 116.333" +
            "C70.667 112.333 73.333 108.333 76 104.333C76.181 103.653 76.204 102.975 76 102.333" +
            "C74.917 98.92 68.082 98.388 66 98.333C65.656 98.324 64 98.333 64 98.333" +
            "C64 98.333 62.344 98.324 62 98.333C59.918 98.388 53.083 98.92 52 102.333" +
            "C51.796 102.975 51.819 103.653 52 104.333C54.667 108.333 57.333 112.333 60 116.333" +
            "C60.958 117.605 62.466 118.353 64.062 118.333Z";

        private const string VisionCapLeft =
            "M9.667 64.062C9.686 65.614 10.429 67.068 11.667 68" +
            "C15.667 70.667 19.667 73.333 23.667 76C24.347 76.181 25.025 76.204 25.667 76" +
            "C29.08 74.917 29.612 68.082 29.667 66C29.676 65.656 29.667 64 29.667 64" +
            "C29.667 64 29.676 62.344 29.667 62C29.612 59.918 29.08 53.083 25.667 52" +
            "C25.025 51.796 24.347 51.819 23.667 52C19.667 54.667 15.667 57.333 11.667 60" +
            "C10.395 60.958 9.647 62.466 9.667 64.062Z";

        private const string VisionCapRight =
            "M118.333 63.938C118.314 62.386 117.571 60.932 116.333 60" +
            "C112.333 57.333 108.333 54.667 104.333 52C103.653 51.819 102.975 51.796 102.333 52" +
            "C98.92 53.083 98.388 59.918 98.333 62C98.324 62.344 98.333 64 98.333 64" +
            "C98.333 64 98.324 65.656 98.333 66C98.388 68.082 98.92 74.917 102.333 76" +
            "C102.975 76.204 103.653 76.181 104.333 76C108.333 73.333 112.333 70.667 116.333 68" +
            "C117.605 67.042 118.353 65.534 118.333 63.938Z";

        private const string VisionCapNorthEast =
            "M101.809 25.103C100.865 24.184 99.694 24 99.083 23.917" +
            "C90.779 22.785 84.477 24.077 83.083 25.917C82.57 26.594 82.237 27.294 82.054 27.976" +
            "C81.438 30.264 82.247 33.609 86.297 37.875C86.534 38.125 87.711 39.289 87.711 39.289" +
            "C87.711 39.289 88.875 40.466 89.125 40.703C93.391 44.753 96.736 45.562 99.024 44.946" +
            "C99.701 44.764 100.401 44.433 101.083 43.917" +
            "C102.994 42.471 104.236 35.732 103.083 27.917" +
            "C102.982 27.232 102.77 26.039 101.809 25.103Z";

        private const string VisionCapSouthWest =
            "M25.941 102.147C26.885 103.066 28.056 103.25 28.667 103.333" +
            "C36.971 104.465 43.273 103.173 44.667 101.333" +
            "C45.18 100.656 45.513 99.956 45.696 99.274C46.312 96.986 45.503 93.641 41.453 89.375" +
            "C41.216 89.125 40.039 87.961 40.039 87.961C40.039 87.961 38.875 86.784 38.625 86.547" +
            "C34.359 82.497 31.015 81.688 28.726 82.304C28.049 82.486 27.349 82.817 26.667 83.333" +
            "C24.756 84.779 23.514 91.518 24.667 99.333" +
            "C24.768 100.018 24.981 101.211 25.941 102.147Z";

        private const string VisionCapNorthWest =
            "M26.191 25.103C27.135 24.184 28.306 24 28.917 23.917" +
            "C37.221 22.785 43.523 24.077 44.917 25.917C45.43 26.594 45.763 27.294 45.946 27.976" +
            "C46.562 30.264 45.753 33.609 41.703 37.875C41.466 38.125 40.289 39.289 40.289 39.289" +
            "C40.289 39.289 39.125 40.466 38.875 40.703C34.609 44.753 31.264 45.562 28.976 44.946" +
            "C28.299 44.764 27.599 44.433 26.917 43.917C25.006 42.471 23.764 35.732 24.917 27.917" +
            "C25.018 27.232 25.23 26.039 26.191 25.103Z";

        private const string VisionCapSouthEast =
            "M102.059 102.147C101.115 103.066 99.944 103.25 99.333 103.333" +
            "C91.029 104.465 84.727 103.173 83.333 101.333" +
            "C82.82 100.656 82.487 99.956 82.304 99.274C81.688 96.986 82.497 93.641 86.547 89.375" +
            "C86.784 89.125 87.961 87.961 87.961 87.961C87.961 87.961 89.125 86.784 89.375 86.547" +
            "C93.641 82.497 96.985 81.688 99.274 82.304" +
            "C99.951 82.486 100.651 82.817 101.333 83.333" +
            "C103.244 84.779 104.486 91.518 103.333 99.333" +
            "C103.232 100.018 103.019 101.211 102.059 102.147Z";

        private const string VisionDenyBadge =
            "M49.565 15.565C56.985 8.145 69.015 8.145 76.435 15.565" +
            "C83.855 22.985 83.855 35.015 76.435 42.435C69.015 49.855 56.985 49.855 49.565 42.435" +
            "C42.145 35.015 42.145 22.985 49.565 15.565ZM53.696 19.696" +
            "C48.557 24.834 48.557 33.166 53.696 38.304C58.834 43.443 67.166 43.443 72.304 38.304" +
            "C77.443 33.166 77.443 24.834 72.304 19.696C67.166 14.557 58.834 14.557 53.696 19.696" +
            "ZM49.652 19.895C49.652 19.895 53.895 15.652 53.895 15.652" +
            "C53.895 15.652 75.573 37.331 75.573 37.331C75.573 37.331 71.331 41.573 71.331 41.573" +
            "C71.331 41.573 49.652 19.895 49.652 19.895Z";

        private const string VisionPersonBadge =
            "M33.008 60.234C28.758 60.234 25.448 63.84 25.415 67.828" +
            "C25.385 71.31 27.864 74.518 31.49 75.422C28.693 75.833 26.109 76.848 23.896 78.459" +
            "C22.266 79.646 16.48 84.358 17.821 89.091C18.46 91.35 21.656 93.803 28.452 95.166" +
            "C31.481 95.773 34.536 95.773 37.565 95.166C44.361 93.803 47.556 91.35 48.196 89.091" +
            "C49.537 84.358 43.751 79.646 42.121 78.459C39.908 76.848 37.324 75.833 34.527 75.422" +
            "C38.153 74.518 40.632 71.31 40.602 67.828C40.569 63.84 37.259 60.234 33.008 60.234Z";

        private const string VisionPen =
            "M52 40C51.037 38.837 49.121 36.698 46 36C44.484 35.661 40.747 35.253 38 38" +
            "C35.253 40.747 35.661 44.484 36 46C36.698 49.121 38.837 51.037 40 52" +
            "C46.68 57.532 69.338 73.686 84 84C73.686 69.338 57.532 46.68 52 40ZM12 24" +
            "C13.42 25.893 15.432 27.254 17.75 27.75C19.298 28.081 23.037 28.463 25.75 25.75" +
            "C28.463 23.037 28.081 19.298 27.75 17.75C27.254 15.432 25.893 13.42 24 12" +
            "C18 10 12 8 6 6C8 12 10 18 12 24Z";

        private const string VisionHelpBadge =
            "M65.25 46.78C68.66 45.03 68.8 40.61 65.5 38.84C61.23 36.56 56.82 42 60.14 45.45" +
            "C61.68 47.06 63.69 47.58 65.25 46.78ZM65.21 36.76C65.96 36.42 66.74 35.69 67.08 35" +
            "C67.24 34.68 67.47 34.3 67.58 34.17C67.7 34.03 67.88 33.65 67.98 33.32" +
            "C68.09 32.99 68.36 32.54 68.59 32.33C68.81 32.12 69 31.86 69 31.75" +
            "C69 31.65 69.24 31.33 69.54 31.04C69.84 30.76 70.27 30.29 70.5 30" +
            "C70.73 29.72 71.14 29.24 71.42 28.93C71.87 28.43 72.21 27.93 72.78 26.93" +
            "C74.11 24.6 74.11 20.88 72.78 18.57C72.67 18.38 72.43 17.97 72.25 17.66" +
            "C71.73 16.78 70.17 15.33 69.33 14.97C68.92 14.79 68.47 14.54 68.33 14.42" +
            "C66.96 13.21 60.15 13.3 58.17 14.55C57.85 14.76 57.46 15 57.32 15.09" +
            "C53.67 17.31 51.93 21.69 53.76 24.03C55.39 26.13 58.94 25.73 59.95 23.33" +
            "C61.08 20.68 62.99 19.66 64.77 20.76C66.94 22.11 66.15 24.91 62.68 28.17" +
            "C62.53 28.3 62.22 28.68 62 29C61.77 29.32 61.49 29.69 61.37 29.83" +
            "C58.03 33.63 60.86 38.72 65.21 36.76Z";

        // ---------------------------------------------------------------------- point artwork
        // Generated from the pack's PSD vector masks; see the class remarks. Each constant is
        // named for the Photoshop layer it came from.

        private const string PointDot =
            "M32 27C34.761 27 37 29.239 37 32C37 34.761 34.761 37 32 37C29.239 37 27 34.761 27 32" +
            "C27 29.239 29.239 27 32 27Z";

        private const string PointBusyRing =
            "M32 13.98C41.952 13.98 50.02 22.048 50.02 32C50.02 41.952 41.952 50.02 32 50.02" +
            "C22.048 50.02 13.98 41.952 13.98 32C13.98 22.048 22.048 13.98 32 13.98ZM32 20.112" +
            "C25.435 20.112 20.112 25.435 20.112 32C20.112 38.565 25.435 43.888 32 43.888" +
            "C38.565 43.888 43.888 38.565 43.888 32C43.888 25.435 38.565 20.112 32 20.112Z";

        private const string PointCrossTop =
            "M32 18C33.667 15.333 35.333 12.667 37 10C35.415 10.66 33.717 11 32 11" +
            "C30.283 11 28.585 10.66 27 10C28.667 12.667 30.333 15.333 32 18Z";

        private const string PointCrossBottom =
            "M32 46C33.667 48.667 35.333 51.333 37 54C35.415 53.34 33.717 53 32 53" +
            "C30.283 53 28.585 53.34 27 54C28.667 51.333 30.333 48.667 32 46Z";

        private const string PointCrossLeft =
            "M18 32C15.333 30.333 12.667 28.667 10 27C10.66 28.585 11 30.283 11 32" +
            "C11 33.717 10.66 35.415 10 37C12.667 35.333 15.333 33.667 18 32Z";

        private const string PointCrossRight =
            "M46 32C48.667 30.333 51.333 28.667 54 27C53.34 28.585 53 30.283 53 32" +
            "C53 33.717 53.34 35.415 54 37C51.333 35.333 48.667 33.667 46 32Z";

        private const string PointCapTop =
            "M32 10C33.667 12.667 35.333 15.333 37 18C35.415 17.34 33.717 17 32 17" +
            "C30.283 17 28.585 17.34 27 18C28.667 15.333 30.333 12.667 32 10Z";

        private const string PointCapBottom =
            "M32 54C33.667 51.333 35.333 48.667 37 46C35.415 46.66 33.717 47 32 47" +
            "C30.283 47 28.585 46.66 27 46C28.667 48.667 30.333 51.333 32 54Z";

        private const string PointCapLeft =
            "M10 32C12.667 30.333 15.333 28.667 18 27C17.34 28.585 17 30.283 17 32" +
            "C17 33.717 17.34 35.415 18 37C15.333 35.333 12.667 33.667 10 32Z";

        private const string PointCapRight =
            "M54 32C51.333 30.333 48.667 28.667 46 27C46.66 28.585 47 30.283 47 32" +
            "C47 33.717 46.66 35.415 46 37C48.667 35.333 51.333 33.667 54 32Z";

        private const string PointCapNorthWest =
            "M16.444 16.444C19.508 17.151 22.572 17.858 25.636 18.565" +
            "C24.049 19.219 22.607 20.179 21.393 21.393C20.179 22.607 19.219 24.049 18.565 25.636" +
            "C17.858 22.572 17.151 19.508 16.444 16.444Z";

        private const string PointCapSouthEast =
            "M48.556 48.556C47.849 45.492 47.142 42.428 46.435 39.364" +
            "C45.781 40.951 44.821 42.393 43.607 43.607C42.393 44.821 40.951 45.781 39.364 46.435" +
            "C42.428 47.142 45.492 47.849 48.556 48.556Z";

        private const string PointCapNorthEast =
            "M47.556 16.444C44.492 17.151 41.428 17.858 38.364 18.565" +
            "C39.951 19.219 41.393 20.179 42.607 21.393C43.821 22.607 44.781 24.049 45.435 25.636" +
            "C46.142 22.572 46.849 19.508 47.556 16.444Z";

        private const string PointCapSouthWest =
            "M15.444 48.556C16.151 45.492 16.858 42.428 17.565 39.364" +
            "C18.219 40.951 19.179 42.393 20.393 43.607C21.607 44.821 23.049 45.781 24.636 46.435" +
            "C21.572 47.142 18.508 47.849 15.444 48.556Z";

        private const string PointPerson =
            "M32 27C29.201 27 27.022 29.374 27 32C26.981 34.293 28.613 36.405 31 37" +
            "C29.158 37.271 27.457 37.939 26 39C24.927 39.781 21.118 42.884 22 46" +
            "C22.421 47.487 24.525 49.103 29 50C30.994 50.4 33.006 50.4 35 50" +
            "C39.475 49.103 41.579 47.487 42 46C42.882 42.884 39.073 39.781 38 39" +
            "C36.543 37.939 34.842 37.271 33 37C35.387 36.405 37.019 34.293 37 32" +
            "C36.978 29.374 34.799 27 32 27Z";

        private const string PointPen =
            "M35 29C34.518 28.418 33.56 27.349 32 27C31.242 26.831 29.374 26.626 28 28" +
            "C26.626 29.374 26.831 31.242 27 32C27.349 33.56 28.418 34.518 29 35" +
            "C32.34 37.766 43.669 45.843 51 51C45.843 43.669 37.766 32.34 35 29Z";

        private const string PointHelpBadge =
            "M33.71 31.5C33.92 31.33 34.27 31.09 34.48 30.98C34.69 30.86 34.98 30.56 35.12 30.29" +
            "C35.26 30.03 35.5 29.66 35.64 29.48C35.84 29.23 35.92 28.92 35.97 28.23" +
            "C36.03 27.27 36.5 26.58 37.45 26.09C38.31 25.63 41.12 22.75 41.12 22.32" +
            "C41.12 22.23 41.29 21.95 41.49 21.7C41.69 21.44 41.9 21.06 41.94 20.84" +
            "C41.99 20.62 42.23 19.99 42.48 19.44C43.15 17.96 43.18 14.76 42.53 13.52" +
            "C42.31 13.1 42.07 12.49 42 12.17C41.67 10.74 39.23 7.72 37.75 6.94" +
            "C37.44 6.78 37.09 6.53 36.97 6.39C36.85 6.25 36.6 6.1 36.41 6.06" +
            "C36.22 6.02 35.72 5.87 35.31 5.73C33.21 5.02 32.91 4.98 31.13 5.05" +
            "C29.63 5.12 29.37 5.16 28.69 5.48C28.27 5.68 27.63 5.91 27.27 5.99" +
            "C26.88 6.09 26.41 6.32 26.11 6.58C25.83 6.81 25.53 7 25.43 7" +
            "C25.2 7 24.19 7.9 23.4 8.8C23.04 9.22 22.61 9.7 22.44 9.88" +
            "C22.27 10.05 22.05 10.44 21.94 10.75C21.83 11.05 21.63 11.42 21.5 11.56" +
            "C21.02 12.08 20.8 13.95 21.12 14.8C21.29 15.25 22.46 16.66 22.82 16.85" +
            "C23.92 17.42 24.7 17.53 25.78 17.24C27.14 16.87 27.59 16.47 28.88 14.48" +
            "C29.72 13.17 30.96 12.73 32.69 13.12C33.63 13.32 34.81 14.54 34.94 15.43" +
            "C35.32 17.96 34.13 19.39 31.2 19.92C29.88 20.15 29.33 20.58 28.53 22" +
            "C27.93 23.05 27.79 28.76 28.34 29.46C28.5 29.65 28.74 30.05 28.88 30.33" +
            "C29.06 30.68 29.27 30.9 29.54 31.01C29.76 31.09 30.08 31.29 30.25 31.44" +
            "C30.89 32.01 33.04 32.05 33.71 31.5ZM32.8 17.8C32.95 17.65 33 17.34 33 16.5" +
            "C33 15.66 32.95 15.35 32.8 15.2C32.65 15.05 32.34 15 31.5 15" +
            "C30.05 15 30 15.05 30 16.5C30 17.34 30.05 17.65 30.2 17.8" +
            "C30.35 17.95 30.66 18 31.5 18C32.34 18 32.65 17.95 32.8 17.8Z";

        private const string PointHelpDot =
            "M32 37C34.761 37 37 39.239 37 42C37 44.761 34.761 47 32 47C29.239 47 27 44.761 27 42" +
            "C27 39.239 29.239 37 32 37Z";

        private static Dictionary<string, CursorGlyph> BuildTable()
        {
            var t = new Dictionary<string, CursorGlyph>(StringComparer.OrdinalIgnoreCase);

            // One geometry, two palettes: `ink`/`paper` swap between the colourways, while a layer
            // naming a palette constant outright (Deny, Spin, Paper) looks the same in both.
            foreach (var (variant, ink, paper) in new[]
            {
                ("dark", Ink, Paper),
                ("light", Paper, Ink),
            })
            {
                t[Key(VisionStyle, variant, KindArrow)] = G(128f, 8f, 8f,
                    P(VisionPointer, ink, paper, Halo));
                t[Key(VisionStyle, variant, KindIBeam)] = G(128f, 64f, 64f,
                    P(VisionText, ink, paper, HaloThin));
                t[Key(VisionStyle, variant, KindHand)] = G(128f, 28f, 8f,
                    P(VisionLink, ink, paper, Halo));
                t[Key(VisionStyle, variant, KindUpArrow)] = G(128f, 64f, 8f,
                    P(VisionAlternate, ink, paper, Halo));
                // wait: the source's rotating gradient, faked as a paper sector orbiting the ring.
                // The ring layer is shared across frames so path caches key it once; the radii are
                // VisionBusy's own (centre 64, ring 19.938..30.25), so the sector sits flush.
                var visionRing = P(VisionBusy, Spin, Ink, Halo);
                var visionWaitFrames = new CursorGlyph[SpinFrames];
                for (int i = 0; i < SpinFrames; i++)
                {
                    visionWaitFrames[i] = G(128f, 64f, 64f, visionRing,
                        P(RingSector(64f, 30.25f, 19.938f, -90f + i * 360f / SpinFrames, SpinSweep),
                            Paper, Ink, HaloThin));
                }
                t[Key(VisionStyle, variant, KindWait)] = A(SpinFrameMs, visionWaitFrames);

                // appstarting: the pointer cannot rotate, so the source's sweeping gradient becomes
                // a fill pulsing towards the accent and back. Frame 0 is the old static colour.
                var visionWorkFrames = new CursorGlyph[SpinFrames];
                for (int i = 0; i < SpinFrames; i++)
                {
                    float mix = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / SpinFrames);
                    visionWorkFrames[i] = G(128f, 8f, 8f,
                        P(VisionWork, Lerp(Paper, Spin, mix), Spin, Halo));
                }
                t[Key(VisionStyle, variant, KindAppStarting)] = A(SpinFrameMs, visionWorkFrames);
                t[Key(VisionStyle, variant, KindCross)] = G(128f, 64f, 64f,
                    P(VisionCrossTop, ink, paper, Halo), P(VisionCrossBottom, ink, paper, Halo),
                    P(VisionCrossRight, ink, paper, Halo), P(VisionCrossLeft, ink, paper, Halo));
                t[Key(VisionStyle, variant, KindSizeNs)] = G(128f, 64f, 64f,
                    P(VisionCapTop, ink, paper, Halo), P(VisionCapBottom, ink, paper, Halo));
                t[Key(VisionStyle, variant, KindSizeWe)] = G(128f, 64f, 64f,
                    P(VisionCapLeft, ink, paper, Halo), P(VisionCapRight, ink, paper, Halo));
                t[Key(VisionStyle, variant, KindSizeAll)] = G(128f, 64f, 64f,
                    P(VisionCapTop, ink, paper, Halo), P(VisionCapBottom, ink, paper, Halo),
                    P(VisionCapLeft, ink, paper, Halo), P(VisionCapRight, ink, paper, Halo));
                t[Key(VisionStyle, variant, KindSizeNesw)] = G(128f, 64f, 64f,
                    P(VisionCapNorthEast, ink, paper, Halo),
                    P(VisionCapSouthWest, ink, paper, Halo));
                t[Key(VisionStyle, variant, KindSizeNwse)] = G(128f, 64f, 64f,
                    P(VisionCapNorthWest, ink, paper, Halo),
                    P(VisionCapSouthEast, ink, paper, Halo));
                t[Key(VisionStyle, variant, KindNo)] = G(128f, 8f, 8f,
                    P(VisionPointer, ink, paper, Halo), P(VisionDenyBadge, Deny, paper, HaloMid));
                t[Key(VisionStyle, variant, KindPerson)] = G(128f, 28f, 8f,
                    P(VisionLink, ink, paper, Halo), P(VisionPersonBadge, ink, paper, Halo));
                t[Key(VisionStyle, variant, KindPen)] = G(128f, 8f, 8f,
                    P(VisionPen, ink, paper, HaloMid));
                t[Key(VisionStyle, variant, KindHelp)] = G(128f, 8f, 8f,
                    P(VisionPointer, ink, paper, Halo), P(VisionHelpBadge, ink, paper, Halo));
            }

            // Point is the same two-colourway arrangement on a 64-unit viewBox.
            foreach (var (variant, ink, paper) in new[]
            {
                ("dark", PointInk, Paper),
                ("light", Paper, PointInk),
            })
            {
                t[Key(PointStyle, variant, KindArrow)] = G(64f, 32f, 32f,
                    P(PointDot, ink, paper, PointHalo));
                t[Key(PointStyle, variant, KindUpArrow)] = G(64f, 32f, 32f,
                    P(PointDot, paper, ink, PointHalo));
                t[Key(PointStyle, variant, KindHand)] = G(64f, 32f, 32f,
                    P(PointDot, PointLink, paper, PointHalo));
                t[Key(PointStyle, variant, KindNo)] = G(64f, 32f, 32f,
                    P(PointDot, PointDeny, PointDenyEdge, PointHalo));
                // appstarting: the source's own animation — the dot cycling between the
                // colourway's base colour and the link blue. Frame 0 is the cycle's midpoint,
                // which is (near) the tint the old static table showed.
                var pointWorkFrames = new CursorGlyph[SpinFrames];
                for (int i = 0; i < SpinFrames; i++)
                {
                    float mix = 0.5f + 0.5f * MathF.Sin(2f * MathF.PI * i / SpinFrames);
                    pointWorkFrames[i] = G(64f, 32f, 32f,
                        P(PointDot, Lerp(ink, PointLink, mix), paper, PointHalo));
                }
                t[Key(PointStyle, variant, KindAppStarting)] = A(SpinFrameMs, pointWorkFrames);

                // wait: the same orbiting-sector fake as Vision's, on PointBusyRing's radii
                // (centre 32, ring 11.888..18.02); ring and dot layers shared across frames. The
                // sector's halo is half width — the ring is barely thicker than a full halo, which
                // would read as a black crescent rather than an outlined highlight.
                var pointRing = P(PointBusyRing, PointLink, paper, PointHalo);
                var pointWaitDot = P(PointDot, ink, paper, PointHalo);
                var pointWaitFrames = new CursorGlyph[SpinFrames];
                for (int i = 0; i < SpinFrames; i++)
                {
                    pointWaitFrames[i] = G(64f, 32f, 32f, pointRing,
                        P(RingSector(32f, 18.02f, 11.888f, -90f + i * 360f / SpinFrames, SpinSweep),
                            paper, ink, PointHalo / 2f),
                        pointWaitDot);
                }
                t[Key(PointStyle, variant, KindWait)] = A(SpinFrameMs, pointWaitFrames);
                t[Key(PointStyle, variant, KindIBeam)] = G(64f, 32f, 32f,
                    P(PointCrossTop, ink, paper, PointHalo),
                    P(PointCrossBottom, ink, paper, PointHalo));
                t[Key(PointStyle, variant, KindCross)] = G(64f, 32f, 32f,
                    P(PointCrossTop, ink, paper, PointHalo),
                    P(PointCrossBottom, ink, paper, PointHalo),
                    P(PointCrossLeft, ink, paper, PointHalo),
                    P(PointCrossRight, ink, paper, PointHalo));
                t[Key(PointStyle, variant, KindSizeNs)] = G(64f, 32f, 32f,
                    P(PointCapTop, ink, paper, PointHalo),
                    P(PointCapBottom, ink, paper, PointHalo), P(PointDot, ink, paper, PointHalo));
                t[Key(PointStyle, variant, KindSizeWe)] = G(64f, 32f, 32f,
                    P(PointCapLeft, ink, paper, PointHalo),
                    P(PointCapRight, ink, paper, PointHalo), P(PointDot, ink, paper, PointHalo));
                t[Key(PointStyle, variant, KindSizeAll)] = G(64f, 32f, 32f,
                    P(PointCapTop, ink, paper, PointHalo),
                    P(PointCapBottom, ink, paper, PointHalo),
                    P(PointCapLeft, ink, paper, PointHalo),
                    P(PointCapRight, ink, paper, PointHalo), P(PointDot, ink, paper, PointHalo));
                t[Key(PointStyle, variant, KindSizeNwse)] = G(64f, 32f, 32f,
                    P(PointCapNorthWest, ink, paper, PointHalo),
                    P(PointCapSouthEast, ink, paper, PointHalo),
                    P(PointDot, ink, paper, PointHalo));
                t[Key(PointStyle, variant, KindSizeNesw)] = G(64f, 32f, 32f,
                    P(PointCapNorthEast, ink, paper, PointHalo),
                    P(PointCapSouthWest, ink, paper, PointHalo),
                    P(PointDot, ink, paper, PointHalo));
                t[Key(PointStyle, variant, KindPerson)] = G(64f, 32f, 32f,
                    P(PointPerson, ink, paper, PointHalo));
                t[Key(PointStyle, variant, KindPen)] = G(64f, 32f, 32f,
                    P(PointPen, ink, paper, PointHalo));
                t[Key(PointStyle, variant, KindHelp)] = G(64f, 32f, 42f,
                    P(PointHelpBadge, ink, paper, PointHalo),
                    P(PointHelpDot, ink, paper, PointHalo));
            }

            // The ful1e5 packs, straight off their own SVG sources — one lookup per (theme, kind),
            // with `wait` and `appstarting` reading the frame folders the packs ship instead of a
            // single file. A kind a pack somehow does not cover is left out rather than faked; the
            // caller falls back to the style's arrow.
            foreach (var pack in Packs)
            {
                foreach (var theme in pack.Themes)
                {
                    foreach (var kind in Kinds)
                    {
                        var hotspot = pack.HotspotOf(kind);
                        var glyph = kind is KindWait or KindAppStarting
                            ? CursorPackLoader.LoadAnimated(theme.Folder, kind, theme.Palette,
                                hotspot.X, hotspot.Y, pack.FrameMs, pack.PeriodMs)
                            : CursorPackLoader.LoadStatic(theme.Folder, kind, theme.Palette,
                                hotspot.X, hotspot.Y);
                        if (glyph != null)
                            t[Key(pack.Style, theme.Id, kind)] = glyph;
                    }
                }
            }

            return t;
        }
    }
}
