using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;

namespace Clowd.UI.Preview.Icons
{
    /// <summary>
    /// Thrown for any construct outside the icons8-fluent subset <see cref="MiniSvgDocument"/> understands.
    /// </summary>
    /// <remarks>
    /// Failing loudly is the whole point. A renderer that quietly skipped what it did not recognise would ship
    /// a half-drawn icon nobody notices until a user sees it; the covering asset test parses every committed
    /// SVG, so an icon carrying a mask, a group, a stroke or a radial gradient breaks the build at the moment
    /// it is added instead. The message names the offending element or attribute so whoever added the asset
    /// can pick a different icon or widen the subset deliberately.
    /// </remarks>
    public sealed class MiniSvgFormatException : Exception
    {
        public MiniSvgFormatException(string message) : base(message) { }

        public MiniSvgFormatException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Entry points for <see cref="MiniSvgDocument"/>.</summary>
    public static class MiniSvg
    {
        /// <summary>Parses <paramref name="svg"/>. Does not take ownership of the stream.</summary>
        public static MiniSvgDocument Parse(Stream svg) => MiniSvgDocument.Parse(svg);

        /// <summary>Parses SVG markup already in memory — the convenient form for tests.</summary>
        public static MiniSvgDocument Parse(string svgText) => MiniSvgDocument.Parse(svgText);
    }

    /// <summary>
    /// A deliberately tiny SVG reader for exactly the shape the icons8 "fluent" file-type icons take: a
    /// <c>viewBox</c>'d root holding flat-filled or linear-gradient-filled paths, rects, polygons, circles and
    /// ellipses, optionally bracketed by <c>&lt;g&gt;</c> wrappers carrying nothing but an opacity, and nothing
    /// else. No transforms on shapes, no masks, no clips, no rendered strokes, no radial gradients, no CSS, no
    /// <c>&lt;use&gt;</c>. The subset is exactly what the 77 committed assets use and not one construct more —
    /// it was widened to fit them, and the covering asset test is what keeps the two in step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists instead of a NuGet SVG library because every candidate (Svg.Skia and friends) carries its
    /// own SkiaSharp reference. NuGet unifies to the highest requested version, which would drift past the
    /// 3.119.4 pin in clowd_ui/Directory.Build.props and trip Clowd.Ui.csproj's VerifySkiaSharpMatchesAvalonia
    /// guard — or worse, load a second native Skia beside Avalonia's. The generic lettered icon has to be
    /// typeset with Skia at runtime for arbitrary extensions anyway, so a Skia drawing path exists either way;
    /// a second renderer would only mean two visual identities for one icon family.
    /// </para>
    /// <para>
    /// It is also not Clowd.VideoSDK's <c>CursorPackLoader</c>: that one is internal to the SDK, says in its
    /// own summary that it is not an SVG engine, flattens every gradient to a single weighted-average colour
    /// and drops masked paths. Those are fine trade-offs for a cursor silhouette and wrong ones for file-type
    /// art, where the gradient <i>is</i> the icon.
    /// </para>
    /// <para>
    /// Parsing costs a few hundred microseconds and happens on a preview worker; the parsed document is
    /// memoized per asset slug by the icon renderer and replayed for every rasterization size.
    /// </para>
    /// </remarks>
    public sealed class MiniSvgDocument : IDisposable
    {
        /// <summary>
        /// One filled shape, already reduced to what a draw call needs: geometry and a paint recipe. Alpha
        /// from <c>opacity</c>, <c>fill-opacity</c> and the colour itself is folded together at parse time so
        /// the draw loop does no arithmetic.
        /// </summary>
        private sealed class Shape
        {
            public SKPath Path;
            public SKShader Shader;   // null for a flat fill; owned by _shaders, never by the shape
            public SKColor Color;     // meaningful only when Shader is null
            public byte Alpha;
        }

        // A bare <g> is the only nesting the art uses, and never more than one deep. This just stops a
        // hand-edited or hostile file from recursing the parser off the stack.
        private const int MaxDepth = 16;

        private readonly List<Shape> _shapes;

        // Keyed by gradient id: one shader per gradient even when several paths reference it, and the document
        // owns them, because SKShader is a native handle and the renderer's parsed-document cache evicts.
        private readonly Dictionary<string, SKShader> _shaders;

        private bool _disposed;

        private MiniSvgDocument(SKRect viewBox, List<Shape> shapes, Dictionary<string, SKShader> shaders)
        {
            ViewBox = viewBox;
            _shapes = shapes;
            _shaders = shaders;
        }

        /// <summary>The root <c>viewBox</c>. All geometry is expressed in these units.</summary>
        public SKRect ViewBox { get; }

        /// <summary>
        /// Replays the document into <paramref name="canvas"/> in <see cref="ViewBox"/> units. The caller owns
        /// the scale: rasterizing at N pixels means <c>canvas.Scale(N / ViewBox.Width)</c> beforehand.
        /// </summary>
        public void Draw(SKCanvas canvas)
        {
            if (canvas == null) throw new ArgumentNullException(nameof(canvas));
            ObjectDisposedException.ThrowIf(_disposed, this);

            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            foreach (var shape in _shapes)
            {
                paint.Shader = shape.Shader;
                // With a shader attached the paint's own colour contributes nothing but its alpha, which Skia
                // multiplies into the shader output — exactly what SVG's opacity/fill-opacity mean here.
                paint.Color = shape.Shader != null
                    ? SKColors.Black.WithAlpha(shape.Alpha)
                    : shape.Color.WithAlpha(shape.Alpha);
                canvas.DrawPath(shape.Path, paint);
            }

            // Detach before the paint goes away so the shared shader's lifetime stays ours alone.
            paint.Shader = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var shape in _shapes) shape.Path?.Dispose();
            foreach (var shader in _shaders.Values) shader?.Dispose();
            _shapes.Clear();
            _shaders.Clear();
        }

        /// <summary>Parses SVG markup already in memory.</summary>
        public static MiniSvgDocument Parse(string svgText)
        {
            if (svgText == null) throw new ArgumentNullException(nameof(svgText));
            using var reader = new StringReader(svgText);
            return ParseCore(XmlReader.Create(reader, ReaderSettings()));
        }

        /// <summary>
        /// Parses <paramref name="svg"/>. The stream is read but not disposed — the caller owns it. Throws
        /// <see cref="MiniSvgFormatException"/> for anything outside the subset.
        /// </summary>
        public static MiniSvgDocument Parse(Stream svg)
        {
            if (svg == null) throw new ArgumentNullException(nameof(svg));
            return ParseCore(XmlReader.Create(svg, ReaderSettings()));
        }

        // DTD processing off and no resolver: these files come off disk, and nothing in the subset needs
        // entities, so there is no reason to leave the door open.
        private static XmlReaderSettings ReaderSettings() =>
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = false };

        private static MiniSvgDocument ParseCore(XmlReader reader)
        {
            XDocument doc;
            using (reader)
            {
                try
                {
                    doc = XDocument.Load(reader);
                }
                catch (XmlException ex)
                {
                    throw new MiniSvgFormatException("MiniSvg: the file is not well-formed XML: " + ex.Message, ex);
                }
            }

            var root = doc.Root;
            if (root == null || root.Name.LocalName != "svg")
                throw new MiniSvgFormatException("MiniSvg: expected a root <svg> element, found <" +
                                                 (root == null ? "nothing" : root.Name.LocalName) + ">.");

            // Everything but viewBox is presentational sugar this renderer ignores: the caller decides the
            // output size, so width/height/version/baseProfile carry no information for us.
            CheckAttributes(root, "viewBox", "width", "height", "version", "id", "x", "y",
                            "baseProfile", "enable-background", "space");

            var viewBox = ParseViewBox(Attr(root, "viewBox"));

            // Two passes, because SVG lets a paint server be declared after the shape referencing it. Both
            // reference icons happen to declare gradients first, but nothing guarantees that for the other
            // fifty-odd assets, and a forward reference would otherwise render as a silent hole.
            var gradients = new Dictionary<string, Gradient>(StringComparer.Ordinal);
            CollectGradients(root, gradients, 0);

            var shapes = new List<Shape>();
            var shaders = new Dictionary<string, SKShader>(StringComparer.Ordinal);
            try
            {
                AddShapes(root, gradients, shaders, shapes, 0, 1f);
            }
            catch
            {
                foreach (var shape in shapes) shape.Path?.Dispose();
                foreach (var shader in shaders.Values) shader?.Dispose();
                throw;
            }

            return new MiniSvgDocument(viewBox, shapes, shaders);
        }

        private static void CollectGradients(XElement parent, Dictionary<string, Gradient> gradients, int depth)
        {
            if (depth > MaxDepth) return; // AddShapes reports the real error

            foreach (var el in parent.Elements())
            {
                if (el.Name.LocalName == "linearGradient")
                {
                    var gradient = ParseGradient(el);
                    gradients[gradient.Id] = gradient;
                }
                else if (el.Name.LocalName == "g")
                {
                    CollectGradients(el, gradients, depth + 1);
                }
            }
        }

        /// <param name="inheritedAlpha">The product of every enclosing group's <c>opacity</c>.</param>
        private static void AddShapes(XElement parent, Dictionary<string, Gradient> gradients,
                                      Dictionary<string, SKShader> shaders, List<Shape> shapes, int depth,
                                      float inheritedAlpha)
        {
            if (depth > MaxDepth)
                throw new MiniSvgFormatException("MiniSvg: nesting deeper than " + MaxDepth +
                                                 " levels; this is not the shape any file-type icon takes.");

            foreach (var el in parent.Elements())
            {
                switch (el.Name.LocalName)
                {
                    case "linearGradient":
                        break; // collected above
                    case "g":
                        // The only two things a <g> does in this family: bracket the lettering glyphs with no
                        // attributes at all (csv.svg), or carry a faint opacity over the emboss and drop-shadow
                        // layers on the brand marks (cplusplus.svg's .05 and .07, php.svg's .05). Group opacity
                        // is strictly an off-screen composite of the whole group, which differs from a
                        // per-child multiply wherever the children overlap; at those alphas over shadow
                        // geometry the difference is invisible at 36px, and the alternative is an SKSurface per
                        // group. A <g> carrying anything else — a transform, a fill to inherit, a clip — still
                        // throws, because each of those really would change the picture.
                        CheckAttributes(el, "id", "opacity");
                        AddShapes(el, gradients, shaders, shapes, depth + 1,
                                  inheritedAlpha * ParseUnitInterval(Attr(el, "opacity"), 1f, "g/opacity"));
                        break;
                    case "path":
                        AddPath(el, gradients, shaders, shapes, inheritedAlpha);
                        break;
                    case "rect":
                        AddRect(el, gradients, shaders, shapes, inheritedAlpha);
                        break;
                    case "polygon":
                        AddPolygon(el, gradients, shaders, shapes, inheritedAlpha);
                        break;
                    case "circle":
                        AddCircle(el, gradients, shaders, shapes, inheritedAlpha);
                        break;
                    case "ellipse":
                        AddEllipse(el, gradients, shaders, shapes, inheritedAlpha);
                        break;
                    default:
                        throw new MiniSvgFormatException(
                            "MiniSvg: unsupported element <" + el.Name.LocalName + ">. The subset is " +
                            "svg / g / linearGradient / stop / path / rect / polygon / circle / ellipse only — " +
                            "no masks, clips, text, <use> or radial gradients.");
                }
            }
        }

        private static void AddPath(XElement el, Dictionary<string, Gradient> gradients,
                                    Dictionary<string, SKShader> shaders, List<Shape> shapes, float inheritedAlpha)
        {
            // clip-rule only means anything inside a <clipPath>, which is not in the subset, so it is accepted
            // and ignored rather than treated as a reason to reject an otherwise fine icon — the icons8 export
            // emits it alongside every fill-rule.
            CheckAttributes(el, "d", "fill", "fill-rule", "clip-rule", "fill-opacity", "opacity",
                            "stroke", "stroke-width", "id");

            var d = Attr(el, "d");
            if (String.IsNullOrWhiteSpace(d))
                throw new MiniSvgFormatException("MiniSvg: <path> is missing its 'd' geometry.");

            // Strokes are not rendered. The only two in the shipped set (powershell.svg) are shadow outlines
            // sitting underneath an opaque copy of the same geometry, so dropping them is invisible — but that
            // is only safe because they paint no fill. A stroke on a filled path would be art we are silently
            // losing, so that is still a hard error.
            if (Attr(el, "stroke") != null && !IsNone(Attr(el, "fill")))
                throw new MiniSvgFormatException("MiniSvg: <path> carries a stroke on a filled shape, which " +
                                                 "this renderer does not draw. Only stroke-on-fill=\"none\" " +
                                                 "decoration may be dropped.");

            // Handed to Skia verbatim. The real art contains literal tabs inside 'd' (the WAV reference reads
            // "...H11<tab>c-1.105,..."); XML attribute-value normalization turns those into spaces before we
            // ever see them and ParseSvgPathData copes with either, so never pre-tokenize this string.
            var path = SKPath.ParseSvgPathData(d);
            if (path == null)
                throw new MiniSvgFormatException("MiniSvg: Skia could not parse the path data \"" + Truncate(d) + "\".");

            // SVG's default fill-rule is nonzero, which is Skia's Winding. ParseSvgPathData does not set it for
            // us and these icons rely on it — the folded-corner shape overlaps the page body.
            path.FillType = ParseFillRule(Attr(el, "fill-rule"));

            Emit(el, path, gradients, shaders, shapes, inheritedAlpha);
        }

        private static void AddRect(XElement el, Dictionary<string, Gradient> gradients,
                                    Dictionary<string, SKShader> shaders, List<Shape> shapes, float inheritedAlpha)
        {
            CheckAttributes(el, "x", "y", "width", "height", "fill", "fill-opacity", "opacity", "id");

            var rect = SKRect.Create(
                ParseLength(Attr(el, "x"), 0f, "rect/x"),
                ParseLength(Attr(el, "y"), 0f, "rect/y"),
                ParseLength(Attr(el, "width"), 0f, "rect/width"),
                ParseLength(Attr(el, "height"), 0f, "rect/height"));

            // Folded into a path so the draw loop has exactly one shape kind to replay.
            var path = new SKPath { FillType = SKPathFillType.Winding };
            path.AddRect(rect);

            Emit(el, path, gradients, shaders, shapes, inheritedAlpha);
        }

        /// <summary>The lettering on typescript.svg and php.svg and the "++" on cplusplus.svg are polygons
        /// rather than paths — same closed filled geometry, written the short way.</summary>
        private static void AddPolygon(XElement el, Dictionary<string, Gradient> gradients,
                                       Dictionary<string, SKShader> shaders, List<Shape> shapes, float inheritedAlpha)
        {
            CheckAttributes(el, "points", "fill", "fill-rule", "clip-rule", "fill-opacity", "opacity", "id");

            var numbers = ParseNumberList(Attr(el, "points") ?? String.Empty);
            if (numbers.Length < 6 || (numbers.Length & 1) != 0)
                throw new MiniSvgFormatException("MiniSvg: <polygon points> needs an even count of at least " +
                                                 "six numbers, found " + numbers.Length + ".");

            var points = new SKPoint[numbers.Length / 2];
            for (var i = 0; i < points.Length; i++)
                points[i] = new SKPoint(numbers[i * 2], numbers[i * 2 + 1]);

            var path = new SKPath { FillType = ParseFillRule(Attr(el, "fill-rule")) };
            path.AddPoly(points, close: true);

            Emit(el, path, gradients, shaders, shapes, inheritedAlpha);
        }

        private static void AddCircle(XElement el, Dictionary<string, Gradient> gradients,
                                      Dictionary<string, SKShader> shaders, List<Shape> shapes, float inheritedAlpha)
        {
            CheckAttributes(el, "cx", "cy", "r", "fill", "fill-opacity", "opacity", "id");

            var path = new SKPath { FillType = SKPathFillType.Winding };
            path.AddCircle(ParseLength(Attr(el, "cx"), 0f, "circle/cx"),
                           ParseLength(Attr(el, "cy"), 0f, "circle/cy"),
                           ParseLength(Attr(el, "r"), 0f, "circle/r"));

            Emit(el, path, gradients, shaders, shapes, inheritedAlpha);
        }

        /// <summary>The database/SQL cylinder caps and blender's lens are ellipses.</summary>
        private static void AddEllipse(XElement el, Dictionary<string, Gradient> gradients,
                                       Dictionary<string, SKShader> shaders, List<Shape> shapes, float inheritedAlpha)
        {
            CheckAttributes(el, "cx", "cy", "rx", "ry", "fill", "fill-opacity", "opacity", "id");

            var cx = ParseLength(Attr(el, "cx"), 0f, "ellipse/cx");
            var cy = ParseLength(Attr(el, "cy"), 0f, "ellipse/cy");
            var rx = ParseLength(Attr(el, "rx"), 0f, "ellipse/rx");
            var ry = ParseLength(Attr(el, "ry"), 0f, "ellipse/ry");

            var path = new SKPath { FillType = SKPathFillType.Winding };
            path.AddOval(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry));

            Emit(el, path, gradients, shaders, shapes, inheritedAlpha);
        }

        /// <summary>Turns a built geometry into a shape, or disposes it when the element paints nothing. Every
        /// shape kind funnels through here so the path is never leaked on the "paints nothing" branch.</summary>
        private static void Emit(XElement el, SKPath path, Dictionary<string, Gradient> gradients,
                                 Dictionary<string, SKShader> shaders, List<Shape> shapes, float inheritedAlpha)
        {
            var shape = BuildShape(el, path, gradients, shaders, inheritedAlpha);
            if (shape != null) shapes.Add(shape);
            else path.Dispose();
        }

        /// <summary>Returns null when the element paints nothing (<c>fill="none"</c> or zero alpha).</summary>
        private static Shape BuildShape(XElement el, SKPath path, Dictionary<string, Gradient> gradients,
                                        Dictionary<string, SKShader> shaders, float inheritedAlpha)
        {
            var alpha = inheritedAlpha *
                        ParseUnitInterval(Attr(el, "opacity"), 1f, "opacity") *
                        ParseUnitInterval(Attr(el, "fill-opacity"), 1f, "fill-opacity");
            if (alpha <= 0f) return null;

            var fill = Attr(el, "fill");
            // An omitted fill is black in SVG, and a good third of the shipped art relies on it: the drop
            // shadows under the brand marks are fill-less paths at a few percent opacity, and inventing any
            // other default would lose them.
            if (String.IsNullOrWhiteSpace(fill)) fill = "#000000";
            if (IsNone(fill)) return null;

            var shape = new Shape { Path = path, Alpha = (byte)Math.Round(alpha * 255f) };

            var gradientId = TryGetUrlReference(fill);
            if (gradientId != null)
            {
                if (!gradients.TryGetValue(gradientId, out var gradient))
                    throw new MiniSvgFormatException("MiniSvg: fill references url(#" + gradientId +
                                                     ") but no <linearGradient> with that id was declared.");
                shape.Shader = GetShader(gradient, shaders);
                if (shape.Shader == null)
                {
                    // A one-stop gradient is a flat fill, and Skia would rather have it that way than as a
                    // degenerate shader.
                    shape.Color = gradient.Colors[0];
                    shape.Alpha = MultiplyAlpha(shape.Alpha, gradient.Colors[0].Alpha);
                }
            }
            else
            {
                shape.Color = ParseColor(fill, "fill");
                shape.Alpha = MultiplyAlpha(shape.Alpha, shape.Color.Alpha);
            }

            return shape;
        }

        private static byte MultiplyAlpha(byte a, byte b) => (byte)((a * b + 127) / 255);

        private static SKShader GetShader(Gradient gradient, Dictionary<string, SKShader> shaders)
        {
            if (gradient.Colors.Length < 2) return null;
            if (shaders.TryGetValue(gradient.Id, out var existing)) return existing;

            // The real gradient, with the file's own gradientTransform as the shader's local matrix — never a
            // weighted-average colour. The icons8 fluent page bodies are a top-to-bottom ramp, and averaging
            // one reads as a flat, muddy card sitting next to a shipped icon.
            var shader = SKShader.CreateLinearGradient(
                gradient.Start, gradient.End, gradient.Colors, gradient.Offsets,
                SKShaderTileMode.Clamp, gradient.Transform);
            if (shader == null)
                throw new MiniSvgFormatException("MiniSvg: Skia refused the linear gradient '" + gradient.Id + "'.");

            shaders[gradient.Id] = shader;
            return shader;
        }

        // ---- linearGradient ---------------------------------------------------------------------------

        private readonly struct Gradient
        {
            public Gradient(string id, SKPoint start, SKPoint end, SKColor[] colors, float[] offsets, SKMatrix transform)
            {
                Id = id;
                Start = start;
                End = end;
                Colors = colors;
                Offsets = offsets;
                Transform = transform;
            }

            public string Id { get; }
            public SKPoint Start { get; }
            public SKPoint End { get; }
            public SKColor[] Colors { get; }
            public float[] Offsets { get; }
            public SKMatrix Transform { get; }
        }

        private static Gradient ParseGradient(XElement el)
        {
            CheckAttributes(el, "id", "x1", "y1", "x2", "y2", "gradientUnits", "gradientTransform");

            var id = Attr(el, "id");
            if (String.IsNullOrEmpty(id))
                throw new MiniSvgFormatException("MiniSvg: <linearGradient> has no id, so nothing can reference it.");

            // objectBoundingBox units would need each referencing shape's bounds folded into the matrix, and no
            // icon in the family uses them. Refuse rather than draw the ramp in the wrong place.
            var units = Attr(el, "gradientUnits");
            if (!String.Equals(units, "userSpaceOnUse", StringComparison.Ordinal))
                throw new MiniSvgFormatException("MiniSvg: <linearGradient id=\"" + id +
                                                 "\"> needs gradientUnits=\"userSpaceOnUse\"; found " +
                                                 (units == null ? "no gradientUnits attribute" : "\"" + units + "\"") + ".");

            var start = new SKPoint(ParseLength(Attr(el, "x1"), 0f, "x1"), ParseLength(Attr(el, "y1"), 0f, "y1"));
            var end = new SKPoint(ParseLength(Attr(el, "x2"), 0f, "x2"), ParseLength(Attr(el, "y2"), 0f, "y2"));
            var transform = ParseTransform(Attr(el, "gradientTransform"), id);

            var colors = new List<SKColor>();
            var offsets = new List<float>();
            foreach (var stopEl in el.Elements())
            {
                if (stopEl.Name.LocalName != "stop")
                    throw new MiniSvgFormatException("MiniSvg: <linearGradient id=\"" + id + "\"> contains <" +
                                                     stopEl.Name.LocalName + ">; only <stop> is supported.");

                CheckAttributes(stopEl, "offset", "stop-color", "stop-opacity", "id");

                var color = ParseColor(Attr(stopEl, "stop-color") ?? "#000000", "stop-color");
                var stopAlpha = ParseUnitInterval(Attr(stopEl, "stop-opacity"), 1f, "stop-opacity");
                colors.Add(color.WithAlpha(MultiplyAlpha(color.Alpha, (byte)Math.Round(stopAlpha * 255f))));
                offsets.Add(ParseOffset(Attr(stopEl, "offset")));
            }

            if (colors.Count == 0)
                throw new MiniSvgFormatException("MiniSvg: <linearGradient id=\"" + id + "\"> has no <stop> children.");

            return new Gradient(id, start, end, colors.ToArray(), offsets.ToArray(), transform);
        }

        /// <summary>
        /// Handles the three transform forms this family actually uses. The WAV reference folds a
        /// <c>translate(0 -2266)</c> around coordinates in the 2200s; drop it and the ramp lands entirely off
        /// the icon, so this is load-bearing rather than defensive.
        /// </summary>
        private static SKMatrix ParseTransform(string value, string gradientId)
        {
            if (String.IsNullOrWhiteSpace(value)) return SKMatrix.CreateIdentity();

            var text = value.Trim();
            var open = text.IndexOf('(');
            var close = text.LastIndexOf(')');
            if (open <= 0 || close != text.Length - 1)
                throw new MiniSvgFormatException("MiniSvg: gradientTransform \"" + value + "\" on '" + gradientId +
                                                 "' is not a single function call.");

            var name = text.Substring(0, open).Trim();
            var args = ParseNumberList(text.Substring(open + 1, close - open - 1));

            switch (name)
            {
                case "translate" when args.Length == 1:
                    return SKMatrix.CreateTranslation(args[0], 0f);
                case "translate" when args.Length == 2:
                    return SKMatrix.CreateTranslation(args[0], args[1]);
                case "scale" when args.Length == 1:
                    return SKMatrix.CreateScale(args[0], args[0]);
                case "scale" when args.Length == 2:
                    return SKMatrix.CreateScale(args[0], args[1]);
                case "matrix" when args.Length == 6:
                    // SVG's matrix(a b c d e f) lists the columns of [a c e; b d f]; SKMatrix's constructor
                    // takes the same six numbers row by row.
                    return new SKMatrix(args[0], args[2], args[4], args[1], args[3], args[5], 0f, 0f, 1f);
                default:
                    throw new MiniSvgFormatException("MiniSvg: gradientTransform \"" + value + "\" on '" + gradientId +
                                                     "' is unsupported; only translate(), scale() and matrix() are.");
            }
        }

        // ---- scalars ----------------------------------------------------------------------------------

        private static SKRect ParseViewBox(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                throw new MiniSvgFormatException("MiniSvg: <svg> has no viewBox, so its geometry has no scale.");

            var parts = ParseNumberList(value);
            if (parts.Length != 4)
                throw new MiniSvgFormatException("MiniSvg: viewBox \"" + value + "\" needs exactly four numbers.");
            if (parts[2] <= 0f || parts[3] <= 0f)
                throw new MiniSvgFormatException("MiniSvg: viewBox \"" + value + "\" has a non-positive extent.");

            return SKRect.Create(parts[0], parts[1], parts[2], parts[3]);
        }

        private static float[] ParseNumberList(string value)
        {
            var parts = value.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new float[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!Single.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                    throw new MiniSvgFormatException("MiniSvg: \"" + parts[i] + "\" in \"" + value + "\" is not a number.");
            }

            return result;
        }

        private static float ParseLength(string value, float fallback, string what)
        {
            if (String.IsNullOrWhiteSpace(value)) return fallback;
            if (!Single.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
                throw new MiniSvgFormatException("MiniSvg: " + what + "=\"" + value + "\" is not a plain number. " +
                                                 "Units (px/em/%) are outside the subset.");
            return result;
        }

        private static float ParseUnitInterval(string value, float fallback, string what)
        {
            if (String.IsNullOrWhiteSpace(value)) return fallback;
            return Math.Clamp(ParseLength(value, fallback, what), 0f, 1f);
        }

        private static float ParseOffset(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return 0f;
            var text = value.Trim();
            if (text.EndsWith("%", StringComparison.Ordinal))
                return Math.Clamp(ParseLength(text.Substring(0, text.Length - 1), 0f, "offset") / 100f, 0f, 1f);
            return Math.Clamp(ParseLength(text, 0f, "offset"), 0f, 1f);
        }

        private static SKPathFillType ParseFillRule(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return SKPathFillType.Winding;
            switch (value.Trim())
            {
                case "nonzero": return SKPathFillType.Winding;
                case "evenodd": return SKPathFillType.EvenOdd;
                default:
                    throw new MiniSvgFormatException("MiniSvg: fill-rule=\"" + value +
                                                     "\" is neither nonzero nor evenodd.");
            }
        }

        /// <summary>Returns the id inside <c>url(#id)</c>, or null when the value is not a reference.</summary>
        private static string TryGetUrlReference(string value)
        {
            var text = value.Trim();
            if (!text.StartsWith("url(", StringComparison.OrdinalIgnoreCase) ||
                !text.EndsWith(")", StringComparison.Ordinal))
                return null;

            var inner = text.Substring(4, text.Length - 5).Trim().Trim('\'', '"');
            if (!inner.StartsWith("#", StringComparison.Ordinal) || inner.Length < 2)
                throw new MiniSvgFormatException("MiniSvg: paint reference \"" + value +
                                                 "\" must be a local url(#id); external references are not supported.");
            return inner.Substring(1);
        }

        private static bool IsNone(string value) =>
            value != null && String.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase);

        private static SKColor ParseColor(string value, string what)
        {
            var text = value.Trim();

            // #rgb expands by digit doubling, per CSS: #fd0 is #ffdd00.
            if (text.Length == 4 && text[0] == '#')
            {
                var r = HexDigit(text[1], value, what);
                var g = HexDigit(text[2], value, what);
                var b = HexDigit(text[3], value, what);
                return new SKColor((byte)(r * 17), (byte)(g * 17), (byte)(b * 17));
            }

            if (text.Length == 7 && text[0] == '#')
            {
                return new SKColor(
                    (byte)((HexDigit(text[1], value, what) << 4) | HexDigit(text[2], value, what)),
                    (byte)((HexDigit(text[3], value, what) << 4) | HexDigit(text[4], value, what)),
                    (byte)((HexDigit(text[5], value, what) << 4) | HexDigit(text[6], value, what)));
            }

            throw new MiniSvgFormatException("MiniSvg: " + what + "=\"" + value + "\" is not a 3- or 6-digit hex " +
                                             "colour, \"none\" or url(#id). Named colours and rgb() are outside the subset.");
        }

        private static int HexDigit(char c, string value, string what)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            throw new MiniSvgFormatException("MiniSvg: " + what + "=\"" + value + "\" contains a non-hex digit.");
        }

        // ---- element/attribute gate -------------------------------------------------------------------

        private static string Attr(XElement el, string name) => el.Attribute(name)?.Value;

        /// <summary>
        /// Rejects any attribute outside <paramref name="allowed"/>. Namespace declarations are skipped — the
        /// icons8 files all carry <c>xmlns</c> on the root and XLinq surfaces that as an attribute. Matching is
        /// on the local name, so a prefixed <c>xml:space</c> reads here as "space".
        /// </summary>
        private static void CheckAttributes(XElement el, params string[] allowed)
        {
            foreach (var attr in el.Attributes())
            {
                if (attr.IsNamespaceDeclaration) continue;

                var name = attr.Name.LocalName;
                var ok = false;
                foreach (var candidate in allowed)
                {
                    if (String.Equals(name, candidate, StringComparison.Ordinal))
                    {
                        ok = true;
                        break;
                    }
                }

                if (!ok)
                    throw new MiniSvgFormatException("MiniSvg: <" + el.Name.LocalName + "> carries the unsupported " +
                                                     "attribute '" + name + "=\"" + Truncate(attr.Value) + "\"'. " +
                                                     "Supported here: " + String.Join(", ", allowed) + ".");
            }
        }

        private static string Truncate(string value) =>
            value != null && value.Length > 60 ? value.Substring(0, 57) + "..." : value;
    }
}
