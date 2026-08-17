using System;
using Clowd.VideoSDK.Composition;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // The click-highlight clocks in the abstract: mode parsing and the ring/press press-and-release
    // curves the compositor and the inspector tiles both animate from.
    public class ClickHighlightTests
    {
        // ------------------------------------------------------------------------------ parsing

        [Theory]
        [InlineData("ripple", HighlightMode.Ripple)]
        [InlineData("Pulse", HighlightMode.Pulse)]
        [InlineData("RING", HighlightMode.Ring)]
        [InlineData("pressure", HighlightMode.Press)]
        [InlineData("press", HighlightMode.Press)] // the pre-rename wire name stays an alias
        public void ModeOf_recognises_every_wire_name_case_insensitively(string name, HighlightMode expected)
        {
            Assert.Equal(expected, ClickHighlight.ModeOf(name));
            Assert.True(ClickHighlight.TryParse(name, out var mode));
            Assert.Equal(expected, mode);
        }

        [Theory]
        [InlineData("none")]
        [InlineData(null)]
        [InlineData("sparkles")] // a newer editor's mode degrades to no highlight, not a wrong one
        public void ModeOf_unknown_is_none(string name)
        {
            Assert.Equal(HighlightMode.None, ClickHighlight.ModeOf(name));
            Assert.False(ClickHighlight.TryParse(name, out _));
        }

        // --------------------------------------------------------------------------------- ring

        [Fact]
        public void Ring_rests_at_full_size()
        {
            // idle: any release was long ago
            Assert.Equal(1.0, ClickHighlight.RingScale(null, double.MaxValue, null, 1.0), 6);
        }

        [Fact]
        public void Ring_closes_to_its_held_size_and_stays_there()
        {
            // a hold old enough to have left the scan window is fully engaged
            Assert.Equal(ClickHighlight.RingShrink, ClickHighlight.RingScale(null, null, null, 1.0), 6);
            Assert.Equal(ClickHighlight.RingShrink,
                ClickHighlight.RingScale(ClickHighlight.RingEngageMs, null, null, 1.0), 6);
        }

        [Fact]
        public void Ring_engagement_moves_inward_monotonically()
        {
            double previous = 1.0 + 1e-9;
            for (double t = 0; t <= ClickHighlight.RingEngageMs; t += ClickHighlight.RingEngageMs / 8)
            {
                double scale = ClickHighlight.RingScale(t, null, null, 1.0);
                Assert.True(scale < previous, $"scale did not shrink at {t}ms");
                Assert.InRange(scale, ClickHighlight.RingShrink, 1.0);
                previous = scale;
            }
        }

        [Fact]
        public void Ring_release_springs_back_out_with_an_overshoot()
        {
            // a full press released: by the end the ring is back at rest…
            Assert.Equal(1.0, ClickHighlight.RingScale(null, ClickHighlight.RingReleaseMs, null, 1.0), 6);

            // …and somewhere on the way it breathes past its resting size (ease-out-back)
            bool overshot = false;
            for (double t = 0; t < ClickHighlight.RingReleaseMs; t += ClickHighlight.RingReleaseMs / 32)
                overshot |= ClickHighlight.RingScale(null, t, null, 1.0) > 1.0;
            Assert.True(overshot, "the release never breathed past the resting radius");
        }

        [Fact]
        public void A_quick_click_breathes_a_shallower_breath()
        {
            // released after a fifth of the engage time: the release starts from a partial
            // shrink, so early in the release the quick click sits wider than the full press
            double quick = ClickHighlight.RingScale(null, 10, ClickHighlight.RingEngageMs / 5, 1.0);
            double full = ClickHighlight.RingScale(null, 10, null, 1.0);
            Assert.True(quick > full, $"quick click ({quick}) should be shallower than a full press ({full})");
            Assert.True(quick < 1.02, "a quick click's release still starts near the pressed size");
        }

        [Fact]
        public void Ring_clock_scales_with_animation_speed()
        {
            // at 2x the engage is half as long: the same wall-clock instant is further along
            double stock = ClickHighlight.RingScale(ClickHighlight.RingEngageMs / 2, null, null, 1.0);
            double fast = ClickHighlight.RingScale(ClickHighlight.RingEngageMs / 2, null, null, 2.0);
            Assert.True(fast < stock, "2x speed should be further into the shrink");
            Assert.Equal(ClickHighlight.RingShrink,
                ClickHighlight.RingScale(ClickHighlight.RingEngageMs / 2, null, null, 2.0), 6);
        }

        // -------------------------------------------------------------------------------- press

        [Fact]
        public void Press_engages_while_held_and_relaxes_after_release()
        {
            Assert.Equal(0.0, ClickHighlight.PressAmount(null, double.MaxValue, null, 1.0), 6);
            Assert.Equal(1.0, ClickHighlight.PressAmount(null, null, null, 1.0), 6);
            Assert.Equal(1.0, ClickHighlight.PressAmount(ClickHighlight.PressEngageMs, null, null, 1.0), 6);
            Assert.Equal(0.0, ClickHighlight.PressAmount(null, ClickHighlight.PressReleaseMs, null, 1.0), 6);

            // mid-engage and mid-release are strictly between the ends
            Assert.InRange(ClickHighlight.PressAmount(ClickHighlight.PressEngageMs / 3, null, null, 1.0),
                0.001, 0.999);
            Assert.InRange(ClickHighlight.PressAmount(null, ClickHighlight.PressReleaseMs / 3, null, 1.0),
                0.001, 0.999);
        }

        [Fact]
        public void A_quick_press_never_reaches_full_depth()
        {
            double quick = ClickHighlight.PressAmount(null, 1, ClickHighlight.PressEngageMs / 4, 1.0);
            Assert.InRange(quick, 0.001, 0.95);
        }

        [Fact]
        public void Clamp01_swallows_a_hand_edited_NaN()
        {
            Assert.Equal(0.0, ClickHighlight.Clamp01(double.NaN));
            Assert.Equal(1.0, ClickHighlight.Clamp01(4.2));
            Assert.Equal(0.0, ClickHighlight.Clamp01(-1));
            Assert.Equal(0.3, ClickHighlight.Clamp01(0.3), 6);
        }
    }
}
