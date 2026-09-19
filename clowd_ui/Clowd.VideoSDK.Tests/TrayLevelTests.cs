using Clowd.UI.Controls.Tray;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The peak-dBFS → 0..1 mapping the tray's level pills are drawn from. Small, but it is the seam
    /// where a missing source (<c>null</c> — the pill collapses to its 8 px minimum) and a silent one
    /// (0 — an empty pill) have to stay distinguishable, and where an out-of-range sample from the
    /// capturer must clamp rather than draw past the end of the track.
    /// </summary>
    public class TrayLevelTests
    {
        [Fact]
        public void Null_stays_null_so_an_absent_source_is_never_a_silent_one()
        {
            Assert.Null(TrayLevel.FromPeakDbfs(null));
        }

        [Theory]
        [InlineData(-60, 0)] // the floor
        [InlineData(0, 1)] // 0 dBFS is full
        [InlineData(-30, 0.5)]
        [InlineData(-45, 0.25)]
        [InlineData(-15, 0.75)]
        [InlineData(-6, 0.9)]
        public void The_minus_60_dB_floor_maps_linearly_onto_zero_to_one(double db, double expected)
        {
            Assert.Equal(expected, TrayLevel.FromPeakDbfs(db).Value, 10);
        }

        [Theory]
        [InlineData(-100)]
        [InlineData(-60.0001)]
        [InlineData(double.NegativeInfinity)] // a truly silent buffer reports -inf dBFS, not -60
        public void Anything_below_the_floor_clamps_to_empty(double db)
        {
            Assert.Equal(0, TrayLevel.FromPeakDbfs(db).Value);
        }

        [Theory]
        [InlineData(5)] // clipping
        [InlineData(0.0001)]
        public void Anything_above_zero_dBFS_clamps_to_full(double db)
        {
            Assert.Equal(1, TrayLevel.FromPeakDbfs(db).Value);
        }
    }
}
