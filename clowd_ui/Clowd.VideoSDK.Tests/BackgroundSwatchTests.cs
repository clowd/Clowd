using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.VideoSDK.Composition;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The mesh cells the timeline paints a wallpaper card with: every catalog row yields a full
    /// grid, the cells are the frame's own colors in the frame's own arrangement, themes of one
    /// style differ from each other, unknown ids describe what actually draws, and the cache
    /// hands out copies rather than its own array.
    /// </summary>
    public class BackgroundSwatchTests
    {
        private const int Cells = BackgroundSwatch.Columns * BackgroundSwatch.Rows;

        public static IEnumerable<object[]> AllSpecs() => BackgroundCatalogTests.AllSpecs();

        private static (int R, int G, int B) Rgb(uint cell)
            => ((int)(cell >> 16) & 0xFF, (int)(cell >> 8) & 0xFF, (int)cell & 0xFF);

        [Theory]
        [MemberData(nameof(AllSpecs))]
        public void Every_row_yields_a_full_grid(string style, string theme)
        {
            var grid = BackgroundSwatch.Grid(style, theme);
            Assert.NotNull(grid);
            Assert.Equal(Cells, grid.Length);
            Assert.All(grid, cell => Assert.True(cell <= 0xFFFFFF, "cell carries no alpha"));
        }

        /// <summary>A mesh of one color is not a mesh: every wallpaper in the library keeps
        /// several distinct cells through a 6x4 resample. A few is the bar rather than most,
        /// because a two-tone poster (Moving Blob) is mostly ground and legitimately resamples to
        /// a handful of colors — a card of it still reads as that wallpaper.</summary>
        [Theory]
        [MemberData(nameof(AllSpecs))]
        public void Cells_are_not_one_flat_color(string style, string theme)
        {
            var grid = BackgroundSwatch.Grid(style, theme);
            Assert.True(grid.Distinct().Count() >= 4,
                style + "/" + theme + " resampled to " + grid.Distinct().Count() + " distinct cells");
        }

        /// <summary>The frame the cells are taken from: 16:9 (the composition's shape, not the
        /// strip's) and a whole number of pixels per cell, so a block of this render is exactly
        /// what one cell covers.</summary>
        private const int FrameWidth = 192;

        private const int FrameHeight = 108;

        /// <summary>Every cell is a color out of the block it covers: no channel of it falls
        /// outside that block's own range. True of any average of those pixels and of nothing
        /// else, so it holds the grid to the frame's geometry — its orientation, its row-major
        /// order and its cell bounds — without restating how the average is taken.</summary>
        [Theory]
        [MemberData(nameof(AllSpecs))]
        public void Cells_lie_inside_the_block_they_cover(string style, string theme)
        {
            var pixels = BackgroundRendererTests.RenderPixels(style, theme, 0, FrameWidth, FrameHeight);
            var grid = BackgroundSwatch.Grid(style, theme);

            for (int row = 0; row < BackgroundSwatch.Rows; row++)
            {
                for (int column = 0; column < BackgroundSwatch.Columns; column++)
                {
                    var (min, max) = BlockRange(pixels, column, row);
                    var cell = Rgb(grid[row * BackgroundSwatch.Columns + column]);
                    var where = style + "/" + theme + " cell (" + column + "," + row + ")";
                    // a byte of rounding either way: the average is taken in linear light and
                    // encoded back, and an all-one-color block can land a step off its own value
                    Assert.True(cell.R >= min.R - 1 && cell.R <= max.R + 1, where + " red");
                    Assert.True(cell.G >= min.G - 1 && cell.G <= max.G + 1, where + " green");
                    Assert.True(cell.B >= min.B - 1 && cell.B <= max.B + 1, where + " blue");
                }
            }
        }

        /// <summary>
        /// On the smooth authored artwork the cell is not merely inside its block's range but
        /// near its mean — loosely, since the two means differ by construction: this one is taken
        /// over stored bytes and the swatch's over linear light. The generative styles are left
        /// out entirely: their blocks straddle hard wave edges, where that difference is the whole
        /// point (a byte average of two saturated colors is the muddier one) and runs wider still.
        /// </summary>
        [Theory]
        [InlineData("big-sur", "default")]
        [InlineData("big-sur", "amber")]
        [InlineData("monterey", "dark")]
        [InlineData("gradient", "sunrise")]
        [InlineData("gradient", "midnight-bloom")]
        public void Cells_sit_at_the_mean_of_smooth_artwork(string style, string theme)
        {
            var pixels = BackgroundRendererTests.RenderPixels(style, theme, 0, FrameWidth, FrameHeight);
            var grid = BackgroundSwatch.Grid(style, theme);

            int cellWidth = FrameWidth / BackgroundSwatch.Columns;
            int cellHeight = FrameHeight / BackgroundSwatch.Rows;
            for (int row = 0; row < BackgroundSwatch.Rows; row++)
            {
                for (int column = 0; column < BackgroundSwatch.Columns; column++)
                {
                    long b = 0, g = 0, r = 0;
                    for (int y = row * cellHeight; y < (row + 1) * cellHeight; y++)
                    {
                        for (int x = column * cellWidth; x < (column + 1) * cellWidth; x++)
                        {
                            int i = (y * FrameWidth + x) * 4;
                            b += pixels[i];
                            g += pixels[i + 1];
                            r += pixels[i + 2];
                        }
                    }

                    int count = cellWidth * cellHeight;
                    var cell = Rgb(grid[row * BackgroundSwatch.Columns + column]);
                    var where = style + "/" + theme + " cell (" + column + "," + row + ")";
                    Assert.True(Math.Abs(cell.R - r / count) <= 48, where + " red");
                    Assert.True(Math.Abs(cell.G - g / count) <= 48, where + " green");
                    Assert.True(Math.Abs(cell.B - b / count) <= 48, where + " blue");
                }
            }
        }

        /// <summary>The per-channel range of the block one cell covers.</summary>
        private static ((int R, int G, int B) Min, (int R, int G, int B) Max) BlockRange(byte[] pixels,
            int column, int row)
        {
            int cellWidth = FrameWidth / BackgroundSwatch.Columns;
            int cellHeight = FrameHeight / BackgroundSwatch.Rows;
            (int R, int G, int B) min = (255, 255, 255);
            (int R, int G, int B) max = (0, 0, 0);
            for (int y = row * cellHeight; y < (row + 1) * cellHeight; y++)
            {
                for (int x = column * cellWidth; x < (column + 1) * cellWidth; x++)
                {
                    int i = (y * FrameWidth + x) * 4;
                    min = (Math.Min(min.R, pixels[i + 2]), Math.Min(min.G, pixels[i + 1]), Math.Min(min.B, pixels[i]));
                    max = (Math.Max(max.R, pixels[i + 2]), Math.Max(max.G, pixels[i + 1]), Math.Max(max.B, pixels[i]));
                }
            }
            return (min, max);
        }

        /// <summary>Two themes of one style are two different meshes — the point of drawing the
        /// card's body from the artwork at all.</summary>
        [Theory]
        [InlineData("big-sur", "default", "teal")]
        [InlineData("monterey", "light", "dark")]
        [InlineData("gradient", "sunrise", "abyss")]
        [InlineData("layered-waves", "source", "midnight")]
        [InlineData("moving-blob", "ember", "forest")]
        public void Themes_of_one_style_differ(string style, string first, string second)
            => Assert.NotEqual(BackgroundSwatch.Grid(style, first), BackgroundSwatch.Grid(style, second));

        /// <summary>An id this build does not know describes the wallpaper it actually draws, by
        /// the catalog's own resolve rules — the card never shows one style's colors under
        /// another's name.</summary>
        [Fact]
        public void Unknown_ids_resolve_the_way_the_catalog_does()
        {
            Assert.Equal(BackgroundSwatch.Grid(BackgroundCatalog.DefaultStyle, null),
                BackgroundSwatch.Grid("no-such-style", "no-such-theme"));
            Assert.Equal(BackgroundSwatch.Grid("big-sur", "default"),
                BackgroundSwatch.Grid("big-sur", "no-such-theme"));
            Assert.Equal(BackgroundSwatch.Grid("BIG-SUR", "TEAL"), BackgroundSwatch.Grid("big-sur", "teal"));
        }

        /// <summary>The cache is asked for the same grid on every repaint of every card; it hands
        /// out a copy each time, so a caller that keeps or edits one cannot change what the next
        /// card draws.</summary>
        [Fact]
        public void Grid_hands_out_copies()
        {
            var first = BackgroundSwatch.Grid("gradient", "orchid");
            var second = BackgroundSwatch.Grid("gradient", "orchid");
            Assert.Equal(first, second);
            Assert.NotSame(first, second);

            first[0] = 0x123456;
            Assert.Equal(second, BackgroundSwatch.Grid("gradient", "orchid"));
        }
    }
}
