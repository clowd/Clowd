using System;
using Clowd.UI.Controls;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // WheelNotchAccumulator is what stands between a trackpad and the properties bar's numeric
    // spinners: it pools fractional wheel deltas until a whole notch has accrued. Pure arithmetic —
    // no Avalonia, no control instance — so the pooling rule is testable without a UI thread
    // (Clowd.Ui exposes its internals here via InternalsVisibleTo).
    public class WheelNotchAccumulatorTests
    {
        [Fact]
        public void A_windows_detent_still_steps_once_per_event()
        {
            // The behaviour that must not change: a real mouse reports ±1 per detent, so every
            // event pays out exactly one step and nothing is ever left pending.
            var acc = new WheelNotchAccumulator();
            for (var i = 0; i < 10; i++)
                Assert.Equal(1, acc.Accumulate(1.0));

            for (var i = 0; i < 10; i++)
                Assert.Equal(-1, acc.Accumulate(-1.0));
        }

        [Fact]
        public void Several_notches_in_one_event_pay_out_all_of_them()
        {
            var acc = new WheelNotchAccumulator();
            Assert.Equal(3, acc.Accumulate(3.0));
            Assert.Equal(-2, acc.Accumulate(-2.0));
        }

        [Fact]
        public void Fractions_are_pooled_and_pay_out_on_the_event_that_completes_a_notch()
        {
            // quarter notches: three of them do nothing, the fourth steps once, and the pool is
            // empty again afterwards (0.25 is exact in binary, so this is an exact assertion).
            var acc = new WheelNotchAccumulator();
            Assert.Equal(0, acc.Accumulate(0.25));
            Assert.Equal(0, acc.Accumulate(0.25));
            Assert.Equal(0, acc.Accumulate(0.25));
            Assert.Equal(1, acc.Accumulate(0.25));
            Assert.Equal(0, acc.Accumulate(0.25));
        }

        [Fact]
        public void A_light_trackpad_flick_is_one_step_or_none_where_it_used_to_be_twenty()
        {
            // The bug: the spinner spun once per *event*, and a Mac trackpad emits a long stream of
            // ~0.05 fractions for the gentlest two-finger nudge — twenty steps out of a movement
            // the user read as "a touch". Pooled, the same flick is worth its one notch.
            var acc = new WheelNotchAccumulator();
            var steps = 0;
            for (var i = 0; i < 20; i++)
                steps += acc.Accumulate(0.05);

            Assert.InRange(steps, 0, 1); // 20 x 0.05 lands on the notch boundary give or take a bit
        }

        [Fact]
        public void A_long_scroll_still_delivers_every_notch_it_earns()
        {
            // pooling must not swallow travel: eight quarter-notches are two steps, not one.
            var acc = new WheelNotchAccumulator();
            var steps = 0;
            for (var i = 0; i < 8; i++)
                steps += acc.Accumulate(0.25);

            Assert.Equal(2, steps);
        }

        [Fact]
        public void Reversing_direction_drops_the_carry_instead_of_paying_it_off()
        {
            var acc = new WheelNotchAccumulator();
            Assert.Equal(0, acc.Accumulate(0.75)); // three quarters of a notch owed upwards

            // the user changes their mind: the first downward event must not be spent cancelling a
            // debt they cannot see, so the pool restarts from this delta alone.
            Assert.Equal(0, acc.Accumulate(-0.75));
            Assert.Equal(-1, acc.Accumulate(-0.5)); // -1.25 pooled downwards: one step, 0.25 left
            Assert.Equal(-1, acc.Accumulate(-0.75));
        }

        [Fact]
        public void A_zero_or_non_finite_delta_is_ignored_and_does_not_poison_the_pool()
        {
            var acc = new WheelNotchAccumulator();
            Assert.Equal(0, acc.Accumulate(0.5));
            Assert.Equal(0, acc.Accumulate(0));
            Assert.Equal(0, acc.Accumulate(Double.NaN));
            Assert.Equal(0, acc.Accumulate(Double.PositiveInfinity));

            // the half notch that was already owed is still owed, and still exact
            Assert.Equal(1, acc.Accumulate(0.5));
        }
    }
}
