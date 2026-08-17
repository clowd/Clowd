using System;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// The shape of the mouse-click highlight, in the abstract: how long it runs, how big it is and
    /// how solid, with no canvas and no Skia anywhere near it. <see cref="CursorCompose"/> draws the
    /// composed frames from these numbers and the inspector's highlight tiles animate their previews
    /// from the same ones, so the picker shows what the render will do.
    /// </summary>
    /// <remarks>
    /// The constants are the obs tracker's, verbatim (<c>tracker.rs</c>): 400 ms, 85% peak opacity
    /// fading linearly to nothing, radius 10 → 40 density-independent units. The tracker re-anchors
    /// its animation to the pointer on every sample while a button is held, so a held button shows
    /// the animation's first frame and nothing else — which is why the held indicator here is
    /// <see cref="RadiusStartDip"/> at <see cref="MaxOpacity"/>, and why the animation proper runs
    /// off the <b>release</b>.
    /// </remarks>
    public static class ClickHighlight
    {
        /// <summary>How long one release animation runs, in project-time milliseconds.</summary>
        public const double DurationMs = 400.0;

        /// <summary>Peak opacity — never fully opaque, so what is under the highlight stays
        /// readable.</summary>
        public const double MaxOpacity = 0.85;

        /// <summary>Radius at the moment of the click, and how much it grows over
        /// <see cref="DurationMs"/>, in DIP (scaled by the monitor's DPI at draw time).</summary>
        public const double RadiusStartDip = 10.0;

        public const double RadiusGrowthDip = 30.0;

        /// <summary>The widest the animation ever gets — what a preview scales its box against.</summary>
        public const double RadiusEndDip = RadiusStartDip + RadiusGrowthDip;

        /// <summary>Whether <paramref name="animation"/> is a drawn highlight, and if so whether it
        /// is the reversed (<c>pulse</c>) one. False for <c>none</c>, null and anything unknown —
        /// a project written by a newer editor degrades to no highlight, not a wrong one.</summary>
        public static bool TryParse(string animation, out bool pulse)
        {
            if (string.Equals(animation, "ripple", StringComparison.OrdinalIgnoreCase))
            {
                pulse = false;
                return true;
            }
            if (string.Equals(animation, "pulse", StringComparison.OrdinalIgnoreCase))
            {
                pulse = true;
                return true;
            }

            pulse = false;
            return false;
        }

        /// <summary>The circle's radius at <paramref name="progress"/> (0 at the release, 1 at the
        /// end), in DIP: ripple expands, pulse runs the same sweep backwards.
        /// <paramref name="clickSize"/> scales the whole sweep, so a shrinking pulse simply starts
        /// from further out.</summary>
        public static double RadiusDip(double progress, bool pulse, double clickSize = 1.0)
        {
            double dip = pulse
                ? RadiusStartDip + (1 - progress) * RadiusGrowthDip
                : RadiusStartDip + progress * RadiusGrowthDip;
            return dip * Factor(clickSize);
        }

        /// <summary>The dot drawn under a held button, in DIP — the animation's first frame held
        /// still, scaled by the item's own hold size.</summary>
        public static double HeldRadiusDip(double holdSize) => RadiusStartDip * Factor(holdSize);

        /// <summary>How long one release animation runs at <paramref name="animationSpeed"/>:
        /// twice the speed, half the time. Never zero, so a caller may divide by it.</summary>
        public static double DurationMsAt(double animationSpeed) => DurationMs / Factor(animationSpeed);

        /// <summary>The circle's opacity at <paramref name="progress"/>, before the highlight
        /// colour's own alpha and the item's opacity are folded in.</summary>
        public static double Opacity(double progress) => (1 - progress) * MaxOpacity;

        /// <summary>
        /// A multiplier as the drawing code may use it: inside <see cref="CursorContent"/>'s
        /// validated range, with anything unusable — a hand-edited project, a NaN, a zero that
        /// would stop the clock — falling back to 1. The model rejects those values and the
        /// inspector cannot produce them; this is what keeps a bad file from drawing nothing at all
        /// instead of merely drawing it wrong.
        /// </summary>
        public static double Factor(double value)
        {
            if (double.IsNaN(value))
                return 1.0;
            return Math.Clamp(value, CursorContent.MinHighlightFactor, CursorContent.MaxHighlightFactor);
        }
    }
}
