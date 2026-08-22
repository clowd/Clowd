using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

// The timeline's pure math (this file, TimelineRowLayout, TimelineHitTester, the persistence
// loader…) is unit-tested against the real production code by the SDK test project.
[assembly: InternalsVisibleTo("Clowd.VideoSDK.Tests")]

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>
    /// The pure tick&lt;-&gt;pixel math behind the multi-track timeline: the horizontal viewport
    /// mapping, anchored zoom, zoom/scroll clamps, ruler tick steps and edit snapping. Deliberately
    /// free of every Avalonia type so <c>Clowd.VideoSDK.Tests</c> can exercise it directly, and so
    /// the ruler, the surface and the header panel all agree on one implementation.
    ///
    /// The horizontal axis is <b>virtual</b>: there is no million-pixel control. A viewport is
    /// <c>(ScrollTicks, TicksPerPixel, ViewportWidth)</c> and x is measured from the left edge of the
    /// drawing surface, so <c>x = (ticks - ScrollTicks) / TicksPerPixel</c>. All times are 100ns
    /// ticks, matching the composition model.
    /// </summary>
    internal static class TimelineViewMath
    {
        /// <summary>Pointer slop, in device-independent pixels, for grabbing the playhead, item
        /// edges and snap targets. Same 6 px the single-track control used.</summary>
        public const double HitTolerance = 6.0;

        /// <summary>Maximum zoom-in: 0.1 ms per pixel. Finer than this is meaningless — a single
        /// frame of 60 fps material is already ~166 px wide here.</summary>
        public const double MinTicksPerPixel = TimeSpan.TicksPerMillisecond / 10.0;

        /// <summary>Absolute zoom-out backstop, used only while the duration or the viewport width
        /// is still unknown — once both are known the ceiling is the fit zoom (see
        /// <see cref="ClampZoom"/>), which is always tighter for a real project.</summary>
        public const double MaxTicksPerPixel = TimeSpan.TicksPerSecond * 60.0;

        /// <summary>The zoom the editor opens at, and the one the reset-zoom button returns to:
        /// one second per 50 px.</summary>
        public const double DefaultTicksPerPixel = TimeSpan.TicksPerSecond / 50.0;

        /// <summary>How far past the end of the timeline the view may scroll, so the last item's
        /// end edge is grabbable instead of being pinned against the right border.</summary>
        public const double OverscrollPx = 40.0;

        /// <summary>Minimum gap between ruler labels; the tick step is the smallest ladder step
        /// that clears it.</summary>
        public const double DefaultTickSpacingPx = 70.0;

        /// <summary>Ruler steps below the doubling range, in ticks: 100 ms, 250 ms, 500 ms, then
        /// 1/5/10/30/60 s. Beyond a minute the step keeps doubling (see
        /// <see cref="PickTickStepTicks"/>).</summary>
        private static readonly long[] TickStepLadder =
        {
            TimeSpan.TicksPerMillisecond * 100,
            TimeSpan.TicksPerMillisecond * 250,
            TimeSpan.TicksPerMillisecond * 500,
            TimeSpan.TicksPerSecond,
            TimeSpan.TicksPerSecond * 5,
            TimeSpan.TicksPerSecond * 10,
            TimeSpan.TicksPerSecond * 30,
            TimeSpan.TicksPerMinute,
        };

        // ------------------------------------------------------------------ tick <-> pixel mapping

        /// <summary>Maps a timeline tick to an x-coordinate inside the viewport. A degenerate zoom
        /// collapses to the viewport origin rather than dividing by zero.</summary>
        public static double TickToX(long ticks, long scrollTicks, double ticksPerPixel)
        {
            if (!(ticksPerPixel > 0))
                return 0;

            return (ticks - scrollTicks) / ticksPerPixel;
        }

        /// <summary>Maps an x-coordinate to timeline ticks. Unclamped — drag deltas need the value
        /// past both edges, otherwise the grab offset distorts when the pointer leaves the
        /// viewport.</summary>
        public static long XToTicks(double x, long scrollTicks, double ticksPerPixel)
        {
            if (!(ticksPerPixel > 0))
                return scrollTicks;

            return scrollTicks + (long)Math.Round(x * ticksPerPixel);
        }

        /// <summary>Clamped counterpart of <see cref="XToTicks"/>, for the scrub/playhead path
        /// where a position outside <c>[0, duration]</c> is meaningless.</summary>
        public static long XToTicksClamped(double x, long scrollTicks, double ticksPerPixel, long durationTicks) =>
            Math.Clamp(XToTicks(x, scrollTicks, ticksPerPixel), 0, Math.Max(0, durationTicks));

        // ------------------------------------------------------------------------ zoom and scroll

        /// <summary>
        /// The scroll position that keeps the tick currently under <paramref name="anchorX"/>
        /// exactly there after the zoom changes — the Ctrl+wheel idiom, where the timeline appears
        /// to expand around the pointer. Rounding costs at most half a tick per step, four orders of
        /// magnitude below one pixel at maximum zoom, so repeated zooming does not drift.
        /// </summary>
        public static long ScrollForAnchoredZoom(long scrollTicks, double oldTicksPerPixel,
            double newTicksPerPixel, double anchorX)
        {
            if (!(oldTicksPerPixel > 0) || !(newTicksPerPixel > 0))
                return scrollTicks;

            var anchorTicks = scrollTicks + anchorX * oldTicksPerPixel;
            return (long)Math.Round(anchorTicks - anchorX * newTicksPerPixel);
        }

        /// <summary>The zoom at which the whole timeline exactly fills the viewport — what the
        /// zoom-to-fit button asks for, and the zoom-out limit. Falls back to
        /// <see cref="MaxTicksPerPixel"/> while the duration or the viewport width is still
        /// unknown.</summary>
        public static double FitTicksPerPixel(long durationTicks, double viewportWidth)
        {
            if (durationTicks <= 0 || !(viewportWidth > 0))
                return MaxTicksPerPixel;

            return Math.Clamp(durationTicks / viewportWidth, MinTicksPerPixel, MaxTicksPerPixel);
        }

        /// <summary>
        /// Clamps a zoom to <c>[<see cref="MinTicksPerPixel"/>, fit-whole-duration]</c>: the user
        /// can never zoom out far enough to leave dead space past the end of the project, nor in
        /// past 0.1 ms/px. A non-positive (or NaN) input resolves to
        /// <see cref="DefaultTicksPerPixel"/>, itself capped at the fit zoom — a project shorter
        /// than one screenful at the default scale opens fitted rather than trailing empty space.
        /// </summary>
        public static double ClampZoom(double ticksPerPixel, long durationTicks, double viewportWidth)
        {
            var fit = FitTicksPerPixel(durationTicks, viewportWidth);
            if (!(ticksPerPixel > 0))
                ticksPerPixel = DefaultTicksPerPixel;

            return Math.Clamp(ticksPerPixel, MinTicksPerPixel, Math.Max(MinTicksPerPixel, fit));
        }

        /// <summary>The furthest left edge the viewport may show: the end of the timeline minus one
        /// viewport, plus a small overscroll. 0 whenever everything already fits.</summary>
        public static long MaxScrollTicks(double ticksPerPixel, long durationTicks, double viewportWidth)
        {
            if (!(ticksPerPixel > 0) || !(viewportWidth > 0) || durationTicks <= 0)
                return 0;

            var spanTicks = viewportWidth * ticksPerPixel;
            if (durationTicks <= spanTicks)
                return 0;

            return (long)Math.Round(durationTicks - spanTicks + OverscrollPx * ticksPerPixel);
        }

        /// <summary>Clamps a scroll offset to <c>[0, <see cref="MaxScrollTicks"/>]</c>.</summary>
        public static long ClampScroll(long scrollTicks, double ticksPerPixel, long durationTicks,
            double viewportWidth)
        {
            if (scrollTicks <= 0)
                return 0;

            return Math.Min(scrollTicks, MaxScrollTicks(ticksPerPixel, durationTicks, viewportWidth));
        }

        // ----------------------------------------------------------------------- pixel alignment

        /// <summary>
        /// Snaps a device-independent coordinate so what is drawn there lands on whole device
        /// pixels: the playhead is a hairline the eye tracks across a moving picture, and a
        /// fractional position turns it into two half-lit columns that read as a soft, translucent
        /// smear rather than a line.
        ///
        /// <paramref name="strokeWidth"/> is the pen width the coordinate is the <i>center</i> of
        /// (0 for a fill edge). A stroke an odd number of pixels wide has to straddle a pixel
        /// center to stay crisp; an even one has to sit on the boundary between two.
        /// </summary>
        public static double SnapToPixel(double x, double renderScaling, double strokeWidth = 0)
        {
            if (!(renderScaling > 0))
                renderScaling = 1;

            var physical = x * renderScaling;
            var strokePx = (int)Math.Round(Math.Max(0, strokeWidth) * renderScaling);

            // odd stroke: the nearest pixel CENTER (every x in [n, n+1) has n + 0.5 as its
            // nearest); even stroke or a fill edge: the nearest boundary.
            physical = strokePx % 2 == 1 ? Math.Floor(physical) + 0.5 : Math.Round(physical);
            return physical / renderScaling;
        }

        // ------------------------------------------------------------------------- ruler tick step

        /// <summary>
        /// Picks the ruler's major tick step so labels never crowd: the smallest of
        /// 100/250/500 ms, 1/5/10/30/60 s whose on-screen spacing reaches
        /// <paramref name="minSpacingPx"/>; past a minute the step keeps doubling. Unlike the
        /// single-track version this is a function of zoom alone — the visible span, not the whole
        /// media, is what decides label density. Returns 0 when there is nothing to draw.
        /// </summary>
        public static long PickTickStepTicks(double ticksPerPixel, double minSpacingPx = DefaultTickSpacingPx)
        {
            if (!(ticksPerPixel > 0) || !(minSpacingPx > 0))
                return 0;

            foreach (var step in TickStepLadder)
            {
                if (step / ticksPerPixel >= minSpacingPx)
                    return step;
            }

            var big = TimeSpan.TicksPerMinute;
            while (big < Int64.MaxValue / 2 && big / ticksPerPixel < minSpacingPx)
                big *= 2;
            return big;
        }

        /// <summary>Ruler label for a tick. <paramref name="stepTicks"/> is the step the label
        /// belongs to: sub-second steps get a tenths digit, otherwise the format matches the
        /// single-track ruler exactly (<c>m:ss</c>, <c>h:mm:ss</c> past an hour).</summary>
        public static string FormatTick(long ticks, long stepTicks = TimeSpan.TicksPerSecond)
        {
            var t = TimeSpan.FromTicks(Math.Max(0, ticks));
            var subSecond = stepTicks > 0 && stepTicks < TimeSpan.TicksPerSecond;

            if (t.TotalHours >= 1)
                return t.ToString(subSecond ? @"h\:mm\:ss\.f" : @"h\:mm\:ss");

            return t.ToString(subSecond ? @"m\:ss\.f" : @"m\:ss");
        }

        // -------------------------------------------------------------------------------- snapping

        /// <summary>How far, in ticks, a dragged edge reaches for a snap target at this zoom — the
        /// pointer tolerance expressed in time, so snapping feels identical at every zoom.</summary>
        public static long ToleranceTicks(double ticksPerPixel) =>
            ticksPerPixel > 0 ? (long)Math.Round(HitTolerance * ticksPerPixel) : 0;

        /// <summary>
        /// Snaps a dragged position to the nearest of <paramref name="targets"/> (timeline origin,
        /// playhead, every other item edge) within <paramref name="toleranceTicks"/>. Returns null
        /// when nothing is close enough — the caller then keeps the raw value and draws no snap
        /// guide. Ties go to the earlier entry, so the target order the caller builds is the
        /// tie-break.
        /// </summary>
        public static long? Snap(long candidateTicks, IReadOnlyList<long> targets, long toleranceTicks)
        {
            if (targets == null || targets.Count == 0 || toleranceTicks < 0)
                return null;

            var bestDistance = Int64.MaxValue;
            long best = 0;
            var found = false;

            for (var i = 0; i < targets.Count; i++)
            {
                var distance = Math.Abs(targets[i] - candidateTicks);
                if (distance <= toleranceTicks && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = targets[i];
                    found = true;
                }
            }

            return found ? best : (long?)null;
        }
    }
}
