using Clowd.UI;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The share strip's enable rules, pinned combination by combination. Two owners drive the Hide
    /// tile — the permanent obscure retirement and the transient resize mode — and the whole point of
    /// <see cref="ShareStripRules"/> is that they are ANDed in one place: a resize that ends after the
    /// helper retired the effect must not hand the user back a Hide tile that sends obscure commands
    /// to a helper that has said it cannot honour them.
    /// </summary>
    public class ShareStripRulesTests
    {
        [Theory]
        [InlineData(true, false, false, true)]   // the only pressable combination
        [InlineData(true, true, false, false)]   // resize mode holds the obscure state
        [InlineData(true, false, true, false)]   // a move is in flight
        [InlineData(true, true, true, false)]
        [InlineData(false, false, false, false)] // retired: the effect is dead for the process
        [InlineData(false, true, false, false)]
        [InlineData(false, false, true, false)]
        [InlineData(false, true, true, false)]
        public void Hide_is_pressable_only_when_available_and_no_resize_is_running_or_pending(
            bool obscureAvailable, bool resizeActive, bool resizeBusy, bool expected)
        {
            Assert.Equal(expected, ShareStripRules.HideEnabled(obscureAvailable, resizeActive, resizeBusy));
        }

        [Fact]
        public void Retirement_survives_the_resize_mode_ending()
        {
            // the corrective / exit call is SetResizeState(false, false); with the tile retired the
            // AND must still say no
            Assert.False(ShareStripRules.HideEnabled(false, false, false));
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)] // one move in flight: a second press would put a second move on the wire
        public void Resize_is_locked_only_while_a_move_is_in_flight(bool resizeBusy, bool expected)
        {
            Assert.Equal(expected, ShareStripRules.ResizeEnabled(resizeBusy));
        }

        [Fact]
        public void Hide_tooltip_names_the_verb_a_press_performs()
        {
            Assert.Equal("Hide the shared region", ShareStripRules.HideToolTip(false));
            Assert.Equal("Show the shared region", ShareStripRules.HideToolTip(true));
        }

        [Fact]
        public void Resize_tooltip_becomes_apply_while_the_mode_is_on()
        {
            Assert.Equal("Move or resize the region", ShareStripRules.ResizeToolTip(false));
            Assert.Equal("Apply the new region", ShareStripRules.ResizeToolTip(true));
        }
    }
}
