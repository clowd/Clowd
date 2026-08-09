using System;
using System.Collections.Generic;
using Clowd.UI.VideoEditor;
using Xunit;

namespace Clowd.Video.Tests
{
    // TimelineMath is the pure math behind TimelineControl (Clowd.Ui exposes internals to this
    // project via InternalsVisibleTo). No Avalonia runtime is needed: the class only uses
    // primitives.
    public class TimelineMathTests
    {
        private const double Tol = TimelineMath.HitTolerance;

        // ---------------------------------------------------------------- time <-> pixel mapping

        [Theory]
        [InlineData(0, 10_000, 10, 100, 10)]        // start of media -> track origin
        [InlineData(10_000, 10_000, 10, 100, 110)]  // end of media -> track right edge
        [InlineData(5_000, 10_000, 10, 100, 60)]    // midpoint
        public void TimeToX_maps_linearly(long ms, long durationMs, double trackX, double trackWidth, double expected)
        {
            Assert.Equal(expected, TimelineMath.TimeToX(ms, durationMs, trackX, trackWidth), 6);
        }

        [Fact]
        public void TimeToX_zero_duration_collapses_to_track_origin()
        {
            Assert.Equal(10, TimelineMath.TimeToX(5_000, 0, 10, 100));
            Assert.Equal(10, TimelineMath.TimeToX(5_000, -1, 10, 100));
        }

        [Fact]
        public void TimeToX_zero_width_collapses_to_track_origin()
        {
            Assert.Equal(10, TimelineMath.TimeToX(5_000, 10_000, 10, 0));
        }

        [Theory]
        [InlineData(10, 0)]       // left edge
        [InlineData(110, 10_000)] // right edge
        [InlineData(60, 5_000)]   // midpoint
        [InlineData(-50, 0)]      // past left edge clamps
        [InlineData(500, 10_000)] // past right edge clamps
        public void XToMs_maps_and_clamps(double x, long expected)
        {
            Assert.Equal(expected, TimelineMath.XToMs(x, 10_000, 10, 100));
        }

        [Fact]
        public void XToMs_zero_duration_or_width_returns_zero()
        {
            Assert.Equal(0, TimelineMath.XToMs(60, 0, 10, 100));
            Assert.Equal(0, TimelineMath.XToMs(60, 10_000, 10, 0));
        }

        [Fact]
        public void MsAtX_is_unclamped()
        {
            Assert.Equal(-5_000, TimelineMath.MsAtX(-40, 10_000, 10, 100));
            Assert.Equal(20_000, TimelineMath.MsAtX(210, 10_000, 10, 100));
        }

        [Fact]
        public void Roundtrip_time_to_x_to_time_is_stable()
        {
            const long duration = 63_137;
            for (long ms = 0; ms <= duration; ms += 1_777)
            {
                var x = TimelineMath.TimeToX(ms, duration, 12, 850);
                var back = TimelineMath.XToMs(x, duration, 12, 850);
                Assert.InRange(back, ms - 40, ms + 40); // sub-pixel quantization only
            }
        }

        // ---------------------------------------------------------------- trim-end sentinel

        [Theory]
        [InlineData(0, 10_000, 10_000)]      // 0 sentinel = to-end
        [InlineData(-5, 10_000, 10_000)]     // negative behaves like the sentinel
        [InlineData(8_000, 10_000, 8_000)]   // explicit end kept
        [InlineData(99_000, 10_000, 10_000)] // stale value beyond media clamps
        public void EffectiveTrimEnd_resolves_sentinel(long trimEndMs, long durationMs, long expected)
        {
            Assert.Equal(expected, TimelineMath.EffectiveTrimEnd(trimEndMs, durationMs));
        }

        // ---------------------------------------------------------------- tick step selection

        [Theory]
        [InlineData(10_000, 1000, 1_000)]    // 100 px/s -> 1s ticks
        [InlineData(60_000, 1000, 5_000)]    // ~16.7 px/s -> 5s ticks
        [InlineData(120_000, 1000, 10_000)]  // ~8.3 px/s -> 10s ticks
        [InlineData(400_000, 1000, 30_000)]  // 30s ticks
        [InlineData(900_000, 1000, 60_000)]  // 60s ticks
        public void PickTickStepMs_uses_spec_steps(long durationMs, double trackWidth, long expected)
        {
            Assert.Equal(expected, TimelineMath.PickTickStepMs(durationMs, trackWidth));
        }

        [Fact]
        public void PickTickStepMs_zero_duration_or_width_returns_zero()
        {
            Assert.Equal(0, TimelineMath.PickTickStepMs(0, 1000));
            Assert.Equal(0, TimelineMath.PickTickStepMs(10_000, 0));
        }

        [Fact]
        public void PickTickStepMs_very_long_media_doubles_beyond_a_minute()
        {
            // 4 hours across 500 px: 60s ticks would be ~2 px apart; the step must grow.
            var step = TimelineMath.PickTickStepMs(4 * 3_600_000L, 500);
            Assert.True(step > 60_000);
            Assert.Equal(0, step % 60_000);
            Assert.True(500.0 * step / (4 * 3_600_000L) >= 60);
        }

        [Theory]
        [InlineData(0, "0:00")]
        [InlineData(65_000, "1:05")]
        [InlineData(3_600_000, "1:00:00")]
        public void FormatTick_formats_mmss_and_hours(long ms, string expected)
        {
            Assert.Equal(expected, TimelineMath.FormatTick(ms));
        }

        // ---------------------------------------------------------------- hit testing

        private static readonly List<(double StartX, double EndX)> NoCuts = new();

        [Fact]
        public void HitTest_playhead_wins_over_everything()
        {
            // Playhead, trim handle and a cut edge all within tolerance of x=100.
            var cuts = new List<(double, double)> { (98, 160) };
            var hit = TimelineMath.HitTest(100, 102, 99, 400, cuts, Tol);
            Assert.Equal(TimelineHitKind.Playhead, hit.Kind);
        }

        [Fact]
        public void HitTest_trim_handles_beat_cut_edges()
        {
            var cuts = new List<(double, double)> { (100, 160) };
            var hit = TimelineMath.HitTest(101, -500, 99, 400, cuts, Tol);
            Assert.Equal(TimelineHitKind.TrimStart, hit.Kind);
        }

        [Fact]
        public void HitTest_nearest_trim_handle_wins()
        {
            // Both handles within tolerance (they can nearly touch); nearest one wins.
            Assert.Equal(TimelineHitKind.TrimStart, TimelineMath.HitTest(101, -500, 100, 106, NoCuts, Tol).Kind);
            Assert.Equal(TimelineHitKind.TrimEnd, TimelineMath.HitTest(105, -500, 100, 106, NoCuts, Tol).Kind);
        }

        [Fact]
        public void HitTest_cut_edges_and_body()
        {
            var cuts = new List<(double, double)> { (100, 160), (300, 340) };

            var start = TimelineMath.HitTest(101, -500, -500, 900, cuts, Tol);
            Assert.Equal(TimelineHitKind.CutStart, start.Kind);
            Assert.Equal(0, start.CutIndex);

            var end = TimelineMath.HitTest(338, -500, -500, 900, cuts, Tol);
            Assert.Equal(TimelineHitKind.CutEnd, end.Kind);
            Assert.Equal(1, end.CutIndex);

            var body = TimelineMath.HitTest(130, -500, -500, 900, cuts, Tol);
            Assert.Equal(TimelineHitKind.CutBody, body.Kind);
            Assert.Equal(0, body.CutIndex);
        }

        [Fact]
        public void HitTest_nearest_cut_edge_wins_between_adjacent_cuts()
        {
            // Two cuts whose facing edges are 8 px apart; x sits between them, nearer the second.
            var cuts = new List<(double, double)> { (100, 150), (158, 200) };
            var hit = TimelineMath.HitTest(155, -500, -500, 900, cuts, Tol);
            Assert.Equal(TimelineHitKind.CutStart, hit.Kind);
            Assert.Equal(1, hit.CutIndex);
        }

        [Fact]
        public void HitTest_outside_everything_is_track()
        {
            var cuts = new List<(double, double)> { (100, 160) };
            var hit = TimelineMath.HitTest(500, 50, 20, 900, cuts, Tol);
            Assert.Equal(TimelineHitKind.Track, hit.Kind);
            Assert.Equal(-1, hit.CutIndex);
        }

        [Fact]
        public void HitTest_nan_handles_never_match()
        {
            // No document attached: trim x-positions are NaN, cuts null.
            var hit = TimelineMath.HitTest(100, double.NaN, double.NaN, double.NaN, null, Tol);
            Assert.Equal(TimelineHitKind.Track, hit.Kind);
        }

        [Fact]
        public void HitTest_tolerance_boundary_is_inclusive()
        {
            Assert.Equal(TimelineHitKind.Playhead, TimelineMath.HitTest(100 + Tol, 100, double.NaN, double.NaN, null, Tol).Kind);
            Assert.Equal(TimelineHitKind.Track, TimelineMath.HitTest(100 + Tol + 0.001, 100, double.NaN, double.NaN, null, Tol).Kind);
        }
    }
}
