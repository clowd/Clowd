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

        // ------------------------------------------------- the axis latch: one swipe, one meaning

        // A swipe as the trackpad actually reports it: mostly the intended axis, with drift, a
        // couple of frames that lean the other way, and a decaying momentum tail.
        private static TimelineScrollDecision[] Swipe(TimelineScrollAxisLatch latch,
            (double x, double y)[] events, double startMs = 1000, double frameMs = 8)
        {
            var result = new TimelineScrollDecision[events.Length];
            for (var i = 0; i < events.Length; i++)
            {
                var (x, y) = events[i];
                var axis = latch.Resolve(x, y, startMs + i * frameMs);
                result[i] = TimelineScrollInput.DecideWheel(x, y, TimelineScrollModifiers.None, Mac, axis);
            }

            return result;
        }

        [Fact]
        public void A_horizontal_swipe_never_zooms_even_on_the_frames_that_drift_vertically()
        {
            // this is the bug: without the latch, events 3 and 6 are "dominantly vertical" on their
            // own and each one jolted the zoom in the middle of a pan.
            var decisions = Swipe(new TimelineScrollAxisLatch(), new[]
            {
                (-0.20, 0.01), (-0.42, -0.02), (-0.55, 0.03), (-0.04, 0.06),
                (-0.50, 0.01), (-0.38, -0.01), (0.01, -0.05), (-0.22, 0.02),
            });

            Assert.DoesNotContain(decisions, d => d.Action == TimelineScrollAction.Zoom);
            Assert.Contains(decisions, d => d.Action == TimelineScrollAction.PanHorizontal);
        }

        [Fact]
        public void A_vertical_swipe_never_pans_even_on_the_frames_that_drift_sideways()
        {
            var decisions = Swipe(new TimelineScrollAxisLatch(), new[]
            {
                (0.01, -0.18), (-0.02, -0.40), (0.03, -0.52), (0.07, -0.03),
                (0.01, -0.44), (-0.06, 0.02), (0.02, -0.30),
            });

            Assert.DoesNotContain(decisions, d => d.Action == TimelineScrollAction.PanHorizontal);
            Assert.Contains(decisions, d => d.Action == TimelineScrollAction.Zoom);
        }

        [Fact]
        public void The_axis_is_committed_within_the_first_few_pixels_of_travel()
        {
            // the commitment must not cost a visible amount of the gesture: at a typical opening
            // delta it lands on the very first event, and by 6 px of travel it is always made.
            var latch = new TimelineScrollAxisLatch();
            Assert.Equal(TimelineScrollAxis.Horizontal, latch.Resolve(-0.2, 0.01, 1000));

            // 0.2 * 50 px is past the threshold, so the drift on the frames that follow cannot
            // change its mind — only movement worth SwitchTravelPx can.
            Assert.Equal(TimelineScrollAxis.Horizontal, latch.Resolve(0.0, -0.06, 1008));
            Assert.Equal(TimelineScrollAxis.Horizontal, latch.Resolve(-0.3, -0.05, 1016));
        }

        [Fact]
        public void A_gesture_that_opens_below_the_threshold_can_still_settle_on_the_other_axis()
        {
            // the first event of a swipe is often a 1 px twitch in a direction the user did not
            // mean; nothing is latched until there is enough travel to be sure.
            var latch = new TimelineScrollAxisLatch();
            Assert.Equal(TimelineScrollAxis.Horizontal, latch.Resolve(-0.02, 0.0, 1000));
            Assert.Equal(TimelineScrollAxis.Vertical, latch.Resolve(0.0, -0.30, 1008));

            // and once it has settled there, the twitch it opened with is no longer competing
            Assert.Equal(TimelineScrollAxis.Vertical, latch.Resolve(-0.02, -0.30, 1016));
        }

        [Fact]
        public void Lifting_the_fingers_ends_the_gesture_so_the_next_one_decides_afresh()
        {
            // the same drift-sized event either way, so what is under test is the gesture boundary
            // and not the size of the delta: inside the gesture it is drift and the axis holds,
            // across the boundary it is the opening of a new swipe and decides that swipe's axis.
            var latch = new TimelineScrollAxisLatch();
            Assert.Equal(TimelineScrollAxis.Horizontal, latch.Resolve(-0.5, 0.0, 1000));
            Assert.Equal(TimelineScrollAxis.Horizontal, latch.Resolve(0.0, -0.06, 1100));

            Assert.Equal(TimelineScrollAxis.Vertical,
                latch.Resolve(0.0, -0.06, 1100 + TimelineScrollAxisLatch.IdleResetMs + 1));
        }

        // Replays a stream of identical events 8 ms apart — the cadence of a real trackpad — and
        // returns the axis after each one.
        private static TimelineScrollAxis[] Hold(TimelineScrollAxisLatch latch, double dx, double dy,
            int count, ref double clockMs)
        {
            var axes = new TimelineScrollAxis[count];
            for (var i = 0; i < count; i++)
            {
                axes[i] = latch.Resolve(dx, dy, clockMs);
                clockMs += 8;
            }

            return axes;
        }

        [Fact]
        public void A_deliberate_turn_changes_axis_without_lifting_the_fingers()
        {
            // the complaint the switch logic exists for: holding the axis against drift must not
            // also mean that turning a pan into a zoom costs a lift and a wait.
            var latch = new TimelineScrollAxisLatch();
            var clock = 1000.0;

            Assert.All(Hold(latch, -0.45, 0.01, 15, ref clock),
                a => Assert.Equal(TimelineScrollAxis.Horizontal, a));

            var turn = Hold(latch, 0.01, -0.45, 15, ref clock);
            var flipped = Array.IndexOf(turn, TimelineScrollAxis.Vertical);

            // within half a dozen events — around 50 ms, faster than a hand can lift and return
            Assert.InRange(flipped, 0, 7);
            Assert.All(turn[flipped..], a => Assert.Equal(TimelineScrollAxis.Vertical, a));

            // and back again, so neither direction is the privileged one
            var back = Hold(latch, -0.45, 0.01, 15, ref clock);
            Assert.InRange(Array.IndexOf(back, TimelineScrollAxis.Horizontal), 0, 7);
        }

        [Fact]
        public void Cross_axis_drift_never_turns_a_sustained_swipe()
        {
            // the other half of the bargain: the axis has to survive a long, wandering swipe with
            // near-stalled frames in it, or the switch logic has simply reintroduced the bug.
            var latch = new TimelineScrollAxisLatch();
            var rng = new Random(7);
            var clock = 1000.0;

            Assert.Equal(TimelineScrollAxis.Horizontal, latch.Resolve(-0.45, 0.01, clock));
            for (var i = 1; i < 400; i++)
            {
                clock += 8;

                // 0.2-0.6 sideways with up to 0.08 of vertical wander, and every so often a frame
                // where the fingers have all but stopped and the drift is all there is
                var dx = i % 17 == 0 ? -0.02 : -(0.2 + rng.NextDouble() * 0.4);
                var dy = (rng.NextDouble() - 0.5) * 0.16;
                Assert.Equal(TimelineScrollAxis.Horizontal, latch.Resolve(dx, dy, clock));
            }
        }

        [Fact]
        public void A_committed_swipe_takes_more_to_overturn_than_a_barely_started_one()
        {
            // the hold is proportional to conviction, which is what keeps the same rule from being
            // either too sticky mid-pan or too stubborn the instant after it commits.
            var barely = new TimelineScrollAxisLatch();
            var clock = 1000.0;
            Hold(barely, -0.15, 0, 1, ref clock);
            var barelyTurn = Array.IndexOf(Hold(barely, 0, -0.45, 12, ref clock), TimelineScrollAxis.Vertical);

            var committed = new TimelineScrollAxisLatch();
            clock = 1000.0;
            Hold(committed, -0.45, 0, 20, ref clock);
            var committedTurn = Array.IndexOf(Hold(committed, 0, -0.45, 12, ref clock), TimelineScrollAxis.Vertical);

            Assert.InRange(barelyTurn, 0, committedTurn);
            Assert.InRange(committedTurn, 0, 7);
        }

        [Fact]
        public void Resetting_the_latch_starts_the_next_event_over()
        {
            // what the pinch handler does, so a magnify cannot leave a stale axis behind it.
            var latch = new TimelineScrollAxisLatch();
            Assert.Equal(TimelineScrollAxis.Horizontal, latch.Resolve(-0.5, 0.0, 1000));
            latch.Reset();
            Assert.Equal(TimelineScrollAxis.Vertical, latch.Resolve(0.0, -0.5, 1008));
        }

        [Fact]
        public void A_latched_axis_only_governs_the_plain_two_finger_gesture()
        {
            // the modified gestures mean one thing regardless of direction, so a latch left over
            // from a swipe must not reach them — nor Windows, which has no such gesture at all.
            Assert.Equal(TimelineScrollAction.Zoom,
                TimelineScrollInput.DecideWheel(0, 0.2, TimelineScrollModifiers.Meta, Mac,
                    TimelineScrollAxis.Horizontal).Action);

            Assert.Equal(TimelineScrollAction.PanHorizontal,
                TimelineScrollInput.DecideWheel(-0.4, 0, TimelineScrollModifiers.Shift, Mac,
                    TimelineScrollAxis.Vertical).Action);

            Assert.Equal(TimelineScrollAction.ScrollRows,
                TimelineScrollInput.DecideWheel(-0.4, 0, TimelineScrollModifiers.Alt, Mac,
                    TimelineScrollAxis.Horizontal).Action);

            Assert.Equal(TimelineScrollAction.Zoom,
                TimelineScrollInput.DecideWheel(1, 0, TimelineScrollModifiers.None, Win,
                    TimelineScrollAxis.Horizontal).Action);
        }

        [Fact]
        public void A_dead_frame_inside_a_gesture_does_nothing_rather_than_the_wrong_thing()
        {
            // a latched horizontal gesture whose event carries only a vertical delta must come out
            // as None: dropping the cross-axis component is the point, and Pan(0) is not a pan.
            var latch = new TimelineScrollAxisLatch();
            var decisions = Swipe(latch, new[] { (-0.5, 0.0), (0.0, -0.4) });

            Assert.Equal(TimelineScrollAction.PanHorizontal, decisions[0].Action);
            Assert.Equal(TimelineScrollAction.None, decisions[1].Action);
        }

        [Fact]
        public void A_non_finite_delta_cannot_poison_the_latch()
        {
            var latch = new TimelineScrollAxisLatch();
            Assert.Equal(TimelineScrollAxis.Horizontal, latch.Resolve(-0.5, 0.0, 1000));
            Assert.Equal(TimelineScrollAxis.Horizontal, latch.Resolve(Double.NaN, 0.0, 1008));
            Assert.Equal(TimelineScrollAxis.Horizontal, latch.Resolve(-0.5, 0.0, 1016));
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
