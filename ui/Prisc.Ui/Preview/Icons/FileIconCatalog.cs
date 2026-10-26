using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text;

namespace Clowd.UI.Preview.Icons
{
    /// <summary>
    /// Colour scheme for the generated lettered file page (see FileIconRenderer, architecture
    /// section 12). The four values are not arbitrary — they were sampled from the real icons8
    /// "fluent" lettered art we ship beside them, so a generated icon for an extension icons8 has
    /// no artwork for sits natively next to a shipped one instead of reading as a fallback.
    /// </summary>
    public enum IconPalette
    {
        /// <summary>Yellow body / orange fold / dark grey letters. Matches ZIP, RAR, TAR, WAV, EPS.</summary>
        Yellow,

        /// <summary>Cyan body / blue fold. Matches MKV, PNG, JPG, VCF and the blank File page. The default.</summary>
        Cyan,

        /// <summary>Pink body / red fold and letters. Matches MP3, AVI, MOV, PPT, EXE.</summary>
        Pink,

        /// <summary>Green body / dark green fold and letters. Matches HTML, CSS, JSON, XML, JAVA, DLL.</summary>
        Green,
    }

    /// <summary>
    /// The static map from a file extension to the artwork that represents it: either one of the 77
    /// icons8 "fluent" SVGs committed under <c>Assets/FileIcons</c>, or a palette for the lettered
    /// page FileIconRenderer typesets at runtime.
    ///
    /// This is code rather than a data asset on purpose. It is consulted once per visible row on
    /// every scroll tick, so it must not parse anything, must not touch disk and must not probe
    /// AssetLoader — the FrozenDictionary lookups below are the whole cost. That every mapped slug
    /// actually resolves to a shipped file is asserted by a unit test (task T14) rather than at
    /// runtime, and <c>Assets/FileIcons/SOURCES.tsv</c> is the provenance record the same test
    /// cross-checks against <see cref="AllSlugs"/>.
    ///
    /// Two rules govern the extension lists, and both are load-bearing:
    ///   * A LETTERED page may only serve extensions its letters actually spell. The CSV page says
    ///     "CSV", so .tsv does not get it; the XLS page says "XLS", so .xlsm does not get it. Those
    ///     extensions fall through to the generated page, which spells them correctly.
    ///   * A BRAND MARK has no letters, so it may serve every extension its application owns — the
    ///     Blender logo covers .blend, the Visual Studio logo covers .sln and the project files.
    /// </summary>
    public static class FileIconCatalog
    {
        /// <summary>Longest extension we will keep. Anything longer is almost certainly not an
        /// extension at all (a dotted filename, a path fragment, junk out of an upload manifest),
        /// and the value ends up inside an <c>avares://</c> URI and a cache key.</summary>
        private const int MaxExtensionLength = 12;

        /// <summary>Slug of the blank icons8 File page. The terminal fallback for everything.</summary>
        public const string UnknownSlug = "file";

        /// <summary>Directory the shipped SVGs live in, as an avares URI prefix. Callers append
        /// "&lt;slug&gt;.svg"; slugs only ever come out of this class's own tables, never off disk,
        /// which is what keeps the composed URI well-formed.</summary>
        public const string AssetUriPrefix = "avares://Clowd.Ui/Assets/FileIcons/";

        // Extension -> shipped asset slug. Keys are already normalized (lowercase, no dot).
        private static readonly FrozenDictionary<string, string> _extensionToSlug =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // --- Office and documents -------------------------------------------------
                ["pdf"] = "pdf",
                ["xls"] = "xls", ["xlsx"] = "xls",
                ["doc"] = "doc", ["docx"] = "doc",
                ["ppt"] = "ppt", ["pptx"] = "ppt",
                ["csv"] = "csv",
                ["txt"] = "txt",
                ["rtf"] = "rtf",
                ["vcf"] = "vcf", ["vcard"] = "vcf",
                // Brand marks: no letters, so every extension the app owns is fair game.
                ["vsd"] = "visio", ["vsdx"] = "visio", ["vsdm"] = "visio", ["vst"] = "visio", ["vstx"] = "visio",
                ["one"] = "onenote", ["onepkg"] = "onenote", ["onetoc2"] = "onenote",
                ["mdb"] = "access", ["accdb"] = "access", ["accde"] = "access",

                // --- Web and markup -------------------------------------------------------
                ["html"] = "html", ["htm"] = "html",
                ["css"] = "css",
                ["json"] = "json",
                ["xml"] = "xml",

                ["sql"] = "sql",

                // --- Source code ----------------------------------------------------------
                // EVERY programming language shares the one Code File page — a bare </> glyph on
                // a blue page — rather than each getting its language's brand mark. A row is
                // telling you "this is a source file", and the C# and Python and Ruby logos say
                // that in twelve different visual languages, none of which belongs to the file-page
                // family the rest of this list is drawn from. The glyph carries no lettering, so it
                // also serves the long tail (Rust, Go, Zig, Elixir …) that icons8 has no mark for
                // at all, without spelling anything wrong.
                ["js"] = "code-file", ["mjs"] = "code-file", ["cjs"] = "code-file", ["jsx"] = "code-file",
                ["ts"] = "code-file", ["tsx"] = "code-file", ["mts"] = "code-file", ["cts"] = "code-file",
                ["py"] = "code-file", ["pyw"] = "code-file", ["pyi"] = "code-file",
                ["cs"] = "code-file", ["csx"] = "code-file",
                ["cpp"] = "code-file", ["cc"] = "code-file", ["cxx"] = "code-file",
                ["hpp"] = "code-file", ["hxx"] = "code-file", ["c"] = "code-file", ["h"] = "code-file",
                ["java"] = "code-file", ["kt"] = "code-file", ["kts"] = "code-file",
                ["php"] = "code-file", ["rb"] = "code-file", ["swift"] = "code-file",
                ["vb"] = "code-file", ["vbs"] = "code-file", ["bas"] = "code-file",
                ["r"] = "code-file", ["rs"] = "code-file", ["go"] = "code-file",
                ["lua"] = "code-file", ["pl"] = "code-file", ["pm"] = "code-file",
                ["fs"] = "code-file", ["fsx"] = "code-file", ["dart"] = "code-file",
                ["scala"] = "code-file", ["clj"] = "code-file", ["ex"] = "code-file",
                ["exs"] = "code-file", ["erl"] = "code-file", ["hs"] = "code-file",
                ["groovy"] = "code-file", ["jl"] = "code-file", ["nim"] = "code-file",
                ["zig"] = "code-file", ["asm"] = "code-file",
                // component files and preprocessed stylesheets are source too; .scss on the CSS
                // page would be a page that spells the wrong thing, which is the one thing the
                // lettered family must never do.
                ["vue"] = "code-file", ["svelte"] = "code-file", ["astro"] = "code-file",
                ["scss"] = "code-file", ["sass"] = "code-file", ["less"] = "code-file",
                ["styl"] = "code-file",
                // shell and batch scripts, for the same reason
                ["sh"] = "code-file", ["bash"] = "code-file", ["zsh"] = "code-file",
                ["fish"] = "code-file", ["bat"] = "code-file", ["cmd"] = "code-file",
                ["ps1"] = "code-file", ["psm1"] = "code-file", ["psd1"] = "code-file",

                // --- Toolchain ------------------------------------------------------------
                // Docker, Git and Visual Studio keep their marks: none of these is a source file
                // the </> page would describe — they are one tool's configuration or project
                // metadata, and the mark is the fastest way to say which tool.
                ["dockerfile"] = "docker",
                // "gitattribute", not "gitattributes": every key here has to be spelled the way
                // NormalizeExtension leaves it, and that truncates at MaxExtensionLength. A key
                // longer than the cap is unreachable — it can never be produced, so it can never
                // be looked up.
                ["gitignore"] = "git", ["gitattribute"] = "git", ["gitmodules"] = "git", ["gitconfig"] = "git",
                // .csproj would otherwise land on the XML page; the more specific icon wins.
                ["sln"] = "visual-studio", ["slnx"] = "visual-studio", ["csproj"] = "visual-studio",
                ["vbproj"] = "visual-studio", ["vcxproj"] = "visual-studio", ["fsproj"] = "visual-studio",

                // --- Archives -------------------------------------------------------------
                // Every archive format shares one zipper-folder icon rather than a lettered page
                // per format. The lettering is what a page icon says; on an archive the zipper
                // already says it, and ZIP/RAR/7Z/TAR pages read as four unrelated documents
                // where the folder reads as one kind of thing. This is also why the long tail
                // below (.gz, .cab, .zst …) is mapped here instead of generating a page whose
                // letters nobody recognises.
                ["zip"] = "archive", ["zipx"] = "archive", ["rar"] = "archive", ["7z"] = "archive",
                ["tar"] = "archive", ["gz"] = "archive", ["tgz"] = "archive", ["bz2"] = "archive",
                ["tbz"] = "archive", ["xz"] = "archive", ["zst"] = "archive", ["lz4"] = "archive",
                ["arj"] = "archive", ["lzh"] = "archive", ["cab"] = "archive",
                ["torrent"] = "torrent",

                // --- Video ----------------------------------------------------------------
                // Every container shares the one Video File page, the same way every language
                // shares the Code File page: what the row is telling you is "this is a video", and
                // MKV versus MOV is not a distinction worth thirteen different pictures. The page
                // is a play triangle rather than lettering, so it spells nothing wrong for the
                // formats icons8 has no page for at all.
                ["mp4"] = "video-file", ["m4v"] = "video-file", ["avi"] = "video-file",
                ["mov"] = "video-file", ["qt"] = "video-file", ["mkv"] = "video-file",
                ["flv"] = "video-file", ["mpg"] = "video-file", ["mpeg"] = "video-file",
                ["mpe"] = "video-file", ["webm"] = "video-file", ["wmv"] = "video-file",
                ["3gp"] = "video-file", ["3g2"] = "video-file", ["vob"] = "video-file",
                ["asf"] = "video-file", ["divx"] = "video-file", ["mxf"] = "video-file",
                // .ts and .mts are deliberately absent: both are also TypeScript, which is far more
                // likely to turn up in a Clowd upload than an MPEG transport stream or AVCHD, and
                // they are mapped to the Code File page above. A key here as well would be a
                // duplicate in this initializer and would throw before the app drew anything.
                ["m2ts"] = "video-file",
                ["ogv"] = "video-file", ["f4v"] = "video-file", ["m2v"] = "video-file",

                // --- Audio ----------------------------------------------------------------
                ["mp3"] = "mp3",
                ["wav"] = "wav", ["wave"] = "wav",
                ["wma"] = "wma",
                ["ogg"] = "ogg",
                ["aac"] = "aac",

                // --- Images ---------------------------------------------------------------
                // One Image File page for every raster format, for the same reason as video: the
                // row is saying "this is a picture", and PNG versus JPEG is not what the reader is
                // asking at 64 pixels. Raw camera formats included — a .cr2 page lettered "CR2"
                // told nobody anything a photo page does not.
                ["png"] = "image-file", ["jpg"] = "image-file", ["jpeg"] = "image-file",
                ["jpe"] = "image-file", ["jfif"] = "image-file", ["gif"] = "image-file",
                ["bmp"] = "image-file", ["tif"] = "image-file", ["tiff"] = "image-file",
                ["webp"] = "image-file", ["avif"] = "image-file", ["heic"] = "image-file",
                ["heif"] = "image-file", ["ico"] = "image-file", ["icns"] = "image-file",
                ["tga"] = "image-file", ["svg"] = "image-file",
                ["raw"] = "image-file", ["cr2"] = "image-file", ["cr3"] = "image-file",
                ["nef"] = "image-file", ["dng"] = "image-file", ["arw"] = "image-file",
                ["orf"] = "image-file", ["rw2"] = "image-file", ["raf"] = "image-file",

                // --- Design ---------------------------------------------------------------
                ["psd"] = "ps", ["psb"] = "ps",
                ["ai"] = "ai-file",
                ["eps"] = "eps",
                ["dwg"] = "dwg",
                ["fig"] = "figma",
                ["xd"] = "adobe-xd",
                ["indd"] = "adobe-indesign", ["idml"] = "adobe-indesign",
                ["aep"] = "adobe-after-effects", ["aepx"] = "adobe-after-effects",
                ["blend"] = "blender", ["blend1"] = "blender",

                // --- Fonts ----------------------------------------------------------------
                ["ttf"] = "ttf",
                ["otf"] = "otf",
                ["woff"] = "woff", ["woff2"] = "woff",

                // --- System ---------------------------------------------------------------
                ["exe"] = "exe",
                ["dll"] = "dll",
                ["apk"] = "apk",
                ["eml"] = "email-document", ["msg"] = "email-document", ["pst"] = "email-document",
                ["lnk"] = "symlink-file", ["url"] = "symlink-file",
                ["db"] = "database", ["db3"] = "database", ["sqlite"] = "database", ["sqlite3"] = "database",
            }.ToFrozenDictionary(StringComparer.Ordinal);

        // Extension -> palette for the GENERATED page. Only extensions icons8 has no artwork for
        // appear here; anything in _extensionToSlug never reaches this table. Everything absent
        // from both falls back to Cyan, which is also the colour of the blank File page, so an
        // unrecognised extension and a nameless file read as members of the same family.
        private static readonly FrozenDictionary<string, IconPalette> _extensionToPalette =
            new Dictionary<string, IconPalette>(StringComparer.Ordinal)
            {
                // Yellow — documents, archives and audio, matching ZIP/RAR/TAR/WAV.
                ["md"] = IconPalette.Yellow, ["markdown"] = IconPalette.Yellow, ["rst"] = IconPalette.Yellow,
                ["log"] = IconPalette.Yellow, ["tsv"] = IconPalette.Yellow, ["nfo"] = IconPalette.Yellow,
                ["odt"] = IconPalette.Yellow, ["ods"] = IconPalette.Yellow, ["odp"] = IconPalette.Yellow,
                ["odg"] = IconPalette.Yellow, ["pages"] = IconPalette.Yellow, ["numbers"] = IconPalette.Yellow,
                ["epub"] = IconPalette.Yellow, ["mobi"] = IconPalette.Yellow, ["azw"] = IconPalette.Yellow,
                ["azw3"] = IconPalette.Yellow, ["fb2"] = IconPalette.Yellow, ["djvu"] = IconPalette.Yellow,
                ["xlsm"] = IconPalette.Yellow, ["xlsb"] = IconPalette.Yellow, ["xlt"] = IconPalette.Yellow,
                ["docm"] = IconPalette.Yellow, ["dot"] = IconPalette.Yellow, ["dotx"] = IconPalette.Yellow,
                ["pptm"] = IconPalette.Yellow, ["pot"] = IconPalette.Yellow, ["potx"] = IconPalette.Yellow,
                // the archive formats that used to letter here now share the zipper-folder icon
                // above; .bak is not an archive and stays a lettered page.
                ["bak"] = IconPalette.Yellow,
                ["iso"] = IconPalette.Yellow, ["img"] = IconPalette.Yellow, ["dmg"] = IconPalette.Yellow,
                ["vhd"] = IconPalette.Yellow, ["vhdx"] = IconPalette.Yellow, ["vmdk"] = IconPalette.Yellow,
                ["flac"] = IconPalette.Yellow, ["opus"] = IconPalette.Yellow, ["m4a"] = IconPalette.Yellow,
                ["m4b"] = IconPalette.Yellow, ["aiff"] = IconPalette.Yellow, ["aif"] = IconPalette.Yellow,
                ["mid"] = IconPalette.Yellow, ["midi"] = IconPalette.Yellow, ["ape"] = IconPalette.Yellow,
                ["ac3"] = IconPalette.Yellow, ["dts"] = IconPalette.Yellow, ["amr"] = IconPalette.Yellow,
                ["oga"] = IconPalette.Yellow, ["mka"] = IconPalette.Yellow,

                // Green — code and configuration, matching HTML/CSS/JSON/XML/JAVA.
                // every source-code extension that used to letter here now shares the Code File
                // page above; what is left is configuration, build and key material — files a
                // </> glyph would misdescribe.
                ["yaml"] = IconPalette.Green, ["yml"] = IconPalette.Green,
                ["toml"] = IconPalette.Green, ["ini"] = IconPalette.Green, ["cfg"] = IconPalette.Green,
                ["conf"] = IconPalette.Green, ["config"] = IconPalette.Green, ["env"] = IconPalette.Green,
                ["editorconfig"] = IconPalette.Green, ["gradle"] = IconPalette.Green, ["cmake"] = IconPalette.Green,
                ["makefile"] = IconPalette.Green, ["patch"] = IconPalette.Green, ["diff"] = IconPalette.Green,
                ["lock"] = IconPalette.Green, ["proto"] = IconPalette.Green, ["graphql"] = IconPalette.Green,
                ["gql"] = IconPalette.Green, ["tf"] = IconPalette.Green, ["hcl"] = IconPalette.Green,
                ["jsonc"] = IconPalette.Green, ["json5"] = IconPalette.Green, ["xsd"] = IconPalette.Green,
                ["xsl"] = IconPalette.Green, ["xslt"] = IconPalette.Green, ["jar"] = IconPalette.Green,
                ["so"] = IconPalette.Green, ["dylib"] = IconPalette.Green, ["sys"] = IconPalette.Green,
                ["msi"] = IconPalette.Green, ["msix"] = IconPalette.Green, ["appx"] = IconPalette.Green,
                ["deb"] = IconPalette.Green, ["rpm"] = IconPalette.Green,
                ["pem"] = IconPalette.Green, ["crt"] = IconPalette.Green, ["cer"] = IconPalette.Green,
                ["key"] = IconPalette.Green, ["pfx"] = IconPalette.Green, ["p12"] = IconPalette.Green,
                ["pub"] = IconPalette.Green, ["ppk"] = IconPalette.Green, ["asc"] = IconPalette.Green,
                ["gpg"] = IconPalette.Green, ["sig"] = IconPalette.Green,

                // Pink — raster and design source, matching PNG's neighbours MP3/AVI/MOV.
                // the raster formats that used to letter here now share the Image File page above.
                // What is left is editable DOCUMENTS that happen to be graphics — an .xcf or an
                // .afdesign is a project you open in one app, not a picture the row can claim to
                // be showing — plus 3D and CAD geometry.
                ["xcf"] = IconPalette.Pink, ["sketch"] = IconPalette.Pink,
                ["afphoto"] = IconPalette.Pink, ["afdesign"] = IconPalette.Pink,
                ["cdr"] = IconPalette.Pink,
                ["obj"] = IconPalette.Pink, ["fbx"] = IconPalette.Pink, ["stl"] = IconPalette.Pink,
                ["dxf"] = IconPalette.Pink, ["ttc"] = IconPalette.Pink,

                // Cyan — video and timed text. Also the implicit default, so these entries exist
                // only to document the intent; removing one changes nothing today.
                // the containers that used to letter here now share the Video File page above;
                // subtitles, playlists and editor projects are not video and stay lettered.
                ["srt"] = IconPalette.Cyan, ["vtt"] = IconPalette.Cyan, ["ass"] = IconPalette.Cyan,
                ["ssa"] = IconPalette.Cyan, ["sub"] = IconPalette.Cyan, ["m3u"] = IconPalette.Cyan,
                ["m3u8"] = IconPalette.Cyan, ["prproj"] = IconPalette.Cyan, ["kdenlive"] = IconPalette.Cyan,
            }.ToFrozenDictionary(StringComparer.Ordinal);

        private static readonly string[] _allSlugs = BuildAllSlugs();

        /// <summary>Every slug this catalog can name, sorted. There must be exactly one
        /// <c>Assets/FileIcons/&lt;slug&gt;.svg</c> and one SOURCES.tsv row per entry — asserted by
        /// the covering test, because nothing checks it at runtime.</summary>
        public static IReadOnlyList<string> AllSlugs => _allSlugs;

        /// <summary>Extension (normalized, dot-stripped) to shipped asset slug.</summary>
        public static FrozenDictionary<string, string> ExtensionToSlug => _extensionToSlug;

        /// <summary>Extension (normalized, dot-stripped) to the palette its generated page uses.
        /// An extension absent from this map generates a <see cref="IconPalette.Cyan"/> page.</summary>
        public static FrozenDictionary<string, IconPalette> ExtensionToPalette => _extensionToPalette;

        /// <summary>
        /// Reduces anything file-shaped — "REPORT.PDF", ".tar.gz", "C:\x\y.PNG", a raw ".ps1" — to a
        /// bare lowercase extension of at most <see cref="MaxExtensionLength"/> characters drawn
        /// only from [a-z0-9]. This is a sanitizer, not a parser: the input reaches us from disk and
        /// from upload manifests, and the result is interpolated straight into an <c>avares://</c>
        /// URI and into an icon cache key, so every other character is dropped rather than escaped.
        /// Returns an empty string when nothing survives.
        /// </summary>
        public static string NormalizeExtension(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath))
                return string.Empty;

            // Take the segment after the last dot, but only if the dot is in the last path segment
            // — "C:\v1.2\README" has a dot that is not an extension.
            int lastDot = nameOrPath.LastIndexOf('.');
            int lastSep = nameOrPath.LastIndexOfAny(new[] { '/', '\\', ':' });
            int start = lastDot > lastSep ? lastDot + 1 : lastSep + 1;

            var sb = new StringBuilder(MaxExtensionLength);
            for (int i = start; i < nameOrPath.Length && sb.Length < MaxExtensionLength; i++)
            {
                char c = nameOrPath[i];
                if (c >= 'a' && c <= 'z') sb.Append(c);
                else if (c >= 'A' && c <= 'Z') sb.Append((char)(c + 32));
                else if (c >= '0' && c <= '9') sb.Append(c);
                // Everything else — spaces, dots, unicode, path separators, control bytes — is
                // dropped. A name that normalizes to junk simply misses both maps and gets the
                // blank page, which is the correct outcome.
            }

            return sb.ToString();
        }

        /// <summary>
        /// The icon tag for a file extension, in the one flat key space FileIconRenderer memoizes
        /// on: <c>"file:&lt;slug&gt;"</c> for shipped art, <c>"gen:&lt;palette&gt;:&lt;LETTERS&gt;"</c>
        /// for a page the renderer typesets. The input may be a bare extension, a file name or a
        /// full path; it is normalized here so no caller has to remember to.
        /// </summary>
        public static string TagForExtension(string ext)
        {
            string norm = NormalizeExtension(ext);
            if (norm.Length == 0)
                return "file:" + UnknownSlug;

            if (_extensionToSlug.TryGetValue(norm, out string slug))
                return "file:" + slug;

            IconPalette palette = _extensionToPalette.TryGetValue(norm, out var p) ? p : IconPalette.Cyan;

            // The letters are carried even when they are too long to typeset — the renderer owns
            // the "more than five characters draws a blank page" rule (architecture section 12), and
            // keeping the tag faithful means a future change to that rule needs no cache flush here.
            return "gen:" + PaletteKey(palette) + ":" + norm.ToUpperInvariant();
        }

        /// <summary>
        /// The icon tag for a session that has no usable extension at all, resolved from
        /// SessionInfo.ContentKind. <paramref name="isVideoProject"/> wins outright: a blank video
        /// project has no media on disk yet, and its ContentKind may still say whatever the session
        /// was created as.
        /// </summary>
        public static string TagForContentKind(string kind, bool isVideoProject)
        {
            if (isVideoProject)
                return "file:video-file";

            if (string.IsNullOrEmpty(kind))
                return "file:" + UnknownSlug;

            if (kind.Equals("image", StringComparison.OrdinalIgnoreCase)) return "file:image-file";
            if (kind.Equals("video", StringComparison.OrdinalIgnoreCase)) return "file:video-file";
            if (kind.Equals("text", StringComparison.OrdinalIgnoreCase)) return "file:txt";
            return "file:" + UnknownSlug;
        }

        /// <summary>The avares URI of a shipped SVG. Safe to compose because
        /// <paramref name="slug"/> can only have come from this class's own tables.</summary>
        public static string AssetUriForSlug(string slug) => AssetUriPrefix + slug + ".svg";

        /// <summary>Lowercase, stable spelling of a palette for the <c>gen:</c> tag. Deliberately
        /// not <c>ToString().ToLowerInvariant()</c> — the tag is a cache key, and renaming an enum
        /// member should not silently repartition the renderer's cache.</summary>
        private static string PaletteKey(IconPalette palette) => palette switch
        {
            IconPalette.Yellow => "yellow",
            IconPalette.Pink => "pink",
            IconPalette.Green => "green",
            _ => "cyan",
        };

        private static string[] BuildAllSlugs()
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var slug in _extensionToSlug.Values)
                set.Add(slug);

            // The three kind-driven slugs and the terminal fallback are reachable through
            // TagForContentKind without appearing in the extension table.
            set.Add(UnknownSlug);
            set.Add("image-file");
            set.Add("video-file");
            set.Add("txt");

            var result = new string[set.Count];
            set.CopyTo(result);
            return result;
        }
    }
}
