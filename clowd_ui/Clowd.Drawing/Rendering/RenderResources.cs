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
        /// the same cap — round caps extend half the stroke width past each endpoint.
        /// </summary>
        public static ImmutablePen GetPen(Color color, double thickness, ImmutableDashStyle dashStyle = null,
                                          PenLineCap lineCap = PenLineCap.Flat)
        {
            var key = new PenKey(color.ToUInt32(), thickness, dashStyle, lineCap);
            if (_pens.TryGetValue(key, out var pen))
                return pen;

            if (_pens.Count >= SoftCap)
                _pens.Clear();

            return _pens.GetOrAdd(key, static k => new ImmutablePen(GetBrush(Color.FromUInt32(k.Color)), k.Thickness, k.Dash, k.Cap));
        }

        private readonly record struct PenKey(uint Color, double Thickness, ImmutableDashStyle Dash, PenLineCap Cap);
    }
}
