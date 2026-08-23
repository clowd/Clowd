using System;

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>What a scroll gesture over the timeline means.</summary>
    internal enum TimelineScrollAction
    {
        /// <summary>Nothing to do — a zero delta, or an axis this platform has no use for. The
        /// event is left unhandled.</summary>
        None,

        /// <summary>Zoom the virtual horizontal axis around the pointer by
        /// <see cref="TimelineScrollDecision.ZoomFactor"/>.</summary>
        Zoom,

        /// <summary>Scroll the virtual horizontal axis by
        /// <see cref="TimelineScrollDecision.PanPixels"/>.</summary>
        PanHorizontal,

        /// <summary>Not ours: leave the event unhandled so the rows' ScrollViewer scrolls the
        /// track stack vertically.</summary>
        ScrollRows,
    }

    /// <summary>The modifier state a scroll decision depends on. Deliberately <i>not</i> Avalonia's
    /// <c>KeyModifiers</c> — this file stays free of Avalonia types like the rest of the timeline's
    /// pure logic, and <see cref="TimelineControl"/> maps the real flags across at the single call
    /// site.</summary>
    [Flags]
    internal enum TimelineScrollModifiers
    {
        None = 0,
        Alt = 1,
        Control = 2,
        Shift = 4,
        Meta = 8,
    }

    /// <summary>A decoded scroll gesture. <see cref="ZoomFactor"/> multiplies the viewport's
    /// ticks-per-pixel (so &gt; 1 zooms out) and <see cref="PanPixels"/> is a signed pixel offset in
    /// the direction the <i>view</i> moves; both are 0/1 for the actions that do not use them.</summary>
    internal readonly record struct TimelineScrollDecision(TimelineScrollAction Action, double PanPixels,
        double ZoomFactor)
    {
        public static readonly TimelineScrollDecision None =
            new TimelineScrollDecision(TimelineScrollAction.None, 0, 1);

        public static readonly TimelineScrollDecision ScrollRows =
            new TimelineScrollDecision(TimelineScrollAction.ScrollRows, 0, 1);

        public static TimelineScrollDecision Pan(double pixels) =>
            pixels == 0 ? None : new TimelineScrollDecision(TimelineScrollAction.PanHorizontal, pixels, 1);

        public static TimelineScrollDecision Zoom(double factor) =>
            factor == 1 ? None : new TimelineScrollDecision(TimelineScrollAction.Zoom, 0, factor);
    }

    /// <summary>
    /// Decides what a wheel / two-finger scroll / pinch over the timeline should do. Pure — no
    /// Avalonia types and no viewport state — and the platform is a <i>parameter</i>, not a
    /// compile-time check, so both platforms' rules are testable from either one.
    ///
    /// The two platforms genuinely disagree about what an unmodified scroll means, and the split is
    /// by convention rather than by device because the device cannot be recovered from the event:
    /// Avalonia's macOS backend divides a precise (trackpad) scroll by 50 and a line-based (mouse
    /// wheel) one by 5 and then reports the same bare <c>Vector</c> for both, so on a Mac
    /// <i>every</i> pointing device delivers small accelerated fractions and a "fractional means
    /// trackpad" test would be guessing (the same thing bit the image editor's zoom — see
    /// <c>DrawingCanvas.OnPointerWheelChanged</c> and issue #68). Hence:
    ///
    /// <list type="bullet">
    /// <item>Windows — unchanged: a wheel over a timeline zooms around the pointer (the rows are
    /// rarely tall enough to have anywhere to scroll to), Shift+wheel pans, Alt+wheel goes to the
    /// rows' scroller.</item>
    /// <item>macOS — a two-finger scroll splits by its dominant axis, so the timeline reads it the
    /// way the Windows wheel reads its two: sideways pans, up/down zooms around the pointer
    /// (proportionally, since the deltas arrive as fractions of a notch rather than as whole
    /// clicks). Cmd/Ctrl+scroll zooms too — the Mac affordance, and harmless now that the plain
    /// gesture agrees with it — and the pinch stays the primary zoom, arriving as a separate
    /// magnify event (<see cref="ZoomFactorForMagnification"/>) rather than as a scroll. The rows'
    /// own vertical scroll is Alt+scroll and the scroll bar, exactly as on Windows.</item>
    /// </list>
    /// </summary>
    internal static class TimelineScrollInput
    {
        /// <summary>Zoom per whole wheel notch. 1.25 is small enough that a notch never loses the
        /// user's place and large enough that a few of them cross an order of magnitude.</summary>
        public const double ZoomStepPerNotch = 1.25;

        /// <summary>How far one whole wheel notch pans the Windows Shift+wheel gesture.</summary>
        public const double WheelScrollPxPerNotch = 60;

        /// <summary>
        /// How far one unit of macOS scroll delta pans. 50 is not arbitrary: it is exactly what
        /// Avalonia's own <c>ScrollContentPresenter</c> multiplies a wheel delta by, and the rows'
        /// ScrollViewer — the control immediately underneath, handling the vertical half of the very
        /// same gesture — uses it. Matching it is what makes a diagonal two-finger drag feel like
        /// one movement instead of two axes at different speeds. It also lands at 1:1 with the
        /// finger for precise deltas, since the backend divided those by 50 on the way in.
        /// </summary>
        public const double MacScrollPxPerDelta = 50;

        /// <summary>
        /// Decodes one wheel / scroll event. <paramref name="deltaX"/> and <paramref name="deltaY"/>
        /// are Avalonia's <c>PointerWheelEventArgs.Delta</c>, in notches and with Avalonia's sign
        /// convention: a positive delta moves the view <i>towards</i> the origin (which is why every
        /// pan below negates it, exactly as <c>ScrollContentPresenter</c> does).
        /// </summary>
        public static TimelineScrollDecision DecideWheel(double deltaX, double deltaY,
            TimelineScrollModifiers modifiers, bool isMacOS)
        {
            // a non-finite delta would ride Math.Pow straight into the viewport's zoom and stick
            // there — nothing downstream clamps a NaN back to a legal zoom.
            if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
                return TimelineScrollDecision.None;

            if (!isMacOS)
            {
                // Windows, unchanged: the tilt wheel's X is used only when there is no Y, so a
                // plain wheel behaves identically whether or not the mouse has one.
                var notches = deltaY != 0 ? deltaY : deltaX;
                if (notches == 0)
                    return TimelineScrollDecision.None;

                if ((modifiers & TimelineScrollModifiers.Shift) != 0)
                    return TimelineScrollDecision.Pan(-notches * WheelScrollPxPerNotch);

                // the rows' vertical scroll, which the plain wheel used to carry — it is also
                // still on the ScrollViewer's own scroll bar.
                if ((modifiers & TimelineScrollModifiers.Alt) != 0)
                    return TimelineScrollDecision.ScrollRows;

                // plain wheel, and Ctrl+wheel (the habit): anchored zoom.
                return TimelineScrollDecision.Zoom(Math.Pow(ZoomStepPerNotch, -notches));
            }

            if ((modifiers & (TimelineScrollModifiers.Meta | TimelineScrollModifiers.Control)) != 0)
            {
                // Cmd+scroll is the Mac zoom affordance everywhere it exists (browsers, Figma,
                // Final Cut); Ctrl+scroll is kept beside it because it is the Windows habit and
                // because macOS only claims it while the accessibility screen zoom is switched on,
                // in which case the app never sees the event at all. The deltas here are fractions
                // of a notch, so the same Pow gives a smooth proportional zoom instead of the
                // stepped one a whole notch produces.
                var notches = deltaY != 0 ? deltaY : deltaX;
                return notches == 0
                    ? TimelineScrollDecision.None
                    : TimelineScrollDecision.Zoom(Math.Pow(ZoomStepPerNotch, -notches));
            }

            if ((modifiers & TimelineScrollModifiers.Alt) != 0)
                return TimelineScrollDecision.ScrollRows;

            if ((modifiers & TimelineScrollModifiers.Shift) != 0)
            {
                // macOS usually swaps the axes itself while Shift is held, but not for every device
                // — this is the same "Shift held and X is zero, so swap" fixup Avalonia's own
                // ScrollContentPresenter carries for the platforms that do not.
                var horizontal = deltaX != 0 ? deltaX : deltaY;
                return TimelineScrollDecision.Pan(-horizontal * MacScrollPxPerDelta);
            }

            // Plain two-finger scroll: sideways pans, up/down zooms — the same two meanings the
            // Windows wheel carries, on the axis the finger actually moved. The dominant axis takes
            // the whole gesture rather than both acting at once: a trackpad reports a little
            // cross-axis drift on every swipe, and honouring it would zoom the view a hair on every
            // horizontal pan (and slide it sideways on every zoom). The rows' vertical scroll is
            // Alt+scroll and the scroll bar, as on Windows.
            if (Math.Abs(deltaX) > Math.Abs(deltaY))
                return TimelineScrollDecision.Pan(-deltaX * MacScrollPxPerDelta);

            return deltaY != 0
                ? TimelineScrollDecision.Zoom(Math.Pow(ZoomStepPerNotch, -deltaY))
                : TimelineScrollDecision.None;
        }

        /// <summary>
        /// The ticks-per-pixel multiplier for one macOS trackpad magnify (pinch) event.
        /// <c>NSEvent.magnification</c> is a <i>relative</i> increment — the view's scale is meant to
        /// become <c>scale * (1 + magnification)</c> — and Avalonia hands it through untouched as the
        /// gesture delta. The timeline's zoom is ticks-per-pixel, which is the inverse of a scale, so
        /// the factor is <c>1 / (1 + m)</c>: pinching open (m &gt; 0) shrinks ticks-per-pixel, i.e.
        /// zooms in. A magnification at or below -1 would divide by zero or flip the axis, so it
        /// collapses to "no change" rather than trusting a number off a device.
        /// </summary>
        public static double ZoomFactorForMagnification(double magnification)
        {
            if (!double.IsFinite(magnification) || magnification <= -1)
                return 1;

            return 1 / (1 + magnification);
        }
    }
}
