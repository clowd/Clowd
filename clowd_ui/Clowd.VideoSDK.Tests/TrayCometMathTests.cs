using System;
using Avalonia;
using Clowd.UI.Controls.Tray;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The entrance comet around a floating tray (<see cref="TrayCometMath"/>): its outline has to close
    /// without a notch, and its run has to fly out of
    /// the start point at full length, make one lap at a fixed speed, vanish back through that point and
    /// then stop for good.
    /// </summary>
    public class TrayCometMathTests
    {
        private static readonly Rect Tray = new Rect(10, 7, 400, 48);

        [Fact]
        public void Perimeter_closes_and_is_continuous()
        {
            var (start, _) = TrayCometMath.PerimeterPoint(Tray, 8, 0);
            var (end, _) = TrayCometMath.PerimeterPoint(Tray, 8, 1);
            Assert.True(Distance(start, end) < 1e-6, $"{start} vs {end}");

            var previous = start;
            for (var i = 1; i <= TrayCometMath.Samples; i++)
            {
                var (p, n) = TrayCometMath.PerimeterPoint(Tray, 8, (double)i / TrayCometMath.Samples);
                Assert.True(Distance(p, previous) < Tray.Width / 16, $"jump at sample {i}");
                Assert.Equal(1, n.Length, 6);
                Assert.True(Tray.Inflate(0.01).Contains(p), $"{p} left the tray at sample {i}");
                previous = p;
            }
        }

        [Fact]
        public void Intensity_is_full_at_the_head_and_dark_past_the_tail()
        {
            Assert.Equal(1, TrayCometMath.Intensity(0.25, 0.25, 0.4), 6);
            var mid = TrayCometMath.Intensity(0.05, 0.25, 0.4);
            Assert.InRange(mid, 0.01, 0.99);
            Assert.Equal(0, TrayCometMath.Intensity(0.8, 0.25, 0.4));
            // the tail wraps past zero
            Assert.True(TrayCometMath.Intensity(0.95, 0.05, 0.4) > 0);
            // no tail yet, nothing lit
            Assert.Equal(0, TrayCometMath.Intensity(0.25, 0.25, 0));
        }

        [Fact]
        public void Head_moves_at_the_same_speed_on_every_tray_and_the_run_ends_once_the_tail_is_back()
        {
            var small = TrayCometMath.Perimeter(new Rect(0, 0, 200, 48), 8);
            var large = TrayCometMath.Perimeter(new Rect(0, 0, 900, 48), 8);
            var second = TimeSpan.FromSeconds(1);

            // the same distance along the rim in the same time, whatever the size
            Assert.Equal(TrayCometMath.Speed, TrayCometMath.Frame(second * 0.5, small).Value * small * 2, 6);
            Assert.Equal(TrayCometMath.Speed, TrayCometMath.Frame(second * 0.5, large).Value * large * 2, 6);

            // so the bigger tray simply takes longer to finish
            var smallRun = TimeSpan.FromSeconds((1 + TrayCometMath.Trail(small)) * small / TrayCometMath.Speed);
            var largeRun = TimeSpan.FromSeconds((1 + TrayCometMath.Trail(large)) * large / TrayCometMath.Speed);
            Assert.True(largeRun > smallRun);
            Assert.NotNull(TrayCometMath.Frame(largeRun - TimeSpan.FromMilliseconds(1), large));
            Assert.Null(TrayCometMath.Frame(largeRun + TimeSpan.FromMilliseconds(1), large));
        }

        [Fact]
        public void Trail_is_a_fixed_length_capped_on_small_trays()
        {
            Assert.Equal(TrayCometMath.TrailPx, TrayCometMath.Trail(2000) * 2000, 6);
            Assert.Equal(TrayCometMath.MaxTrailFraction, TrayCometMath.Trail(300));
        }

        [Fact]
        public void Comet_is_full_strength_on_its_lap_and_dark_either_side_of_it()
        {
            const double edge = 0.02, trail = 0.4;

            // mid-lap, nothing is cut: the head burns in full and the tail at its own strength
            Assert.Equal(1, TrayCometMath.Brightness(0.5, 0.5, trail, edge), 6);
            Assert.Equal(TrayCometMath.Intensity(0.4, 0.5, trail), TrayCometMath.Brightness(0.4, 0.5, trail, edge), 6);

            // leaving: the head is out of the start point at full strength, its tail behind the start is not drawn
            Assert.Equal(TrayCometMath.Intensity(0.1, 0.1, trail), TrayCometMath.Brightness(0.1, 0.1, trail, edge), 6);
            Assert.Equal(0, TrayCometMath.Brightness(0.95, 0.1, trail, edge));
            Assert.InRange(TrayCometMath.Brightness(edge / 2, 0.1, trail, edge), 0.01, 0.99);

            // arriving: the head is through the start point again and gone, the tail short of it still burns
            Assert.Equal(0, TrayCometMath.Brightness(0.1, 1.1, trail, edge));
            Assert.Equal(TrayCometMath.Intensity(0.95, 1.1, trail), TrayCometMath.Brightness(0.95, 1.1, trail, edge), 6);
            Assert.InRange(TrayCometMath.Brightness(1 - edge / 2, 1.1, trail, edge), 0.01, 0.99);
        }

        [Fact]
        public void Tail_tapers_and_stays_solid_while_the_head_runs_hot()
        {
            var head = TrayCometMath.Look(1);
            Assert.Equal((1.0, 1.0, 1.0), head);

            // halfway down the tail it is still opaque accent, a little narrower, with no head tint left
            var mid = TrayCometMath.Look(0.5);
            Assert.Equal(1, mid.Opacity);
            Assert.InRange(mid.Width, TrayCometMath.TailWidth, 0.99);
            Assert.True(mid.Heat < 0.1);

            var end = TrayCometMath.Look(0);
            Assert.Equal((TrayCometMath.TailWidth, 0.0, 0.0), end);
        }

        private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
    }
}
