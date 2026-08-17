using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// One filled/stroked shape of a themed cursor glyph. <see cref="PathData"/> is SVG path
    /// syntax accepted by <c>SKPath.ParseSvgPathData</c> and is expressed in the owning
    /// <see cref="CursorGlyph.ViewBox"/> coordinate space. Layers are painted in list order
    /// (first = bottom), which is the source SVG's document order.
    /// </summary>
    public sealed class CursorGlyphPath
    {
        internal CursorGlyphPath(string pathData, uint fill, uint stroke, float strokeWidth)
        {
            PathData = pathData;
            Fill = new SKColor(fill);
            Stroke = new SKColor(stroke);
            StrokeWidth = strokeWidth;
        }

        /// <summary>SVG path data, in viewBox units.</summary>
        public string PathData { get; }

        /// <summary>Fill colour (ARGB; the source SVG's <c>fill</c> folded together with its
        /// <c>opacity</c>). Never transparent — every stored layer is a real fill.</summary>
        public SKColor Fill { get; }

        /// <summary>Contrast-halo colour (ARGB), or transparent when the layer has no halo.
        /// The halo is a *centred* stroke, so a caller must paint the stroke pass first and the
        /// fill on top, otherwise the halo eats half the glyph's ink.</summary>
        public SKColor Stroke { get; }

        /// <summary>Halo width in viewBox units (0 when <see cref="Stroke"/> is transparent).</summary>
        public float StrokeWidth { get; }

        /// <summary>True when this layer wants a contrast halo painted behind its fill.</summary>
        public bool HasStroke => Stroke.Alpha > 0 && StrokeWidth > 0;
    }

    /// <summary>
    /// A themed cursor shape: a square viewBox, the hotspot inside it, and the layers to paint.
    /// Immutable and shared process-wide — a caller may cache parsed <c>SKPath</c>s keyed by the
    /// instance, but must not mutate anything reachable from here.
    /// </summary>
    public sealed class CursorGlyph
    {
        internal CursorGlyph(float viewBox, float hotspotX, float hotspotY, CursorGlyphPath[] paths)
        {
            ViewBox = viewBox;
            Hotspot = new SKPoint(hotspotX, hotspotY);
            Paths = Array.AsReadOnly(paths);
        }

        /// <summary>Side of the (always square) source viewBox. Draw scale for a target glyph of
        /// <c>px</c> pixels is <c>px / ViewBox</c>.</summary>
        public float ViewBox { get; }

        /// <summary>The point of the glyph that sits on the sampled cursor position, in viewBox
        /// units. See <see cref="CursorAssets"/> for how each kind's hotspot was derived.</summary>
        public SKPoint Hotspot { get; }

        /// <summary>Layers, bottom-first.</summary>
        public IReadOnlyList<CursorGlyphPath> Paths { get; }
    }

    /// <summary>
    /// Static vector artwork for the themed cursor styles of <c>CursorContent</c> — the styles
    /// other than <c>native</c>, which draws the recorded 512×512 cursor box instead and never
    /// consults this table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Artwork is icons8, normalised by hand into path data so nothing new is taken on beside the
    /// pinned SkiaSharp (no SVG parser at runtime, no image assets). Normalisation rules applied:
    /// <c>&lt;rect&gt;</c>/<c>&lt;polygon&gt;</c> primitives were expanded to explicit
    /// <c>M…L…Z</c> paths with any <c>rotate()</c> baked into the emitted vertices; a layer's
    /// <c>opacity</c> was folded into its fill alpha; and degenerate open "hairline" sub-paths —
    /// contributing no area once implicitly closed — were dropped. No source path was reshaped.
    /// </para>
    /// <para>
    /// The five monochrome styles carry a white halo so a black glyph survives dark video; the two
    /// paper/ink styles ship their own dark outline layers and get none.
    /// </para>
    /// <para>
    /// Hotspot heuristics (all in viewBox units): <b>arrow</b> = the arrowhead's tip vertex read
    /// off the source path — not the bounding-box corner, which sits outside the ink wherever the
    /// tip is rounded; <b>hand</b> = the index fingertip, i.e. the mid-x of the index-finger column
    /// at the finger's top edge; <b>ibeam</b> = the centre of the glyph bounds, matching the OS
    /// I-beam whose hotspot is its middle. Every hotspot is inside its glyph's ink bounds
    /// (asserted by <c>CursorAssetsTests</c>).
    /// </para>
    /// <para>
    /// Coverage gaps and substitutions: icons8 has no I-beam in the <c>softteal</c>,
    /// <c>papercut</c> or <c>doodle</c> families (nor in the neighbouring <c>cotton</c>/<c>dusk</c>
    /// ones), so <see cref="TryGet"/> returns null for those and the caller falls back to the
    /// style's arrow, exactly as it does for <c>custom</c> and the unmodelled cursor kinds.
    /// <c>softteal</c> has no hand-cursor either, so its "one finger" gesture — the same pointing
    /// hand, same palette — stands in. <c>doodle</c> has neither, so its hand borrows
    /// <c>papercut</c>'s hand-cursor (the closest playful colour icon that exists); its arrow is
    /// icons8's doodle "select cursor".
    /// </para>
    /// </remarks>
    public static class CursorAssets
    {
        /// <summary>The style <c>CursorContent.Style</c> defaults to.</summary>
        public const string DefaultStyle = "ios-glyph";

        /// <summary>The style that draws the recorded cursor box; never present in this table.</summary>
        public const string NativeStyle = "native";

        /// <summary>Cursor kinds with dedicated artwork. Any other kind resolves to <c>arrow</c>.</summary>
        public const string KindArrow = "arrow";
        public const string KindHand = "hand";
        public const string KindIBeam = "ibeam";

        private const uint Black = 0xFF000000;
        private const uint Black35 = 0x59000000; // opacity=".35"
        private const uint White = 0xFFFFFFFF;
        private const uint Teal = 0xFF306263;
        private const uint PaperBlue = 0xFF0037FF;
        private const uint PaperSky = 0xFF52AFFF;
        private const uint PaperInk35 = 0x59383838; // #383838 @ opacity=".35"
        private const uint PaperSkinDeep = 0xFFFFA6A3;
        private const uint PaperSkin = 0xFFFFC1BF;
        private const uint DoodleBlue = 0xFF4B7BB2;

        // Halo widths are 2 units at the ios-glyph 30-unit viewBox, carried across the other
        // monochrome styles at the same visual weight (2/30 of the viewBox).
        private const float Halo24 = 1.6f;
        private const float Halo30 = 2.0f;
        private const float Halo48 = 3.2f;

        /// <summary>The themed styles, in picker order. Excludes <see cref="NativeStyle"/>.</summary>
        public static IReadOnlyList<string> Styles { get; } = Array.AsReadOnly(new[]
        {
            "ios-glyph", "material", "fluent", "plumpy", "softteal", "papercut", "doodle",
        });

        /// <summary>
        /// The glyph for a (style, kind) pair, or null when the pair has no artwork — an unknown
        /// or <c>native</c> style, an unknown kind, or one of the documented per-style gaps. A
        /// caller that gets null for a kind should retry with <see cref="KindArrow"/>.
        /// </summary>
        public static CursorGlyph TryGet(string style, string kind)
        {
            if (string.IsNullOrEmpty(style) || string.IsNullOrEmpty(kind))
                return null;
            return Table.TryGetValue(style + "/" + kind, out var glyph) ? glyph : null;
        }

        private static readonly Dictionary<string, CursorGlyph> Table = BuildTable();

        private static CursorGlyphPath P(string pathData, uint fill, uint stroke = 0, float strokeWidth = 0)
            => new CursorGlyphPath(pathData, fill, stroke, strokeWidth);

        private static CursorGlyph G(float viewBox, float hotspotX, float hotspotY, params CursorGlyphPath[] paths)
            => new CursorGlyph(viewBox, hotspotX, hotspotY, paths);

        private static Dictionary<string, CursorGlyph> BuildTable()
        {
            var t = new Dictionary<string, CursorGlyph>(StringComparer.OrdinalIgnoreCase);

            // ---- ios-glyph (icons8 ios11 glyphs, viewBox 30) — the default style ------------
            t["ios-glyph/arrow"] = G(30f, 8.5f, 3.5f, P(
                "M 9 3 A 1 1 0 0 0 8 4 L 8 21 A 1 1 0 0 0 9 22 A 1 1 0 0 0 9.796875 21.601562 " +
                "L 12.919922 18.119141 L 16.382812 26.117188 C 16.701812 26.855187 17.566828 27.188469 " +
                "18.298828 26.855469 C 19.020828 26.527469 19.340672 25.678078 19.013672 24.955078 " +
                "L 15.439453 17.039062 L 21 17 A 1 1 0 0 0 22 16 A 1 1 0 0 0 21.628906 15.222656 " +
                "L 9.7832031 3.3789062 A 1 1 0 0 0 9 3 z", Black, White, Halo30));
            t["ios-glyph/hand"] = G(30f, 11f, 2.5f, P(
                "M 11 2 C 9.895 2 9 2.895 9 4 L 9 12 L 9 13 L 9 19.5 C 6.448 18.201 5.289 18 4 18 " +
                "C 2.503911 18 1.0097445 18.577311 1.0019531 20.486328 L 5.5 22.5 L 8.65625 25.65625 " +
                "C 10.15625 27.15625 12.192453 28 14.314453 28 L 21 28 C 23.209 28 25 26.209 25 24 " +
                "L 25 14 A 2 2 0 0 0 23 12 A 2 2 0 0 0 21.193359 13.148438 C 21.066812 13.062233 21 13 21 13 " +
                "A 2 2 0 0 0 19 11 A 2 2 0 0 0 17 13 L 17 12 C 17 10.895 16.105 10 15 10 " +
                "C 13.895 10 13 10.895 13 12 L 13 4 C 13 2.895 12.105 2 11 2 z " +
                "M 1.0019531 20.486328 L 1 20.486328 L 1 20.5 C 1 20.494991 1.0019328 20.491319 1.0019531 20.486328 z",
                Black, White, Halo30));
            t["ios-glyph/ibeam"] = G(30f, 15f, 15f, P(
                "M 9 3 A 1.0001 1.0001 0 1 0 9 5 L 12 5 C 13.104545 5 14 5.8954545 14 7 L 14 14 L 11 14 " +
                "A 1.0001 1.0001 0 1 0 11 16 L 14 16 L 14 23 C 14 24.104545 13.104545 25 12 25 L 9 25 " +
                "A 1.0001 1.0001 0 1 0 9 27 L 12 27 C 13.196286 27 14.264543 26.454192 15 25.611328 " +
                "C 15.735457 26.454192 16.803714 27 18 27 L 21 27 A 1.0001 1.0001 0 1 0 21 25 L 18 25 " +
                "C 16.895455 25 16 24.104545 16 23 L 16 16 L 19 16 A 1.0001 1.0001 0 1 0 19 14 L 16 14 " +
                "L 16 7 C 16 5.8954545 16.895455 5 18 5 L 21 5 A 1.0001 1.0001 0 1 0 21 3 L 18 3 " +
                "C 16.803714 3 15.735457 3.5458078 15 4.3886719 C 14.264543 3.5458078 13.196286 3 12 3 L 9 3 z",
                Black, White, Halo30));

            // ---- material (icons8 m_rounded, viewBox 24) ------------------------------------
            t["material/arrow"] = G(24f, 7.4f, 3.3f, P(
                "M8.3,3.213l9.468,8.836c0.475,0.443,0.2,1.24-0.447,1.296L13.2,13.7l2.807,6.21 " +
                "c0.272,0.602,0.006,1.311-0.596,1.585l0,0 c-0.61,0.277-1.33,0-1.596-0.615L11.1,14.6l-2.833,2.695 " +
                "C7.789,17.749,7,17.411,7,16.751V3.778C7,3.102,7.806,2.752,8.3,3.213z", Black, White, Halo24));
            t["material/hand"] = G(24f, 9.5f, 1.5f, P(
                "M 9.5 1 C 8.672 1 8 1.672 8 2.5 L 8 9 L 8 14 L 8 15.060547 L 5.3378906 13.710938 " +
                "C 4.7798906 13.427938 4.1072344 13.492906 3.6152344 13.878906 C 2.8562344 14.474906 " +
                "2.7887031 15.601203 3.4707031 16.283203 L 8.3085938 21.121094 C 8.8715937 21.684094 " +
                "9.6346875 22 10.429688 22 L 17 22 C 18.657 22 20 20.657 20 19 L 20 12.193359 " +
                "C 20 11.216359 19.292125 10.381703 18.328125 10.220703 L 11 9 L 11 2.5 C 11 1.672 10.328 1 9.5 1 z",
                Black, White, Halo24));
            t["material/ibeam"] = G(24f, 12f, 12f, P(
                "M 9 2 A 1.0001 1.0001 0 1 0 9 4 C 9.8333333 4 10.421991 4.2041597 10.802734 4.3945312 " +
                "C 10.899195 4.4427618 10.93573 4.4711718 11 4.5097656 L 11 11 L 9 11 A 1.0001 1.0001 0 1 0 9 13 " +
                "L 11 13 L 11 19.490234 C 10.93573 19.528828 10.899195 19.557238 10.802734 19.605469 " +
                "C 10.421991 19.79584 9.8333333 20 9 20 A 1.0001 1.0001 0 1 0 9 22 C 10.166667 22 11.078009 21.70416 " +
                "11.697266 21.394531 C 11.883184 21.301572 11.85968 21.280666 12 21.1875 C 12.14032 21.28067 " +
                "12.116816 21.301572 12.302734 21.394531 C 12.921991 21.70416 13.833333 22 15 22 " +
                "A 1.0001 1.0001 0 1 0 15 20 C 14.166667 20 13.578009 19.79584 13.197266 19.605469 " +
                "C 13.100805 19.557238 13.06427 19.528828 13 19.490234 L 13 13 L 15 13 A 1.0001 1.0001 0 1 0 15 11 " +
                "L 13 11 L 13 4.5097656 C 13.06427 4.4711718 13.100805 4.4427618 13.197266 4.3945312 " +
                "C 13.578009 4.2041597 14.166667 4 15 4 A 1.0001 1.0001 0 1 0 15 2 C 13.833333 2 12.921991 2.2958402 " +
                "12.302734 2.6054688 C 12.116816 2.6984278 12.14032 2.7193337 12 2.8125 C 11.85968 2.7193337 " +
                "11.883184 2.6984278 11.697266 2.6054688 C 11.078009 2.2958402 10.166667 2 9 2 z",
                Black, White, Halo24));

            // ---- fluent (icons8 fluent-systems-filled, viewBox 48) --------------------------
            t["fluent/arrow"] = G(48f, 11.8f, 4f, P(
                "M35.654,24.09L13.524,3.404c-0.437-0.407-1.074-0.517-1.622-0.28C11.354,3.362,11,3.902,11,4.5v30 " +
                "c0,0.577,0.331,1.103,0.851,1.352c0.519,0.251,1.137,0.18,1.587-0.181l6.112-4.892l5.777,13.306 " +
                "c0.33,0.76,1.214,1.109,1.973,0.778l4.586-1.992c0.76-0.33,1.108-1.213,0.778-1.973l-3.044-7.011 " +
                "l-2.733-6.294l7.914-0.915c0.581-0.067,1.07-0.466,1.253-1.021C36.237,25.1,36.081,24.49,35.654,24.09z",
                Black, White, Halo48));
            t["fluent/hand"] = G(48f, 22.5f, 3.6f, P(
                "M37.911,20.996c-0.216-0.058-0.437-0.106-0.658-0.147L27,19c0,0,0-11.162,0-11.5C27,5.015,24.985,3,22.5,3 " +
                "S18,5.015,18,7.5C18,9.347,18,25,18,25c0,0.001-0.001,0.002-0.001,0.002S18,25,13.618,23.668 " +
                "C12.092,23.203,10.76,23,9.629,23c-1.012,0-1.871,0.178-2.586,0.535c-2.072,1.031-3.095,2.798-3.04,5.255 " +
                "c0.005,0.215,0.128,0.408,0.32,0.503c0,0,4.612,2.257,7.034,3.534c1.518,0.8,6.143,2.302,9.585,9.621 " +
                "c0.875,1.86,3.118,2.729,5.23,2.524c5.713-0.555,6.416-0.616,10.211-1.001c2.441-0.248,3.512-1.946,4.256-4.178 " +
                "c0.845-2.536,2.323-6.691,3.086-9.336C44.914,26.334,42.121,22.116,37.911,20.996z", Black, White, Halo48));
            t["fluent/ibeam"] = G(48f, 24f, 24f, P(
                "M 17 5 A 2.0002 2.0002 0 1 0 17 9 L 20.5 9 C 21.352124 9 22 9.647876 22 10.5 L 22 22 L 20 22 " +
                "A 2.0002 2.0002 0 1 0 20 26 L 22 26 L 22 37.5 C 22 38.352124 21.352124 39 20.5 39 L 17 39 " +
                "A 2.0002 2.0002 0 1 0 17 43 L 20.5 43 C 21.842913 43 23.039891 42.45261 24 41.638672 " +
                "C 24.960109 42.45261 26.157087 43 27.5 43 L 31 43 A 2.0002 2.0002 0 1 0 31 39 L 27.5 39 " +
                "C 26.647876 39 26 38.352124 26 37.5 L 26 26 L 28 26 A 2.0002 2.0002 0 1 0 28 22 L 26 22 " +
                "L 26 10.5 C 26 9.647876 26.647876 9 27.5 9 L 31 9 A 2.0002 2.0002 0 1 0 31 5 L 27.5 5 " +
                "C 26.157087 5 24.960109 5.5473905 24 6.3613281 C 23.039891 5.5473905 21.842913 5 20.5 5 L 17 5 z",
                Black, White, Halo48));

            // ---- plumpy (icons8 plumpy, viewBox 24) — body at 35% behind opaque detail ------
            t["plumpy/arrow"] = G(24f, 6.8f, 2.4f,
                P("M18.584,12.854L8.091,2.361C7.319,1.59,6,2.136,6,3.227v15.044c0,0.996,1.103,1.596,1.939,1.054 " +
                  "l3.1-2.008l1.954-0.337l1.229-1.212l3.735-0.797C18.932,14.763,19.288,13.559,18.584,12.854z",
                  Black35, White, Halo24),
                P("M11.039,17.318l1.911,3.72c0.447,0.87,1.515,1.213,2.385,0.766l0,0c0.87-0.447,1.213-1.514,0.766-2.384 " +
                  "l-1.878-3.651", Black, White, Halo24));
            t["plumpy/hand"] = G(24f, 9f, 1.4f,
                P("M17.493,9.082L11,8V3c0-1.104-0.896-2-2-2S7,1.896,7,3v10.064l-0.186-0.186c-1.172-1.172-3.071-1.172-4.243,0 " +
                  "l-0.279,0.279c-0.391,0.391-0.391,1.024,0,1.414l5.641,5.641l0.141-0.141C8.682,20.643,9.494,21,10.395,21h7.335 " +
                  "c1.254,0,2.27-1.016,2.27-2.27v-6.689C20,10.574,18.94,9.323,17.493,9.082z", Black35, White, Halo24),
                P("M19.5,19c-0.386,0-11.614,0-12,0C6.672,19,6,19.672,6,20.5S6.672,22,7.5,22c0.386,0,11.614,0,12,0 " +
                  "c0.828,0,1.5-0.672,1.5-1.5S20.328,19,19.5,19z", Black, White, Halo24));
            t["plumpy/ibeam"] = G(24f, 12f, 12.5f,
                P("M10.5,6.5C10.5,5.67,9.83,5,9,5H8.5C7.67,5,7,4.33,7,3.5C7,2.67,7.67,2,8.5,2H9c2.48,0,4.5,2.02,4.5,4.5V11h-3V6.5z",
                  Black, White, Halo24),
                P("M17,21.5c0,0.83-0.67,1.5-1.5,1.5H15c-2.48,0-4.5-2.02-4.5-4.5V14h3v4.5c0,0.83,0.67,1.5,1.5,1.5h0.5 " +
                  "C16.33,20,17,20.67,17,21.5z", Black, White, Halo24),
                P("M12,10c-0.829,0-1.5-0.671-1.5-1.5v-2C10.5,4.019,12.519,2,15,2h0.5C16.329,2,17,2.671,17,3.5S16.329,5,15.5,5H15 " +
                  "c-0.827,0-1.5,0.673-1.5,1.5v2C13.5,9.329,12.829,10,12,10z", Black, White, Halo24),
                P("M9,23H8.5C7.671,23,7,22.329,7,21.5S7.671,20,8.5,20H9c0.827,0,1.5-0.673,1.5-1.5v-2c0-0.829,0.671-1.5,1.5-1.5 " +
                  "s1.5,0.671,1.5,1.5v2C13.5,20.981,11.481,23,9,23z", Black, White, Halo24),
                P("M15.5,14h-2L12,15l-1.5-1h-2C7.671,14,7,13.329,7,12.5S7.671,11,8.5,11h2l1.5-1l1.5,1h2 " +
                  "c0.829,0,1.5,0.671,1.5,1.5S16.329,14,15.5,14z", Black35, White, Halo24));

            // ---- softteal (icons8 softteal, viewBox 24) — no I-beam in the family -----------
            t["softteal/arrow"] = G(24f, 6.6f, 1.4f, P(
                "M20.509,14.055L8.057,1.362C7.306,0.596,6.003,1.127,6.002,2.2L5.987,19.981 " +
                "c-0.001,0.968,1.086,1.538,1.882,0.986l3.136-2.137c0.522-0.356,1.239-0.144,1.485,0.438l1.299,3.086 " +
                "c0.223,0.529,0.83,0.781,1.362,0.564l0.71-0.29c0.54-0.22,0.796-0.84,0.57-1.377l-1.312-3.116 " +
                "c-0.25-0.593,0.118-1.264,0.752-1.373l3.984-0.687C20.809,15.914,21.187,14.746,20.509,14.055z",
                Teal, White, Halo24));
            // "one finger" stands in for the missing hand-cursor: same pointing gesture, same palette.
            t["softteal/hand"] = G(24f, 8.5f, 1.4f, P(
                "M18.078,10.245c-0.393,0.192-0.682,0.32-1.086-0.065c-0.658-0.625-1.279-1-2.457-0.427 " +
                "c-0.355,0.173-0.785,0.129-1.08-0.134c-0.985-0.878-1.74-0.849-2.74-0.034C10.29,9.93,10,9.748,10,9.229V2.5 " +
                "C10,1.672,9.328,1,8.5,1S7,1.672,7,2.5v9.766c0,0.42-0.262,0.788-0.654,0.937C5.464,13.54,4,14.347,4,16 " +
                "c0,1.818,1.225,3.462,2.319,4.567C7.242,21.499,8.513,22,9.824,22L16,22c2.209,0,4-1.791,4-4v-6.355 " +
                "C20,10.358,18.906,9.841,18.078,10.245z", Teal, White, Halo24));

            // ---- papercut (icons8 papercut, viewBox 120) — rect/polygon expanded to paths ---
            // The two 35% layers are the source's offset drop shadows; they must stay under their
            // coloured twins, hence source order is preserved verbatim.
            t["papercut/arrow"] = G(120f, 28.014f, 10.007f,
                P("M52.606,69.235 L72.658,56.047 L101.781,100.328 L81.729,113.516 Z", Black35),
                P("M50.407,65.893 L70.459,52.705 L99.582,96.987 L79.530,110.174 Z", PaperBlue),
                P("M105.926,59.869 L65.362,70.986 L39.085,103.827 L28.547,15.007 Z", Black35),
                P("M105.392,54.869 L64.829,65.986 L38.551,98.827 L28.014,10.007 Z", PaperSky));
            t["papercut/hand"] = G(120f, 51.5f, 12f,
                P("M31.48,72.063l11.524,8.503V57.999h68v51h-70L10,75.999 C15.26,69.469,24.733,67.084,31.48,72.063z", PaperInk35),
                P("M53.558,84.354l-10.554,20.645L10,70.838l0,0c5.26-6.53,14.733-7.754,21.48-2.775L53.558,84.354z", PaperSkinDeep),
                P("M42.949,20.526v42.527h17.055V20.526c0-4.71-3.818-8.527-8.527-8.527l0,0 " +
                  "C46.767,11.999,42.949,15.817,42.949,20.526z", PaperSkin),
                P("M59.949,50.526v28.473h17.055V50.526c0-4.71-3.818-8.527-8.527-8.527l0,0 " +
                  "C63.767,41.999,59.949,45.817,59.949,50.526z", PaperSkin),
                P("M76.949,50.526v28.473h17.055V50.526c0-4.71-3.818-8.527-8.527-8.527l0,0 " +
                  "C80.767,41.999,76.949,45.817,76.949,50.526z", PaperSkin),
                P("M93.949,50.526v28.473h17.055V50.526c0-4.71-3.818-8.527-8.527-8.527l0,0 " +
                  "C97.767,41.999,93.949,45.817,93.949,50.526z", PaperSkin),
                P("M43.004,49.999 L111.004,49.999 L111.004,104.999 L43.004,104.999 Z", PaperSkin));

            // ---- doodle (icons8 doodle, viewBox 48) — hand borrowed from papercut -----------
            t["doodle/arrow"] = G(48f, 14.1f, 6f,
                P("M41.886,28.191c-4.186,0.785-10.996,1.48-15.56,2.449C24,33.371,14.408,43.088,14.408,43.088 " +
                  "c-0.309-12.571-0.309-24.201-0.309-37.116", DoodleBlue),
                P("M14.908,43.088c-0.234-9.569-0.294-19.14-0.306-28.712c-0.003-2.801-0.003-5.602-0.003-8.404 " +
                  "c-0.251,0.144-0.502,0.288-0.752,0.432c1.247,0.692,2.355,1.739,3.452,2.636c2.09,1.71,4.152,3.453,6.217,5.193 " +
                  "c4.532,3.819,9.033,7.689,13.707,11.336c0.906,0.707,1.819,1.41,2.763,2.066c0.558,0.388,1.13,0.791,1.768,1.038 " +
                  "c0-0.321,0-0.643,0-0.964c-3.034,0.564-6.102,0.931-9.155,1.371c-1.558,0.225-3.116,0.459-4.666,0.739 " +
                  "c-0.55,0.099-1.52,0.106-1.96,0.467c-0.229,0.189-0.421,0.484-0.621,0.705c-2.805,3.116-5.766,6.098-8.697,9.094 " +
                  "c-0.865,0.884-1.731,1.768-2.6,2.649c-0.453,0.458,0.254,1.166,0.707,0.707c2.172-2.201,4.332-4.413,6.479-6.638 " +
                  "c1.444-1.497,2.887-2.997,4.295-4.529c0.253-0.276,0.506-0.553,0.754-0.833c0.082-0.092,0.159-0.215,0.255-0.291 " +
                  "c0.159-0.126,0.461-0.142,0.763-0.2c1.51-0.294,3.03-0.533,4.551-0.759c3.386-0.503,6.794-0.892,10.161-1.518 " +
                  "c0.535-0.1,0.41-0.805,0-0.964c0.064,0.025-0.084-0.037-0.182-0.088c-0.113-0.059-0.225-0.122-0.335-0.187 " +
                  "c-0.297-0.176-0.585-0.368-0.869-0.563c-0.795-0.546-1.565-1.128-2.328-1.718c-2.14-1.653-4.228-3.376-6.308-5.103 " +
                  "c-4.515-3.748-8.962-7.579-13.493-11.308c-1.317-1.084-2.651-2.367-4.152-3.2c-0.329-0.183-0.752,0.046-0.752,0.432 " +
                  "c0,9.63-0.001,19.26,0.146,28.889c0.042,2.742,0.096,5.485,0.163,8.227C13.924,43.73,14.924,43.733,14.908,43.088z",
                  Black),
                P("M17.126,20.583c0.037-2.645-0.027-5.286,0.01-7.931c0.009-0.644-0.991-0.644-1,0 " +
                  "c-0.037,2.645,0.027,5.286-0.01,7.931C16.117,21.226,17.117,21.227,17.126,20.583L17.126,20.583z", Black));
            t["doodle/hand"] = t["papercut/hand"];

            return t;
        }
    }
}
