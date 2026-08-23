using System;
using Clowd.UI.VideoEditor.Timeline;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // TimelineScrollInput decides what a wheel / two-finger scroll / pinch over the timeline means.
    // It takes the platform as a parameter rather than asking OperatingSystem, which is the whole
    // point of it being a separate pure function: the Windows rules are covered from a Mac (and the
    // macOS rules from a Windows CI box) instead of being asserted by hand on one machine and taken
    // on trust on the other. Clowd.Ui exposes its internals here via InternalsVisibleTo.
    public class TimelineScrollInputTests
    {
        private const bool Win = false;
        private const bool Mac = true;

        private static TimelineScrollDecision Wheel(double x, double y, bool mac,
            TimelineScrollModifiers mods = TimelineScrollModifiers.None) =>
            TimelineScrollInput.DecideWheel(x, y, mods, mac);

        // -------------------------------------------------------- Windows: exactly as it shipped

        [Fact]
        public void Windows_plain_wheel_zooms_around_the_pointer()
        {
            // one notch away from the user zooms in, i.e. divides ticks-per-pixel by the step
            var inward = Wheel(0, 1, Win);
            Assert.Equal(TimelineScrollAction.Zoom, inward.Action);
            Assert.Equal(1 / TimelineScrollInput.ZoomStepPerNotch, inward.ZoomFactor, 12);

            var outward = Wheel(0, -1, Win);
            Assert.Equal(TimelineScrollAction.Zoom, outward.Action);
            Assert.Equal(TimelineScrollInput.ZoomStepPerNotch, outward.ZoomFactor, 12);
        }

        [Fact]
        public void Windows_ctrl_wheel_zooms_too_because_that_is_the_habit()
        {
            var plain = Wheel(0, 1, Win);
            var ctrl = Wheel(0, 1, Win, TimelineScrollModifiers.Control);
            Assert.Equal(plain, ctrl);
        }

        [Fact]
        public void Windows_shift_wheel_pans_a_whole_notch_and_beats_alt()
        {
            var pan = Wheel(0, 1, Win, TimelineScrollModifiers.Shift);
            Assert.Equal(TimelineScrollAction.PanHorizontal, pan.Action);
            Assert.Equal(-TimelineScrollInput.WheelScrollPxPerNotch, pan.PanPixels, 12);

            // Shift is tested before Alt, so the combination still pans — the order the shipped
            // if/else chain used, kept because Alt's only job is to opt out of the zoom.
            Assert.Equal(pan, Wheel(0, 1, Win, TimelineScrollModifiers.Shift | TimelineScrollModifiers.Alt));
        }

        [Fact]
        public void Windows_alt_wheel_belongs_to_the_rows_scroller()
        {
            Assert.Equal(TimelineScrollAction.ScrollRows, Wheel(0, 1, Win, TimelineScrollModifiers.Alt).Action);
            Assert.Equal(0, Wheel(0, 1, Win, TimelineScrollModifiers.Alt).PanPixels);
        }

        [Fact]
        public void Windows_uses_the_tilt_wheel_only_when_there_is_no_vertical_delta()
        {
            // a tilt wheel alone reads as the wheel axis...
            Assert.Equal(TimelineScrollAction.Zoom, Wheel(1, 0, Win).Action);
            // ...and is ignored the moment the real wheel is turning, so a tilting hand cannot
            // change what a notch does.
            Assert.Equal(Wheel(0, 1, Win), Wheel(-3, 1, Win));
        }

        [Fact]
        public void A_zero_or_non_finite_delta_does_nothing_on_either_platform()
        {
            foreach (var mac in new[] { Win, Mac })
            {
                Assert.Equal(TimelineScrollAction.None, Wheel(0, 0, mac).Action);
                Assert.Equal(TimelineScrollAction.None, Wheel(Double.NaN, 1, mac).Action);
                Assert.Equal(TimelineScrollAction.None, Wheel(0, Double.NaN, mac).Action);
                Assert.Equal(TimelineScrollAction.None, Wheel(0, Double.PositiveInfinity, mac).Action);
            }
        }

        // ------------------------------------------------------- macOS: the axis is the gesture

        [Fact]
        public void Mac_two_finger_vertical_scroll_zooms_proportionally()
        {
            // up/down is the wheel gesture, and a trackpad delivers it in fractions of a notch: the
            // zoom has to be proportional to the delta, not stepped, or twenty of these fly the
            // timeline through several orders of magnitude before the fingers leave the glass.
            var factor = 1.0;
            for (var i = 0; i < 20; i++)
            {
                var d = Wheel(0.0, -0.06, Mac);
                Assert.Equal(TimelineScrollAction.Zoom, d.Action);
                factor *= d.ZoomFactor;
            }

            // twenty sixth-of-a-notch flicks land exactly where 1.2 notches of a wheel would
            Assert.Equal(Math.Pow(TimelineScrollInput.ZoomStepPerNotch, 20 * 0.06), factor, 12);
        }

        [Fact]
        public void Mac_two_finger_horizontal_scroll_pans_the_timeline()
        {
            var d = Wheel(-0.5, 0, Mac);
            Assert.Equal(TimelineScrollAction.PanHorizontal, d.Action);

            // Avalonia's sign convention: a positive delta moves the view towards the origin, so a
            // negative one scrolls it forwards in time. The gain is the 50 px per delta unit the
            // rows' own ScrollViewer uses for the vertical half of the same gesture.
            Assert.Equal(0.5 * TimelineScrollInput.MacScrollPxPerDelta, d.PanPixels, 12);
            Assert.Equal(-0.5 * TimelineScrollInput.MacScrollPxPerDelta, Wheel(0.5, 0, Mac).PanPixels, 12);
        }

        [Fact]
        public void Mac_a_diagonal_scroll_goes_to_whichever_axis_dominates()
        {
            // a trackpad reports cross-axis drift on every swipe; the smaller axis is dropped so a
            // pan cannot also zoom the view a hair, nor a zoom slide it sideways.
            Assert.Equal(TimelineScrollAction.Zoom, Wheel(0.02, -0.30, Mac).Action);
            Assert.Equal(TimelineScrollAction.PanHorizontal, Wheel(-0.30, 0.02, Mac).Action);
        }

        [Fact]
        public void Mac_cmd_and_ctrl_scroll_zoom_proportionally()
        {
            foreach (var mod in new[] { TimelineScrollModifiers.Meta, TimelineScrollModifiers.Control })
            {
                var d = Wheel(0, 0.2, Mac, mod);
                Assert.Equal(TimelineScrollAction.Zoom, d.Action);

                // a fifth of a notch is a fifth of a notch's worth of zoom — the fractions a Mac
                // delivers have to accumulate smoothly, not step.
                Assert.Equal(Math.Pow(TimelineScrollInput.ZoomStepPerNotch, -0.2), d.ZoomFactor, 12);
                Assert.InRange(d.ZoomFactor, 0.9, 1.0);
            }
        }

        [Fact]
        public void Mac_zoom_accumulates_to_the_same_place_a_whole_notch_reaches()
        {
            // five fifth-notch events must land exactly where one Windows notch does, otherwise the
            // two platforms would drift apart at the same physical amount of scrolling.
            var factor = 1.0;
            for (var i = 0; i < 5; i++)
                factor *= Wheel(0, 0.2, Mac, TimelineScrollModifiers.Meta).ZoomFactor;

            Assert.Equal(Wheel(0, 1, Win).ZoomFactor, factor, 12);
        }

        [Fact]
        public void Mac_shift_scroll_pans_from_whichever_axis_the_device_used()
        {
            // macOS swaps the axes itself for most devices while Shift is held...
            var swapped = Wheel(-0.4, 0, Mac, TimelineScrollModifiers.Shift);
            // ...and for the ones that do not, the vertical delta is read as horizontal.
            var unswapped = Wheel(0, -0.4, Mac, TimelineScrollModifiers.Shift);

            Assert.Equal(TimelineScrollAction.PanHorizontal, swapped.Action);
            Assert.Equal(swapped, unswapped);
        }

        [Fact]
        public void Mac_alt_scroll_still_hands_the_event_to_the_rows_scroller()
        {
            Assert.Equal(TimelineScrollAction.ScrollRows, Wheel(-0.4, 0, Mac, TimelineScrollModifiers.Alt).Action);
        }

        [Fact]
        public void An_unmodified_vertical_scroll_zooms_on_both_platforms()
        {
            // what is left of the platform split: the gesture means the same thing on both, only
            // the gain differs — a Mac's deltas are accelerated fractions, a wheel's are notches.
            Assert.Equal(TimelineScrollAction.Zoom, Wheel(0, 1, Win).Action);
            Assert.Equal(TimelineScrollAction.Zoom, Wheel(0, 1, Mac).Action);
            Assert.Equal(Wheel(0, 1, Win).ZoomFactor, Wheel(0, 1, Mac).ZoomFactor, 12);

            // the horizontal half is where they still differ: a Mac pans on the bare gesture, a
            // wheel needs Shift for it.
            Assert.Equal(TimelineScrollAction.PanHorizontal, Wheel(1, 0, Mac).Action);
            Assert.Equal(TimelineScrollAction.Zoom, Wheel(1, 0, Win).Action);
        }

        // ------------------------------------------------------------------------ pinch (magnify)

        [Fact]
        public void Pinching_open_zooms_in_and_pinching_closed_zooms_out()
        {
            // NSEvent.magnification is a relative scale increment and the timeline's zoom is its
            // inverse (ticks per pixel), so opening the fingers must shrink the factor.
            Assert.Equal(1 / 1.1, TimelineScrollInput.ZoomFactorForMagnification(0.1), 12);
            Assert.Equal(1 / 0.9, TimelineScrollInput.ZoomFactorForMagnification(-0.1), 12);
        }

        [Fact]
        public void A_pinch_that_does_not_move_changes_nothing()
        {
            Assert.Equal(1, TimelineScrollInput.ZoomFactorForMagnification(0));
        }

        [Fact]
        public void An_impossible_magnification_is_refused_rather_than_dividing_by_zero()
        {
            // -1 would be a scale of zero and anything past it flips the axis; neither may reach
            // the viewport, where a NaN or negative ticks-per-pixel sticks.
            Assert.Equal(1, TimelineScrollInput.ZoomFactorForMagnification(-1));
            Assert.Equal(1, TimelineScrollInput.ZoomFactorForMagnification(-2.5));
            Assert.Equal(1, TimelineScrollInput.ZoomFactorForMagnification(Double.NaN));
            Assert.Equal(1, TimelineScrollInput.ZoomFactorForMagnification(Double.PositiveInfinity));
        }

        [Fact]
        public void A_whole_pinch_gesture_is_a_useful_amount_of_zoom()
        {
            // A real pinch arrives as a stream of small increments. Thirty of them at 0.02 — a
            // short, unhurried spread of the fingers — should be worth roughly a doubling: enough
            // that the gesture is obviously working, not so much that the timeline vanishes.
            var factor = 1.0;
            for (var i = 0; i < 30; i++)
                factor *= TimelineScrollInput.ZoomFactorForMagnification(0.02);

            Assert.InRange(factor, 0.4, 0.7);
        }
    }
}
