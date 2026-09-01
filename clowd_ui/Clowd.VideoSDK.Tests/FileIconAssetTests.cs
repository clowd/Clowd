using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Clowd.UI.Preview;
using Clowd.UI.Preview.Icons;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The covering test for the shipped file-type icon set and the lettered page generated beside
    /// it. Clowd.Ui has no test project of its own, so the asset-integrity checks live here — this
    /// project already references Clowd.Ui for the timeline math suites, which is what makes
    /// <see cref="MiniSvg"/>, <see cref="FileIconCatalog"/> and <see cref="FileIconRenderer"/>
    /// reachable at all.
    ///
    /// <para>
    /// What each group is actually guarding:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>Parse and ink.</b> <see cref="MiniSvg"/> is a deliberately closed
    ///   subset that throws on anything it does not implement, precisely so an icon it cannot draw
    ///   fails the build instead of shipping as a blank square. That contract is worth nothing
    ///   unless something parses all 77 — and parsing is only half of it, because a document that
    ///   parses to zero shapes throws nothing and rasterizes to nothing.</description></item>
    ///   <item><description><b>Catalog / files / SOURCES.tsv agreement.</b> Three lists of the same
    ///   77 names, maintained by hand in three places. Nothing at runtime notices when they
    ///   disagree: a catalog slug with no asset degrades silently to the blank file page, and an
    ///   asset with no catalog entry is dead weight carried in the exe.</description></item>
    ///   <item><description><b>Map key reachability.</b> Every lookup into the extension maps
    ///   normalizes its input first, so a key that is not already in normalized form can never be
    ///   matched — an unreachable entry that looks perfectly correct in the source. A 13-character
    ///   "gitattributes" key (the normalizer caps at 12) shipped in exactly that state, because a
    ///   dead mapping just falls through to the generated page and looks fine.</description></item>
    ///   <item><description><b>The lettered template.</b> The one part of the icon set typeset at
    ///   runtime rather than drawn by an artist, and therefore the one part whose output depends on
    ///   a font load and a cap-height solve that can both quietly go wrong. Its whole design goal is
    ///   that a generated page is the same drawing as a shipped one with different letters on it,
    ///   which is checked here against the two assets it was transcribed from.</description></item>
    /// </list>
    /// </summary>
    public class FileIconAssetTests
    {
        /// <summary>Rasterization size for the ink and comparison checks. Above the 36x36 the tile
        /// draws at, so antialiasing contributes proportionally less to a coverage number, and one
        /// of the four sizes <see cref="FileIconRenderer"/> is designed to be asked for.</summary>
        private const int RasterSize = 96;

        /// <summary>
        /// Coverage floor for a shipped icon. Today's lightest is <c>git.svg</c> at 30.3% and the
        /// heaviest is <c>powershell.svg</c> at 64.5%; a full file page runs about 47%. 15% is half
        /// of the lightest thing in the set — loose enough that a genuinely sparse new icon passes,
        /// tight enough that "the parse produced a document which draws nothing" cannot.
        /// </summary>
        private const double MinIconCoverage = 0.15;

        // ------------------------------------------------------------- Avalonia's asset loader
        //
        // Declared first on purpose: static field initializers run in declaration order when the
        // type is initialized, so the bind is in place before any test method can reach
        // FileIconRenderer. That ordering is load-bearing rather than tidy — the renderer memoizes
        // its rasters for the life of the process, so a single Render() before the bind would cache
        // a degraded fallback page under a real slug's key and every later assertion would compare
        // one fallback page against another and pass. This class is the only thing in the assembly
        // that touches FileIconRenderer or IconFonts; a second one would have to force the bind too.

        private static string _assetLoaderFailure;
        private static readonly bool _assetLoaderBound = TryBindAssetLoader();

        // An explicit — and otherwise empty — static constructor, which is load-bearing. Without one
        // the compiler marks the class `beforefieldinit` and the runtime defers the initializer
        // until the first *static field* access; every test below reads only consts (inlined at
        // compile time) and members of other classes, so the bind never ran and all 77 asset renders
        // quietly compared a fallback page against a fallback page. Declaring the ctor drops
        // `beforefieldinit`, which pins initialization to before the first instance is created —
        // and xunit constructs the class once per test.
        static FileIconAssetTests() { }

        /// <summary>
        /// Binds a real <c>IAssetLoader</c> so <c>avares://</c> resolves outside a running app.
        /// <see cref="FileIconRenderer"/> reaches for the shipped SVGs and for Inter that way, and
        /// without a loader every one of those reads degrades — silently and by design — to a drawn
        /// fallback page in a substitute face, which would let this whole suite pass while covering
        /// none of the asset path.
        /// </summary>
        /// <remarks>
        /// Reflection, because Avalonia 12.1's reference assembly hides both
        /// <c>AvaloniaLocator.CurrentMutable</c> and <c>Avalonia.Platform.StandardAssetLoader</c>;
        /// they exist only in the runtime assembly. The alternative is standing up a real
        /// <c>AppBuilder</c>, which drags a windowing platform into a headless test run for the sake
        /// of reading embedded resources. If an Avalonia upgrade moves these, the tests below fail
        /// loudly with the reason rather than skipping green — that is deliberate, and the fix is
        /// here rather than in the icon code.
        /// </remarks>
        private static bool TryBindAssetLoader()
        {
            try
            {
                var runtime = typeof(AvaloniaLocator).Assembly;
                var loaderType = runtime.GetType("Avalonia.Platform.StandardAssetLoader");
                var loaderInterface = runtime.GetType("Avalonia.Platform.IAssetLoader");
                if (loaderType == null || loaderInterface == null)
                {
                    _assetLoaderFailure = "Avalonia.Platform." +
                        (loaderType == null ? "StandardAssetLoader" : "IAssetLoader") +
                        " is no longer in " + runtime.GetName().Name + ".";
                    return false;
                }

                var currentMutable = typeof(AvaloniaLocator).GetProperty(
                    "CurrentMutable", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (currentMutable == null)
                {
                    _assetLoaderFailure = "AvaloniaLocator.CurrentMutable is gone.";
                    return false;
                }

                // The ctor takes an optional entry assembly, used only to resolve the shorthand
                // "resm:"/relative forms. Every URI here is fully qualified avares://, so null is right.
                object loader = loaderType.GetConstructor(new[] { typeof(Assembly) }) != null
                    ? Activator.CreateInstance(loaderType, new object[] { null })
                    : Activator.CreateInstance(loaderType);

                object locator = currentMutable.GetValue(null);

                var bind = locator.GetType().GetMethods()
                    .First(m => m.Name == "Bind" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                    .MakeGenericMethod(loaderInterface);
                object registration = bind.Invoke(locator, null);

                var toConstant = registration.GetType().GetMethods().First(m => m.Name == "ToConstant");
                if (toConstant.IsGenericMethodDefinition)
                    toConstant = toConstant.MakeGenericMethod(loaderInterface);
                toConstant.Invoke(registration, new[] { loader });

                return true;
            }
            catch (Exception ex)
            {
                _assetLoaderFailure = ex.GetBaseException().Message;
                return false;
            }
        }

        /// <summary>
        /// The bind itself, asserted rather than assumed. Everything that touches a
        /// <c>file:</c> tag or the lettering font depends on it, and a failure here explains those
        /// failures in one line instead of thirty.
        /// </summary>
        [Fact]
        public void The_avalonia_asset_loader_binds_outside_a_running_app()
        {
            Assert.True(_assetLoaderBound,
                "Could not bind an IAssetLoader by reflection, so avares:// cannot resolve and the " +
                "shipped SVGs and Inter are both unreachable: " + _assetLoaderFailure +
                " Fix FileIconAssetTests.TryBindAssetLoader — this is a test-host concern, not an " +
                "icon regression.");
        }

        /// <summary>
        /// The lettering really is the embedded Inter SemiBold. This is the assertion behind every
        /// generated page in the suite: the letter size is solved from the face's own cap height
        /// against a reference of 7.883 viewBox units, so a substitute face (Segoe UI is what
        /// <see cref="IconFonts"/>' fallback chain lands on here) produces a page that is drawn,
        /// plausible, and the wrong size beside the shipped art — with nothing in the output to say
        /// so. Skia cannot see Inter by family name, which is exactly why it is loaded as a
        /// resource and why an unbound asset loader is a silent downgrade rather than an error.
        /// </summary>
        [Fact]
        public void The_lettering_face_is_the_embedded_inter()
        {
            Assert.True(IconFonts.LetteringIsInter,
                "IconFonts fell back to '" + IconFonts.Lettering.FamilyName + "' instead of the " +
                "embedded Inter SemiBold; every generated icon's cap height is a substitute's.");
        }

        // ---------------------------------------------------------------------------- the asset tree

        private static readonly Lazy<string> _iconDir = new Lazy<string>(FindIconDir);

        /// <summary>
        /// Walks up from the test binary to <c>clowd_ui/Clowd.Ui/Assets/FileIcons</c>. The same
        /// find-the-checkout walk <see cref="TestFFmpeg"/> and the AI-sidecar suites use, for the
        /// same reason: the test binary's own directory is several configurations deep and its
        /// depth changes with the TFM.
        /// </summary>
        private static string FindIconDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "clowd_ui", "Clowd.Ui", "Assets", "FileIcons");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "Could not find clowd_ui/Clowd.Ui/Assets/FileIcons by walking up from " +
                AppContext.BaseDirectory + "; the asset-integrity tests read the shipped art off " +
                "the repo tree rather than out of the built resources.");
        }

        private static string IconDir => _iconDir.Value;

        /// <summary>Every shipped slug, taken from the files on disk rather than from the catalog.
        /// The two are cross-checked against each other below, so neither may be the source of
        /// truth for the other's test.</summary>
        private static IEnumerable<string> ShippedSlugs() =>
            Directory.EnumerateFiles(IconDir, "*.svg")
                     .Select(Path.GetFileNameWithoutExtension)
                     .OrderBy(s => s, StringComparer.Ordinal);

        public static IEnumerable<object[]> AllShippedIcons() =>
            ShippedSlugs().Select(slug => new object[] { slug });

        // -------------------------------------------------------------------- parse and draw ink

        /// <summary>
        /// Every shipped SVG parses through the real subset parser and rasterizes to real ink,
        /// read straight off the repo tree. <see cref="MiniSvg"/> throws by design on an element or
        /// attribute outside its subset, so an icon fetched or hand-edited into something it cannot
        /// draw fails here rather than rendering blank in the recents list. The coverage half
        /// catches the other failure: a document that parses cleanly but drops every shape.
        /// </summary>
        [Theory]
        [MemberData(nameof(AllShippedIcons))]
        public void Every_shipped_icon_parses_and_rasterizes_to_real_ink(string slug)
        {
            string path = Path.Combine(IconDir, slug + ".svg");

            MiniSvgDocument document;
            using (var stream = File.OpenRead(path))
                document = MiniSvg.Parse(stream);

            using (document)
            {
                Assert.True(document.ViewBox.Width > 0 && document.ViewBox.Height > 0,
                    slug + ".svg parsed with an empty viewBox (" + document.ViewBox + ").");

                double coverage = Coverage(RasterizeDocument(document, RasterSize));

                Assert.True(coverage >= MinIconCoverage,
                    slug + ".svg parsed but rasterized to " + Percent(coverage) + " ink coverage at " +
                    RasterSize + "px, under the " + Percent(MinIconCoverage) + " floor — i.e. the " +
                    "parse produced a document that draws (almost) nothing.");
            }
        }

        /// <summary>
        /// The same 77 assets again, this time through <see cref="FileIconRenderer"/> and therefore
        /// through <c>avares://</c> — the path the app actually takes. It covers what the repo-tree
        /// test cannot: that the resource is embedded under the URI the catalog composes for it.
        /// A slug whose asset does not load does not throw and does not come back null; the
        /// renderer's fallback chain quietly serves the blank file page instead, which is why the
        /// assertion is "this is not the blank page" rather than "this is not null".
        /// </summary>
        [Theory]
        [MemberData(nameof(AllShippedIcons))]
        public void Every_shipped_icon_renders_through_the_asset_loader(string slug)
        {
            var pixels = FileIconRenderer.Render("file:" + slug, RasterSize);

            AssertIsAnIcon(pixels, RasterSize);
            Assert.True(Coverage(pixels.Bgra) >= MinIconCoverage,
                "file:" + slug + " rendered to " + Percent(Coverage(pixels.Bgra)) + " ink coverage.");

            // file.svg IS the blank page — it is the asset the cyan template was transcribed from,
            // and its identity with the generated one is asserted on its own below.
            if (string.Equals(slug, FileIconCatalog.UnknownSlug, StringComparison.Ordinal))
                return;

            var fallback = FileIconRenderer.Render("gen:cyan:", RasterSize);
            Assert.True(DifferingPixels(fallback.Bgra, pixels.Bgra) > 0,
                "file:" + slug + " rasterized to exactly the blank file page, which means its " +
                "avares asset did not load and the renderer fell back. Check that " + slug +
                ".svg is embedded under " + FileIconCatalog.AssetUriForSlug(slug) + ".");
        }

        // ------------------------------------------------------- catalog, files and SOURCES.tsv

        /// <summary>
        /// <see cref="FileIconCatalog.AllSlugs"/> and the shipped files name the same things. A
        /// slug with no file degrades silently to the blank page at runtime; a file with no slug is
        /// unreachable art carried in the exe. Neither is visible without this.
        /// </summary>
        [Fact]
        public void Catalog_slugs_and_shipped_files_are_the_same_set()
        {
            Assert.Equal(
                ShippedSlugs().ToArray(),
                FileIconCatalog.AllSlugs.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        }

        /// <summary>The slug the whole degradation chain terminates on has to be one of the shipped
        /// assets, or every unrecognized extension lands on the generated page instead of the
        /// authored one.</summary>
        [Fact]
        public void The_unknown_slug_is_a_shipped_asset()
        {
            Assert.Contains(FileIconCatalog.UnknownSlug, ShippedSlugs());
        }

        /// <summary>Every slug the content-kind fallback can name is shipped too. That path is
        /// reached by sessions with no usable extension at all — the ones most likely to be looked
        /// at and least likely to be noticed when they go blank.</summary>
        [Theory]
        [InlineData("image", false, "image-file")]
        [InlineData("video", false, "video-file")]
        [InlineData("text", false, "txt")]
        [InlineData(null, true, "video-file")]
        [InlineData("", false, "file")]
        public void Content_kind_fallback_slugs_are_shipped(string kind, bool isVideoProject, string expectedSlug)
        {
            Assert.Equal("file:" + expectedSlug, FileIconCatalog.TagForContentKind(kind, isVideoProject));
            Assert.Contains(expectedSlug, ShippedSlugs());
        }

        /// <summary>
        /// SOURCES.tsv is the provenance record for the shipped icons: one row per icon, carrying
        /// the icons8 id it came from so the art can be refetched. It is written by hand at fetch
        /// time and read by nothing at runtime, which is exactly why it drifts.
        /// </summary>
        [Fact]
        public void Sources_tsv_has_exactly_one_row_per_shipped_icon()
        {
            string[] lines = File.ReadAllLines(Path.Combine(IconDir, "SOURCES.tsv"))
                                 .Where(l => !string.IsNullOrWhiteSpace(l))
                                 .ToArray();

            Assert.Equal(new[] { "slug", "icons8Id", "icons8Name", "platform" }, lines[0].Split('\t'));

            var rows = lines.Skip(1).Select(l => l.Split('\t')).ToArray();
            foreach (var row in rows)
            {
                Assert.Equal(4, row.Length);
                foreach (string cell in row)
                    Assert.False(string.IsNullOrWhiteSpace(cell),
                        "SOURCES.tsv row '" + string.Join(" | ", row) + "' has an empty column.");
            }

            var tsvSlugs = rows.Select(r => r[0]).OrderBy(s => s, StringComparer.Ordinal).ToArray();

            Assert.Equal(ShippedSlugs().ToArray(), tsvSlugs);
            Assert.Equal(tsvSlugs.Length, tsvSlugs.Distinct(StringComparer.Ordinal).Count());
        }

        // ------------------------------------------------------------ the extension lookup maps

        /// <summary>
        /// Every key in both extension maps survives <see cref="FileIconCatalog.NormalizeExtension"/>
        /// unchanged. Every lookup normalizes its input first, so a key that does not equal its own
        /// normalized form can never be matched by anything — a mapping that reads correctly,
        /// compiles, and does nothing. The 13-character "gitattributes" key shipped in exactly that
        /// state (the normalizer caps at 12) and only turned up when this assertion was written.
        /// </summary>
        [Fact]
        public void Every_extension_map_key_is_already_normalized()
        {
            var unreachable = FileIconCatalog.ExtensionToSlug.Keys
                .Concat(FileIconCatalog.ExtensionToPalette.Keys)
                .Select(key => (Key: key, Normalized: FileIconCatalog.NormalizeExtension(key)))
                .Where(p => !string.Equals(p.Key, p.Normalized, StringComparison.Ordinal))
                .Select(p => "'" + p.Key + "' normalizes to '" + p.Normalized + "'")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            Assert.True(unreachable.Length == 0,
                "These extension-map keys can never be reached, because every lookup normalizes its " +
                "input first: " + string.Join("; ", unreachable));
        }

        /// <summary>
        /// The two maps are disjoint. <see cref="FileIconCatalog.TagForExtension"/> consults the
        /// shipped map first and returns, so an extension present in both has a palette entry that
        /// nothing will ever read — usually a maintainer's note that the shipped art was meant to
        /// be replaced by a generated page, silently going nowhere.
        /// </summary>
        [Fact]
        public void The_shipped_and_generated_extension_maps_are_disjoint()
        {
            var both = FileIconCatalog.ExtensionToSlug.Keys
                .Intersect(FileIconCatalog.ExtensionToPalette.Keys, StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            Assert.True(both.Length == 0,
                "These extensions map to both shipped art and a generated palette; the palette " +
                "entry is dead: " + string.Join(", ", both));
        }

        /// <summary>Every slug an extension maps to is one that exists. A typo here is invisible at
        /// runtime — the renderer's fallback chain serves the blank file page instead.</summary>
        [Fact]
        public void Every_mapped_extension_names_a_shipped_slug()
        {
            var shipped = ShippedSlugs().ToHashSet(StringComparer.Ordinal);

            var missing = FileIconCatalog.ExtensionToSlug
                .Where(kv => !shipped.Contains(kv.Value))
                .Select(kv => "." + kv.Key + " -> " + kv.Value)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            Assert.True(missing.Length == 0,
                "These extensions map to a slug with no Assets/FileIcons/<slug>.svg behind it: " +
                string.Join(", ", missing));
        }

        // ------------------------------------------------------------------- the lettered template

        /// <summary>
        /// The generated page is typeset at runtime, so unlike the shipped art it fails on a font
        /// load or a cap-height solve rather than on a parse. This covers the length rule's lower
        /// half: 1 through 5 characters all draw letters.
        /// </summary>
        /// <remarks>
        /// The comparison is against the letterless page rather than an absolute coverage number.
        /// Ink coverage barely moves when a few glyphs are added to a full-bleed page — every one
        /// of these sits at 46.9%, against 46.9% for the blank one — so a coverage floor would pass
        /// just as happily on a page with nothing typeset on it. Differing from the blank page is
        /// the assertion with teeth (155 pixels for "M", 361 for "WAV"), and being *identical* to
        /// it is what proves the over-length rule below.
        /// </remarks>
        [Theory]
        [InlineData("M")]
        [InlineData("GO")]
        [InlineData("WAV")]
        [InlineData("WEBM")]
        [InlineData("HEIF5")]
        public void The_lettered_page_typesets_one_to_five_characters(string letters)
        {
            var blank = FileIconRenderer.Render("gen:cyan:", RasterSize);
            var lettered = FileIconRenderer.Render("gen:cyan:" + letters, RasterSize);

            AssertIsAnIcon(lettered, RasterSize);
            Assert.True(Coverage(lettered.Bgra) >= 0.30,
                "The generated '" + letters + "' page covers only " + Percent(Coverage(lettered.Bgra)) +
                " of the tile; the page body alone should be most of it.");

            Assert.True(DifferingPixels(blank.Bgra, lettered.Bgra) > 0,
                "The generated '" + letters + "' page is pixel-identical to the letterless page, so " +
                "nothing was typeset on it.");
        }

        /// <summary>
        /// Six characters or more draws the page with no letters at all, as does an empty tag. The
        /// rule exists because the 25.6-unit ink band cannot hold more than five glyphs legibly at
        /// 36x36 even condensed, and letters spilling off the page read worse than none.
        /// </summary>
        [Theory]
        [InlineData("GSHEET")]
        [InlineData("AFDESIGN")]
        [InlineData("")]
        public void The_lettered_page_is_blank_for_zero_or_more_than_five_characters(string letters)
        {
            var blank = FileIconRenderer.Render("gen:cyan:", RasterSize);
            var page = FileIconRenderer.Render("gen:cyan:" + letters, RasterSize);

            AssertIsAnIcon(page, RasterSize);
            Assert.Equal(0, DifferingPixels(blank.Bgra, page.Bgra));
        }

        /// <summary>
        /// The generated page renders at every size the icon producer quantizes render scaling to.
        /// The cap-height solve and the [6.5, 12] size clamp both scale with the surface, and the
        /// clamp is the part that could plausibly swallow the letters at one size and not another.
        /// </summary>
        [Theory]
        [InlineData(48)]
        [InlineData(72)]
        [InlineData(96)]
        [InlineData(144)]
        public void The_lettered_page_renders_at_every_quantized_size(int size)
        {
            var blank = FileIconRenderer.Render("gen:yellow:", size);
            var lettered = FileIconRenderer.Render("gen:yellow:PDF", size);

            AssertIsAnIcon(lettered, size);
            Assert.True(DifferingPixels(blank.Bgra, lettered.Bgra) > 0,
                "Nothing was typeset on the generated page at " + size + "px.");
        }

        /// <summary>
        /// The four palettes draw four different pages. They are hand-transcribed hex literals; two
        /// of them accidentally sharing a colour would look entirely plausible in the source and
        /// would quietly collapse the distinction the catalog spends 179 map entries making.
        /// </summary>
        [Fact]
        public void The_four_palettes_are_visibly_distinct()
        {
            var pages = new[] { "yellow", "cyan", "pink", "green" }
                .Select(p => (Palette: p, Pixels: FileIconRenderer.Render("gen:" + p + ":TXT", RasterSize)))
                .ToArray();

            foreach (var page in pages)
                AssertIsAnIcon(page.Pixels, RasterSize);

            for (int i = 0; i < pages.Length; i++)
                for (int j = i + 1; j < pages.Length; j++)
                    Assert.True(DifferingPixels(pages[i].Pixels.Bgra, pages[j].Pixels.Bgra) > 0,
                        "The " + pages[i].Palette + " and " + pages[j].Palette +
                        " palettes rasterize identically.");
        }

        /// <summary>
        /// The letterless cyan page and the shipped <c>file.svg</c> are the same drawing, to the
        /// byte. The generated template's body path, corner path and cyan palette — flat body,
        /// gradient corner and both its endpoints — were transcribed from that one asset by hand,
        /// which is what lets a generated page sit in a list beside the authored ones without
        /// reading as a different icon set. Nothing else checks that claim, and a single edited
        /// coordinate or hex digit on either side breaks this and only this.
        /// </summary>
        /// <remarks>
        /// Asserted three ways round on purpose: the renderer's own avares path, the renderer's
        /// generated path, and a raster of the file off the repo tree through
        /// <see cref="MiniSvg"/>. The third is what says the parser and the hardcoded transcription
        /// agree, rather than merely that the renderer agrees with itself.
        /// </remarks>
        [Fact]
        public void The_letterless_cyan_page_is_the_shipped_file_page()
        {
            var generated = FileIconRenderer.Render("gen:cyan:", RasterSize);
            var throughAssetLoader = FileIconRenderer.Render("file:" + FileIconCatalog.UnknownSlug, RasterSize);

            AssertIsAnIcon(generated, RasterSize);
            AssertIsAnIcon(throughAssetLoader, RasterSize);
            Assert.Equal(0, DifferingPixels(generated.Bgra, throughAssetLoader.Bgra));

            byte[] fromRepoTree;
            using (var stream = File.OpenRead(Path.Combine(IconDir, FileIconCatalog.UnknownSlug + ".svg")))
            using (var document = MiniSvg.Parse(stream))
                fromRepoTree = RasterizeDocument(document, RasterSize);

            Assert.Equal(0, DifferingPixels(generated.Bgra, fromRepoTree));
        }

        /// <summary>
        /// The cap-height solve, checked where it is actually observable: the letters on a generated
        /// yellow WAV page land on the letters of the shipped <c>wav.svg</c> the yellow template was
        /// traced from. This is the assertion behind the whole "solve the font size from
        /// <c>metrics.CapHeight</c> against a reference of 7.883 viewBox units" design — the number
        /// is unverifiable by inspection, and getting it wrong produces a page that is drawn,
        /// plausible, and visibly the wrong size only when set beside a shipped icon.
        /// </summary>
        /// <remarks>
        /// Both ink boxes are measured by differencing against the letterless yellow page, so the
        /// shared body geometry cancels and only the glyphs are left. At 96px the two agree exactly
        /// on the baseline and the cap line and differ by one pixel on the left edge — 0.5 viewBox
        /// units, which is the raster quantization. The 2px tolerance is that, doubled.
        /// </remarks>
        [Fact]
        public void The_generated_letters_sit_where_the_shipped_ones_do()
        {
            var blank = FileIconRenderer.Render("gen:yellow:", RasterSize);
            var generated = InkBox(FileIconRenderer.Render("gen:yellow:WAV", RasterSize).Bgra, blank.Bgra);
            var shipped = InkBox(FileIconRenderer.Render("file:wav", RasterSize).Bgra, blank.Bgra);

            Assert.False(generated.IsEmpty, "The generated WAV page has no letters on it at all.");
            Assert.False(shipped.IsEmpty,
                "wav.svg is indistinguishable from the letterless yellow page — either the asset " +
                "did not load or the yellow template has drifted from the art it was traced from.");

            const int Tolerance = 2;
            Assert.True(Math.Abs(generated.Left - shipped.Left) <= Tolerance &&
                        Math.Abs(generated.Right - shipped.Right) <= Tolerance &&
                        Math.Abs(generated.Top - shipped.Top) <= Tolerance &&
                        Math.Abs(generated.Bottom - shipped.Bottom) <= Tolerance,
                "The generated WAV lettering occupies " + generated + " but the shipped wav.svg's " +
                "occupies " + shipped + " at " + RasterSize + "px. The cap-height solve, the " +
                "baseline or the ink-box centring has drifted from the art.");
        }

        /// <summary>A malformed or hostile tag degrades to a drawn page rather than throwing or
        /// handing back an empty buffer. <see cref="FileIconRenderer.Render"/> takes a bare string
        /// and is public, so it cannot assume the catalog produced its input — and a slug goes
        /// straight into an <c>avares://</c> URI.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("gen:")]
        [InlineData("gen:mauve:ABC")]
        [InlineData("nonsense")]
        [InlineData("file:")]
        [InlineData("file:../../etc/passwd")]
        [InlineData("file:no-such-slug")]
        public void A_malformed_tag_degrades_to_a_drawn_page(string tag)
        {
            var pixels = FileIconRenderer.Render(tag, RasterSize);

            AssertIsAnIcon(pixels, RasterSize);
            Assert.True(Coverage(pixels.Bgra) >= MinIconCoverage,
                "Tag '" + (tag ?? "<null>") + "' produced an (almost) empty buffer rather than a page.");
        }

        // ------------------------------------------------------------------------------- plumbing

        private static void AssertIsAnIcon(PreviewPixels pixels, int size)
        {
            Assert.NotNull(pixels);
            Assert.Equal(PreviewKind.Icon, pixels.Kind);
            Assert.Equal(size, pixels.Width);
            Assert.Equal(size, pixels.Height);
            Assert.Equal(size * size * 4, pixels.Bgra.Length);
        }

        /// <summary>
        /// The same pipeline <c>FileIconRenderer.Rasterize</c> uses: composite premultiplied (the
        /// only alpha type Skia will render into) and read back unpremultiplied, so a buffer
        /// produced here is byte-comparable with one the renderer produced.
        /// </summary>
        private static byte[] RasterizeDocument(MiniSvgDocument document, int size)
        {
            var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            Assert.NotNull(surface);

            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale(size / document.ViewBox.Width, size / document.ViewBox.Height);
            surface.Canvas.Translate(-document.ViewBox.Left, -document.ViewBox.Top);
            document.Draw(surface.Canvas);
            surface.Canvas.Flush();

            using var pixmap = surface.PeekPixels();
            Assert.NotNull(pixmap);

            var bgra = new byte[size * size * 4];
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                var target = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                Assert.True(pixmap.ReadPixels(target, pin.AddrOfPinnedObject(), size * 4));
            }
            finally
            {
                pin.Free();
            }

            return bgra;
        }

        /// <summary>Fraction of pixels carrying meaningful alpha. The threshold sits above pure
        /// antialiasing fringe, so a document that draws nothing but a hairline cannot pass.</summary>
        private static double Coverage(byte[] bgra)
        {
            int lit = 0;
            for (int i = 3; i < bgra.Length; i += 4)
                if (bgra[i] >= 32) lit++;
            return lit / (double)(bgra.Length / 4);
        }

        private static int DifferingPixels(byte[] a, byte[] b)
        {
            Assert.Equal(a.Length, b.Length);

            int differing = 0;
            for (int i = 0; i < a.Length; i += 4)
                if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3])
                    differing++;
            return differing;
        }

        /// <summary>
        /// The bounding box, in pixels, of everything <paramref name="page"/> draws that
        /// <paramref name="reference"/> does not. Differencing rather than thresholding because the
        /// two pages share an opaque body: absolute ink has no edges to find, and the dark lettering
        /// over a mid-tone page is not separable by alpha or by luminance alone.
        /// </summary>
        private static PixelBox InkBox(byte[] page, byte[] reference)
        {
            Assert.Equal(page.Length, reference.Length);

            int size = (int)Math.Round(Math.Sqrt(page.Length / 4.0));
            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = (y * size + x) * 4;
                    if (page[i] == reference[i] && page[i + 1] == reference[i + 1] &&
                        page[i + 2] == reference[i + 2] && page[i + 3] == reference[i + 3])
                        continue;

                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            }

            // Left/Top stay at their sentinel maxima when nothing differed, so an explicit empty
            // box is returned rather than a default-valued one, which would read as a 1x1 box at
            // the origin.
            return right < left ? PixelBox.Empty : new PixelBox(left, top, right, bottom);
        }

        private readonly record struct PixelBox(int Left, int Top, int Right, int Bottom)
        {
            public static PixelBox Empty => new PixelBox(0, 0, -1, -1);

            public bool IsEmpty => Right < Left;

            public override string ToString() =>
                "x[" + Left + "," + Right + "] y[" + Top + "," + Bottom + "]";
        }

        private static string Percent(double fraction) =>
            fraction.ToString("P1", CultureInfo.InvariantCulture);
    }
}
