using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Clowd.VideoSDK.Playback;

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

        /// <summary>Minimum gap between two ruler notches. Never reached on an unwarped ruler (its
        /// minor notches are a fifth of a step at least <see cref="DefaultTickSpacingPx"/> wide);
        /// it is the crowding limit inside a slowed span, where the same step of output time buys
        /// fewer pixels.</summary>
        public const double MinorTickSpacingPx = 5.0;

        /// <summary>Backstop on <see cref="BuildRulerMarks"/>'s walk, so a pathological warp cannot
        /// turn one render pass into an unbounded loop. Two orders of magnitude above the ~150
        /// notches a full-width viewport actually holds.</summary>
        private const int MaxRulerMarks = 4096;

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

        /// <summary>One notch on the ruler: where it lands on the timeline's (project-time) x axis,
        /// and the output instant it stands for — the two are the same tick only on unwarped
        /// footage.</summary>
        internal readonly record struct RulerMark(double X, long OutputTicks, bool IsMajor);

        /// <summary>The notches for one ruler pass, with the major step they were laid out on (the
        /// step a label is formatted against — see <see cref="FormatTick"/>).</summary>
        internal readonly record struct RulerTicks(long StepTicks, IReadOnlyList<RulerMark> Marks);

        /// <summary>
        /// The ruler's notches for the current view. The timeline's x axis is <b>project</b> time,
        /// but every time the ruler <i>says</i> is output time — the clock the finished video runs
        /// on, which is what the transport readout and the exported file agree with. So the notches
        /// step evenly through output time and each one lands wherever
        /// <see cref="TimeWarp.ToProject"/> puts that instant on the project axis: a 2x span spends
        /// twice the pixels per notch as the footage around it, a 0.5x span half. The spacing is
        /// the speed change, drawn.
        ///
        /// The step is <see cref="PickTickStepTicks"/> of the zoom alone, exactly as it always was,
        /// so <b>unwarped footage is untouched</b>: same ladder, same notches, same labels, whether
        /// or not a speed item sits somewhere else in the view. Only the warped spans move.
        /// Notches closer than <see cref="MinorTickSpacingPx"/> to the one before them are dropped,
        /// majors evicting the minors they crowd — which can only bite inside a slowed span, since
        /// an unwarped ruler's minor notches are a fifth of a step that already clears
        /// <see cref="DefaultTickSpacingPx"/>.
        ///
        /// A null or identity warp is the plain ruler this replaced, tick for tick.
        /// </summary>
        public static RulerTicks BuildRulerMarks(long scrollTicks, double ticksPerPixel,
            double viewportWidth, long durationTicks, TimeWarp warp)
        {
            if (!(ticksPerPixel > 0) || !(viewportWidth > 0) || durationTicks <= 0)
                return new RulerTicks(0, Array.Empty<RulerMark>());

            var projectStart = Math.Max(0, scrollTicks);
            var projectEnd = Math.Min(durationTicks, scrollTicks + (long)Math.Round(viewportWidth * ticksPerPixel));
            if (projectEnd < projectStart)
                return new RulerTicks(0, Array.Empty<RulerMark>());

            var warped = warp is { IsIdentity: false };
            var outputStart = warped ? warp.ToOutput(projectStart) : projectStart;
            var outputEnd = warped ? warp.ToOutput(projectEnd) : projectEnd;

            var step = PickTickStepTicks(ticksPerPixel);
            if (step <= 0)
                return new RulerTicks(0, Array.Empty<RulerMark>());

            // minor notches at a fifth of the step; the first mark starts at or just before the
            // left edge so a notch straddling it is not lost.
            var minorStep = step / 5;
            var increment = minorStep > 0 ? minorStep : step;
            var marks = new List<RulerMark>();

            for (var t = outputStart - outputStart % increment; t <= outputEnd; t += increment)
            {
                var isMajor = t % step == 0;
                var x = TickToX(warped ? warp.ToProject(t) : t, scrollTicks, ticksPerPixel);

                // a major never yields to the minors it crowds — that notch is the one carrying a
                // label, and the label ladder has to stay on round numbers.
                while (isMajor && marks.Count > 0 && !marks[^1].IsMajor
                    && x - marks[^1].X < MinorTickSpacingPx)
                    marks.RemoveAt(marks.Count - 1);

                if (marks.Count > 0 && x - marks[^1].X < MinorTickSpacingPx)
                    continue;

                marks.Add(new RulerMark(x, t, isMajor));
                if (marks.Count >= MaxRulerMarks)
                    break;
            }

            return new RulerTicks(step, marks);
        }

        /// <summary>A stretch of warped time on the x axis, with the instantaneous speed at each
        /// end: equal on a constant-factor span (a flat wash between hard edges), different across
        /// a transition ramp (a fade from one to the other).</summary>
        internal readonly record struct SpeedBand(double X0, double X1, double SpeedStart, double SpeedEnd);

        /// <summary>
        /// The visible warped stretches, as x spans — one per <see cref="TimeWarp"/> segment that
        /// bends time, clipped to the view. Speed-1 spans produce nothing at all, so unwarped
        /// footage is left bare; the entry/exit ramps come through as their own bands whose two
        /// ends differ, which is what draws a ramp as a gradient and a hard speed change as an
        /// edge. Empty for a null or identity warp.
        /// </summary>
        public static List<SpeedBand> BuildSpeedBands(long scrollTicks, double ticksPerPixel,
            double viewportWidth, long durationTicks, TimeWarp warp)
        {
            var bands = new List<SpeedBand>();
            if (warp is not { IsIdentity: false } || !(ticksPerPixel > 0) || !(viewportWidth > 0)
                || durationTicks <= 0)
                return bands;

            var visibleStart = Math.Max(0, scrollTicks);
            var visibleEnd = Math.Min(durationTicks, scrollTicks + (long)Math.Round(viewportWidth * ticksPerPixel));

            foreach (var segment in warp.Segments)
            {
                var start = Math.Max(segment.ProjectStartTicks, visibleStart);
                var end = Math.Min(segment.ProjectEndTicks, visibleEnd);
                if (end <= start)
                    continue;

                // sampled at the ends of the *clipped* span, so a ramp running off the edge of the
                // view keeps the slope of the part still on screen
                var speedStart = warp.SpeedAt(start);
                var speedEnd = warp.SpeedAt(end - 1);
                if (speedStart == 1 && speedEnd == 1)
                    continue;

                bands.Add(new SpeedBand(TickToX(start, scrollTicks, ticksPerPixel),
                    TickToX(end, scrollTicks, ticksPerPixel), speedStart, speedEnd));
            }

            return bands;
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
