using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// One theme of a cursor pack, as the recoloring it performs: which of the stored artwork's
    /// colors becomes which. A color the pack writes literally and means literally — the white of
    /// a "no entry" bar, the macOS spinner's own four — is not in the map and passes through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two families reach a palette from opposite directions. A ful1e5 pack draws every cursor
    /// in placeholder colors (<c>#00FF00</c> body, <c>#0000FF</c> outline, <c>#FF0000</c> accent)
    /// and ships a <c>render.json</c> naming the real color of each per theme, so its map is those
    /// three keys. Neon ships one folder per color instead, all the same drawing; one of them is
    /// stored and the rest are its map, keyed on that stored theme's own colors.
    /// </para>
    /// <para>
    /// The background library reuses this class unchanged: each generative wallpaper's palette
    /// themes are a map from the file's own fills to a shared swatch table (see
    /// <see cref="BackgroundCatalog"/>), applied to flat fills and gradient stops alike.
    /// </para>
    /// </remarks>
    internal sealed class CursorPackPalette
    {
        private readonly Dictionary<uint, uint> _map = new Dictionary<uint, uint>();

        /// <summary>A palette from (source, target) pairs, both as <c>0xRRGGBB</c>.</summary>
        internal CursorPackPalette(params (uint From, uint To)[] pairs)
        {
            foreach (var (from, to) in pairs)
                _map[from & 0x00FFFFFF] = to & 0x00FFFFFF;
        }

        /// <summary>
        /// The theme color for a source color, keeping its alpha: only the RGB decides, so a
        /// mapped color that reaches here part-transparent (a ful1e5 pack fades one through a
        /// gradient, Neon draws its outer glow at 55%) is still the same key.
        /// </summary>
        internal uint Resolve(uint sourceArgb)
            => _map.TryGetValue(sourceArgb & 0x00FFFFFF, out var target)
                ? (sourceArgb & 0xFF000000) | target
                : sourceArgb;
    }

    /// <summary>
    /// Reads the embedded artwork of the SVG-sourced cursor packs — the ful1e5 four (Bibata,
    /// BreezeX, macOS, Fuchsia) and Neon — into <see cref="CursorGlyph"/>s. The files under
    /// <c>Composition/CursorPacks</c> are each pack's own SVG sources, copied verbatim but for the
    /// ful1e5 packs' raster filter chains and clip rects; re-syncing a pack is a re-copy, and no
    /// artwork is transcribed or traced along the way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the packs use is a narrow slice of SVG — flat <c>&lt;path&gt;</c>, <c>&lt;circle&gt;</c>
    /// and <c>&lt;rect&gt;</c> in one viewBox (256 units for the ful1e5 packs, 32 for Neon), paint
    /// inherited down a few groups, a handful of transforms, and the gradients, Figma "outside
    /// stroke" masks and SMIL animations noted below — so this is a reader for that slice and not an
    /// SVG engine. Anything it does not understand is skipped rather than guessed at.
    /// </para>
    /// <para>The five places the source cannot be taken literally, and what happens instead:</para>
    /// <list type="bullet">
    /// <item><b>Gradients.</b> A <c>fill="url(#…)"</c> is flattened to its stops' average, weighted
    /// by their offsets — the packs use them for the macOS beachball's segments and for a soft
    /// vignette on BreezeX's crosshair, and both read correctly as one color at cursor size.</item>
    /// <item><b>Outside strokes.</b> Figma exports an outside stroke either as a second, expanded
    /// path (which needs nothing — it is already the outline ring) or as a masked path over a
    /// duplicate of the shape. The masked path cannot be drawn without the mask, so it is dropped
    /// and the duplicate pair it belonged to collapses into one layer: the upper fill, haloed in
    /// the lower one's color, at the width the mask's own bounds imply.</item>
    /// <item><b>Centered strokes.</b> A <c>stroke</c> attribute paints centered and <i>over</i> the
    /// fill, so all of it shows; a halo here paints under the fill and only its outer half shows.
    /// The stored width is therefore twice the authored one, which keeps the outline as thick as
    /// the pack drew it at the cost of half a stroke on the silhouette.</item>
    /// <item><b>Even-odd fills.</b> Normalized to the nonzero winding every stored path is in, so
    /// a hole stays a hole when <c>CursorCompose</c> parses it back.</item>
    /// <item><b>Blur.</b> Neon draws each cursor three times — a wide dim stroke, a brighter one,
    /// and a pale core — with the outer two under a Gaussian blur that makes them a glow. There is
    /// no blur to be had here, so the three land as concentric bands: the same colors at the same
    /// widths and opacities, with a hard edge where the source has a falloff. It reads as neon,
    /// drawn flat.</item>
    /// </list>
    /// <para>
    /// A layer the pack leaves unstroked stays unstroked. These packs do not halo every shape the
    /// way Vision and Point do — the ful1e5 four draw the outline as a solid backing silhouette and
    /// stack the body color inside it, Neon stacks its glow bands — so a glyph's contrast comes
    /// from the layers under the top one, not from a stroke on each.
    /// </para>
    /// </remarks>
    internal static class CursorPackLoader
    {
        private const string ResourceRoot = "Clowd.VideoSDK.Composition.CursorPacks.";

        /// <summary>The pack's authored stroke width doubled, since a halo only shows its outer
        /// half; see the class remarks.</summary>
        private const float HaloScale = 2f;

        private static readonly Assembly Assembly = typeof(CursorPackLoader).Assembly;

        private static readonly string[] ResourceNames = Assembly.GetManifestResourceNames();

        /// <summary>The still for a (pack, kind) pair in one theme's colors, or null when the pack
        /// carries no artwork for it.</summary>
        internal static CursorGlyph LoadStatic(string pack, string kind, CursorPackPalette palette,
            float hotspotX, float hotspotY)
        {
            var document = TryRead(ResourceRoot + pack + "." + kind + ".svg");
            return document == null ? null : ReadGlyph(document, palette, hotspotX, hotspotY);
        }

        /// <summary>
        /// The looping animation for a (pack, kind) pair, however the pack spells one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A ful1e5 pack draws every frame and ships them as a folder, so those are read in file
        /// order at the pack's own declared delay and handed to <see cref="CursorGlyph"/>'s
        /// frame-list constructor as they are.
        /// </para>
        /// <para>
        /// Neon animates in the file instead, with SMIL — a <c>&lt;animate&gt;</c> cycling the
        /// stroke through its eight hues and an <c>&lt;animateTransform&gt;</c> pulsing the shape's
        /// scale. Nothing downstream can hold a live animation, so the loop is sampled into stills
        /// here, one every <paramref name="frameMs"/> across <paramref name="periodMs"/>. Each
        /// declared duration is stretched to the nearest whole number of cycles inside that period
        /// so every one closes where it opened (see <see cref="AnimationPhase"/>) — Neon's 6 s
        /// color cycle runs a touch slower for it, its 1.6 s pulse not at all.
        /// </para>
        /// <para>Null when the pack has neither a frame folder nor a file for the kind.</para>
        /// </remarks>
        internal static CursorGlyph LoadAnimated(string pack, string kind, CursorPackPalette palette,
            float hotspotX, float hotspotY, float frameMs, float periodMs = 0)
        {
            string prefix = ResourceRoot + pack + "." + kind + ".";
            string single = prefix + "svg";
            // A frame is `<pack>.<kind>.<number>.svg`, which shares its prefix with the kind's own
            // `<pack>.<kind>.svg` — so the single file has to be held out, or a pack that animates
            // in one file would be read as a one-frame folder.
            var names = ResourceNames.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)
                    && n.EndsWith(".svg", StringComparison.Ordinal)
                    && !string.Equals(n, single, StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            if (names.Length > 0)
            {
                // Frame 0 fixes the geometry the loop is presented in: the packs' frames drift by a
                // fraction of a unit in viewBox and hotspot (their sources are exported one by one),
                // and an animated glyph is one shape at one size by construction.
                var drawn = new CursorGlyph[names.Length];
                float viewBox = 0;
                for (int i = 0; i < names.Length; i++)
                {
                    drawn[i] = ReadGlyph(TryRead(names[i]), palette, hotspotX, hotspotY,
                        i == 0 ? 0 : viewBox);
                    if (i == 0)
                        viewBox = drawn[0].ViewBox;
                }
                return new CursorGlyph(frameMs, drawn);
            }

            var document = TryRead(single);
            if (document == null)
                return null;
            // A kind the pack does not actually animate is simply its still.
            if (periodMs <= 0 || frameMs <= 0 || !document.Descendants()
                    .Any(e => e.Name.LocalName is "animate" or "animateTransform"))
            {
                return ReadGlyph(document, palette, hotspotX, hotspotY);
            }

            int count = Math.Max(2, (int)Math.Round(periodMs / frameMs));
            var sampled = new CursorGlyph[count];
            for (int i = 0; i < count; i++)
            {
                sampled[i] = ReadGlyph(document, palette, hotspotX, hotspotY,
                    i == 0 ? 0 : sampled[0].ViewBox, new AnimationClock(i * frameMs, periodMs));
            }
            return new CursorGlyph(frameMs, sampled);
        }

        /// <summary>The parsed resource, or null when the pack has no such file.</summary>
        private static XElement TryRead(string resourceName)
        {
            if (Array.IndexOf(ResourceNames, resourceName) < 0)
                return null;
            using var stream = Assembly.GetManifestResourceStream(resourceName);
            using var reader = new StreamReader(stream);
            return XDocument.Parse(reader.ReadToEnd()).Root;
        }

        /// <summary>Where in a sampled loop a frame sits, and how long the loop is. Null while
        /// reading a still, which is what makes every animation invisible to a static read.</summary>
        private sealed class AnimationClock
        {
            internal AnimationClock(float atMs, float periodMs)
            {
                AtMs = atMs;
                PeriodMs = periodMs;
            }

            internal float AtMs { get; }

            internal float PeriodMs { get; }
        }

        /// <summary>One SVG as a glyph. <paramref name="forceViewBox"/> overrides the file's own,
        /// which is how an animation's frames are held to frame 0's geometry.</summary>
        private static CursorGlyph ReadGlyph(XElement root, CursorPackPalette palette,
            float hotspotX, float hotspotY, float forceViewBox = 0, AnimationClock clock = null)
        {
            float viewBox = forceViewBox > 0 ? forceViewBox : ReadViewBox(root);
            var gradients = ReadGradients(root);
            var masks = ReadMasks(root);

            var layers = new List<Layer>();
            // Paint inherits, and every one of these files opens with `fill="none"` on the root —
            // which is what makes a bare `<path stroke=…>` an outline ring and not a black blob.
            Walk(root, SKMatrix.Identity, Paint.Root(root), gradients, layers, clock);
            var paths = Resolve(layers, masks, palette);

            // The hotspot is quoted against the packs' own 256-unit render; a file whose viewBox
            // drifted off 256 (a few of the animation frames do) scales it to match.
            float scale = viewBox / 256f;
            return new CursorGlyph(viewBox, hotspotX * scale, hotspotY * scale, paths);
        }

        // ------------------------------------------------------------------------------ reading

        /// <summary>The viewBox's longer side. Every pack authors square-ish boxes on 256; the odd
        /// 257 is an export artifact, and squaring off the longer side keeps the art inside.</summary>
        private static float ReadViewBox(XElement root)
        {
            var parts = ((string)root.Attribute("viewBox") ?? "0 0 256 256")
                .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            float width = parts.Length > 2 ? Num(parts[2]) : 256f;
            float height = parts.Length > 3 ? Num(parts[3]) : width;
            return Math.Max(width, height);
        }

        /// <summary>Gradient id → the one color it flattens to (see the class remarks).</summary>
        private static Dictionary<string, uint> ReadGradients(XElement root)
        {
            var table = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var gradient in root.Descendants()
                .Where(e => e.Name.LocalName is "linearGradient" or "radialGradient"))
            {
                string id = (string)gradient.Attribute("id");
                if (id == null)
                    continue;

                var stops = gradient.Elements().Where(e => e.Name.LocalName == "stop").ToArray();
                if (stops.Length == 0)
                    continue;

                // Each stop covers the span to its neighbors' midpoints, so a color that holds
                // over most of the ramp weighs most. A lone stop is the whole ramp.
                double r = 0, g = 0, b = 0, a = 0, total = 0;
                for (int i = 0; i < stops.Length; i++)
                {
                    float at = Num((string)stops[i].Attribute("offset") ?? (i == 0 ? "0" : "1"));
                    float lo = i == 0 ? 0f : (at + Num((string)stops[i - 1].Attribute("offset") ?? "0")) / 2f;
                    float hi = i == stops.Length - 1
                        ? 1f
                        : (at + Num((string)stops[i + 1].Attribute("offset") ?? "1")) / 2f;
                    double weight = Math.Max(hi - lo, 0.0001);

                    uint color = ParseColor((string)stops[i].Attribute("stop-color") ?? "black", 0xFF000000);
                    float opacity = Num((string)stops[i].Attribute("stop-opacity") ?? "1");
                    r += ((color >> 16) & 0xFF) * weight;
                    g += ((color >> 8) & 0xFF) * weight;
                    b += (color & 0xFF) * weight;
                    a += ((color >> 24) & 0xFF) * opacity * weight;
                    total += weight;
                }

                table[id] = (uint)Math.Round(a / total) << 24 | (uint)Math.Round(r / total) << 16
                    | (uint)Math.Round(g / total) << 8 | (uint)Math.Round(b / total);
            }
            return table;
        }

        /// <summary>Mask id → the bounds Figma padded around the shape, which is what says how far
        /// outside the path its outside stroke reached.</summary>
        private static Dictionary<string, SKRect> ReadMasks(XElement root)
        {
            var table = new Dictionary<string, SKRect>(StringComparer.Ordinal);
            foreach (var mask in root.Descendants().Where(e => e.Name.LocalName == "mask"))
            {
                string id = (string)mask.Attribute("id");
                if (id == null)
                    continue;
                float x = Num((string)mask.Attribute("x") ?? "0");
                float y = Num((string)mask.Attribute("y") ?? "0");
                float w = Num((string)mask.Attribute("width") ?? "0");
                float h = Num((string)mask.Attribute("height") ?? "0");
                if (w > 0 && h > 0)
                    table[id] = SKRect.Create(x, y, w, h);
            }
            return table;
        }

        /// <summary>A drawable element as read, before roles, masks and duplicates are settled.</summary>
        private sealed class Layer
        {
            internal string PathData;
            internal uint FillArgb;
            internal uint StrokeArgb;
            internal float StrokeWidth;
            internal bool FillIsNone;
            internal string MaskId;

            /// <summary>The stroke came from an animation's own value list rather than the stored
            /// artwork, so a theme must not recolor it: Neon's spinner cycles all eight of the
            /// pack's hues whichever theme is picked, and remapping the one that happens to be the
            /// stored theme's would put a duplicate in the cycle.</summary>
            internal bool StrokeIsLiteral;
        }

        /// <summary>
        /// The paint an element inherits from its ancestors. SVG resolves <c>fill</c>, <c>stroke</c>,
        /// <c>stroke-width</c> and <c>opacity</c> down the tree, and Neon leans on all four — it
        /// paints a whole glow layer by putting them on a <c>&lt;g&gt;</c> and leaving its paths
        /// bare.
        /// </summary>
        private readonly struct Paint
        {
            private Paint(string fill, string stroke, string strokeWidth, float opacity,
                bool strokeIsLiteral)
            {
                Fill = fill;
                Stroke = stroke;
                StrokeWidth = strokeWidth;
                Opacity = opacity;
                StrokeIsLiteral = strokeIsLiteral;
            }

            internal string Fill { get; }

            internal string Stroke { get; }

            internal string StrokeWidth { get; }

            /// <summary>Group opacities multiply down the tree.</summary>
            internal float Opacity { get; }

            /// <summary>The stroke came from an animation rather than the artwork, and inherits as
            /// such — the packs animate a whole group, and it is the paths inside it that end up
            /// carrying the color.</summary>
            internal bool StrokeIsLiteral { get; }

            /// <summary>The root's paint. Its <c>fill</c> defaults to <c>none</c>, which every one
            /// of these files states anyway — and is what makes a bare <c>&lt;path stroke=…&gt;</c>
            /// an outline and not a black blob.</summary>
            internal static Paint Root(XElement root) => new Paint(
                (string)root.Attribute("fill") ?? "none",
                (string)root.Attribute("stroke"),
                (string)root.Attribute("stroke-width"),
                Num((string)root.Attribute("opacity") ?? "1"),
                false);

            /// <summary>This paint with whatever <paramref name="element"/> overrides.</summary>
            internal Paint Inherit(XElement element)
            {
                string stroke = (string)element.Attribute("stroke");
                return new Paint(
                    (string)element.Attribute("fill") ?? Fill,
                    stroke ?? Stroke,
                    (string)element.Attribute("stroke-width") ?? StrokeWidth,
                    Opacity * Num((string)element.Attribute("opacity") ?? "1"),
                    // a stroke stated here is the artwork's own again
                    stroke == null && StrokeIsLiteral);
            }

            /// <summary>This paint with its stroke replaced by an animation's current value.</summary>
            internal Paint WithStroke(string stroke)
                => stroke == null ? this : new Paint(Fill, stroke, StrokeWidth, Opacity, true);
        }

        private static void Walk(XElement element, SKMatrix ctm, Paint inherited,
            Dictionary<string, uint> gradients, List<Layer> layers, AnimationClock clock)
        {
            foreach (var child in element.Elements())
            {
                switch (child.Name.LocalName)
                {
                    // Definitions and mask bodies are read separately; a filter is a raster effect
                    // (a drop shadow in the ful1e5 packs, Neon's glow blur) and has no place in flat
                    // artwork. The animation elements are read by whoever they animate.
                    case "defs":
                    case "mask":
                    case "filter":
                    case "clipPath":
                    case "animate":
                    case "animateTransform":
                        continue;

                    case "g":
                    {
                        var paint = inherited.Inherit(child).WithStroke(AnimatedStroke(child, clock));
                        var inner = Concat(Concat(ctm, ReadTransform(child)),
                            AnimatedTransform(child, clock));
                        Walk(child, inner, paint, gradients, layers, clock);
                        continue;
                    }

                    case "path":
                        AddLayer(child, (string)child.Attribute("d"), ctm, inherited, gradients,
                            layers, clock);
                        continue;

                    case "circle":
                        AddLayer(child, CirclePath(child), ctm, inherited, gradients, layers, clock);
                        continue;

                    case "rect":
                        AddLayer(child, RectPath(child), ctm, inherited, gradients, layers, clock);
                        continue;

                    default:
                        Walk(child, ctm, inherited.Inherit(child), gradients, layers, clock);
                        continue;
                }
            }
        }

        private static void AddLayer(XElement element, string pathData, SKMatrix ctm, Paint inherited,
            Dictionary<string, uint> gradients, List<Layer> layers, AnimationClock clock)
        {
            if (string.IsNullOrWhiteSpace(pathData))
                return;

            using var path = SKPath.ParseSvgPathData(pathData);
            if (path == null || path.IsEmpty)
                return;

            // The nonzero winding every stored path is in — see the class remarks.
            bool evenOdd = (string)element.Attribute("fill-rule") == "evenodd";
            if (evenOdd)
            {
                path.FillType = SKPathFillType.EvenOdd;
                using var wound = new SKPath();
                if (path.Simplify(wound))
                {
                    path.Rewind();
                    path.AddPath(wound);
                }
                path.FillType = SKPathFillType.Winding;
            }

            bool transformed = !ctm.IsIdentity;
            if (transformed)
                path.Transform(ctm);

            string animatedStroke = AnimatedStroke(element, clock);
            var paint = inherited.Inherit(element).WithStroke(animatedStroke);

            bool fillIsNone = paint.Fill == "none";
            uint fill = ApplyOpacity(ResolvePaint(paint.Fill, gradients, 0xFF000000),
                paint.Opacity * Num((string)element.Attribute("fill-opacity") ?? "1"));
            uint stroke = ApplyOpacity(ResolvePaint(paint.Stroke, gradients, 0), paint.Opacity);

            float strokeWidth = Num(paint.StrokeWidth ?? "0");
            if (transformed)
                strokeWidth *= AverageScale(ctm);

            layers.Add(new Layer
            {
                // Reserializing is only worth it when something actually moved; a path the reader
                // left alone keeps the pack's own bytes.
                PathData = transformed || evenOdd ? path.ToSvgPathData() : pathData,
                FillArgb = fill,
                StrokeArgb = stroke,
                StrokeWidth = strokeWidth,
                FillIsNone = fillIsNone,
                MaskId = MaskIdOf((string)element.Attribute("mask")),
                StrokeIsLiteral = paint.StrokeIsLiteral,
            });
        }

        // ----------------------------------------------------------------------------- animation

        /// <summary>
        /// Where a declared duration sits at this frame, as a fraction of one cycle. The duration is
        /// first stretched to the nearest whole number of cycles inside the sampled loop, so an
        /// animation always closes where it opened rather than jumping at the loop point; an
        /// animation longer than the loop runs once across it.
        /// </summary>
        private static float AnimationPhase(float declaredMs, AnimationClock clock)
        {
            if (declaredMs <= 0 || clock == null || clock.PeriodMs <= 0)
                return 0;
            float cycles = Math.Max(1, (float)Math.Round(clock.PeriodMs / declaredMs));
            float fitted = clock.PeriodMs / cycles;
            return clock.AtMs % fitted / fitted;
        }

        /// <summary>The <c>values</c> list of an animation as the pair it sits between at this
        /// frame's phase, walked as SMIL's default linear ramp — one segment per neighboring
        /// pair.</summary>
        private static bool SampleValues(XElement animation, AnimationClock clock,
            out string from, out string to, out float t)
        {
            from = to = null;
            t = 0;
            var values = ((string)animation.Attribute("values") ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (values.Length == 0)
                return false;
            if (values.Length == 1)
            {
                from = to = values[0].Trim();
                return true;
            }

            float phase = AnimationPhase(Milliseconds((string)animation.Attribute("dur")), clock);
            float at = phase * (values.Length - 1);
            int segment = Math.Min((int)at, values.Length - 2);
            from = values[segment].Trim();
            to = values[segment + 1].Trim();
            t = at - segment;
            return true;
        }

        /// <summary>The color an <c>&lt;animate attributeName="stroke"&gt;</c> child paints at this
        /// frame, or null when the element has none (or is being read as a still).</summary>
        private static string AnimatedStroke(XElement element, AnimationClock clock)
        {
            if (clock == null)
                return null;
            foreach (var animation in element.Elements()
                .Where(e => e.Name.LocalName == "animate"
                    && (string)e.Attribute("attributeName") == "stroke"))
            {
                if (!SampleValues(animation, clock, out var from, out var to, out float t))
                    continue;
                uint mixed = Mix(ParseColor(from, 0xFF000000), ParseColor(to, 0xFF000000), t);
                return "#" + (mixed & 0x00FFFFFF).ToString("X6", CultureInfo.InvariantCulture);
            }
            return null;
        }

        /// <summary>The transform an <c>&lt;animateTransform&gt;</c> child applies at this frame.
        /// Only <c>scale</c> is read — it is the one Neon uses, to pulse its spinner — and it
        /// replaces nothing, since the groups carrying it have no transform of their own.</summary>
        private static SKMatrix AnimatedTransform(XElement element, AnimationClock clock)
        {
            if (clock == null)
                return SKMatrix.Identity;
            foreach (var animation in element.Elements()
                .Where(e => e.Name.LocalName == "animateTransform"
                    && (string)e.Attribute("type") == "scale"))
            {
                if (!SampleValues(animation, clock, out var from, out var to, out float t))
                    continue;
                float a = Num(from);
                float b = Num(to);
                float scale = a + (b - a) * t;
                if (scale > 0)
                    return SKMatrix.CreateScale(scale, scale);
            }
            return SKMatrix.Identity;
        }

        /// <summary>Per-channel ARGB interpolation, <paramref name="t"/> in [0, 1].</summary>
        private static uint Mix(uint from, uint to, float t)
        {
            uint Channel(int shift)
            {
                int a = (int)((from >> shift) & 0xFF);
                int b = (int)((to >> shift) & 0xFF);
                return (uint)Math.Clamp((int)Math.Round(a + (b - a) * (double)t), 0, 255);
            }

            return Channel(24) << 24 | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
        }

        /// <summary>An SMIL duration (<c>6s</c>, <c>1.6s</c>, <c>400ms</c>) in milliseconds.</summary>
        private static float Milliseconds(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;
            text = text.Trim();
            if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
                return Num(text.Substring(0, text.Length - 2));
            if (text.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                return Num(text.Substring(0, text.Length - 1)) * 1000f;
            return Num(text) * 1000f;
        }

        // ---------------------------------------------------------------------------- resolving

        /// <summary>
        /// The read layers as stored ones: masked paths dropped and the duplicate pairs they stood
        /// for collapsed, bare strokes turned into the outline they draw, and every color put
        /// through the theme's palette.
        /// </summary>
        private static CursorGlyphPath[] Resolve(List<Layer> layers,
            Dictionary<string, SKRect> masks, CursorPackPalette palette)
        {
            // A dropped outside-stroke leaves its width behind in its own geometry: the masked path
            // is the shape grown by that width all round, so the gap between its bounds and the
            // shape's is the width itself. (The mask's declared box is padded further still, which
            // is why it is only used to recognize the pair.) Zero when the file has no such mask.
            var expanded = new List<SKRect>();
            foreach (var layer in layers)
            {
                if (layer.MaskId == null || !masks.ContainsKey(layer.MaskId))
                    continue;
                using var path = SKPath.ParseSvgPathData(layer.PathData);
                if (path != null && !path.IsEmpty)
                    expanded.Add(path.Bounds);
            }
            layers.RemoveAll(l => l.MaskId != null);

            float OutsideWidthFor(string pathData)
            {
                if (expanded.Count == 0)
                    return 0;
                using var path = SKPath.ParseSvgPathData(pathData);
                if (path == null || path.IsEmpty)
                    return 0;
                var shape = path.Bounds;
                float best = 0;
                foreach (var grown in expanded)
                {
                    if (!grown.Contains(shape))
                        continue;
                    // the four gaps agree to within rounding; the smallest is the honest one
                    float width = Math.Min(
                        Math.Min(shape.Left - grown.Left, grown.Right - shape.Right),
                        Math.Min(shape.Top - grown.Top, grown.Bottom - shape.Bottom));
                    if (width > best)
                        best = width;
                }
                return best;
            }

            // A shape drawn twice — once in the outline color, once in the body color on top — is
            // the other half of that same outside-stroke export. One layer, haloed, is the picture.
            var byPath = new Dictionary<string, int>(StringComparer.Ordinal);
            var collapsed = new List<Layer>(layers.Count);
            foreach (var layer in layers)
            {
                if (byPath.TryGetValue(layer.PathData, out int at)
                    && collapsed[at].FillArgb != layer.FillArgb && !layer.FillIsNone)
                {
                    float outside = OutsideWidthFor(layer.PathData);
                    collapsed[at] = new Layer
                    {
                        PathData = layer.PathData,
                        FillArgb = layer.FillArgb,
                        StrokeArgb = collapsed[at].FillArgb,
                        StrokeWidth = outside > 0 ? outside : layer.StrokeWidth,
                        FillIsNone = false,
                        StrokeIsLiteral = layer.StrokeIsLiteral,
                    };
                    continue;
                }
                byPath[layer.PathData] = collapsed.Count;
                collapsed.Add(layer);
            }

            var result = new List<CursorGlyphPath>(collapsed.Count);
            foreach (var layer in collapsed)
            {
                string pathData = layer.PathData;
                uint fill = layer.FillArgb;
                uint stroke = layer.StrokeArgb;
                float width = layer.StrokeWidth * HaloScale;
                bool fillIsLiteral = false;

                if (layer.FillIsNone)
                {
                    // Nothing to fill: the stroke *is* the shape — Figma's other way of writing an
                    // outside stroke, and how Neon draws everything, each glow band being one more
                    // stroke of the same path — so it becomes the outline it draws, and keeps no
                    // halo of its own.
                    if (stroke == 0 || layer.StrokeWidth <= 0)
                        continue;
                    pathData = Outline(pathData, layer.StrokeWidth);
                    fill = stroke;
                    fillIsLiteral = layer.StrokeIsLiteral;
                    stroke = 0;
                    width = 0;
                }
                else if (stroke == 0 || width <= 0)
                {
                    stroke = 0;
                    width = 0;
                }

                if ((fill >> 24) == 0)
                    continue;

                result.Add(new CursorGlyphPath(
                    pathData,
                    fillIsLiteral ? fill : palette.Resolve(fill),
                    stroke == 0 ? 0 : layer.StrokeIsLiteral ? stroke : palette.Resolve(stroke),
                    width));
            }
            return result.ToArray();
        }

        // ------------------------------------------------------------------------------- pieces

        /// <summary>A stroke as the outline it fills — the same conversion <c>CursorAssets</c> uses
        /// for the shapes a pack draws without a fill.</summary>
        private static string Outline(string pathData, float strokeWidth)
        {
            using var source = SKPath.ParseSvgPathData(pathData);
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round,
            };
            using var filled = new SKPath();
            paint.GetFillPath(source, filled);
            return filled.ToSvgPathData();
        }

        private static string CirclePath(XElement element)
        {
            float cx = Num((string)element.Attribute("cx") ?? "0");
            float cy = Num((string)element.Attribute("cy") ?? "0");
            float r = Num((string)element.Attribute("r") ?? "0");
            if (r <= 0)
                return null;
            using var path = new SKPath();
            path.AddCircle(cx, cy, r);
            return path.ToSvgPathData();
        }

        private static string RectPath(XElement element)
        {
            float x = Num((string)element.Attribute("x") ?? "0");
            float y = Num((string)element.Attribute("y") ?? "0");
            float w = Num((string)element.Attribute("width") ?? "0");
            float h = Num((string)element.Attribute("height") ?? "0");
            if (w <= 0 || h <= 0)
                return null;
            float rx = Num((string)element.Attribute("rx") ?? "0");
            float ry = Num((string)element.Attribute("ry") ?? rx.ToString(CultureInfo.InvariantCulture));
            using var path = new SKPath();
            if (rx > 0 || ry > 0)
                path.AddRoundRect(SKRect.Create(x, y, w, h), rx, ry > 0 ? ry : rx);
            else
                path.AddRect(SKRect.Create(x, y, w, h));
            return path.ToSvgPathData();
        }

        /// <summary>The <c>translate</c>/<c>scale</c>/<c>rotate</c>/<c>matrix</c> forms the packs
        /// use on a group, in SVG's own right-to-left order.</summary>
        private static SKMatrix ReadTransform(XElement element)
        {
            string text = (string)element.Attribute("transform");
            if (string.IsNullOrWhiteSpace(text))
                return SKMatrix.Identity;

            var result = SKMatrix.Identity;
            foreach (System.Text.RegularExpressions.Match op in
                System.Text.RegularExpressions.Regex.Matches(text, @"(\w+)\s*\(([^)]*)\)"))
            {
                var a = op.Groups[2].Value
                    .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(Num).ToArray();
                var m = op.Groups[1].Value switch
                {
                    "translate" => SKMatrix.CreateTranslation(a[0], a.Length > 1 ? a[1] : 0),
                    "scale" => SKMatrix.CreateScale(a[0], a.Length > 1 ? a[1] : a[0]),
                    "rotate" => a.Length > 2
                        ? SKMatrix.CreateRotationDegrees(a[0], a[1], a[2])
                        : SKMatrix.CreateRotationDegrees(a[0]),
                    "matrix" => new SKMatrix(a[0], a[2], a[4], a[1], a[3], a[5], 0, 0, 1),
                    _ => SKMatrix.Identity,
                };
                result = Concat(result, m);
            }
            return result;
        }

        private static SKMatrix Concat(SKMatrix outer, SKMatrix inner)
            => inner.IsIdentity ? outer : outer.IsIdentity ? inner : SKMatrix.Concat(outer, inner);

        /// <summary>What a transform does to a stroke width, which has no direction of its own.</summary>
        private static float AverageScale(SKMatrix m)
            => (float)((Math.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY)
                + Math.Sqrt(m.SkewX * m.SkewX + m.ScaleY * m.ScaleY)) / 2);

        private static uint ResolvePaint(string text, Dictionary<string, uint> gradients, uint fallback)
        {
            if (string.IsNullOrEmpty(text) || text == "none")
                return fallback;
            if (text.StartsWith("url(", StringComparison.Ordinal))
            {
                string id = MaskIdOf(text);
                return id != null && gradients.TryGetValue(id, out var color) ? color : fallback;
            }
            return ParseColor(text, fallback);
        }

        private static uint ApplyOpacity(uint color, float opacity)
        {
            if (opacity >= 1f || color == 0)
                return color;
            uint alpha = (uint)Math.Clamp(Math.Round(((color >> 24) & 0xFF) * opacity), 0, 255);
            return alpha << 24 | (color & 0x00FFFFFF);
        }

        private static uint ParseColor(string text, uint fallback)
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text))
                return fallback;
            if (text == "white")
                return 0xFFFFFFFF;
            if (text == "black")
                return 0xFF000000;
            if (text[0] == '#' && text.Length == 7
                && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                return 0xFF000000 | rgb;
            }
            if (text[0] == '#' && text.Length == 4
                && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var s))
            {
                uint r = (s >> 8) & 0xF, g = (s >> 4) & 0xF, b = s & 0xF;
                return 0xFF000000 | r << 20 | r << 16 | g << 12 | g << 8 | b << 4 | b;
            }
            return fallback;
        }

        /// <summary>The id inside a <c>url(#id)</c> reference, or null when there is none.</summary>
        private static string MaskIdOf(string reference)
        {
            if (string.IsNullOrEmpty(reference))
                return null;
            int open = reference.IndexOf('#');
            int close = reference.IndexOf(')', open + 1);
            return open < 0 || close < 0 ? null : reference.Substring(open + 1, close - open - 1);
        }

        private static float Num(string text)
            => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0f;
    }
}
