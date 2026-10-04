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
        public void CanvasRightClick_drops_to_click_through()
        {
            Assert.Equal(Through, Drawing.CanvasRightClick());
            Assert.Equal(Through, Through.CanvasRightClick());
            Assert.Equal(Hidden, Hidden.CanvasRightClick());
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
        public void Colour_or_size_takes_up_the_brush_from_click_through_hidden_or_eraser()
        {
            var eraser = Drawing.PickTool(ToolType.Eraser);
            var throughArrow = Drawing.PickTool(ToolType.Arrow) with { ClickThrough = true };
            foreach (var from in new[] { Through, Hidden, eraser, throughArrow })
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
        public void IsToolLit_only_for_the_picked_tool_while_drawing()
        {
            Assert.True(Drawing.IsToolLit(ToolType.Brush));
            Assert.False(Drawing.IsToolLit(ToolType.Arrow));
            Assert.False(Through.IsToolLit(ToolType.Brush));
            Assert.False(Hidden.IsToolLit(ToolType.Brush));
            Assert.False((Drawing with { Hidden = true }).IsToolLit(ToolType.Brush));
        }
    }
}
