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
    /// The three colour roles a ful1e5 cursor pack authors against, and what one of its themes
    /// paints them. The packs draw every cursor with placeholder colours — <c>#00FF00</c> for the
    /// body, <c>#0000FF</c> for the outline, <c>#FF0000</c> for the one accent that some cursors
    /// carry — and ship a <c>render.json</c> naming the real colour of each per theme; that table
    /// is what a <see cref="CursorPackPalette"/> holds. A colour the pack writes literally (the
    /// white of a "no entry" bar, the spinner's own four) is not a role and passes straight through.
    /// </summary>
    internal sealed class CursorPackPalette
    {
        internal CursorPackPalette(uint body, uint outline, uint accent)
        {
            Body = body;
            Outline = outline;
            Accent = accent;
        }

        internal uint Body { get; }

        internal uint Outline { get; }

        internal uint Accent { get; }

        /// <summary>
        /// The theme colour for a source colour: a role's real colour, or the source's own when it
        /// wrote a literal. Only the RGB decides the role — a placeholder that reaches here at part
        /// opacity (the packs fade one through a gradient) is the same role, and keeps its alpha.
        /// <c>#FE0000</c> is deliberately <i>not</i> the accent: the packs' own colour maps name
        /// <c>#FF0000</c> and nothing else, so the off-by-one red they paint a "no entry" badge with
        /// stays the red they meant.
        /// </summary>
        internal uint Resolve(uint sourceArgb)
        {
            uint alpha = sourceArgb & 0xFF000000;
            return (sourceArgb & 0x00FFFFFF) switch
            {
                0x00FF00 => alpha | (Body & 0x00FFFFFF),
                0x0000FF => alpha | (Outline & 0x00FFFFFF),
                0xFF0000 => alpha | (Accent & 0x00FFFFFF),
                _ => sourceArgb,
            };
        }
    }

    /// <summary>
    /// Reads the embedded artwork of the ful1e5 cursor packs (Bibata, BreezeX, macOS, Fuchsia)
    /// into <see cref="CursorGlyph"/>s. The files under <c>Composition/CursorPacks</c> are
    /// each pack's own SVG sources, copied verbatim but for their raster filter chains and clip
    /// rects, which nothing here can draw; re-syncing a pack is a re-copy, and no artwork is
    /// transcribed or traced along the way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the packs use is a narrow slice of SVG — flat <c>&lt;path&gt;</c>, <c>&lt;circle&gt;</c>
    /// and <c>&lt;rect&gt;</c> in one 256-unit viewBox, a handful of group transforms, and the
    /// gradients and Figma "outside stroke" masks noted below — so this is a reader for that slice
    /// and not an SVG engine. Anything it does not understand is skipped rather than guessed at.
    /// </para>
    /// <para>The four places the source cannot be taken literally, and what happens instead:</para>
    /// <list type="bullet">
    /// <item><b>Gradients.</b> A <c>fill="url(#…)"</c> is flattened to its stops' average, weighted
    /// by their offsets — the packs use them for the macOS beachball's segments and for a soft
    /// vignette on BreezeX's crosshair, and both read correctly as one colour at cursor size.</item>
    /// <item><b>Outside strokes.</b> Figma exports an outside stroke either as a second, expanded
    /// path (which needs nothing — it is already the outline ring) or as a masked path over a
    /// duplicate of the shape. The masked path cannot be drawn without the mask, so it is dropped
    /// and the duplicate pair it belonged to collapses into one layer: the upper fill, haloed in
    /// the lower one's colour, at the width the mask's own bounds imply.</item>
    /// <item><b>Centred strokes.</b> A <c>stroke</c> attribute paints centred and <i>over</i> the
    /// fill, so all of it shows; a halo here paints under the fill and only its outer half shows.
    /// The stored width is therefore twice the authored one, which keeps the outline as thick as
    /// the pack drew it at the cost of half a stroke on the silhouette.</item>
    /// <item><b>Even-odd fills.</b> Normalised to the nonzero winding every stored path is in, so
    /// a hole stays a hole when <c>CursorCompose</c> parses it back.</item>
    /// </list>
    /// <para>
    /// A layer the pack leaves unstroked stays unstroked. These packs do not halo every shape the
    /// way Vision and Point do — they draw the outline as a solid backing silhouette and stack the
    /// body colour inside it — so the glyph's contrast comes from its bottom layer, not from a
    /// stroke on each one.
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

        /// <summary>The still for a (pack, kind) pair in one theme's colours, or null when the pack
        /// carries no artwork for it.</summary>
        internal static CursorGlyph LoadStatic(string pack, string kind, CursorPackPalette palette,
            float hotspotX, float hotspotY)
        {
            string name = ResourceRoot + pack + "." + kind + ".svg";
            return Array.IndexOf(ResourceNames, name) < 0
                ? null
                : ReadGlyph(name, palette, hotspotX, hotspotY);
        }

        /// <summary>
        /// The looping animation for a (pack, kind) pair: every frame the pack ships, in file order,
        /// at its own declared delay. Each frame carries the whole picture (the packs animate by
        /// redrawing, not by transforming a layer), so the frames are read as stills and handed to
        /// <see cref="CursorGlyph"/>'s frame-list constructor. Null when the pack has no such
        /// animation.
        /// </summary>
        internal static CursorGlyph LoadAnimated(string pack, string kind, CursorPackPalette palette,
            float hotspotX, float hotspotY, float frameMs)
        {
            string prefix = ResourceRoot + pack + "." + kind + ".";
            var names = ResourceNames.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)
                    && n.EndsWith(".svg", StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            if (names.Length == 0)
                return null;

            // Frame 0 fixes the geometry the loop is presented in: the packs' frames drift by a
            // fraction of a unit in viewBox and hotspot (their sources are exported one by one), and
            // an animated glyph is one shape at one size by construction.
            var frames = new CursorGlyph[names.Length];
            float viewBox = 0;
            for (int i = 0; i < names.Length; i++)
            {
                frames[i] = ReadGlyph(names[i], palette, hotspotX, hotspotY, i == 0 ? 0 : viewBox);
                if (i == 0)
                    viewBox = frames[0].ViewBox;
            }
            return new CursorGlyph(frameMs, frames);
        }

        /// <summary>One SVG as a glyph. <paramref name="forceViewBox"/> overrides the file's own,
        /// which is how an animation's frames are held to frame 0's geometry.</summary>
        private static CursorGlyph ReadGlyph(string resourceName, CursorPackPalette palette,
            float hotspotX, float hotspotY, float forceViewBox = 0)
        {
            using var stream = Assembly.GetManifestResourceStream(resourceName);
            using var reader = new StreamReader(stream);
            var root = XDocument.Parse(reader.ReadToEnd()).Root;

            float viewBox = forceViewBox > 0 ? forceViewBox : ReadViewBox(root);
            var gradients = ReadGradients(root);
            var masks = ReadMasks(root);

            var layers = new List<Layer>();
            // Paint inherits, and every one of these files opens with `fill="none"` on the root —
            // which is what makes a bare `<path stroke=…>` an outline ring and not a black blob.
            Walk(root, SKMatrix.Identity, (string)root.Attribute("fill") ?? "none", gradients, layers);
            var paths = Resolve(layers, masks, palette);

            // The hotspot is quoted against the packs' own 256-unit render; a file whose viewBox
            // drifted off 256 (a few of the animation frames do) scales it to match.
            float scale = viewBox / 256f;
            return new CursorGlyph(viewBox, hotspotX * scale, hotspotY * scale, paths);
        }

        // ------------------------------------------------------------------------------ reading

        /// <summary>The viewBox's longer side. Every pack authors square-ish boxes on 256; the odd
        /// 257 is an export artefact, and squaring off the longer side keeps the art inside.</summary>
        private static float ReadViewBox(XElement root)
        {
            var parts = ((string)root.Attribute("viewBox") ?? "0 0 256 256")
                .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            float width = parts.Length > 2 ? Num(parts[2]) : 256f;
            float height = parts.Length > 3 ? Num(parts[3]) : width;
            return Math.Max(width, height);
        }

        /// <summary>Gradient id → the one colour it flattens to (see the class remarks).</summary>
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

                // Each stop covers the span to its neighbours' midpoints, so a colour that holds
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

                    uint colour = ParseColour((string)stops[i].Attribute("stop-color") ?? "black", 0xFF000000);
                    float opacity = Num((string)stops[i].Attribute("stop-opacity") ?? "1");
                    r += ((colour >> 16) & 0xFF) * weight;
                    g += ((colour >> 8) & 0xFF) * weight;
                    b += (colour & 0xFF) * weight;
                    a += ((colour >> 24) & 0xFF) * opacity * weight;
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
        }

        private static void Walk(XElement element, SKMatrix ctm, string inheritedFill,
            Dictionary<string, uint> gradients, List<Layer> layers)
        {
            foreach (var child in element.Elements())
            {
                string fill = (string)child.Attribute("fill") ?? inheritedFill;
                switch (child.Name.LocalName)
                {
                    // Definitions and mask bodies are read separately; a filter is a raster effect
                    // (every one in these packs is a drop shadow) and has no place in flat artwork.
                    case "defs":
                    case "mask":
                    case "filter":
                    case "clipPath":
                        continue;

                    case "g":
                        Walk(child, Concat(ctm, ReadTransform(child)), fill, gradients, layers);
                        continue;

                    case "path":
                        AddLayer(child, (string)child.Attribute("d"), ctm, fill, gradients, layers);
                        continue;

                    case "circle":
                        AddLayer(child, CirclePath(child), ctm, fill, gradients, layers);
                        continue;

                    case "rect":
                        AddLayer(child, RectPath(child), ctm, fill, gradients, layers);
                        continue;

                    default:
                        Walk(child, ctm, fill, gradients, layers);
                        continue;
                }
            }
        }

        private static void AddLayer(XElement element, string pathData, SKMatrix ctm, string fillText,
            Dictionary<string, uint> gradients, List<Layer> layers)
        {
            if (string.IsNullOrWhiteSpace(pathData))
                return;

            using var path = SKPath.ParseSvgPathData(pathData);
            if (path == null || path.IsEmpty)
                return;

            // The nonzero winding every stored path is in — see the class remarks.
            if ((string)element.Attribute("fill-rule") == "evenodd")
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

            bool fillIsNone = fillText == "none";
            uint fill = ResolvePaint(fillText, gradients, 0xFF000000);
            fill = ApplyOpacity(fill, element, "fill-opacity");

            uint stroke = ResolvePaint((string)element.Attribute("stroke"), gradients, 0);
            float strokeWidth = Num((string)element.Attribute("stroke-width") ?? "0");
            if (transformed)
                strokeWidth *= AverageScale(ctm);

            layers.Add(new Layer
            {
                // Reserialising is only worth it when something actually moved; a path the reader
                // left alone keeps the pack's own bytes.
                PathData = transformed || (string)element.Attribute("fill-rule") == "evenodd"
                    ? path.ToSvgPathData()
                    : pathData,
                FillArgb = fill,
                StrokeArgb = stroke,
                StrokeWidth = strokeWidth,
                FillIsNone = fillIsNone,
                MaskId = MaskIdOf((string)element.Attribute("mask")),
            });
        }

        // ---------------------------------------------------------------------------- resolving

        /// <summary>
        /// The read layers as stored ones: masked paths dropped and the duplicate pairs they stood
        /// for collapsed, bare strokes turned into the outline they draw, and every colour put
        /// through the theme's palette.
        /// </summary>
        private static CursorGlyphPath[] Resolve(List<Layer> layers,
            Dictionary<string, SKRect> masks, CursorPackPalette palette)
        {
            // A dropped outside-stroke leaves its width behind in its own geometry: the masked path
            // is the shape grown by that width all round, so the gap between its bounds and the
            // shape's is the width itself. (The mask's declared box is padded further still, which
            // is why it is only used to recognise the pair.) Zero when the file has no such mask.
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

            // A shape drawn twice — once in the outline colour, once in the body colour on top — is
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

                if (layer.FillIsNone)
                {
                    // Nothing to fill: the stroke *is* the shape (Figma's other way of writing an
                    // outside stroke), so it becomes the outline ring it draws and keeps no halo.
                    if (stroke == 0 || layer.StrokeWidth <= 0)
                        continue;
                    pathData = Outline(pathData, layer.StrokeWidth);
                    fill = stroke;
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

                result.Add(new CursorGlyphPath(pathData, palette.Resolve(fill),
                    stroke == 0 ? 0 : palette.Resolve(stroke), width));
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
                return id != null && gradients.TryGetValue(id, out var colour) ? colour : fallback;
            }
            return ParseColour(text, fallback);
        }

        private static uint ApplyOpacity(uint colour, XElement element, string attribute)
        {
            float opacity = Num((string)element.Attribute(attribute) ?? "1")
                * Num((string)element.Attribute("opacity") ?? "1");
            if (opacity >= 1f)
                return colour;
            uint alpha = (uint)Math.Clamp(Math.Round(((colour >> 24) & 0xFF) * opacity), 0, 255);
            return alpha << 24 | (colour & 0x00FFFFFF);
        }

        private static uint ParseColour(string text, uint fallback)
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
