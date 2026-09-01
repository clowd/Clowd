using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using SkiaSharp;

namespace Clowd.UI.Preview.Icons
{
    /// <summary>
    /// Turns an icon tag into pixels: either one of the shipped icons8 "fluent" SVGs under
    /// <c>Assets/FileIcons</c>, or the lettered file page typeset here at runtime for the
    /// extensions icons8 has no artwork for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Worker threads only.</b> The first call forces three cold starts that must not land on the UI or
    /// render thread: FileIconCatalog's frozen dictionaries (~1 ms of dictionary building in a static field
    /// initializer), IconFonts' embedded-resource font load, and the first asset open. Everything after that
    /// is a dictionary probe.
    /// </para>
    /// <para>
    /// Two memo tiers, sized so preview churn can never evict them (there are 77 shipped slugs and a
    /// generated page per unseen extension, shared across thousands of rows):
    /// parsed <see cref="MiniSvgDocument"/> per slug, capped at 96, and finished
    /// <see cref="PreviewPixels"/> per (tag, pixel size), capped at 128. A parsed document owns native SKPath
    /// and SKShader handles, so eviction disposes it — which is also why every path through this class holds
    /// <c>_gate</c>: Lane A runs two workers, and a document must not be disposed while the other one is
    /// mid-<c>Draw</c>. Rasterizing is well under a millisecond and almost always a memo hit, so serializing
    /// it costs nothing worth measuring and buys a cache that needs no per-entry refcount.
    /// </para>
    /// </remarks>
    public static class FileIconRenderer
    {
        /// <summary>Parsed-document cap (architecture section 5.4). Comfortably above the 77 shipped
        /// slugs, so in practice nothing is ever evicted and no icon is parsed twice.</summary>
        private const int DocumentCapacity = 96;

        /// <summary>Rasterized-result cap (architecture section 5.4). Keyed by tag AND size, so the same
        /// icon at 48 and at 96 are two entries — a mixed-DPI multi-monitor session is the normal case.</summary>
        private const int RasterCapacity = 128;

        // A preview tile asks for 48/72/96/144. The bounds only exist so a bad caller cannot ask for a
        // zero-sized surface or a 64 MB one; they are not a policy.
        private const int MinPixelSize = 8;
        private const int MaxPixelSize = 512;

        private const string FileTagPrefix = "file:";
        private const string GeneratedTagPrefix = "gen:";

        private static readonly object _gate = new object();

        private static readonly Lru<string, MiniSvgDocument> _documents =
            new Lru<string, MiniSvgDocument>(DocumentCapacity, static doc => doc.Dispose());

        private static readonly Lru<RasterKey, PreviewPixels> _rasters =
            new Lru<RasterKey, PreviewPixels>(RasterCapacity, null);

        // One line per slug that failed to load, for the life of the process. A missing or unparsable asset
        // is a packaging/authoring regression, not a transient condition: it will fail identically on every
        // one of the thousands of rows that ask for it, so it gets said once.
        private static readonly HashSet<string> _reportedSlugs = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Rasterizes an icon tag to a square BGRA buffer. <paramref name="iconTag"/> is the flat key space
        /// FileIconCatalog produces: <c>"file:&lt;slug&gt;"</c> for shipped art or
        /// <c>"gen:&lt;palette&gt;:&lt;LETTERS&gt;"</c> for a generated page. Result is memoized per
        /// (tag, size) and, apart from the very first call for a given asset, involves no I/O at all.
        /// </summary>
        /// <returns>
        /// The pixels, always <see cref="PreviewKind.Icon"/>. Null only when Skia could not give us a raster
        /// surface at all — a missing, malformed or unparsable asset degrades to the blank file page instead,
        /// so callers never have to distinguish "no icon" from "wrong icon".
        /// </returns>
        public static PreviewPixels Render(string iconTag, int pixelSize)
        {
            if (string.IsNullOrEmpty(iconTag))
                iconTag = FileTagPrefix + FileIconCatalog.UnknownSlug;

            pixelSize = Math.Clamp(pixelSize, MinPixelSize, MaxPixelSize);

            var key = new RasterKey(iconTag, pixelSize);
            lock (_gate)
            {
                if (_rasters.TryGet(key, out var cached))
                    return cached;

                var pixels = RenderCore(iconTag, pixelSize);
                // A null is a Skia surface failure, which is a process-level condition; memoizing it would
                // turn a transient allocation failure into a permanently iconless session.
                if (pixels != null)
                    _rasters.Set(key, pixels);
                return pixels;
            }
        }

        private static PreviewPixels RenderCore(string iconTag, int pixelSize)
        {
            if (iconTag.StartsWith(GeneratedTagPrefix, StringComparison.Ordinal))
            {
                ParseGeneratedTag(iconTag, out var palette, out string letters);
                return Rasterize(pixelSize, canvas => DrawLetteredPage(canvas, pixelSize, palette, letters));
            }

            string slug = ParseSlug(iconTag);
            var document = GetDocument(slug);

            // The fallback chain of architecture section 11: the named asset, then the blank File page's
            // asset, then the lettered template with no letters — which IS the blank File page, drawn from
            // the same geometry and palette the shipped one was authored with.
            if (document == null && !string.Equals(slug, FileIconCatalog.UnknownSlug, StringComparison.Ordinal))
                document = GetDocument(FileIconCatalog.UnknownSlug);

            if (document == null)
                return Rasterize(pixelSize, canvas => DrawLetteredPage(canvas, pixelSize, IconPalette.Cyan, string.Empty));

            var doc = document;
            return Rasterize(pixelSize, canvas =>
            {
                // The canonical replay: scale the viewBox onto the surface, then let the document draw in
                // its own units. Both axes are scaled independently because nothing guarantees a square
                // viewBox, even though all 77 shipped assets have one.
                canvas.Scale(pixelSize / doc.ViewBox.Width, pixelSize / doc.ViewBox.Height);
                canvas.Translate(-doc.ViewBox.Left, -doc.ViewBox.Top);
                doc.Draw(canvas);
            });
        }

        /// <summary>Parses and memoizes one shipped asset. Caller holds <c>_gate</c>.</summary>
        private static MiniSvgDocument GetDocument(string slug)
        {
            if (slug.Length == 0)
                return null;

            if (_documents.TryGet(slug, out var cached))
                return cached;

            MiniSvgDocument document;
            try
            {
                // Everything AssetLoader-shaped stays inside the try, the Uri construction included:
                // AssetLoader.Exists throws on a malformed URI rather than returning false (the
                // TrackTip.axaml.cs:88-105 precedent), and Open throws for an asset that is simply absent.
                using var stream = AssetLoader.Open(new Uri(FileIconCatalog.AssetUriForSlug(slug)));
                document = MiniSvg.Parse(stream);
            }
            catch (Exception ex)
            {
                if (_reportedSlugs.Add(slug))
                    Debug.WriteLine("FileIconRenderer: icon asset '" + slug + "' is unusable (" + ex.Message +
                                    "); falling back to the blank file page.");
                return null;
            }

            _documents.Set(slug, document);
            return document;
        }

        /// <summary>Extracts the slug from a <c>file:</c> tag, sanitized. Returns an empty string for
        /// anything else, which sends the caller down the fallback chain.</summary>
        private static string ParseSlug(string iconTag)
        {
            if (!iconTag.StartsWith(FileTagPrefix, StringComparison.Ordinal))
                return string.Empty;

            string slug = iconTag.Substring(FileTagPrefix.Length);

            // Render is public and its tag is a string, so the slug is validated here rather than trusted:
            // it goes straight into an avares:// URI, and the one thing we will not do is compose a URI out
            // of unchecked text. Well-formed tags always pass — every slug the catalog names is
            // lowercase alphanumerics plus a hyphen ("c-lang", "image-file", "visual-studio").
            if (slug.Length == 0 || slug.Length > 32)
                return string.Empty;

            foreach (char c in slug)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
                if (!ok)
                    return string.Empty;
            }

            return slug;
        }

        /// <summary>Splits <c>gen:&lt;palette&gt;:&lt;LETTERS&gt;</c>. An unrecognized palette word falls to
        /// Cyan, which is the catalog's own default, and the letters are re-normalized so a hand-made tag
        /// cannot put arbitrary text on a file page.</summary>
        private static void ParseGeneratedTag(string iconTag, out IconPalette palette, out string letters)
        {
            palette = IconPalette.Cyan;
            letters = string.Empty;

            int paletteStart = GeneratedTagPrefix.Length;
            int split = iconTag.IndexOf(':', paletteStart);
            if (split < 0)
                return;

            switch (iconTag.Substring(paletteStart, split - paletteStart))
            {
                case "yellow": palette = IconPalette.Yellow; break;
                case "pink": palette = IconPalette.Pink; break;
                case "green": palette = IconPalette.Green; break;
                default: palette = IconPalette.Cyan; break;
            }

            letters = FileIconCatalog.NormalizeExtension(iconTag.Substring(split + 1)).ToUpperInvariant();
        }

        private static PreviewPixels Rasterize(int pixelSize, Action<SKCanvas> draw)
        {
            // Composited premultiplied — the only alpha type Skia will render into — and read back
            // unpremultiplied, which is what PreviewPixels promises and what the engine hands to a
            // WriteableBitmap created with AlphaFormat.Unpremul.
            var surfaceInfo = new SKImageInfo(pixelSize, pixelSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(surfaceInfo);
            if (surface == null)
                return null;

            surface.Canvas.Clear(SKColors.Transparent);
            draw(surface.Canvas);
            surface.Canvas.Flush();

            using var pixmap = surface.PeekPixels();
            if (pixmap == null)
                return null;

            var bgra = new byte[(long)pixelSize * pixelSize * 4];
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                var target = new SKImageInfo(pixelSize, pixelSize, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                if (!pixmap.ReadPixels(target, pin.AddrOfPinnedObject(), pixelSize * 4))
                    return null;
            }
            finally
            {
                pin.Free();
            }

            return new PreviewPixels(bgra, pixelSize, pixelSize, PreviewKind.Icon);
        }

        // ---------------------------------------------------------------------------------------------
        // The generated lettered page (architecture section 12).
        //
        // Geometry and palettes are transcribed from the real icons8 "fluent" art we ship beside them —
        // the WAV page (icons8 id hzrhjFl7EGX1) for the yellow lettered case and the blank File page
        // (XWoSyGbnshH2) for the cyan one — so a generated icon is the same drawing as a shipped one with
        // different letters on it, not a lookalike. Same provenance-comment convention as
        // Assets/VectorIcons.axaml:196. All coordinates are in the 48-unit icons8 viewBox.
        // ---------------------------------------------------------------------------------------------

        /// <summary>The page itself. Byte-identical across the whole icons8 lettered family.</summary>
        private const string BodyPathData =
            "M39,16v25c0,1.105-0.895,2-2,2H11c-1.105,0-2-0.895-2-2V7c0-1.105,0.895-2,2-2h17L39,16z";

        /// <summary>The folded top-right corner. Also byte-identical across the family.</summary>
        private const string CornerPathData = "M28,5v9c0,1.105,0.895,2,2,2h9L28,5z";

        // Lettering constants, all measured off the WAV reference art rather than chosen. Changing any of
        // them changes how a generated icon sits next to a shipped one, so they are named, not inlined.

        /// <summary>Size the face is first measured at; the real size is solved from its cap height.</summary>
        private const float ProbeSize = 10f;

        /// <summary>The WAV reference cap height: its glyph tops sit at y=22 and its baseline at y=29.883.
        /// Solving for this is what makes a 3-letter generated page match the shipped art exactly.</summary>
        private const float ReferenceCapHeight = 7.883f;

        /// <summary>The WAV reference ink band, x from 11.211 to 36.789. Wider text is condensed into it.</summary>
        private const float ReferenceInkWidth = 25.6f;

        /// <summary>The reference baseline.</summary>
        private const float LetterBaselineY = 29.883f;

        /// <summary>Horizontal centre of the page. The INK box is centred on this, not the advance width —
        /// that is how the real art is optically centred, and it is why a 1-letter and a 4-letter page look
        /// like the same design.</summary>
        private const float LetterCentreX = 24f;

        /// <summary>Below this the letters stop being readable at 36 logical pixels, so a page that would
        /// need to go smaller keeps the size and lets the condensing above do the work.</summary>
        private const float MinLetterSize = 6.5f;

        /// <summary>Above this a 1-letter page starts to look like a different icon family.</summary>
        private const float MaxLetterSize = 12f;

        /// <summary>At five characters the text is condensed rather than shrunk below legibility.</summary>
        private const float CondensedScaleX = 0.85f;

        /// <summary>More than this and the page is drawn blank. A blank page beats mud, and the tag still
        /// carries the letters — FileIconCatalog deliberately does not apply this rule, so it can change
        /// here without invalidating a single cache key.</summary>
        private const int MaxLetterCount = 5;

        private static void DrawLetteredPage(SKCanvas canvas, int pixelSize, IconPalette palette, string letters)
        {
            canvas.Scale(pixelSize / (float)PreviewFormat.IconUnitPx);

            var template = TemplateFor(palette);

            // Parsed per rasterization rather than cached: a generated page is memoized by (tag, size) so
            // this runs once per distinct icon, and an SKPath shared between the two Lane A workers would
            // have to be guarded even though the parse costs microseconds.
            using var body = SKPath.ParseSvgPathData(BodyPathData);
            using var corner = SKPath.ParseSvgPathData(CornerPathData);
            if (body == null || corner == null)
                return;

            body.FillType = SKPathFillType.Winding;
            corner.FillType = SKPathFillType.Winding;

            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            Fill(canvas, paint, body, template.Body);
            Fill(canvas, paint, corner, template.Corner);

            if (letters.Length > 0 && letters.Length <= MaxLetterCount)
                DrawLetters(canvas, letters, template.Letters);
        }

        private static void Fill(SKCanvas canvas, SKPaint paint, SKPath path, in IconFill fill)
        {
            using var shader = fill.CreateShader();
            paint.Shader = shader;
            paint.Color = fill.Start;
            canvas.DrawPath(path, paint);
            // Detached before the shader is disposed at the end of this scope.
            paint.Shader = null;
        }

        private static void DrawLetters(SKCanvas canvas, string text, SKColor color)
        {
            using var font = new SKFont(IconFonts.Lettering, ProbeSize)
            {
                // The canvas is already scaled to the target size, so the glyphs are outlines being
                // transformed, not a grid-fitted screen font. Hinting would fight that transform.
                Hinting = SKFontHinting.None,
                Edging = SKFontEdging.Antialias,
                Subpixel = true,
            };

            // Skia measures cap height in its y-down convention, so the metric comes back negative; it also
            // documents zero as "unknown", which a substitute face picked up by IconFonts' fallback chain
            // can genuinely report. Taking the magnitude and backstopping zero keeps a wrong-but-drawn page
            // instead of an infinite font size.
            float capHeight = Math.Abs(font.Metrics.CapHeight);
            if (capHeight <= 0.01f)
                capHeight = ProbeSize * 0.72f;

            font.Size = ProbeSize * ReferenceCapHeight / capHeight;

            if (text.Length == MaxLetterCount)
                font.ScaleX = CondensedScaleX;

            font.MeasureText(text, out SKRect ink);
            if (ink.Width > ReferenceInkWidth)
            {
                font.Size *= ReferenceInkWidth / ink.Width;
                font.MeasureText(text, out ink);
            }

            float clamped = Math.Clamp(font.Size, MinLetterSize, MaxLetterSize);
            if (clamped != font.Size)
            {
                // The ink box drives the centring below, so a clamp that moved the size has to be measured
                // again — otherwise a clamped page is centred for a size it is not drawn at.
                font.Size = clamped;
                font.MeasureText(text, out ink);
            }

            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
            canvas.DrawText(text, LetterCentreX - ink.MidX, LetterBaselineY, font, paint);
        }

        private static IconTemplate TemplateFor(IconPalette palette) => palette switch
        {
            // Documents, archives, audio. The gradient endpoints are the WAV icon's own
            // x1=18.02 y1=2271.777 x2=27.655 y2=2308.597 with its gradientTransform="translate(0 -2266)"
            // folded in, which is why there is no transform here.
            IconPalette.Yellow => new IconTemplate(
                IconFill.Gradient(0xfffede00, 0xffffd000, 18.02f, 5.777f, 27.655f, 42.597f),
                IconFill.Flat(0xffe97c01),
                new SKColor(0xff3c3c3c)),

            // Code and config.
            IconPalette.Green => new IconTemplate(
                IconFill.Flat(0xff33c4a2),
                IconFill.Flat(0xff107c41),
                new SKColor(0xff185c37)),

            // Raster and design source.
            IconPalette.Pink => new IconTemplate(
                IconFill.Flat(0xffffadc8),
                IconFill.Flat(0xffe72636),
                new SKColor(0xffe72636)),

            // Video and the default. The corner gradient is verbatim from the blank File page, which is
            // what makes a letterless cyan page identical to the shipped file.svg.
            _ => new IconTemplate(
                IconFill.Flat(0xff50e6ff),
                IconFill.Gradient(0xff3079d6, 0xff297cd2, 28.529f, 15.472f, 33.6f, 10.4f),
                new SKColor(0xff3c3c3c)),
        };

        /// <summary>One palette's three fills.</summary>
        private readonly struct IconTemplate
        {
            public IconTemplate(in IconFill body, in IconFill corner, SKColor letters)
            {
                Body = body;
                Corner = corner;
                Letters = letters;
            }

            public readonly IconFill Body;
            public readonly IconFill Corner;
            public readonly SKColor Letters;
        }

        /// <summary>A flat colour or a two-stop linear gradient in viewBox units — the only two fills the
        /// icons8 lettered family uses.</summary>
        private readonly struct IconFill
        {
            private IconFill(SKColor start, SKColor end, SKPoint from, SKPoint to, bool isGradient)
            {
                Start = start;
                _end = end;
                _from = from;
                _to = to;
                _isGradient = isGradient;
            }

            /// <summary>The flat colour, or the gradient's first stop. Doubles as the paint colour a
            /// gradient shader is multiplied against, where only its alpha matters.</summary>
            public readonly SKColor Start;

            private readonly SKColor _end;
            private readonly SKPoint _from;
            private readonly SKPoint _to;
            private readonly bool _isGradient;

            public static IconFill Flat(uint argb)
                => new IconFill(new SKColor(argb), default, default, default, false);

            public static IconFill Gradient(uint argbStart, uint argbEnd, float x0, float y0, float x1, float y1)
                => new IconFill(new SKColor(argbStart), new SKColor(argbEnd),
                                new SKPoint(x0, y0), new SKPoint(x1, y1), true);

            /// <summary>The shader for this fill, or null when it is flat. Caller owns it.</summary>
            public SKShader CreateShader()
                => _isGradient
                    ? SKShader.CreateLinearGradient(_from, _to, new[] { Start, _end }, null, SKShaderTileMode.Clamp)
                    : null;
        }

        private readonly record struct RasterKey(string Tag, int PixelSize);

        /// <summary>
        /// A plain capacity-bounded LRU. Both icon caches are small, fixed and read far more often than
        /// written, so this is a Dictionary over a LinkedList rather than anything cleverer; the eviction
        /// callback exists because one of the two values owns native handles.
        /// </summary>
        private sealed class Lru<TKey, TValue>
        {
            private readonly int _capacity;
            private readonly Action<TValue> _onEvict;
            private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map;

            // Most-recently-used at the head.
            private readonly LinkedList<KeyValuePair<TKey, TValue>> _order =
                new LinkedList<KeyValuePair<TKey, TValue>>();

            public Lru(int capacity, Action<TValue> onEvict)
            {
                _capacity = capacity;
                _onEvict = onEvict;
                _map = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(capacity);
            }

            public bool TryGet(TKey key, out TValue value)
            {
                if (!_map.TryGetValue(key, out var node))
                {
                    value = default;
                    return false;
                }

                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }

            public void Set(TKey key, TValue value)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    _order.Remove(existing);
                    _map.Remove(key);
                    _onEvict?.Invoke(existing.Value.Value);
                }

                _map[key] = _order.AddFirst(new KeyValuePair<TKey, TValue>(key, value));

                while (_map.Count > _capacity)
                {
                    var oldest = _order.Last;
                    _order.RemoveLast();
                    _map.Remove(oldest.Value.Key);
                    _onEvict?.Invoke(oldest.Value.Value);
                }
            }
        }
    }
}
