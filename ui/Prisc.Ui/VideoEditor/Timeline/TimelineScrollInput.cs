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

    /// <summary>Which axis a two-finger gesture has been committed to. <see cref="Undecided"/> is
    /// the state before enough travel has accumulated to tell (and what a caller passes when it has
    /// no latch at all, e.g. the unit tests for a single isolated event).</summary>
    internal enum TimelineScrollAxis
    {
        Undecided,
        Horizontal,
        Vertical,
    }

    /// <summary>
    /// Remembers which axis the two-finger gesture <i>currently in progress</i> committed to, so a
    /// single swipe is a pan or a zoom for its whole length and never both.
    ///
    /// Picking the dominant axis per event is not enough on its own: a real horizontal swipe is not
    /// a straight line, and the stream it produces is mostly <c>(-0.4, 0.01)</c> with the occasional
    /// <c>(0.02, -0.05)</c> as the fingers settle or lift. Deciding each of those on its own merits
    /// zooms the view a notch in the middle of a pan. So the axis is decided once, from the travel
    /// accumulated over the opening few pixels of the gesture, and then held.
    ///
    /// The gesture's end has to be inferred: Avalonia surfaces no phase for a macOS scroll (AppKit's
    /// began/ended/momentum phases are not carried on <c>PointerWheelEventArgs</c>), and momentum
    /// events keep arriving after the fingers lift. A gap of <see cref="IdleResetMs"/> with no
    /// scroll is therefore what ends one — comfortably longer than the ~8 ms cadence of a live
    /// gesture or its momentum tail, and far shorter than the pause between two deliberate swipes.
    ///
    /// That gap must not be the <i>only</i> way out, though, or turning a pan into a zoom means
    /// lifting the fingers and waiting — which is exactly what a latch that only ever unlatches on
    /// idle feels like. So the axis is held against drift, not against intent: the accumulators
    /// forget at a <see cref="MemoryMs"/> time constant, and when the off-axis travel <i>recently</i>
    /// outweighs the held axis by <see cref="SwitchRatio"/> (and is worth
    /// <see cref="SwitchTravelPx"/> on its own) the gesture changes axis without ever leaving the
    /// glass. Turning a firm swipe through 90° re-latches in about 50 ms — under half a dozen
    /// events, quicker than a hand can lift — while the drift the latch exists for never comes near
    /// either bar. The hold is proportional to conviction: a swipe the user has committed to needs
    /// a correspondingly firm shove to overturn, a barely-started one turns almost freely.
    /// </summary>
    internal sealed class TimelineScrollAxisLatch
    {
        /// <summary>A quiet gap this long (milliseconds) means the fingers have left the glass and
        /// the next event starts a fresh gesture.</summary>
        public const double IdleResetMs = 200;

        /// <summary>How much recent finger travel (in the pixels
        /// <see cref="TimelineScrollInput.MacScrollPxPerDelta"/> converts to) first decides the
        /// axis. Small enough that the commitment happens within the first frame or two — before
        /// the eye can see the view do the wrong thing — and large enough that the decision is made
        /// from a real direction rather than from one 0.01 jitter event.</summary>
        public const double DecisionTravelPx = 6;

        /// <summary>The time constant (milliseconds) over which travel is forgotten. Each
        /// accumulator therefore reads as "how far this axis has moved just now" rather than "over
        /// the whole gesture", which is what lets a change of direction overtake the held axis while
        /// the fingers stay down. At ~50 ms it spans roughly six trackpad events: long enough that
        /// one stray frame cannot swing it, short enough that a deliberate turn registers at once.</summary>
        public const double MemoryMs = 50;

        /// <summary>How much recent travel the challenging axis must be worth before it can take the
        /// gesture over. Comfortably above the ~20 px of recent travel that steady cross-axis drift
        /// sustains, so a wandering finger never trips it — only actual movement does.</summary>
        public const double SwitchTravelPx = 25;

        /// <summary>How far the challenging axis must out-travel the held one to take over. Bare
        /// dominance is not enough — the two axes cross briefly whenever a gesture turns — but 1.5x
        /// is reached within a few events of a genuine change of direction.</summary>
        public const double SwitchRatio = 1.5;

        private TimelineScrollAxis _axis;
        private double _recentX;
        private double _recentY;
        private double _lastTimestampMs;
        private bool _started;

        /// <summary>The axis this event belongs to. Before the latch commits, the answer tracks the
        /// recent travel, so the opening pixels of a swipe already do the right thing; after it
        /// commits, only a decisive turn (or the gesture ending) changes it.</summary>
        public TimelineScrollAxis Resolve(double deltaX, double deltaY, double timestampMs)
        {
            if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY) || !double.IsFinite(timestampMs))
                return _axis;

            // a backwards timestamp can only be a wrap or a different clock; treat it as a new
            // gesture rather than letting the elapsed test go negative and hold the old axis.
            var elapsed = timestampMs - _lastTimestampMs;
            if (!_started || elapsed < 0 || elapsed > IdleResetMs)
                Reset();
            else
            {
                var decay = Math.Exp(-elapsed / MemoryMs);
                _recentX *= decay;
                _recentY *= decay;
            }

            _started = true;
            _lastTimestampMs = timestampMs;
            _recentX += Math.Abs(deltaX) * TimelineScrollInput.MacScrollPxPerDelta;
            _recentY += Math.Abs(deltaY) * TimelineScrollInput.MacScrollPxPerDelta;

            if (_axis == TimelineScrollAxis.Undecided)
            {
                if (_recentX == 0 && _recentY == 0)
                    return TimelineScrollAxis.Undecided;

                var dominant = _recentX > _recentY
                    ? TimelineScrollAxis.Horizontal
                    : TimelineScrollAxis.Vertical;

                if (Math.Max(_recentX, _recentY) >= DecisionTravelPx)
                    _axis = dominant;

                return dominant;
            }

            var held = _axis == TimelineScrollAxis.Horizontal ? _recentX : _recentY;
            var challenger = _axis == TimelineScrollAxis.Horizontal ? _recentY : _recentX;

            if (challenger >= SwitchTravelPx && challenger > held * SwitchRatio)
                _axis = _axis == TimelineScrollAxis.Horizontal
                    ? TimelineScrollAxis.Vertical
                    : TimelineScrollAxis.Horizontal;

            return _axis;
        }

        /// <summary>Forgets the gesture in progress, so the next event decides its axis afresh.</summary>
        public void Reset()
        {
            _axis = TimelineScrollAxis.Undecided;
            _recentX = 0;
            _recentY = 0;
            _started = false;
            _lastTimestampMs = 0;
        }
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
        /// <paramref name="axis"/> is the axis the gesture in progress has committed to, from the
        /// caller's <see cref="TimelineScrollAxisLatch"/>; it is consulted only by the plain macOS
        /// two-finger branch, the one gesture whose meaning depends on direction.
        public static TimelineScrollDecision DecideWheel(double deltaX, double deltaY,
            TimelineScrollModifiers modifiers, bool isMacOS,
            TimelineScrollAxis axis = TimelineScrollAxis.Undecided)
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
            // Windows wheel carries, on the axis the finger actually moved. One axis takes the whole
            // gesture rather than both acting at once: a trackpad reports cross-axis drift on every
            // swipe, and honouring it would zoom the view a hair on every horizontal pan (and slide
            // it sideways on every zoom). Which axis that is comes from the caller's latch, which
            // holds it steady for the length of the swipe; falling back to this event's own dominant
            // axis when there is no latch keeps a lone event decidable on its own. Once the axis is
            // settled the other component is simply dropped — a horizontal gesture's stray dy does
            // not zoom, and a vertical one's stray dx does not pan. The rows' vertical scroll is
            // Alt+scroll and the scroll bar, as on Windows.
            var isHorizontal = axis switch
            {
                TimelineScrollAxis.Horizontal => true,
                TimelineScrollAxis.Vertical => false,
                _ => Math.Abs(deltaX) > Math.Abs(deltaY),
            };

            if (isHorizontal)
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
