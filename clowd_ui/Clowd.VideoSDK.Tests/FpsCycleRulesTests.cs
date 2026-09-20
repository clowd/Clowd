using System;
using System.Linq;
using Clowd.UI;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The FPS tile's cycle, pinned per monitor. The list is easy to get subtly wrong in exactly the
    /// ways that only show on hardware nobody on the team has (a 59 Hz laptop panel, a 144 Hz
    /// gaming monitor), so every shape the user described is a case here.
    /// </summary>
    public class FpsCycleRulesTests
    {
        [Theory]
        [InlineData(60, new[] { 30, 60 })]
        [InlineData(59, new[] { 30, 59 })]
        [InlineData(61, new[] { 30, 61 })]
        [InlineData(80, new[] { 30, 60, 80 })]
        [InlineData(75, new[] { 30, 60, 75 })]
        [InlineData(120, new[] { 30, 60, 120 })]
        [InlineData(119, new[] { 30, 60, 119 })]
        [InlineData(144, new[] { 30, 60, 120, 144 })]
        [InlineData(240, new[] { 30, 60, 120, 240 })]
        [InlineData(30, new[] { 30 })]
        [InlineData(24, new[] { 24 })]
        public void Options_follow_the_native_rate(double nativeHz, int[] expected)
        {
            Assert.Equal(expected, FpsCycleRules.Options(nativeHz).ToArray());
        }

        [Fact]
        public void A_fractional_native_rate_rounds_before_it_is_compared()
        {
            Assert.Equal(new[] { 30, 60 }, FpsCycleRules.Options(59.94).ToArray());
            Assert.Equal(new[] { 30, 60, 120, 144 }, FpsCycleRules.Options(143.98).ToArray());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(Double.NaN)]
        [InlineData(Double.PositiveInfinity)]
        public void An_unknown_native_rate_offers_the_bare_presets(double nativeHz)
        {
            Assert.Equal(FpsCycleRules.Presets, FpsCycleRules.Options(nativeHz));
        }

        [Fact]
        public void The_list_never_carries_a_duplicate()
        {
            foreach (var hz in Enumerable.Range(1, 300))
            {
                var options = FpsCycleRules.Options(hz);
                Assert.Equal(options.Count, options.Distinct().Count());
                Assert.Equal(options, options.OrderBy(o => o));
                Assert.Equal(hz, options[options.Count - 1]);
            }
        }

        [Theory]
        [InlineData(30, 60)]
        [InlineData(60, 120)]
        [InlineData(120, 144)]
        [InlineData(144, 30)]
        public void Next_walks_up_and_wraps(int current, int expected)
        {
            Assert.Equal(expected, FpsCycleRules.Next(current, new[] { 30, 60, 120, 144 }));
        }

        [Theory]
        [InlineData(25, 30)]   // typed on the settings page: below the first stop
        [InlineData(90, 120)]  // between two stops
        [InlineData(200, 30)]  // above the last stop: wraps
        public void Next_from_a_value_that_is_not_a_stop_lands_on_one(int current, int expected)
        {
            Assert.Equal(expected, FpsCycleRules.Next(current, new[] { 30, 60, 120, 144 }));
        }

        [Fact]
        public void Next_with_a_single_option_stays_there()
        {
            Assert.Equal(24, FpsCycleRules.Next(24, new[] { 24 }));
        }

        [Theory]
        [InlineData(29.9, "30")]
        [InlineData(18.4, "18")]
        [InlineData(0, "0")]
        [InlineData(Double.NaN, "0")]
        [InlineData(-3, "0")]
        [InlineData(119.6, "120")]
        public void A_measured_rate_prints_as_a_whole_number(double fps, string expected)
        {
            Assert.Equal(expected, FpsCycleRules.FormatFps(fps));
        }
    }
}
