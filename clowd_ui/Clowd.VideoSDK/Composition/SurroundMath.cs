using System;
using Clowd.VideoSDK.Model;
using SkiaSharp;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The one place a <see cref="Surround"/>'s fractions become pixels, and the Skia filter
    /// that draws it. Every kind is expressed as a <b>decoration-only</b> filter: it paints what
    /// sits <i>behind</i> the item (the shadow, the glow, the outline ring) and nothing of the item
    /// itself, so a caller draws the same content twice — once through this filter, once plain on
    /// top. One shape of code for all three kinds, and the item's own pixels are never blurred or
    /// tinted by accident.
    /// </summary>
    /// <remarks>
    /// The scalars live here rather than in the draws because the editor's tiles and the composer
    /// must not drift: this is <c>ClickHighlight</c>'s arrangement, for the same reason.
    /// </remarks>
    public static class SurroundMath
    {
        /// <summary>The fixed light direction: down-right at 45°, so a distance of <c>d</c> offsets
        /// the shadow by <c>d · cos45</c> on both axes.</summary>
        private static readonly double Diagonal = Math.Sqrt(0.5);

        /// <summary>An outline thinner than this many pixels dilates to nothing — the filter would
        /// cost a layer and draw no ring.</summary>
        private const double MinOutlinePx = 0.5;

        /// <summary>What a surround's fractions are measured against: the shorter side of the
        /// item's drawn rect. The shorter one so a wide item cannot grow a blur taller than
        /// itself.</summary>
        public static double ReferenceExtent(SKRect rect) =>
            Math.Min(Math.Abs(rect.Width), Math.Abs(rect.Height));

        /// <summary>The shadow's offset on each axis, in canvas pixels (see
        /// <see cref="Diagonal"/>).</summary>
        public static double OffsetPx(Surround surround, double extentPx) =>
            Fraction(surround?.Distance ?? 0, Surround.MaxDistance) * extentPx * Diagonal;

        /// <summary>The blur radius of a shadow or glow, in canvas pixels.</summary>
        public static double BlurPx(Surround surround, double extentPx) =>
            Fraction(surround?.Size ?? 0, Surround.MaxSize) * extentPx;

        /// <summary>The outline's thickness, in canvas pixels — the dilate radius, which grows the
        /// silhouette outward by exactly that much.</summary>
        public static double OutlinePx(Surround surround, double extentPx) =>
            Fraction(surround?.Size ?? 0, Surround.MaxSize) * extentPx;

        /// <summary>
        /// The filter that paints the surround and nothing else, or null when there is nothing to
        /// paint — no surround, a fully transparent color, or dials at zero. A null return is the
        /// caller's signal to skip the decoration pass entirely (and with it a whole layer).
        /// </summary>
        public static SKImageFilter CreateDecoration(Surround surround, double extentPx)
        {
            if (surround == null || extentPx <= 0)
                return null;

            var color = new SKColor(surround.Color);
            if (color.Alpha == 0)
                return null;

            switch (surround.Kind)
            {
                case SurroundKind.Shadow:
                {
                    float offset = (float)OffsetPx(surround, extentPx);
                    float blur = (float)BlurPx(surround, extentPx);
                    if (offset <= 0 && blur <= 0)
                        return null; // a hard silhouette exactly behind the item: invisible
                    return SKImageFilter.CreateDropShadowOnly(offset, offset, blur, blur, color);
                }

                case SurroundKind.Glow:
                {
                    // the same shadow with nowhere to fall — a halo the item sits in the middle of
                    float blur = (float)BlurPx(surround, extentPx);
                    if (blur <= 0)
                        return null;
                    return SKImageFilter.CreateDropShadowOnly(0, 0, blur, blur, color);
                }

                case SurroundKind.Outline:
                {
                    // grow the silhouette, then tint what grew: SrcIn keeps the dilated alpha and
                    // replaces every color with the outline's, so the ring is solid whatever the
                    // item's own pixels are.
                    float radius = (float)OutlinePx(surround, extentPx);
                    if (radius < MinOutlinePx)
                        return null;
                    // neither part is disposed here: the composed filter holds the native
                    // references, and the wrappers are collected like the ones CursorCompose's
                    // per-frame filters already are.
                    var dilate = SKImageFilter.CreateDilate(radius, radius);
                    var tint = SKColorFilter.CreateBlendMode(color, SKBlendMode.SrcIn);
                    return SKImageFilter.CreateColorFilter(tint, dilate);
                }

                default:
                    return null;
            }
        }

        /// <summary>Clamps a stored dial to its documented range, collapsing NaN to zero — a
        /// project is validated, but a filter must never be handed a NaN.</summary>
        private static double Fraction(double value, double max) =>
            Double.IsNaN(value) ? 0 : Math.Clamp(value, 0, max);
    }
}
