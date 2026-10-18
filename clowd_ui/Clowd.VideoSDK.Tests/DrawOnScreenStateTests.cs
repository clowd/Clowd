using Clowd.UI.DrawOnScreen;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The draw-on-screen mode rules: tool, click-through and hide interact in ways that are easy to
    /// break (hidden ink must never swallow clicks; picking a tool must always resume drawing).
    /// </summary>
    public class DrawOnScreenStateTests
    {
        private static readonly DrawOnScreenState Drawing = DrawOnScreenState.Initial.PickTool(ToolType.Brush);
        private static readonly DrawOnScreenState Through = Drawing with { ClickThrough = true };
        private static readonly DrawOnScreenState Hidden = Drawing with { ClickThrough = true, Hidden = true };

        [Fact]
        public void Initial_is_red_medium_brush_in_click_through()
        {
            Assert.Equal(new DrawOnScreenState(ToolType.Brush, true, false, 0, 1), DrawOnScreenState.Initial);
        }

        [Fact]
        public void Restored_is_initial_with_the_remembered_colour_and_size()
        {
            Assert.Equal(DrawOnScreenState.Initial, DrawOnScreenState.Restored(null, null));
            Assert.Equal(DrawOnScreenState.Initial with { ColorIndex = 4, SizeIndex = 3 }, DrawOnScreenState.Restored(4, 3));
        }

        [Fact]
        public void Restored_clamps_out_of_range_indices()
        {
            var s = DrawOnScreenState.Restored(99, -1);
            Assert.Equal(DrawPalette.Colors.Count - 1, s.ColorIndex);
            Assert.Equal(0, s.SizeIndex);
        }

        [Fact]
        public void PickTool_resumes_drawing_from_any_mode()
        {
            foreach (var from in new[] { Drawing, Through, Hidden })
            {
                var s = from.PickTool(ToolType.Rectangle);
                Assert.Equal(ToolType.Rectangle, s.Tool);
                Assert.False(s.ClickThrough);
                Assert.False(s.Hidden);
            }
        }

        [Fact]
        public void PickTool_keeps_colour_and_size()
        {
            var s = (Drawing with { ColorIndex = 4, SizeIndex = 3 }).PickTool(ToolType.Eraser);
            Assert.Equal(4, s.ColorIndex);
            Assert.Equal(3, s.SizeIndex);
        }

        [Fact]
        public void ToggleClickThrough_on_keeps_the_ink_visible()
        {
            var s = Drawing.ToggleClickThrough();
            Assert.True(s.ClickThrough);
            Assert.False(s.Hidden);
            Assert.Equal(Drawing.Tool, s.Tool);
        }

        [Fact]
        public void ToggleClickThrough_off_also_unhides()
        {
            Assert.Equal(Drawing, Through.ToggleClickThrough());
            Assert.Equal(Drawing, Hidden.ToggleClickThrough());
        }

        [Fact]
        public void StopDrawing_drops_to_click_through()
        {
            Assert.Equal(Through, Drawing.StopDrawing());
            Assert.Equal(Through, Through.StopDrawing());
            Assert.Equal(Hidden, Hidden.StopDrawing());
        }

        [Fact]
        public void Hiding_forces_click_through()
        {
            Assert.Equal(Hidden, Drawing.ToggleHide());
            Assert.Equal(Hidden, Through.ToggleHide());
        }

        [Fact]
        public void Unhiding_resumes_the_tool()
        {
            Assert.Equal(Drawing, Hidden.ToggleHide());
        }

        [Fact]
        public void AfterUndoOrClear_shows_the_ink_and_goes_click_through()
        {
            Assert.Equal(Through, Drawing.AfterUndoOrClear());
            Assert.Equal(Through, Through.AfterUndoOrClear());
            Assert.Equal(Through, Hidden.AfterUndoOrClear());
            Assert.Equal(ToolType.Arrow, Drawing.PickTool(ToolType.Arrow).AfterUndoOrClear().Tool);
        }

        [Theory]
        [InlineData(-5, 0)]
        [InlineData(0, 0)]
        [InlineData(5, 5)]
        [InlineData(7, 7)]
        [InlineData(8, 7)]
        [InlineData(100, 7)]
        public void SelectColor_clamps(int index, int expected)
        {
            var s = Drawing.SelectColor(index);
            Assert.Equal(expected, s.ColorIndex);
            Assert.Equal(Drawing with { ColorIndex = expected }, s);
        }

        [Theory]
        [InlineData(-1, 0)]
        [InlineData(2, 2)]
        [InlineData(3, 3)]
        [InlineData(4, 3)]
        public void SelectSize_clamps(int index, int expected)
        {
            var s = Drawing.SelectSize(index);
            Assert.Equal(expected, s.SizeIndex);
            Assert.Equal(Drawing with { SizeIndex = expected }, s);
        }

        [Fact]
        public void Colour_or_size_takes_up_the_brush_from_click_through_hidden_select_or_eraser()
        {
            var eraser = Drawing.PickTool(ToolType.Eraser);
            var select = Drawing.PickTool(ToolType.Pointer);
            var throughArrow = Drawing.PickTool(ToolType.Arrow) with { ClickThrough = true };
            foreach (var from in new[] { Through, Hidden, eraser, select, throughArrow })
            {
                foreach (var s in new[] { from.SelectColor(3), from.SelectSize(2) })
                {
                    Assert.Equal(ToolType.Brush, s.Tool);
                    Assert.False(s.ClickThrough);
                    Assert.False(s.Hidden);
                }
            }
        }

        [Fact]
        public void Colour_or_size_keeps_a_drawing_tool_in_hand()
        {
            var text = Drawing.PickTool(ToolType.Text);
            Assert.Equal(text with { ColorIndex = 4 }, text.SelectColor(4));
            Assert.Equal(text with { SizeIndex = 3 }, text.SelectSize(3));
        }

        [Fact]
        public void Restored_carries_the_remembered_keys_button()
        {
            Assert.False(DrawOnScreenState.Restored(null, null).ShowKeys);
            Assert.True(DrawOnScreenState.Restored(null, null, showKeys: true).ShowKeys);
        }

        [Fact]
        public void Keys_toggle_alone_and_survive_every_other_transition()
        {
            foreach (var start in new[] { Drawing, Through, Hidden })
            {
                var on = start.ToggleKeys();
                Assert.Equal(start with { ShowKeys = true }, on);
                Assert.Equal(start, on.ToggleKeys());

                Assert.True(on.PickTool(ToolType.Arrow).ShowKeys);
                Assert.True(on.ToggleClickThrough().ShowKeys);
                Assert.True(on.ToggleHide().ShowKeys);
                Assert.True(on.AfterUndoOrClear().ShowKeys);
                Assert.True(on.SelectColor(2).ShowKeys);
            }
        }

        [Fact]
        public void IsToolLit_only_for_the_picked_tool_while_drawing()
        {
            Assert.True(Drawing.IsToolLit(ToolType.Brush));
            Assert.False(Drawing.IsToolLit(ToolType.Arrow));
            Assert.False(Through.IsToolLit(ToolType.Brush));
            Assert.False(Hidden.IsToolLit(ToolType.Brush));
            Assert.False((Drawing with { Hidden = true }).IsToolLit(ToolType.Brush));
        }

        [Fact]
        public void IsDrawing_only_while_the_canvases_take_the_mouse()
        {
            Assert.True(Drawing.IsDrawing);
            Assert.False(Through.IsDrawing);
            Assert.False(Hidden.IsDrawing);
            Assert.False(DrawOnScreenState.Initial.IsDrawing);
        }

        [Fact]
        public void Hotkeys_are_live_while_the_session_is_focused_or_a_tool_is_in_hand()
        {
            Assert.True(Drawing.AreHotkeysLive(focused: false));
            Assert.True(Through.AreHotkeysLive(focused: true));
            Assert.True(Hidden.AreHotkeysLive(focused: true));
            Assert.False(Through.AreHotkeysLive(focused: false));
            Assert.False(Hidden.AreHotkeysLive(focused: false));
        }
    }
}
