using Clowd.UI.Controls.Tray;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The start/centre/end arithmetic behind <c>TrayZonePanel</c>. Pure numbers, so no Avalonia
    /// platform is needed (see TrayGlyphsTests for why this project has none).
    /// </summary>
    public class TrayZoneLayoutTests
    {
        [Fact]
        public void Equal_groups_put_the_centre_in_the_middle()
        {
            var (start, center, end) = TrayZoneLayout.Arrange(total: 300, startLen: 50, centerLen: 100, endLen: 50);

            Assert.Equal(0, start);
            Assert.Equal(100, center); // 50 + (200 − 100) / 2
            Assert.Equal(250, end);
        }

        [Fact]
        public void Centre_is_centred_in_the_free_space_not_the_whole_axis()
        {
            // a long start group and a short end group: the centre group sits midway between them,
            // which is right of the axis midpoint
            var (_, center, end) = TrayZoneLayout.Arrange(total: 400, startLen: 200, centerLen: 60, endLen: 40);

            Assert.Equal(250, center); // 200 + (160 − 60) / 2
            Assert.Equal(360, end);
        }

        [Fact]
        public void Overflow_never_overlaps_the_groups()
        {
            // 80 + 60 + 40 = 180 in a 150 axis: the centre stays right after the start group and the end
            // group is pushed past the trailing edge rather than painted over the centre
            var (start, center, end) = TrayZoneLayout.Arrange(total: 150, startLen: 80, centerLen: 60, endLen: 40);

            Assert.Equal(0, start);
            Assert.Equal(80, center);
            Assert.Equal(140, end);
        }

        [Fact]
        public void Exact_fit_packs_the_groups_end_to_end()
        {
            var (_, center, end) = TrayZoneLayout.Arrange(total: 180, startLen: 80, centerLen: 60, endLen: 40);

            Assert.Equal(80, center);
            Assert.Equal(140, end);
        }

        [Fact]
        public void Zero_length_centre_sits_at_the_midpoint_of_the_gap()
        {
            var (_, center, end) = TrayZoneLayout.Arrange(total: 200, startLen: 40, centerLen: 0, endLen: 60);

            Assert.Equal(90, center); // 40 + 100 / 2
            Assert.Equal(140, end);
        }

        [Fact]
        public void Empty_axis_collapses_everything_to_the_start()
        {
            var (start, center, end) = TrayZoneLayout.Arrange(total: 0, startLen: 0, centerLen: 0, endLen: 0);

            Assert.Equal(0, start);
            Assert.Equal(0, center);
            Assert.Equal(0, end);
        }
    }
}
