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
        /// units — the pack's own hotspot, off its <c>.cur</c> headers.</summary>
        public SKPoint Hotspot { get; }

        /// <summary>Layers, bottom-first.</summary>
        public IReadOnlyList<CursorGlyphPath> Paths { get; }
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
    /// The one style here is <c>vision</c> — the Vision Cursor pack by iDarques (CC BY-NC-ND 4.0),
    /// which covers every drawable kind in a dark and a light colourway. It is read out of the
    /// pack's own Photoshop sources rather than traced: <c>pointer128black.psd</c> and its White
    /// twin carry every cursor as a shape layer, and their vector masks are the path data below at
    /// the authored 128 document size. The constants are named after the PSD layer each came from.
    /// </para>
    /// <para>
    /// Faithfulness notes, all of them forced by what this table can hold (flat fills and one
    /// centred contrast stroke per layer, no image assets and no SVG parser at runtime beyond the
    /// pinned SkiaSharp):
    /// </para>
    /// <list type="bullet">
    /// <item>The pack strokes its shapes <i>outside</i> the path. A centred stroke painted under
    /// the layer's own fill is the same picture at double the authored width, which is where the
    /// 16/12/8 halo widths come from (the pack authors 8/6/4).</item>
    /// <item>Photoshop marks a hole with a path operation and leaves the winding alone; the same
    /// hole is spelled here as a reversed contour, which is what the nonzero fill rule wants.</item>
    /// <item><c>wait</c> and <c>appstarting</c> are gradient-filled/stroked in the source (they are
    /// the pack's two animated cursors). Each is flattened to the gradient's accent stop —
    /// <see cref="Spin"/> — which is the colour that tells them apart from the plain pointer.</item>
    /// <item>The <c>help</c> badge is live text in DIN Round Pro in the source, a font that cannot
    /// be shipped, so that one glyph is traced from the PSD's own rasterised type layer.</item>
    /// <item>Only one of the two diagonal-resize cursors is authored per file (the Black source has
    /// the NESW one, the White source the NWSE one). Both colourways use the Black source's
    /// geometry, mirrored about the viewBox midline for the other diagonal, so the pair matches.</item>
    /// </list>
    /// <para>
    /// Every glyph is checked against the pack's own rendered PNGs by <c>CursorAssetsTests</c>'
    /// sibling verification, and every hotspot is the pack's own, off its <c>.cur</c> headers
    /// scaled to 128 (pointer 2/32, link 7/32, alternate 16/2, the centred ones 16/16).
    /// </para>
    /// </remarks>
    public static class CursorAssets
    {
        /// <summary>The style <c>CursorContent.Style</c> defaults to.</summary>
        public const string DefaultStyle = VisionStyle;

        /// <summary>The style that draws the recorded cursor box; never present in this table.</summary>
        public const string NativeStyle = "native";

        private const string VisionStyle = "vision";

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
            VisionStyle,
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

        private static readonly Dictionary<string, CursorVariant[]> VariantTable
            = new Dictionary<string, CursorVariant[]>(StringComparer.OrdinalIgnoreCase)
            {
                // Dark first: an ink glyph with a paper halo is the one that reads on arbitrary
                // video without being chosen, so it is what an unset variant draws.
                [VisionStyle] = new[]
                {
                    new CursorVariant("dark", "Dark"),
                    new CursorVariant("light", "Light"),
                },
            };

        private static readonly Dictionary<string, CursorGlyph> Table = BuildTable();

        private static CursorGlyphPath P(string pathData, uint fill, uint stroke, float strokeWidth)
            => new CursorGlyphPath(pathData, fill, stroke, strokeWidth);

        private static CursorGlyph G(float viewBox, float hotspotX, float hotspotY, params CursorGlyphPath[] paths)
            => new CursorGlyph(viewBox, hotspotX, hotspotY, paths);

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
                t[Key(VisionStyle, variant, KindWait)] = G(128f, 64f, 64f,
                    P(VisionBusy, Spin, Ink, Halo));
                t[Key(VisionStyle, variant, KindAppStarting)] = G(128f, 8f, 8f,
                    P(VisionWork, Paper, Spin, Halo));
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

            return t;
        }
    }
}
