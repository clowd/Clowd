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

        /// <summary>
        /// Draw on Screen: a palette and brush, the tile that opens the drawing toolbar from the recording
        /// and share strips. Not one of the spec's §13 set — it was added later, as a filled icon (the
        /// path is the supplied artwork verbatim), so it is fill-only where the spec's stroke icons are not.
        /// </summary>
        public static readonly TrayGlyph Draw = new TrayGlyph("draw", Array.Empty<string>(), new[]
        {
            "M 12.208984 1.3769531 C 11.251471 1.39406 10.309463 1.4916016 9.4394531 1.6132812 C 7.1194278 1.9377604 5.2890625 2.5019531 5.2890625 2.5019531 A 1.0002222 1.0002222 0 0 0 5.8769531 4.4140625 C 5.8769531 4.4140625 7.5680722 3.8942709 9.7167969 3.59375 C 11.865522 3.2932291 14.442471 3.2813423 15.980469 4.0253906 C 16.731147 4.3883888 17.027998 4.7296141 17.144531 4.9648438 C 17.261064 5.2000734 17.262154 5.3983149 17.171875 5.6992188 C 16.991325 6.3010262 16.3621 7.011428 15.970703 7.4980469 A 1.0001 1.0001 0 0 0 15.96875 7.4980469 C 14.856953 8.8812841 14.942908 10.87146 16.244141 11.939453 C 17.545373 13.007446 19.657791 13.156691 22.277344 12.140625 A 1.0001662 1.0001662 0 1 0 21.554688 10.275391 C 19.340239 11.134325 18.014439 10.803585 17.513672 10.392578 C 17.012904 9.9815709 16.933089 9.4937159 17.529297 8.7519531 L 17.527344 8.7519531 C 17.73204 8.497572 18.699987 7.55988 19.085938 6.2734375 C 19.278911 5.6302162 19.305327 4.8245516 18.935547 4.078125 C 18.565767 3.3316984 17.864884 2.7146112 16.851562 2.2246094 C 15.725061 1.6796336 14.451326 1.4458424 13.169922 1.3886719 C 12.849571 1.3743792 12.528156 1.3712508 12.208984 1.3769531 z M 3.2363281 5.1132812 A 1.0001 1.0001 0 0 0 2.3183594 5.7949219 C 1.98473 6.7910776 2.0341111 8.0750811 2.3886719 9.4785156 C 2.7432327 10.88195 3.4670918 12.388094 4.9101562 13.212891 C 5.593481 13.603282 6.3806423 13.806499 7.1601562 13.806641 C 7.2465176 14.172523 7.4325003 14.518438 7.7011719 14.787109 A 1.0001 1.0001 0 0 0 7.75 14.833984 L 16.355469 22.34375 A 1.0001 1.0001 0 0 0 16.361328 22.349609 C 16.828716 22.751929 17.404236 22.878548 17.978516 22.828125 L 18.046875 22.888672 L 18.113281 22.822266 C 18.355986 22.788073 18.596988 22.728867 18.828125 22.630859 A 1.0001 1.0001 0 0 0 18.857422 22.632812 L 18.851562 22.621094 C 18.883539 22.607057 18.918123 22.602409 18.951172 22.591797 L 18.857422 22.632812 A 1.0001 1.0001 0 0 0 19.591797 22.3125 A 1.0001 1.0001 0 0 0 19.59375 22.308594 C 20.417908 21.412686 20.269179 20.062012 19.503906 19.183594 A 1.0001 1.0001 0 0 0 19.501953 19.183594 L 11.988281 10.589844 A 1.0001 1.0001 0 0 0 11.941406 10.541016 C 11.598736 10.199215 11.131472 9.985172 10.65625 9.9511719 C 10.648423 9.9506119 10.640644 9.9516342 10.632812 9.9511719 C 10.628381 9.9151863 10.63026 9.8774871 10.625 9.8417969 C 10.484181 8.8862621 10.06159 7.9681949 9.2988281 7.328125 C 8.0866788 6.3106122 6.6427243 6.4053983 5.6894531 6.3671875 C 5.2128176 6.3480821 4.847668 6.2967034 4.6386719 6.2089844 C 4.4296757 6.1212653 4.3305534 6.0581226 4.1875 5.7207031 A 1.0001 1.0001 0 0 0 3.2363281 5.1132812 z M 4.2910156 8.1289062 C 4.745946 8.2501075 5.2040041 8.3489855 5.609375 8.3652344 C 6.6831039 8.4082734 7.4758212 8.4078874 8.0136719 8.859375 C 8.2829096 9.0853051 8.5673504 9.61105 8.6445312 10.134766 C 8.7217122 10.658481 8.5998732 11.105603 8.4082031 11.314453 C 7.8591617 11.912707 6.7817624 11.978985 5.9023438 11.476562 C 5.1584082 11.05136 4.6020642 10.072597 4.328125 8.9882812 C 4.2361193 8.6241014 4.3173467 8.4805267 4.2910156 8.1289062 z M 10.574219 12.013672 L 12.136719 13.798828 L 10.958984 14.976562 L 9.171875 13.417969 L 10.574219 12.013672 z",
        });

        public static IReadOnlyList<TrayGlyph> All { get; } = new[]
        {
            Mic, MicOff, Spk, SpkOff, Cam, CamOff, Sliders, X, Rotate, Chev,
            Check, Eye, EyeOff, Resize, Play, Pause, Stop, Rec, Draw,
        };
    }
}
