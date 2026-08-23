using System;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// Turns a stream of wheel deltas into whole notches, for the controls whose response to a
    /// wheel is a discrete step rather than a proportional movement.
    ///
    /// A Windows mouse reports exactly ±1 per detent, so every event pays out a step and this is a
    /// pass-through. A Mac trackpad reports a long stream of small fractions instead (Avalonia's
    /// macOS backend divides a precise scroll by 50 and a line-based one by 5), and a control that
    /// treats each of those as a full notch — which is what the properties bar's spinners used to do
    /// — slews its value by dozens of steps from one light two-finger flick.
    ///
    /// A mutable struct, one per control: keep it in a field and call <see cref="Accumulate"/>.
    /// Note that a <i>proportional</i> response (a zoom, a scroll offset) must not pool deltas like
    /// this — it should just scale by the fraction, as the timeline and the image editor's canvas
    /// do; pooling is only right where the destination is a ladder of discrete values.
    /// </summary>
    internal struct WheelNotchAccumulator
    {
        private double _pending;

        /// <summary>
        /// Adds one event's delta and returns how many whole notches have come due — signed, and 0
        /// while the stream is still short of one. The leftover fraction is carried, so a slow
        /// scroll still steps eventually and no notch is ever paid twice.
        ///
        /// A change of direction drops the carry instead of paying it off first: after an upward
        /// flick leaves +0.9 pending, a downward nudge has to read as a downward nudge, and a
        /// carried debt would make the control sit dead for a whole notch of scrolling before it
        /// moved the way the user just asked.
        /// </summary>
        public int Accumulate(double delta)
        {
            // an infinite or NaN delta would truncate to a garbage notch count below (the unchecked
            // double->int conversion is not saturating), so it never enters the pool.
            if (!double.IsFinite(delta) || delta == 0)
                return 0;

            if (Math.Sign(delta) != Math.Sign(_pending))
                _pending = 0;

            _pending += delta;

            var notches = (int)_pending; // truncates towards zero: 1.9 -> 1, -1.9 -> -1
            _pending -= notches;
            return notches;
        }
    }
}
