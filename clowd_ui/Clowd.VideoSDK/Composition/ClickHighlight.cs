using System;
using Clowd.VideoSDK.Model;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>Which highlight a <see cref="CursorContent.ClickAnimation"/> wire name selects.
    /// <see cref="None"/> covers null, <c>none</c> and every unknown value — a project written by
    /// a newer editor degrades to no highlight, not a wrong one.</summary>
    public enum HighlightMode
    {
        None,

        /// <summary>An expanding, fading circle fired by each release.</summary>
        Ripple,

        /// <summary>The same fade with the radius shrinking instead.</summary>
        Pulse,

        /// <summary>A circle outline pinned to the pointer at all times: it eases inward while a
        /// button is held and springs back out on the release, so a quick click reads as one
        /// breath in and out.</summary>
        Ring,

        /// <summary>No drawn shape at all — the pixels under the pointer stretch toward it while
        /// a button is held, like a finger pressing down on paper.</summary>
        Press,
    }

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

        // ------------------------------------------------------------------------------- ring

        /// <summary>The ring's resting radius in DIP — the circle a still pointer wears. Sized to
        /// sit comfortably around a 100% themed glyph (40px at monitor scale 1).</summary>
        public const double RingRadiusDip = 18.0;

        /// <summary>The ring outline's stroke width in DIP.</summary>
        public const double RingStrokeDip = 2.5;

        /// <summary>How far the ring closes under a held button, as a fraction of the resting
        /// radius.</summary>
        public const double RingShrink = 0.65;

        /// <summary>How long the ring takes to ease in to its held size, in project-time ms.</summary>
        public const double RingEngageMs = 130.0;

        /// <summary>How long the ring takes to spring back out after the release. Longer than the
        /// way in — the overshoot (see <see cref="RingScale"/>) needs room to settle.</summary>
        public const double RingReleaseMs = 320.0;

        // ------------------------------------------------------------------------------ press

        /// <summary>How far out the press warp reaches, in DIP. Deliberately wide and shallow:
        /// the effect is a dent in the page, not a lens.</summary>
        public const double PressRadiusDip = 100.0;

        /// <summary>The sampling stretch at the very centre of a fully-engaged press — how hard
        /// the paper is pushed down.</summary>
        public const double PressMaxAmount = 0.30;

        /// <summary>How long the warp takes to press in / relax out, in project-time ms.</summary>
        public const double PressEngageMs = 160.0;

        public const double PressReleaseMs = 260.0;

        /// <summary>Whether <paramref name="animation"/> is a drawn highlight, and if so which one.
        /// False (and <see cref="HighlightMode.None"/>) for <c>none</c>, null and anything unknown —
        /// a project written by a newer editor degrades to no highlight, not a wrong one.</summary>
        public static bool TryParse(string animation, out HighlightMode mode)
        {
            mode = ModeOf(animation);
            return mode != HighlightMode.None;
        }

        /// <summary>The mode a wire name selects; <see cref="HighlightMode.None"/> for anything
        /// unrecognised.</summary>
        public static HighlightMode ModeOf(string animation)
        {
            if (string.Equals(animation, "ripple", StringComparison.OrdinalIgnoreCase))
                return HighlightMode.Ripple;
            if (string.Equals(animation, "pulse", StringComparison.OrdinalIgnoreCase))
                return HighlightMode.Pulse;
            if (string.Equals(animation, "ring", StringComparison.OrdinalIgnoreCase))
                return HighlightMode.Ring;
            // "press" is the mode's original wire name, kept as an alias for projects written
            // before it was renamed to "pressure"
            if (string.Equals(animation, "pressure", StringComparison.OrdinalIgnoreCase)
                || string.Equals(animation, "press", StringComparison.OrdinalIgnoreCase))
                return HighlightMode.Press;
            return HighlightMode.None;
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

        /// <summary>An opacity as the drawing code may use it: 0..1, with NaN (a hand-edited
        /// project) drawing nothing rather than poisoning the alpha arithmetic.</summary>
        public static double Clamp01(double value)
            => double.IsNaN(value) ? 0.0 : Math.Clamp(value, 0.0, 1.0);

        // -------------------------------------------------------------------- press/ring clocks
        //
        // Both take the same picture of the click: how long ago the button went down
        // (null = long ago / unknown, i.e. fully engaged), how long ago it came back up
        // (null = still held), and how long it was down for (null = long enough to have fully
        // engaged). All three are project-time ms; animationSpeed rescales the clocks exactly as
        // it does the ripple's. A quick click hands the release a partial engagement, which is
        // what makes it read as one breath rather than a snap.

        /// <summary>The ring's radius multiplier at this moment: 1 at rest, eased down toward
        /// <see cref="RingShrink"/> while held, and sprung back out on the release — deliberately
        /// overshooting past 1 (ease-out-back) so a click breathes in and out instead of
        /// stopping dead.</summary>
        public static double RingScale(double? sinceDownMs, double? sinceUpMs,
            double? pressDurationMs, double animationSpeed)
        {
            double engageMs = RingEngageMs / Factor(animationSpeed);
            double releaseMs = RingReleaseMs / Factor(animationSpeed);

            if (sinceUpMs == null)
                return 1 - (1 - RingShrink) * Engagement(sinceDownMs, engageMs);

            double r = Math.Clamp(sinceUpMs.Value / releaseMs, 0.0, 1.0);
            if (r >= 1)
                return 1.0;

            double engaged = Engagement(pressDurationMs, engageMs);
            return 1 - (1 - RingShrink) * engaged * (1 - EaseOutBack(r));
        }

        /// <summary>How hard the press warp pushes at this moment, 0..1: eased in while held,
        /// eased back to nothing over <see cref="PressReleaseMs"/> after the release. A quick
        /// click never reaches 1 and relaxes from wherever it got to.</summary>
        public static double PressAmount(double? sinceDownMs, double? sinceUpMs,
            double? pressDurationMs, double animationSpeed)
        {
            double engageMs = PressEngageMs / Factor(animationSpeed);
            double releaseMs = PressReleaseMs / Factor(animationSpeed);

            if (sinceUpMs == null)
                return Engagement(sinceDownMs, engageMs);

            double r = Math.Clamp(sinceUpMs.Value / releaseMs, 0.0, 1.0);
            if (r >= 1)
                return 0.0;
            return Engagement(pressDurationMs, engageMs) * (1 - EaseOutCubic(r));
        }

        /// <summary>How far a press of <paramref name="heldMs"/> (null = long enough) has engaged,
        /// 0..1, ease-out so the landing is quick and the settle soft.</summary>
        private static double Engagement(double? heldMs, double engageMs)
        {
            if (heldMs == null)
                return 1.0;
            return EaseOutCubic(Math.Clamp(heldMs.Value / engageMs, 0.0, 1.0));
        }

        private static double EaseOutCubic(double t)
        {
            double u = 1 - t;
            return 1 - u * u * u;
        }

        /// <summary>The standard back ease-out: rises past 1 (peak ≈1.10) before settling, which
        /// is the ring's breathe-out.</summary>
        private static double EaseOutBack(double t)
        {
            const double c1 = 1.70158;
            const double c3 = c1 + 1;
            double u = t - 1;
            return 1 + c3 * u * u * u + c1 * u * u;
        }
    }
}
