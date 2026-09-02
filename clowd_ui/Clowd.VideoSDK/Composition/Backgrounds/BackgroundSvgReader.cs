using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// Reads a wallpaper SVG from <c>Composition/Backgrounds/Art</c> into an <see cref="SvgScene"/>.
    /// A sibling of <see cref="CursorPackLoader"/> with a different intermediate form: that
    /// reader bakes every transform into its paths and flattens gradients to one color, which
    /// is right for a cursor and wrong for a wallpaper whose gradients are in user space and
    /// whose groups move.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The subset.</b> This is a reader for exactly what the shipped files use, and not an SVG
    /// engine: <c>&lt;rect&gt;</c>, <c>&lt;circle&gt;</c>, <c>&lt;ellipse&gt;</c>,
    /// <c>&lt;path&gt;</c> (verbs <c>M m L l H h V v C c Z z</c>), each with its own
    /// <c>transform</c> (<c>translate</c>/<c>scale</c>/<c>rotate</c>/<c>matrix</c>),
    /// <c>opacity</c>, <c>clip-path</c> and <c>filter</c>; <c>&lt;g&gt;</c> with the same four;
    /// <c>&lt;use&gt;</c> of a <c>&lt;g&gt;</c> that precedes it under the same parent, with
    /// <c>x</c>/<c>y</c>, <c>transform</c>, <c>opacity</c>, <c>clip-path</c> and <c>filter</c>
    /// of its own; <c>&lt;linearGradient&gt;</c> and <c>&lt;radialGradient&gt;</c> in both unit
    /// systems with <c>gradientTransform</c> and stops carrying
    /// <c>stop-color</c>/<c>stop-opacity</c>; <c>fill</c>, <c>fill-opacity</c>, <c>stroke</c>,
    /// <c>stroke-width</c>, <c>stroke-opacity</c>, <c>opacity</c> and <c>mix-blend-mode</c> (as
    /// attributes or in <c>style</c>); <c>&lt;clipPath&gt;</c> holding rects and paths;
    /// <c>&lt;filter&gt;</c> whose effect is one <c>feGaussianBlur</c> (<c>feFlood</c>/<c>feBlend</c>
    /// boilerplate around it is accepted and ignored), in either
    /// <c>color-interpolation-filters</c> space; and SMIL <c>&lt;animate&gt;</c> on
    /// <c>cx</c>/<c>cy</c>/<c>r</c>/<c>d</c> and <c>&lt;animateTransform type="translate"&gt;</c>,
    /// in the <c>values</c>+<c>keyTimes</c>, linear, indefinite form (see <see cref="SmilTrack"/>).
    /// </para>
    /// <para>
    /// <b>The <c>&lt;use&gt;</c> form</b> is the one Monterey needs and no wider: Figma's
    /// backdrop-blur emulation is a <c>&lt;use&gt;</c> of the stack painted so far (a sibling
    /// <c>&lt;g&gt;</c> with an id), clipped to the blurred layer's own shape and put through a
    /// <c>userSpaceOnUse</c> Gaussian blur. Restricting the target to a preceding sibling group
    /// is what makes the clone exact without a second pass: the referenced nodes were read
    /// under the same inherited paint the <c>&lt;use&gt;</c> sits in, so the already-built
    /// subtree is shared as the clone's child (the tree is immutable, so sharing is free), and
    /// a target that has not been read yet, sits under another parent, or is not a group is a
    /// skip. A <c>&lt;use&gt;</c> carrying its own <c>fill</c>/<c>stroke</c> would reach into
    /// the clone where the original inherits, so that is a skip too. The three Monterey layers
    /// are what soften the lower-right swoosh; without them its stacked strands show hard
    /// silhouette edges, a difference measured at 3% of the frame beyond 16 levels against a
    /// browser, so they are not optional.
    /// </para>
    /// <para>
    /// <b>Filter color space.</b> <c>color-interpolation-filters</c> is read (from the
    /// primitive, the filter, or any ancestor, as the inherited property it is) and defaults
    /// to <c>linearRGB</c> as the specification says, so a file that says nothing (Breathing
    /// Field) blurs in linear light like the browser it was approved in, and Monterey's
    /// explicit <c>sRGB</c> blurs in sRGB. See <see cref="SvgGroup.BlurLinearRgb"/> for why the
    /// choice is visible.
    /// </para>
    /// <para>
    /// <b>The contract.</b> Anything outside that subset is skipped, never guessed at, and never
    /// throws: the element (or the animation, or the gradient) is left out, one line naming it
    /// and the reason goes into <see cref="SvgScene.Skipped"/>, and the rest of the file is read
    /// as though it were not there. A malformed path, a fill naming a missing gradient, a
    /// filter with no blur in it, an animation whose values do not all share one path
    /// skeleton, a second <c>dur</c> disagreeing with the first — all skips. The tests hold every
    /// shipped file's skip list to the deliberate omissions below, so a future asset the reader
    /// only half understands fails CI rather than drawing half a wallpaper.
    /// </para>
    /// <para>The two places the files are deliberately not taken literally:</para>
    /// <list type="bullet">
    /// <item><b>Filters other than a Gaussian blur are unsupported</b>, and the element that
    /// references one is skipped whole. That is what drops the Gradient files' grain rect
    /// (<c>feTurbulence</c> at 5% opacity, a sub-1% luminance change and the most expensive thing
    /// in those files) — skipping the filter alone would leave a black rect at 5%.</item>
    /// <item><b>The blur is rasterized at a resolution the artwork fixes</b> (a set number
    /// of pixels per sigma under a ceiling; see <see cref="SvgGroup.BlurSigmaWorkingPx"/>)
    /// over a region that is the viewBox inflated by two sigma on every side when the filter
    /// declares a percentage region. SVG's percentage region is relative to the filtered
    /// group's bounding box, which for an animated group changes every frame; the browser hard
    /// cuts the blurred mass there, while this lets an edge circle blur symmetrically. A
    /// <c>filterUnits="userSpaceOnUse"</c> region with absolute numbers is honored literally.</item>
    /// </list>
    /// <para>
    /// Paint inherits down the tree with SVG's root default of black (not the <c>none</c>
    /// CursorPackLoader assumes for cursor packs); Monterey's root <c>fill="none"</c> therefore
    /// reaches its unfilled paths as expected. Colors are compared as <c>0xRRGGBB</c>, so the
    /// mixed-case hex across the files (<c>#FF0066</c> vs <c>#ff0066</c>) is one key to a
    /// palette.
    /// </para>
    /// </remarks>
    internal static class BackgroundSvgReader
    {
        /// <summary>The scene for a parsed SVG document's root element.</summary>
        internal static SvgScene Read(XElement root)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            var ctx = new Context(ReadViewBox(root));
            ctx.ReadGradients(root);
            ctx.ReadClipPaths(root);
            ctx.ReadFilters(root);

            var rootPaint = Inherited.Root(root);
            var children = ctx.Walk(root, rootPaint, SKMatrix.Identity);
            var group = new SvgGroup(SKMatrix.Identity, null, 1f, null, 0, SKRect.Empty, 0, false, children.ToArray());
            return new SvgScene(ctx.ViewBox, group, ctx.PeriodTicks, ctx.HasBlendModes, ctx.Skipped.AsReadOnly());
        }

        // ------------------------------------------------------------------------------ viewBox

        private static SKRect ReadViewBox(XElement root)
        {
            var parts = SvgPathSkeleton.ParseNumbers((string)root.Attribute("viewBox"));
            if (parts != null && parts.Length == 4 && parts[2] > 0 && parts[3] > 0)
                return SKRect.Create(parts[0], parts[1], parts[2], parts[3]);

            // No usable viewBox: fall back to width/height, then to a nominal square, so a file
            // still draws at some size rather than failing to load.
            float w = Num(Attr(root, "width"), 0), h = Num(Attr(root, "height"), 0);
            return w > 0 && h > 0 ? SKRect.Create(0, 0, w, h) : SKRect.Create(0, 0, 100, 100);
        }

        // -------------------------------------------------------------------------- inheritance

        /// <summary>The presentation properties SVG resolves down the tree that this reader
        /// honors: <c>fill</c>, <c>fill-opacity</c>, <c>stroke</c>, <c>stroke-opacity</c> and
        /// <c>stroke-width</c>. A group's <c>opacity</c> is not inherited (it is a layer).</summary>
        private readonly struct Inherited
        {
            private Inherited(string fill, float fillOpacity, string stroke, float strokeOpacity, float strokeWidth)
            {
                Fill = fill;
                FillOpacity = fillOpacity;
                Stroke = stroke;
                StrokeOpacity = strokeOpacity;
                StrokeWidth = strokeWidth;
            }

            internal string Fill { get; }
            internal float FillOpacity { get; }
            internal string Stroke { get; }
            internal float StrokeOpacity { get; }
            internal float StrokeWidth { get; }

            /// <summary>SVG's initial values (<c>fill: black</c>, <c>stroke: none</c>,
            /// <c>stroke-width: 1</c>) with whatever the root states.</summary>
            internal static Inherited Root(XElement root)
                => new Inherited("black", 1f, "none", 1f, 1f).With(root);

            internal Inherited With(XElement element) => new Inherited(
                Attr(element, "fill") ?? Fill,
                FillOpacity * Num(Attr(element, "fill-opacity"), 1f),
                Attr(element, "stroke") ?? Stroke,
                StrokeOpacity * Num(Attr(element, "stroke-opacity"), 1f),
                Num(Attr(element, "stroke-width"), StrokeWidth));
        }

        // --------------------------------------------------------------------------- the walk

        private sealed class FilterSpec
        {
            internal float StdDeviation;
            internal SKRect? Region;
            /// <summary>Mix in linear light (the default) rather than in sRGB.</summary>
            internal bool LinearRgb;
        }

        private sealed class Context
        {
            internal readonly SKRect ViewBox;
            internal readonly Dictionary<string, SvgGradientSpec> Gradients = new Dictionary<string, SvgGradientSpec>(StringComparer.Ordinal);
            internal readonly Dictionary<string, SKPath> ClipPaths = new Dictionary<string, SKPath>(StringComparer.Ordinal);
            internal readonly Dictionary<string, FilterSpec> Filters = new Dictionary<string, FilterSpec>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> UnsupportedFilters = new Dictionary<string, string>(StringComparer.Ordinal);
            /// <summary>Every <c>&lt;g&gt;</c> with an id read so far, with the element it came
            /// from, for <c>&lt;use&gt;</c> to clone (see the class remarks for the rule).</summary>
            internal readonly Dictionary<string, (SvgGroup Node, XElement Element)> GroupsById = new Dictionary<string, (SvgGroup, XElement)>(StringComparer.Ordinal);
            internal readonly List<string> Skipped = new List<string>();
            internal long PeriodTicks;
            internal bool HasBlendModes;

            internal Context(SKRect viewBox)
            {
                ViewBox = viewBox;
            }

            internal void Skip(XElement element, string reason)
            {
                string id = (string)element.Attribute("id");
                Skipped.Add(element.Name.LocalName + (id != null ? "#" + id : "") + ": " + reason);
            }

            // ------------------------------------------------------------------ definitions

            internal void ReadGradients(XElement root)
            {
                foreach (var gradient in root.Descendants().Where(e => e.Name.LocalName is "linearGradient" or "radialGradient"))
                {
                    string id = (string)gradient.Attribute("id");
                    if (id == null)
                        continue;
                    if ((string)gradient.Attribute("href") != null || (string)gradient.Attribute(Xlink + "href") != null)
                    {
                        Skip(gradient, "gradient inheritance by href is not supported");
                        continue;
                    }
                    string spread = (string)gradient.Attribute("spreadMethod");
                    if (spread != null && spread != "pad")
                    {
                        Skip(gradient, "spreadMethod '" + spread + "' is not supported");
                        continue;
                    }

                    bool radial = gradient.Name.LocalName == "radialGradient";
                    bool objectBoundingBox = (string)gradient.Attribute("gradientUnits") != "userSpaceOnUse";
                    float refW = objectBoundingBox ? 1f : ViewBox.Width;
                    float refH = objectBoundingBox ? 1f : ViewBox.Height;
                    // A percent in userSpaceOnUse is of the viewport; a unitless number is literal.
                    // A radial's radius is a percent of the normalized diagonal, close enough to the
                    // mean of the two sides for the viewports here (none of the files use it).
                    float refR = objectBoundingBox ? 1f : (ViewBox.Width + ViewBox.Height) / 2f;

                    float x1, y1, x2 = 0, y2 = 0, r = 0;
                    if (radial)
                    {
                        x1 = Coord((string)gradient.Attribute("cx") ?? "50%", refW);
                        y1 = Coord((string)gradient.Attribute("cy") ?? "50%", refH);
                        r = Coord((string)gradient.Attribute("r") ?? "50%", refR);
                        string fx = (string)gradient.Attribute("fx"), fy = (string)gradient.Attribute("fy");
                        if ((fx != null && Coord(fx, refW) != x1) || (fy != null && Coord(fy, refH) != y1))
                        {
                            Skip(gradient, "a focal point off the center is not supported");
                            continue;
                        }
                    }
                    else
                    {
                        x1 = Coord((string)gradient.Attribute("x1") ?? "0%", refW);
                        y1 = Coord((string)gradient.Attribute("y1") ?? "0%", refH);
                        x2 = Coord((string)gradient.Attribute("x2") ?? "100%", refW);
                        y2 = Coord((string)gradient.Attribute("y2") ?? "0%", refH);
                    }

                    var transform = ReadTransform((string)gradient.Attribute("gradientTransform"));

                    var argb = new List<uint>();
                    var offsets = new List<float>();
                    bool bad = false;
                    foreach (var stop in gradient.Elements().Where(e => e.Name.LocalName == "stop"))
                    {
                        if (!TryParseColor(Attr(stop, "stop-color") ?? "black", out uint color))
                        {
                            Skip(stop, "stop-color '" + Attr(stop, "stop-color") + "' is not a color");
                            bad = true;
                            break;
                        }
                        float opacity = Math.Clamp(Num(Attr(stop, "stop-opacity"), 1f), 0f, 1f);
                        // `offset` is optional and defaults to 0 (Monterey omits it on 20 of 58
                        // stops); offsets are clamped and made monotonic as the spec says.
                        float offset = Math.Clamp(Coord(Attr(stop, "offset") ?? "0", 1f), 0f, 1f);
                        if (offsets.Count > 0 && offset < offsets[offsets.Count - 1])
                            offset = offsets[offsets.Count - 1];
                        argb.Add(WithOpacity(color, opacity));
                        offsets.Add(offset);
                    }
                    if (bad)
                        continue;
                    if (argb.Count == 0)
                    {
                        Skip(gradient, "no stops");
                        continue;
                    }

                    Gradients[id] = new SvgGradientSpec(radial, x1, y1, x2, y2, r, objectBoundingBox, transform,
                        argb.ToArray(), offsets.ToArray());
                }
            }

            internal void ReadClipPaths(XElement root)
            {
                foreach (var clip in root.Descendants().Where(e => e.Name.LocalName == "clipPath"))
                {
                    string id = (string)clip.Attribute("id");
                    if (id == null)
                        continue;
                    if ((string)clip.Attribute("clipPathUnits") == "objectBoundingBox")
                    {
                        Skip(clip, "clipPathUnits objectBoundingBox is not supported");
                        continue;
                    }

                    var path = new SKPath();
                    bool bad = false;
                    foreach (var child in clip.Elements())
                    {
                        SKPath piece = child.Name.LocalName switch
                        {
                            "rect" => RectPath(child),
                            "ellipse" => EllipsePath(child),
                            "circle" => CirclePath(child),
                            "path" => TryStaticPath((string)child.Attribute("d")),
                            _ => null,
                        };
                        if (piece == null)
                        {
                            Skip(clip, "contains a '" + child.Name.LocalName + "' the reader cannot turn into a clip");
                            bad = true;
                            break;
                        }
                        var childTransform = ReadTransform((string)child.Attribute("transform"));
                        if (!childTransform.IsIdentity)
                            piece.Transform(childTransform);
                        path.AddPath(piece);
                        piece.Dispose();
                    }
                    if (bad)
                    {
                        path.Dispose();
                        continue;
                    }

                    var transform = ReadTransform((string)clip.Attribute("transform"));
                    if (!transform.IsIdentity)
                        path.Transform(transform);
                    ClipPaths[id] = path;
                }
            }

            internal void ReadFilters(XElement root)
            {
                foreach (var filter in root.Descendants().Where(e => e.Name.LocalName == "filter"))
                {
                    string id = (string)filter.Attribute("id");
                    if (id == null)
                        continue;

                    float stdDeviation = -1;
                    XElement blurPrimitive = null;
                    string unsupported = null;
                    foreach (var primitive in filter.Elements())
                    {
                        switch (primitive.Name.LocalName)
                        {
                            case "feGaussianBlur":
                            {
                                var sigma = SvgPathSkeleton.ParseNumbers((string)primitive.Attribute("stdDeviation"));
                                if (sigma == null || sigma.Length == 0 || stdDeviation >= 0)
                                {
                                    unsupported = "more than one blur, or a blur without a stdDeviation";
                                    break;
                                }
                                // A two-value stdDeviation is anisotropic; nothing here writes one,
                                // and a wallpaper blur is round, so the first is taken.
                                stdDeviation = sigma[0];
                                blurPrimitive = primitive;
                                break;
                            }
                            // Figma's boilerplate around a blur: a transparent flood blended in
                            // normal mode is the identity.
                            case "feFlood":
                            case "feBlend":
                                break;
                            default:
                                unsupported = "'" + primitive.Name.LocalName + "' is not supported";
                                break;
                        }
                        if (unsupported != null)
                            break;
                    }
                    if (unsupported == null && stdDeviation < 0)
                        unsupported = "has no feGaussianBlur";
                    if (unsupported != null)
                    {
                        UnsupportedFilters[id] = unsupported;
                        continue;
                    }

                    // The property is inherited and may sit on the primitive, the filter or an
                    // ancestor; the initial value is linearRGB and `auto` is left to the
                    // implementation, which browsers and resvg take as linearRGB too.
                    string space = InheritedAttr(blurPrimitive, "color-interpolation-filters") ?? "linearRGB";
                    bool linearRgb;
                    switch (space.Trim())
                    {
                        case "sRGB": linearRgb = false; break;
                        case "linearRGB":
                        case "auto": linearRgb = true; break;
                        default:
                            UnsupportedFilters[id] = "color-interpolation-filters '" + space + "' is not supported";
                            continue;
                    }

                    SKRect? region = null;
                    if ((string)filter.Attribute("filterUnits") == "userSpaceOnUse")
                    {
                        string x = (string)filter.Attribute("x"), y = (string)filter.Attribute("y");
                        string w = (string)filter.Attribute("width"), h = (string)filter.Attribute("height");
                        if (x != null && y != null && w != null && h != null
                            && !x.Contains('%') && !y.Contains('%') && !w.Contains('%') && !h.Contains('%'))
                        {
                            region = SKRect.Create(Num(x, 0), Num(y, 0), Num(w, 0), Num(h, 0));
                        }
                    }
                    Filters[id] = new FilterSpec { StdDeviation = Math.Max(0, stdDeviation), Region = region, LinearRgb = linearRgb };
                }
            }

            // ------------------------------------------------------------------------- nodes

            /// <summary>The drawable children of <paramref name="element"/>, in document order.
            /// <paramref name="ctm"/> is the static transform accumulated so far, used only to
            /// place a blur region in a group's own space.</summary>
            internal List<SvgNode> Walk(XElement element, Inherited inherited, SKMatrix ctm)
            {
                var nodes = new List<SvgNode>();
                foreach (var child in element.Elements())
                {
                    switch (child.Name.LocalName)
                    {
                        // Read separately (definitions) or by their owners (animations), or
                        // carrying nothing drawable.
                        case "defs":
                        case "title":
                        case "desc":
                        case "metadata":
                        case "clipPath":
                        case "filter":
                        case "linearGradient":
                        case "radialGradient":
                        case "stop":
                        case "animate":
                        case "animateTransform":
                        case "animateMotion":
                        case "set":
                            continue;

                        case "g":
                        {
                            var group = ReadGroup(child, inherited, ctm);
                            if (group != null)
                                nodes.Add(group);
                            continue;
                        }

                        case "rect":
                        case "ellipse":
                        case "path":
                        case "circle":
                        {
                            var node = ReadShape(child, inherited, ctm);
                            if (node != null)
                                nodes.Add(node);
                            continue;
                        }

                        case "use":
                        {
                            var node = ReadUse(child, ctm);
                            if (node != null)
                                nodes.Add(node);
                            continue;
                        }

                        default:
                            Skip(child, "element is not supported");
                            continue;
                    }
                }
                return nodes;
            }

            private SvgGroup ReadGroup(XElement element, Inherited inherited, SKMatrix ctm)
            {
                var transform = ReadTransform((string)element.Attribute("transform"));
                if (!TryReadClip(element, out var clip))
                    return null;
                if (!TryReadFilter(element, out var filter))
                    return null;

                SmilTrack translate = null;
                foreach (var animation in element.Elements().Where(e => e.Name.LocalName is "animate" or "animateTransform"))
                {
                    if (animation.Name.LocalName == "animateTransform"
                        && (string)animation.Attribute("attributeName") == "transform"
                        && (string)animation.Attribute("type") == "translate"
                        && translate == null)
                    {
                        translate = ReadTrack(animation, 2, ParseTranslate);
                        continue;
                    }
                    Skip(animation, "only animateTransform type=translate is supported on a group");
                }

                float opacity = Math.Clamp(Num(Attr(element, "opacity"), 1f), 0f, 1f);
                var inner = Concat(ctm, transform);
                var children = Walk(element, inherited.With(element), inner);

                var group = new SvgGroup(transform, translate, opacity, clip, filter?.StdDeviation ?? 0,
                    BlurRegionFor(filter, inner), BlurScaleFor(filter), filter?.LinearRgb ?? false, children.ToArray());
                string id = (string)element.Attribute("id");
                if (id != null)
                    GroupsById[id] = (group, element);
                return group;
            }

            /// <summary>
            /// A <c>&lt;use&gt;</c> as a group of one whose child is the subtree already built
            /// for the referenced <c>&lt;g&gt;</c>; null (and a skip) outside the form the class
            /// remarks describe. The <c>x</c>/<c>y</c> translate is appended to the right of the
            /// element's own transform, as SVG 2 specifies, so it moves the clone in the
            /// clone's own space.
            /// </summary>
            private SvgGroup ReadUse(XElement element, SKMatrix ctm)
            {
                string href = (string)element.Attribute("href") ?? (string)element.Attribute(Xlink + "href");
                string id = href != null && href.StartsWith("#", StringComparison.Ordinal) ? href.Substring(1).Trim() : null;
                if (string.IsNullOrEmpty(id))
                {
                    Skip(element, "use without a local #id href is not supported");
                    return null;
                }
                if (!GroupsById.TryGetValue(id, out var target))
                {
                    Skip(element, "use of #" + id + ", which is not a <g> read earlier in the file");
                    return null;
                }
                if (target.Element.Parent != element.Parent)
                {
                    Skip(element, "use of #" + id + " from outside the group's own parent is not supported");
                    return null;
                }
                foreach (var name in new[] { "fill", "fill-opacity", "stroke", "stroke-opacity", "stroke-width", "mix-blend-mode" })
                {
                    if (Attr(element, name) != null)
                    {
                        Skip(element, "a use carrying its own " + name + " is not supported");
                        return null;
                    }
                }
                if (!TryReadClip(element, out var clip))
                    return null;
                if (!TryReadFilter(element, out var filter))
                    return null;
                foreach (var animation in element.Elements().Where(e => e.Name.LocalName is "animate" or "animateTransform"))
                    Skip(animation, "animation on a use is not supported");

                var transform = Concat(ReadTransform((string)element.Attribute("transform")),
                    SKMatrix.CreateTranslation(Num(Attr(element, "x"), 0), Num(Attr(element, "y"), 0)));
                float opacity = Math.Clamp(Num(Attr(element, "opacity"), 1f), 0f, 1f);
                var inner = Concat(ctm, transform);
                return new SvgGroup(transform, null, opacity, clip, filter?.StdDeviation ?? 0,
                    BlurRegionFor(filter, inner), BlurScaleFor(filter), filter?.LinearRgb ?? false, new SvgNode[] { target.Node });
            }

            /// <summary>
            /// The user-space rectangle a blurred group is rasterized over: the filter's own
            /// region when it declares an absolute one, else the viewBox seen from the group's
            /// user space, grown by two sigma on every side so the mass just outside the
            /// picture still bleeds in (see the class remarks for why not the bbox rule).
            /// Empty for no filter.
            /// </summary>
            private SKRect BlurRegionFor(FilterSpec filter, SKMatrix groupCtm)
            {
                if (filter == null)
                    return SKRect.Empty;
                if (filter.Region.HasValue)
                    return filter.Region.Value;

                var region = ViewBox;
                if (!groupCtm.IsIdentity && groupCtm.TryInvert(out var inverse))
                    region = inverse.MapRect(ViewBox);
                float pad = 2f * filter.StdDeviation;
                region.Inflate(pad, pad);
                return region;
            }

            /// <summary>
            /// Working pixels per user unit for a blurred group's surface; 0 for no filter.
            /// The lower of the two limits: <see cref="SvgGroup.BlurWorkingPx"/> across the
            /// viewBox's longer side, and <see cref="SvgGroup.BlurSigmaWorkingPx"/> per sigma.
            /// A wide blur (Breathing Field) is bound by the second, a narrow one (Monterey)
            /// by the first; both depend on the file alone, so the picture is the same at
            /// every output size.
            /// </summary>
            private float BlurScaleFor(FilterSpec filter)
            {
                if (filter == null)
                    return 0;
                float k = SvgGroup.BlurWorkingPx / Math.Max(ViewBox.Width, ViewBox.Height);
                if (filter.StdDeviation > 0)
                    k = Math.Min(k, SvgGroup.BlurSigmaWorkingPx / filter.StdDeviation);
                return k;
            }

            private SvgNode ReadShape(XElement element, Inherited inherited, SKMatrix ctm)
            {
                var transform = ReadTransform((string)element.Attribute("transform"));
                if (!TryReadClip(element, out var clip))
                    return null;
                if (!TryReadFilter(element, out var filter))
                    return null;

                var paint = inherited.With(element);
                float opacity = Math.Clamp(Num(Attr(element, "opacity"), 1f), 0f, 1f);
                if (!TryResolvePaint(element, paint.Fill, out var fill))
                    return null;
                if (!TryResolvePaint(element, paint.Stroke, out var stroke))
                    return null;
                float fillAlpha = Math.Clamp(paint.FillOpacity, 0f, 1f) * opacity;
                float strokeAlpha = Math.Clamp(paint.StrokeOpacity, 0f, 1f) * opacity;
                if (!TryReadBlendMode(element, out var blend))
                    return null;

                SvgNode node;
                switch (element.Name.LocalName)
                {
                    case "circle":
                    {
                        float cx = Num(Attr(element, "cx"), 0), cy = Num(Attr(element, "cy"), 0), r = Num(Attr(element, "r"), 0);
                        SmilTrack cxTrack = null, cyTrack = null, rTrack = null;
                        foreach (var animation in element.Elements().Where(e => e.Name.LocalName is "animate" or "animateTransform"))
                        {
                            string attribute = (string)animation.Attribute("attributeName");
                            if (animation.Name.LocalName == "animate" && attribute is "cx" or "cy" or "r")
                            {
                                var track = ReadTrack(animation, 1, ParseOne);
                                if (attribute == "cx") cxTrack = track;
                                else if (attribute == "cy") cyTrack = track;
                                else rTrack = track;
                                continue;
                            }
                            Skip(animation, "only cx, cy and r animate on a circle");
                        }
                        if (r <= 0 && rTrack == null)
                            return null;
                        node = new SvgCircle(cx, cy, r, cxTrack, cyTrack, rTrack, fill, fillAlpha, stroke, strokeAlpha,
                            paint.StrokeWidth, blend);
                        break;
                    }

                    case "path":
                    {
                        string d = (string)element.Attribute("d");
                        SvgPathSkeleton skeleton;
                        float[] numbers;
                        try
                        {
                            (skeleton, numbers) = SvgPathSkeleton.Parse(d);
                        }
                        catch (FormatException ex)
                        {
                            Skip(element, "path data: " + ex.Message);
                            return null;
                        }

                        SmilTrack dTrack = null;
                        foreach (var animation in element.Elements().Where(e => e.Name.LocalName is "animate" or "animateTransform"))
                        {
                            if (animation.Name.LocalName == "animate" && (string)animation.Attribute("attributeName") == "d" && dTrack == null)
                            {
                                dTrack = ReadTrack(animation, skeleton.NumberCount, value => ParseSameShape(value, skeleton));
                                continue;
                            }
                            Skip(animation, "only d animates on a path");
                        }

                        if (dTrack != null)
                        {
                            node = new SvgShape(skeleton, dTrack, fill, fillAlpha, stroke, strokeAlpha, paint.StrokeWidth, blend);
                        }
                        else
                        {
                            var path = skeleton.Build(numbers);
                            if (path.IsEmpty)
                            {
                                path.Dispose();
                                return null;
                            }
                            node = new SvgShape(path, fill, fillAlpha, stroke, strokeAlpha, paint.StrokeWidth, blend);
                        }
                        break;
                    }

                    default:
                    {
                        foreach (var animation in element.Elements().Where(e => e.Name.LocalName is "animate" or "animateTransform"))
                            Skip(animation, "animation on a " + element.Name.LocalName + " is not supported");
                        var path = element.Name.LocalName == "rect" ? RectPath(element) : EllipsePath(element);
                        if (path == null)
                            return null;
                        node = new SvgShape(path, fill, fillAlpha, stroke, strokeAlpha, paint.StrokeWidth, blend);
                        break;
                    }
                }

                // A shape carrying its own transform, clip or blur is wrapped in a group of one,
                // so the group is the only node that knows how to transform, clip or filter. The
                // order the group applies them in is SVG's for a shape too: the transform
                // establishes the shape's user space, and its clip-path is in that space.
                if (!transform.IsIdentity || clip != null || filter != null)
                {
                    var inner = Concat(ctm, transform);
                    return new SvgGroup(transform, null, 1f, clip, filter?.StdDeviation ?? 0,
                        BlurRegionFor(filter, inner), BlurScaleFor(filter), filter?.LinearRgb ?? false, new[] { node });
                }
                return node;
            }

            // ---------------------------------------------------------------------- attributes

            private bool TryReadClip(XElement element, out SKPath clip)
            {
                clip = null;
                string reference = Attr(element, "clip-path");
                if (reference == null || reference == "none")
                    return true;
                string id = IdOf(reference);
                if (id != null && ClipPaths.TryGetValue(id, out clip))
                    return true;
                Skip(element, "clip-path " + reference + " names no usable clipPath");
                return false;
            }

            private bool TryReadFilter(XElement element, out FilterSpec filter)
            {
                filter = null;
                string reference = Attr(element, "filter");
                if (reference == null || reference == "none")
                    return true;
                string id = IdOf(reference);
                if (id != null && Filters.TryGetValue(id, out filter))
                    return true;
                Skip(element, id != null && UnsupportedFilters.TryGetValue(id, out var why)
                    ? "filter #" + id + " " + why
                    : "filter " + reference + " names no filter");
                return false;
            }

            private bool TryReadBlendMode(XElement element, out SKBlendMode mode)
            {
                mode = SKBlendMode.SrcOver;
                string text = Attr(element, "mix-blend-mode");
                if (text == null || text == "normal")
                    return true;
                switch (text)
                {
                    case "saturation": mode = SKBlendMode.Saturation; break;
                    case "soft-light": mode = SKBlendMode.SoftLight; break;
                    case "multiply": mode = SKBlendMode.Multiply; break;
                    case "screen": mode = SKBlendMode.Screen; break;
                    case "overlay": mode = SKBlendMode.Overlay; break;
                    case "darken": mode = SKBlendMode.Darken; break;
                    case "lighten": mode = SKBlendMode.Lighten; break;
                    case "difference": mode = SKBlendMode.Difference; break;
                    case "hue": mode = SKBlendMode.Hue; break;
                    case "color": mode = SKBlendMode.Color; break;
                    case "luminosity": mode = SKBlendMode.Luminosity; break;
                    default:
                        Skip(element, "mix-blend-mode '" + text + "' is not supported");
                        return false;
                }
                HasBlendModes = true;
                return true;
            }

            /// <summary>A fill or stroke value as a paint: null for <c>none</c>; false (and a
            /// skip) for a color the reader cannot read or a gradient it did not keep.</summary>
            private bool TryResolvePaint(XElement element, string text, out SvgPaint paint)
            {
                paint = null;
                if (string.IsNullOrWhiteSpace(text) || text.Trim() == "none")
                    return true;
                text = text.Trim();
                if (text.StartsWith("url(", StringComparison.Ordinal))
                {
                    string id = IdOf(text);
                    if (id != null && Gradients.TryGetValue(id, out var gradient))
                    {
                        paint = new SvgPaint(gradient);
                        return true;
                    }
                    Skip(element, "paint " + text + " names no usable gradient");
                    return false;
                }
                if (TryParseColor(text, out uint argb))
                {
                    paint = new SvgPaint(argb);
                    return true;
                }
                Skip(element, "paint '" + text + "' is not a color the reader knows");
                return false;
            }

            // ---------------------------------------------------------------------- animation

            /// <summary>A track for an animation element, or null (with a skip) when it is
            /// outside the accepted form or its <c>dur</c> disagrees with the file's.</summary>
            private SmilTrack ReadTrack(XElement animation, int stride, Func<string, float[]> parse)
            {
                var track = SmilTrack.TryParse(animation, stride, parse, out string rejection);
                if (track == null)
                {
                    Skip(animation, rejection);
                    return null;
                }
                if (PeriodTicks == 0)
                {
                    PeriodTicks = track.DurationTicks;
                }
                else if (PeriodTicks != track.DurationTicks)
                {
                    Skip(animation, "dur disagrees with the file's first animation");
                    return null;
                }
                return track;
            }

            private static float[] ParseOne(string value)
            {
                var numbers = SvgPathSkeleton.ParseNumbers(value);
                return numbers != null && numbers.Length == 1 ? numbers : null;
            }

            private static float[] ParseTranslate(string value)
            {
                var numbers = SvgPathSkeleton.ParseNumbers(value);
                if (numbers == null || numbers.Length == 0 || numbers.Length > 2)
                    return null;
                return numbers.Length == 2 ? numbers : new[] { numbers[0], 0f };
            }

            private static float[] ParseSameShape(string value, SvgPathSkeleton skeleton)
            {
                try
                {
                    var (shape, numbers) = SvgPathSkeleton.Parse(value);
                    return shape.SameAs(skeleton) ? numbers : null;
                }
                catch (FormatException)
                {
                    return null;
                }
            }
        }

        // ------------------------------------------------------------------------------ pieces

        private static SKPath RectPath(XElement element)
        {
            float x = Num(Attr(element, "x"), 0), y = Num(Attr(element, "y"), 0);
            float w = Num(Attr(element, "width"), 0), h = Num(Attr(element, "height"), 0);
            if (w <= 0 || h <= 0)
                return null;
            string rxText = Attr(element, "rx"), ryText = Attr(element, "ry");
            float rx = Num(rxText ?? ryText, 0), ry = Num(ryText ?? rxText, 0);
            var path = new SKPath();
            if (rx > 0 || ry > 0)
                path.AddRoundRect(SKRect.Create(x, y, w, h), rx > 0 ? rx : ry, ry > 0 ? ry : rx);
            else
                path.AddRect(SKRect.Create(x, y, w, h));
            return path;
        }

        private static SKPath EllipsePath(XElement element)
        {
            float cx = Num(Attr(element, "cx"), 0), cy = Num(Attr(element, "cy"), 0);
            float rx = Num(Attr(element, "rx"), 0), ry = Num(Attr(element, "ry"), 0);
            if (rx <= 0 || ry <= 0)
                return null;
            var path = new SKPath();
            path.AddOval(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry));
            return path;
        }

        private static SKPath CirclePath(XElement element)
        {
            float cx = Num(Attr(element, "cx"), 0), cy = Num(Attr(element, "cy"), 0), r = Num(Attr(element, "r"), 0);
            if (r <= 0)
                return null;
            var path = new SKPath();
            path.AddCircle(cx, cy, r);
            return path;
        }

        private static SKPath TryStaticPath(string d)
        {
            try
            {
                var (skeleton, numbers) = SvgPathSkeleton.Parse(d);
                return skeleton.Build(numbers);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>The <c>translate</c>/<c>scale</c>/<c>rotate</c>/<c>matrix</c> forms, in SVG's
        /// own right-to-left order (the same grammar as <c>CursorPackLoader.ReadTransform</c>);
        /// identity for none, and for a form outside those four.</summary>
        internal static SKMatrix ReadTransform(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return SKMatrix.Identity;

            var result = SKMatrix.Identity;
            foreach (Match op in Regex.Matches(text, @"(\w+)\s*\(([^)]*)\)"))
            {
                var a = SvgPathSkeleton.ParseNumbers(op.Groups[2].Value) ?? Array.Empty<float>();
                SKMatrix m;
                switch (op.Groups[1].Value)
                {
                    case "translate" when a.Length >= 1:
                        m = SKMatrix.CreateTranslation(a[0], a.Length > 1 ? a[1] : 0);
                        break;
                    case "scale" when a.Length >= 1:
                        m = SKMatrix.CreateScale(a[0], a.Length > 1 ? a[1] : a[0]);
                        break;
                    case "rotate" when a.Length >= 1:
                        m = a.Length > 2
                            ? SKMatrix.CreateRotationDegrees(a[0], a[1], a[2])
                            : SKMatrix.CreateRotationDegrees(a[0]);
                        break;
                    case "matrix" when a.Length == 6:
                        m = new SKMatrix(a[0], a[2], a[4], a[1], a[3], a[5], 0, 0, 1);
                        break;
                    default:
                        continue;
                }
                result = Concat(result, m);
            }
            return result;
        }

        private static SKMatrix Concat(SKMatrix outer, SKMatrix inner)
            => inner.IsIdentity ? outer : outer.IsIdentity ? inner : SKMatrix.Concat(outer, inner);

        private static readonly XNamespace Xlink = XNamespace.Get("http://www.w3.org/1999/xlink");

        /// <summary>An inherited property looked up on <paramref name="element"/> and then each
        /// ancestor in turn, the way SVG resolves one (<c>inherit</c> defers to the parent);
        /// null when nothing on the way to the root states it.</summary>
        private static string InheritedAttr(XElement element, string name)
        {
            for (var e = element; e != null; e = e.Parent)
            {
                string value = Attr(e, name);
                if (value != null && value.Trim() != "inherit")
                    return value;
            }
            return null;
        }

        /// <summary>An attribute that may also be written as a CSS declaration in <c>style</c>,
        /// which takes precedence over the presentation attribute as in a browser.</summary>
        private static string Attr(XElement element, string name)
        {
            string style = (string)element.Attribute("style");
            if (!string.IsNullOrEmpty(style))
            {
                foreach (var declaration in style.Split(';'))
                {
                    int colon = declaration.IndexOf(':');
                    if (colon <= 0)
                        continue;
                    if (declaration.AsSpan(0, colon).Trim().Equals(name, StringComparison.Ordinal))
                        return declaration.Substring(colon + 1).Trim();
                }
            }
            return (string)element.Attribute(name);
        }

        /// <summary>A coordinate that is either a number or a percentage of <paramref name="reference"/>.</summary>
        private static float Coord(string text, float reference)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;
            text = text.Trim();
            if (text.EndsWith("%", StringComparison.Ordinal))
                return Num(text.Substring(0, text.Length - 1), 0) / 100f * reference;
            return Num(text, 0);
        }

        private static float Num(string text, float fallback)
            => text != null && float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;

        /// <summary>The id inside a <c>url(#id)</c> reference, or null when there is none.</summary>
        private static string IdOf(string reference)
        {
            if (string.IsNullOrEmpty(reference))
                return null;
            int open = reference.IndexOf('#');
            int close = reference.IndexOf(')', open + 1);
            return open < 0 || close < 0 ? null : reference.Substring(open + 1, close - open - 1).Trim();
        }

        /// <summary><c>#rrggbb</c>, <c>#rgb</c>, <c>white</c>, <c>black</c> and <c>transparent</c>
        /// as ARGB; false for anything else (named colors beyond those, <c>rgb()</c>, and
        /// <c>currentColor</c> are not in the corpus and are not guessed at).</summary>
        internal static bool TryParseColor(string text, out uint argb)
        {
            argb = 0;
            text = text?.Trim();
            if (string.IsNullOrEmpty(text))
                return false;
            switch (text.ToLowerInvariant())
            {
                case "white": argb = 0xFFFFFFFF; return true;
                case "black": argb = 0xFF000000; return true;
                case "transparent": argb = 0x00000000; return true;
            }
            if (text[0] == '#' && text.Length == 7
                && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                argb = 0xFF000000 | rgb;
                return true;
            }
            if (text[0] == '#' && text.Length == 4
                && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var s))
            {
                uint r = (s >> 8) & 0xF, g = (s >> 4) & 0xF, b = s & 0xF;
                argb = 0xFF000000 | r << 20 | r << 16 | g << 12 | g << 8 | b << 4 | b;
                return true;
            }
            return false;
        }

        private static uint WithOpacity(uint argb, float opacity)
        {
            if (opacity >= 1f)
                return argb;
            uint alpha = (uint)Math.Clamp(Math.Round(((argb >> 24) & 0xFF) * opacity), 0, 255);
            return alpha << 24 | (argb & 0x00FFFFFF);
        }
    }
}
