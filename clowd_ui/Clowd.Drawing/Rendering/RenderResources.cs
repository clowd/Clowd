using System;
using System.Collections.Concurrent;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Clowd.Drawing.Rendering
{
    /// <summary>
    /// Process-wide immutable brush/pen cache (final-design §A.5, fixes R6). Draw AND hit-test
    /// paths ask for pens/brushes by value instead of allocating mutable ones per call — a warm
    /// render pass performs zero brush/pen allocations.
    ///
    /// PORT NOTE (RenderResources): replace every `new SolidColorBrush(...)` / `new Pen(...)` in
    /// DrawObject/Draw/Contains/Bounds with GetBrush/GetPen, keeping thickness/dash arguments
    /// identical (Pen and ImmutablePen share the same defaults: flat caps, miter join). Dashed
    /// pens must pass a shared static <see cref="ImmutableDashStyle"/> (e.g. <see cref="Dash4x4"/>)
    /// — the pen cache key compares the dash style by reference.
    /// </summary>
    internal static class RenderResources
    {
        // a color-slider scrub can mint thousands of distinct colors over a session; when the
        // caches exceed this soft cap they are simply dumped — immutable resources still held by
        // in-flight draws stay valid, and the working set repopulates on the next frame
        private const int SoftCap = 4096;

        /// <summary>The 4-on/4-off dash used by DrawDashedBorder and the selection marquee.</summary>
        public static readonly ImmutableDashStyle Dash4x4 = new ImmutableDashStyle(new double[] { 4, 4 }, 0);

        /// <summary>Ink dash for <see cref="LineDashStyle.Dashed"/>. Dash arrays are in units of
        /// pen thickness, so the pattern scales with the stroke width.</summary>
        public static readonly ImmutableDashStyle DashStroke = new ImmutableDashStyle(new double[] { 4, 2 }, 0);

        /// <summary>Ink dash for <see cref="LineDashStyle.Dotted"/>. Square dots — pens come from
        /// this cache with the ImmutablePen default (flat) caps, same as every other stroke.</summary>
        public static readonly ImmutableDashStyle DotStroke = new ImmutableDashStyle(new double[] { 1, 2 }, 0);

        private static readonly ConcurrentDictionary<uint, ImmutableSolidColorBrush> _brushes =
            new ConcurrentDictionary<uint, ImmutableSolidColorBrush>();

        private static readonly ConcurrentDictionary<PenKey, ImmutablePen> _pens =
            new ConcurrentDictionary<PenKey, ImmutablePen>();

        public static ImmutableSolidColorBrush GetBrush(Color color)
        {
            var key = color.ToUInt32();
            if (_brushes.TryGetValue(key, out var brush))
                return brush;

            if (_brushes.Count >= SoftCap)
                _brushes.Clear();

            return _brushes.GetOrAdd(key, static k => new ImmutableSolidColorBrush(Color.FromUInt32(k)));
        }

        /// <summary>
        /// The shared dash instance for an ink dash style, or null for Solid. Must return the same
        /// instance every call — <see cref="GetPen"/> keys on the dash style BY REFERENCE.
        /// </summary>
        public static ImmutableDashStyle GetDash(LineDashStyle style) => style switch
        {
            LineDashStyle.Dashed => DashStroke,
            LineDashStyle.Dotted => DotStroke,
            _ => null,
        };

        /// <summary>
        /// The cap default matches Pen/ImmutablePen (flat) so existing callers are unaffected;
        /// line-shaped ink (line/arrow/pencil) passes Round, and the matching BOUNDS pen must pass
        /// the same cap — round caps extend half the stroke width past each endpoint. The join
        /// defaults to Miter for the same reason; the arrow head passes Round to soften its corners.
        /// </summary>
        public static ImmutablePen GetPen(Color color, double thickness, ImmutableDashStyle dashStyle = null,
                                          PenLineCap lineCap = PenLineCap.Flat, PenLineJoin lineJoin = PenLineJoin.Miter)
        {
            var key = new PenKey(color.ToUInt32(), thickness, dashStyle, lineCap, lineJoin);
            if (_pens.TryGetValue(key, out var pen))
                return pen;

            if (_pens.Count >= SoftCap)
                _pens.Clear();

            return _pens.GetOrAdd(key, static k => new ImmutablePen(GetBrush(Color.FromUInt32(k.Color)), k.Thickness, k.Dash, k.Cap, k.Join));
        }

        private static readonly Color DarkText = Color.FromRgb(0x1F, 0x1F, 0x1F);

        // fills darker than this get white text: keeps white on red/blue/green (L≈0.2–0.35) and
        // switches to dark on orange/yellow/pastels (L≳0.45)
        private const double WhiteTextMaxLuminance = 0.4;

        /// <summary>
        /// Near-black or white, whichever reads better on <paramref name="background"/> — for
        /// text drawn on an object-colored fill (notes, step badges, measure labels), where the
        /// user picks the fill. Judged on WCAG relative luminance, but biased toward white: the
        /// pure contrast-ratio crossover (~0.18) puts dark text on saturated mid tones such as a
        /// stock blue or green, where white is what reads as intended. Translucent fills are judged
        /// as if over white, which is what most screenshots are.
        /// </summary>
        public static Color GetContrastingText(Color background)
        {
            static double Channel(byte v, byte a)
            {
                var c = (v * a + 255 * (255 - a)) / (255.0 * 255.0); // composite over white
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            var l = 0.2126 * Channel(background.R, background.A)
                    + 0.7152 * Channel(background.G, background.A)
                    + 0.0722 * Channel(background.B, background.A);

            return l < WhiteTextMaxLuminance ? Colors.White : DarkText;
        }

        private readonly record struct PenKey(uint Color, double Thickness, ImmutableDashStyle Dash, PenLineCap Cap, PenLineJoin Join);
    }
}
