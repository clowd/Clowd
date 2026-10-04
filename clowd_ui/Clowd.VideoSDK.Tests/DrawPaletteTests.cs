using System.Linq;
using Clowd.UI.DrawOnScreen;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The draw-on-screen toolbar's tables, pinned to the approved mockup.
    /// </summary>
    public class DrawPaletteTests
    {
        [Fact]
        public void Colors_are_the_mockup_swatches_in_order()
        {
            var hex = DrawPalette.Colors.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}").ToArray();
            Assert.Equal(new[] { "#DF2828", "#E27F38", "#E2CF38", "#86E238", "#359EC9", "#7855D4", "#F4F4F4", "#1E1E1E" }, hex);
            Assert.All(DrawPalette.Colors, c => Assert.Equal(255, c.A));
        }

        [Fact]
        public void ColorKeys_match_the_colors()
        {
            Assert.Equal(new[]
            {
                "Draw_Color_Red", "Draw_Color_Orange", "Draw_Color_Yellow", "Draw_Color_Green",
                "Draw_Color_Blue", "Draw_Color_Purple", "Draw_Color_White", "Draw_Color_Black",
            }, DrawPalette.ColorKeys);
        }

        [Fact]
        public void Sizes_table()
        {
            Assert.Equal(new[]
            {
                new DrawPalette.DrawSize("Draw_Size_Thin", 3, 6, 18),
                new DrawPalette.DrawSize("Draw_Size_Medium", 9, 10, 34),
                new DrawPalette.DrawSize("Draw_Size_Thick", 15, 14, 50),
                new DrawPalette.DrawSize("Draw_Size_Huge", 21, 18, 66),
            }, DrawPalette.Sizes);
        }

        [Fact]
        public void Brush_line_width_is_half_the_stroke()
        {
            // GraphicBrush's ink scales from this (see DrawPalette.LineWidthFor); halving keeps a brush line near the shapes' weight
            var widths = DrawPalette.Sizes.Select(s => DrawPalette.LineWidthFor(ToolType.Brush, s)).ToArray();
            Assert.Equal(new[] { 1.5, 4.5, 7.5, 10.5 }, widths);
        }

        [Theory]
        [InlineData(ToolType.Arrow)]
        [InlineData(ToolType.Line)]
        [InlineData(ToolType.Rectangle)]
        [InlineData(ToolType.Ellipse)]
        public void Shape_line_width_is_the_stroke(ToolType tool)
        {
            // shapes stroke with a pen LineWidth wide
            var widths = DrawPalette.Sizes.Select(s => DrawPalette.LineWidthFor(tool, s)).ToArray();
            Assert.Equal(new[] { 3.0, 9, 15, 21 }, widths);
        }

        [Fact]
        public void Tools_in_toolbar_order_with_eraser_last()
        {
            Assert.Equal(new[]
            {
                ToolType.Pointer, ToolType.Brush, ToolType.Arrow, ToolType.Line, ToolType.Rectangle,
                ToolType.Ellipse, ToolType.Text, ToolType.Eraser,
            }, DrawPalette.Tools);
        }

        [Fact]
        public void Tool_keys()
        {
            Assert.Equal("Draw_Tool_Brush", DrawPalette.ToolKey(ToolType.Brush));
            Assert.Equal("Draw_Tool_Eraser", DrawPalette.ToolKey(ToolType.Eraser));
            Assert.Equal("Draw_Tool_Pointer", DrawPalette.ToolKey(ToolType.Pointer));
            Assert.Equal(
                new[] { "IconDrawSelect", "IconToolBrush", "IconToolArrow", "IconToolLine", "IconToolRectangle", "IconToolEllipse", "IconToolText", "IconToolEraser" },
                DrawPalette.Tools.Select(DrawPalette.ToolIconKey));
        }

        [Fact]
        public void Defaults()
        {
            Assert.Equal(0, DrawPalette.DefaultColorIndex);
            Assert.Equal(1, DrawPalette.DefaultSizeIndex);
            Assert.Equal(ToolType.Brush, DrawPalette.DefaultTool);
        }
    }
}
