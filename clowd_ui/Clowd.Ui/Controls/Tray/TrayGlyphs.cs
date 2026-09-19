using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// One icon of the tray set: SVG path data on a 24-unit canvas plus the stroke width it was drawn
    /// with. The path strings are the design spec's own data, copied verbatim; the spec's rect, circle
    /// and polygon shapes were converted to path data at authoring time so there is no shape-to-path
    /// conversion code to get wrong at runtime.
    /// <para>
    /// The two union <see cref="Geometry"/> objects (<see cref="StrokeGeometry"/>,
    /// <see cref="FillGeometry"/>) are built lazily because <see cref="StreamGeometry"/> needs a
    /// platform render interface: the unit tests reference this assembly with no Avalonia platform at
    /// all, and they must be able to read the path strings without a renderer existing.
    /// </para>
    /// </summary>
    public sealed class TrayGlyph
    {
        private readonly Lazy<Geometry> _strokeUnion;
        private readonly Lazy<Geometry> _fillUnion;

        public TrayGlyph(string name, string[] strokePaths, string[] fillPaths = null, double strokeWidth = 1.8)
        {
            Name = name;
            StrokePaths = strokePaths ?? Array.Empty<string>();
            FillPaths = fillPaths ?? Array.Empty<string>();
            StrokeWidth = strokeWidth;
            _strokeUnion = new Lazy<Geometry>(() => Union(StrokePaths));
            _fillUnion = new Lazy<Geometry>(() => Union(FillPaths));
        }

        public string Name { get; }

        /// <summary>Path data on the 24-unit canvas, stroked with <see cref="StrokeWidth"/>, never filled.</summary>
        public IReadOnlyList<string> StrokePaths { get; }

        /// <summary>Path data on the 24-unit canvas, filled with the foreground and never stroked.</summary>
        public IReadOnlyList<string> FillPaths { get; }

        /// <summary>Canvas units, so it scales with the glyph: 1.8 for everything but the rotate arrow (2.4).</summary>
        public double StrokeWidth { get; }

        /// <summary>
        /// All of <see cref="StrokePaths"/> as ONE geometry, so the glyph can be stroked in a single draw
        /// op; null when the glyph has no stroke. Drawing each path separately double-composites where two
        /// of them overlap, which is invisible at full opacity and obvious at the spec's .45 "off" and .4
        /// disabled opacities — the micOff/eyeOff slash crossing would read ≈.69 instead of .45.
        /// </summary>
        public Geometry StrokeGeometry => _strokeUnion.Value;

        /// <summary>All of <see cref="FillPaths"/> as ONE geometry; null when the glyph has no fill.</summary>
        public Geometry FillGeometry => _fillUnion.Value;

        private static IReadOnlyList<Geometry> ParseAll(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0)
                return Array.Empty<Geometry>();

            var parsed = new Geometry[paths.Count];
            for (var i = 0; i < paths.Count; i++)
                parsed[i] = StreamGeometry.Parse(paths[i]);
            return parsed;
        }

        /// <summary>
        /// Combines the paths into one geometry. A <see cref="GeometryGroup"/> rather than one path string
        /// joined together, because a path may open with a RELATIVE moveto (cam's lens cone is
        /// <c>"m15.5 10 6-3.5…"</c>): concatenated, that would be measured from the previous subpath's
        /// current point instead of the canvas origin. <see cref="FillRule.NonZero"/> so overlapping
        /// subpaths union instead of cancelling to a hole — no two fill paths in the set overlap (only
        /// <c>pause</c> has two, and they are the spec's two separate bars), and each is wound the same
        /// clockwise way the §13 rect/circle/polygon conversions produce, so the rule never removes ink.
        /// </summary>
        private static Geometry Union(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0)
                return null;

            // The children are parsed fresh here rather than shared with anything else: a
            // GeometryCollection takes ownership of what is put in it.
            return new GeometryGroup
            {
                FillRule = FillRule.NonZero,
                Children = new GeometryCollection(ParseAll(paths)),
            };
        }
    }

    /// <summary>
    /// The tray icon set (design spec §13), keyed by the spec's own short names. These are the only
    /// glyphs the floating strips use: the app's existing filled VectorIcons geometries are drawn for a
    /// different weight and are deliberately not reused here.
    /// </summary>
    public static class TrayGlyphs
    {
        public static readonly TrayGlyph Mic = new TrayGlyph("mic", new[]
        {
            "M12 2.5a3 3 0 0 0-3 3v6.5a3 3 0 0 0 6 0V5.5a3 3 0 0 0-3-3Z",
            "M19 11v1a7 7 0 0 1-14 0v-1",
            "M12 19v2.5",
        });

        public static readonly TrayGlyph MicOff = new TrayGlyph("micOff", new[]
        {
            "M9 9v3a3 3 0 0 0 5.1 2.1",
            "M15 9.3V5.5a3 3 0 0 0-5.9-.7",
            "M19 11v1a7 7 0 0 1-11.3 5.5",
            "M5 11v1a7 7 0 0 0 .4 2.3",
            "M12 19v2.5",
            "M3 3l18 18",
        });

        public static readonly TrayGlyph Spk = new TrayGlyph("spk", new[]
        {
            "M15.5 8.5a5 5 0 0 1 0 7",
            "M18.5 5.5a9 9 0 0 1 0 13",
        }, new[]
        {
            "M11 5 6.5 9H3v6h3.5L11 19V5Z",
        });

        public static readonly TrayGlyph SpkOff = new TrayGlyph("spkOff", new[]
        {
            "M22 9l-6 6",
            "M16 9l6 6",
        }, new[]
        {
            "M11 5 6.5 9H3v6h3.5L11 19V5Z",
        });

        public static readonly TrayGlyph Cam = new TrayGlyph("cam", new[]
        {
            // spec: rect x2.5 y6.5 w13 h11 rx2.5
            "M5,6.5 h8 a2.5,2.5 0 0 1 2.5,2.5 v6 a2.5,2.5 0 0 1 -2.5,2.5 h-8 a2.5,2.5 0 0 1 -2.5,-2.5 v-6 a2.5,2.5 0 0 1 2.5,-2.5 Z",
            "m15.5 10 6-3.5v11l-6-3.5",
        });

        public static readonly TrayGlyph CamOff = new TrayGlyph("camOff", new[]
        {
            "M10.5 6.5H13a2.5 2.5 0 0 1 2.5 2.5v1l6-3.5v11l-2-1.2",
            "M15.5 15.5V15a2.5 2.5 0 0 1-2.5 2.5H5A2.5 2.5 0 0 1 2.5 15V9A2.5 2.5 0 0 1 5 6.5h.5",
            "M3 3l18 18",
        });

        public static readonly TrayGlyph Sliders = new TrayGlyph("sliders", new[]
        {
            "M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1",
            // spec: circle(15,6,r2)
            "M13,6 a2,2 0 1 0 4,0 a2,2 0 1 0 -4,0 Z",
            // spec: circle(9,12,r2)
            "M7,12 a2,2 0 1 0 4,0 a2,2 0 1 0 -4,0 Z",
            // spec: circle(17,18,r2)
            "M15,18 a2,2 0 1 0 4,0 a2,2 0 1 0 -4,0 Z",
        });

        public static readonly TrayGlyph X = new TrayGlyph("x", new[]
        {
            "M6 6l12 12M18 6 6 18",
        });

        /// <summary>Drawn in the 14 px rotate button, so its stroke is 2.4 units to stay visible at that size.</summary>
        public static readonly TrayGlyph Rotate = new TrayGlyph("rotate", new[]
        {
            "M20.5 12a8.5 8.5 0 1 1-2.6-6.1",
            "M21 3v5h-5",
        }, null, 2.4);

        public static readonly TrayGlyph Chev = new TrayGlyph("chev", new[]
        {
            "m6 9 6 6 6-6",
        });

        public static readonly TrayGlyph Check = new TrayGlyph("check", new[]
        {
            "m5 12.5 4.5 4.5L19 7.5",
        });

        public static readonly TrayGlyph Eye = new TrayGlyph("eye", new[]
        {
            "M2.5 12s3.5-6 9.5-6 9.5 6 9.5 6-3.5 6-9.5 6-9.5-6-9.5-6Z",
            // spec: circle(12,12,r3)
            "M9,12 a3,3 0 1 0 6,0 a3,3 0 1 0 -6,0 Z",
        });

        public static readonly TrayGlyph EyeOff = new TrayGlyph("eyeOff", new[]
        {
            "M3 3l18 18",
            "M10.6 6.3A10 10 0 0 1 12 6c6 0 9.5 6 9.5 6a16 16 0 0 1-3 3.6",
            "M6.5 6.6A16 16 0 0 0 2.5 12s3.5 6 9.5 6a9.6 9.6 0 0 0 4.3-1",
            "M9.9 9.9a3 3 0 0 0 4.2 4.2",
        });

        public static readonly TrayGlyph Resize = new TrayGlyph("resize", new[]
        {
            "M15 3.5h5.5V9M9 20.5H3.5V15M20.5 3.5l-6.5 6.5M3.5 20.5 10 14",
        });

        public static readonly TrayGlyph Play = new TrayGlyph("play", Array.Empty<string>(), new[]
        {
            // spec: polygon 7,4 19,12 7,20
            "M7,4 L19,12 L7,20 Z",
        });

        public static readonly TrayGlyph Pause = new TrayGlyph("pause", Array.Empty<string>(), new[]
        {
            // spec: rect x6 y4 w4 h16 rx1.2
            "M7.2,4 h1.6 a1.2,1.2 0 0 1 1.2,1.2 v13.6 a1.2,1.2 0 0 1 -1.2,1.2 h-1.6 a1.2,1.2 0 0 1 -1.2,-1.2 v-13.6 a1.2,1.2 0 0 1 1.2,-1.2 Z",
            // spec: rect x14 y4 w4 h16 rx1.2
            "M15.2,4 h1.6 a1.2,1.2 0 0 1 1.2,1.2 v13.6 a1.2,1.2 0 0 1 -1.2,1.2 h-1.6 a1.2,1.2 0 0 1 -1.2,-1.2 v-13.6 a1.2,1.2 0 0 1 1.2,-1.2 Z",
        });

        public static readonly TrayGlyph Stop = new TrayGlyph("stop", Array.Empty<string>(), new[]
        {
            // spec: rect x6 y6 w12 h12 rx2.5
            "M8.5,6 h7 a2.5,2.5 0 0 1 2.5,2.5 v7 a2.5,2.5 0 0 1 -2.5,2.5 h-7 a2.5,2.5 0 0 1 -2.5,-2.5 v-7 a2.5,2.5 0 0 1 2.5,-2.5 Z",
        });

        public static readonly TrayGlyph Rec = new TrayGlyph("rec", Array.Empty<string>(), new[]
        {
            // spec: circle(12,12,r7)
            "M5,12 a7,7 0 1 0 14,0 a7,7 0 1 0 -14,0 Z",
        });

        public static IReadOnlyList<TrayGlyph> All { get; } = new[]
        {
            Mic, MicOff, Spk, SpkOff, Cam, CamOff, Sliders, X, Rotate, Chev,
            Check, Eye, EyeOff, Resize, Play, Pause, Stop, Rec,
        };
    }
}
