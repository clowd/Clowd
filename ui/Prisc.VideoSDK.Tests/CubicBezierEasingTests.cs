using System;
using Clowd.UI.Controls.Tray;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="CubicBezierEasing"/> against CSS's cubic-bezier: the end points are pinned, the
    /// linear curve is the identity, and the hint's fly-in curve overshoots and settles.
    /// </summary>
    public class CubicBezierEasingTests
    {
        [Fact]
        public void Ends_are_pinned()
        {
            var easing = new CubicBezierEasing(0.2, 0.8, 0.3, 1.5);
            Assert.Equal(0, easing.Ease(0));
            Assert.Equal(1, easing.Ease(1));
            Assert.Equal(0, easing.Ease(-0.5));
            Assert.Equal(1, easing.Ease(1.5));
        }

        [Fact]
        public void Linear_control_points_are_the_identity()
        {
            var easing = new CubicBezierEasing(1 / 3d, 1 / 3d, 2 / 3d, 2 / 3d);
            for (var x = 0.05; x < 1; x += 0.05)
                Assert.Equal(x, easing.Ease(x), 6);
        }

        [Fact]
        public void Css_ease_matches_known_values()
        {
            // cubic-bezier(.25,.1,.25,1), CSS "ease", sampled from a browser
            var ease = new CubicBezierEasing(0.25, 0.1, 0.25, 1);
            Assert.Equal(0.8024, ease.Ease(0.5), 3);
            Assert.Equal(0.4094, ease.Ease(0.25), 3);
        }

        [Fact]
        public void Fly_in_overshoots_and_settles()
        {
            var easing = new CubicBezierEasing(0.2, 0.8, 0.3, 1.5);
            var peak = 0d;
            for (var x = 0.0; x <= 1; x += 0.01)
                peak = Math.Max(peak, easing.Ease(x));

            Assert.True(peak > 1.05, $"peak {peak}");
            Assert.True(easing.Ease(0.99) > 1 && easing.Ease(0.99) < peak);
        }

        [Fact]
        public void X_outside_the_unit_interval_is_refused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CubicBezierEasing(-0.1, 0, 1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CubicBezierEasing(0, 0, 1.1, 1));
        }
    }
}
