using System;
using System.Collections.Generic;
using System.IO;
using Prisc.UI.VideoEditor;
using Prisc.UI.VideoEditor.Inspector;
using Prisc.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The inspector's shape-picker tiles (<see cref="ShapeKindTile"/>, which
    /// <see cref="ShapeKindPreview"/> draws through): the tile's own rules, and a sheet of every
    /// kind in a handful of styles drawn exactly as the picker draws them, on the tile's plate, at
    /// 1x, 2x and an enlarged 4x, written to the temp folder for a person to look at. Skia only, no
    /// Avalonia: the control does nothing but lay the plate down and hand the canvas over.
    /// </summary>
    public class ShapeKindTileTests
    {
        // the tile's size in the picker (InspectorPanel.axaml's ShapeKindPreview)
        private const double TileW = 28, TileH = 22;

        private static readonly SKColor Plate = new SKColor(0x74, 0x74, 0x74);

        /// <summary>One sheet row: a name and the picker's bound style.</summary>
        private sealed record Style(string Name, string Stroke, double Width, string FillColor, bool Fill,
            ShapeDash Dash = ShapeDash.Solid, double Corner = 0);

        private static readonly Style[] Styles =
        {
            new("new (red outline)", ShapeContent.DefaultStrokeColor, 6, ShapeContent.DefaultFillColor, false),
            new("fill, dashed, r24", ShapeContent.DefaultStrokeColor, 6, ShapeContent.DefaultFillColor, true,
                ShapeDash.Dashed, 24),
            new("fill only", ShapeContent.DefaultStrokeColor, 0, "#FF2E7D32", true),
            new("nothing drawn", ShapeContent.DefaultStrokeColor, 0, ShapeContent.DefaultFillColor, false),
            new("black, dotted", "#FF000000", 6, ShapeContent.DefaultFillColor, false, ShapeDash.Dotted),
            new("white, dark fill", "#FFFFFFFF", 12, "#B0000000", true, Corner: 200),
        };

        private static ShapeContent TileOf(ShapeKind kind, Style style) =>
            ShapeKindTile.Shape(kind, style.Stroke, style.Width, style.FillColor, style.Fill, style.Dash,
                style.Corner, ShapeContent.DefaultPolygonSides, ShapeContent.DefaultStarPoints,
                ShapeContent.DefaultStarInnerRatio);

        [Fact]
        public void A_visible_outline_is_drawn_two_tile_pixels_wide_whatever_its_width()
        {
            foreach (var width in new[] { 1.0, 6, 40, 200 })
            {
                var tile = ShapeKindTile.Shape(ShapeKind.Rectangle, "#FFFF0000", width, "#59FF0000", false,
                    ShapeDash.Solid, 0, 6, 5, 0.5);
                Assert.Equal(ShapeKindTile.StrokeWidth, tile.StrokeWidth);
                Assert.Equal(2, tile.StrokeWidth * ShapeKindTile.PixelScale);
            }
        }

        [Fact]
        public void An_outline_that_is_off_leaves_the_fill_alone()
        {
            var noWidth = ShapeKindTile.Shape(ShapeKind.Star, "#FFFF0000", 0, "#FF00FF00", true, ShapeDash.Solid,
                0, 6, 5, 0.5);
            Assert.Equal(0, noWidth.StrokeWidth);

            var clear = ShapeKindTile.Shape(ShapeKind.Star, "#00FF0000", 6, "#FF00FF00", true, ShapeDash.Solid,
                0, 6, 5, 0.5);
            Assert.Equal(0, clear.StrokeWidth);
        }

        [Fact]
        public void A_tile_that_would_draw_nothing_draws_a_white_outline()
        {
            var nothing = ShapeKindTile.Shape(ShapeKind.Ellipse, "#FFFF0000", 0, "#00FF0000", true,
                ShapeDash.Solid, 0, 6, 5, 0.5);
            Assert.Equal("#FFFFFFFF", nothing.StrokeColor);
            Assert.Equal(ShapeKindTile.StrokeWidth, nothing.StrokeWidth);

            var clearLine = ShapeKindTile.Shape(ShapeKind.Arrow, "#00FF0000", 6, "#00FF0000", false,
                ShapeDash.Solid, 0, 6, 5, 0.5);
            Assert.Equal("#FFFFFFFF", clearLine.StrokeColor);
        }

        [Fact]
        public void Corner_radii_shrink_to_the_tile_and_dials_round_to_the_model()
        {
            var tile = ShapeKindTile.Shape(ShapeKind.Polygon, "#FFFF0000", 6, "#59FF0000", false,
                ShapeDash.Solid, 50, 7.4, 4.6, 0.3);
            Assert.Equal(4, tile.CornerRadius, 9);
            Assert.Equal(7, tile.PolygonSides);
            Assert.Equal(5, tile.StarPoints);
            Assert.Equal(0.3, tile.StarInnerRatio);

            var huge = ShapeKindTile.Shape(ShapeKind.Rectangle, "#FFFF0000", 6, "#59FF0000", false,
                ShapeDash.Solid, 1000, 6, 5, 0.5);
            Assert.Equal(8, huge.CornerRadius);
        }

        [Fact]
        public void A_closed_kind_draws_in_a_centered_square_and_a_line_across_the_inset_plate()
        {
            var square = ShapeKindTile.Box(ShapeKind.Star, TileW, TileH);
            Assert.Equal(16, square.Width, 3);
            Assert.Equal(16, square.Height, 3);
            Assert.Equal(TileW / 2, square.MidX, 3);
            Assert.Equal(TileH / 2, square.MidY, 3);

            var line = ShapeKindTile.Box(ShapeKind.Arrow, TileW, TileH);
            Assert.Equal(SKRect.Create(3, 3, 22, 16), line);

            var arrow = ShapeKindTile.Shape(ShapeKind.Arrow, "#FFFF0000", 6, "#59FF0000", false,
                ShapeDash.Solid, 0, 6, 5, 0.5);
            Assert.True(arrow.StartX < arrow.EndX && arrow.StartY > arrow.EndY); // bottom left to top right
            Assert.Equal(0, arrow.Bend);

            Assert.True(ShapeKindTile.Box(ShapeKind.Rectangle, 4, 4).IsEmpty);
        }

        /// <summary>The sheet: every kind (columns) in every style (rows), each tile drawn at 4x
        /// over the sidebar's two grounds, then the first style at 1x and 2x, the sizes the picker
        /// really shows. Every tile must put ink on its plate.</summary>
        [Fact]
        public void Every_kind_in_every_style_draws_on_its_plate()
        {
            const int Zoom = 4, LabelW = 150, Gap = 10;
            var kinds = Enum.GetValues<ShapeKind>();
            int cellW = (int)(TileW * Zoom) + Gap, cellH = (int)(TileH * Zoom) + Gap + 16;
            int sheetW = LabelW + kinds.Length * cellW;
            int sheetH = (Styles.Length + 2) * cellH + 60;

            using var sheet = new SKBitmap(sheetW, sheetH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(sheet);
            // the compact chrome's near-black on the left half of the rows, the modern light ground
            // on the right half, so the plate is judged against both
            canvas.Clear(new SKColor(0x2D, 0x2D, 0x30));
            using (var light = new SKPaint { Color = new SKColor(0xF4, 0xF5, 0xF7) })
                canvas.DrawRect(LabelW + kinds.Length / 2 * cellW, 0, sheetW, sheetH, light);

            using var typeface = SKTypeface.CreateDefault();
            using var font = new SKFont(typeface, 12);
            using var labelPaint = new SKPaint { IsAntialias = true, Color = new SKColor(0xCC, 0xCC, 0xCC) };
            using var darkLabel = new SKPaint { IsAntialias = true, Color = new SKColor(0x40, 0x40, 0x40) };

            for (int row = 0; row < Styles.Length; row++)
            {
                int y = row * cellH;
                canvas.DrawText(Styles[row].Name, 6, y + cellH / 2f, SKTextAlign.Left, font, labelPaint);
                for (int k = 0; k < kinds.Length; k++)
                {
                    int x = LabelW + k * cellW;
                    var shape = TileOf(kinds[k], Styles[row]);
                    var pixels = DrawTile(canvas, x, y, Zoom, shape);
                    AssertInked(pixels, $"{kinds[k]} / {Styles[row].Name}");
                    var ink = k >= kinds.Length / 2 ? darkLabel : labelPaint;
                    canvas.DrawText(ShapeKinds.Label(kinds[k]), x + (float)(TileW * Zoom / 2), y + cellH - 6,
                        SKTextAlign.Center, font, ink);
                }
            }

            // the real sizes: 1x and 2x, as on a standard and a high-density display
            for (int scale = 1; scale <= 2; scale++)
            {
                int y = (Styles.Length + scale - 1) * cellH + 20;
                canvas.DrawText($"{Styles[0].Name} at {scale}x", 6, y + 20, SKTextAlign.Left, font, labelPaint);
                for (int k = 0; k < kinds.Length; k++)
                    DrawTile(canvas, LabelW + k * cellW, y, scale, TileOf(kinds[k], Styles[0]));
            }

            canvas.Flush();
            var path = Path.Combine(Path.GetTempPath(), "prisc-shape-picker-tiles.png");
            using (var data = SKImage.FromBitmap(sheet).Encode(SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(path, data.ToArray());
            TestContext.Current.TestOutputHelper?.WriteLine($"picker tiles: {path}");
        }

        /// <summary>Draws one tile the way <see cref="ShapeKindPreview"/> does (plate, then
        /// <see cref="ShapeKindTile.Draw"/> in tile coordinates) at <paramref name="zoom"/>, and
        /// returns its pixels on their own for the ink check.</summary>
        private static SKColor[] DrawTile(SKCanvas sheet, int x, int y, int zoom, ShapeContent shape)
        {
            int w = (int)(TileW * zoom), h = (int)(TileH * zoom);
            using var tile = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(tile))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Scale(zoom);
                var plate = new SKRoundRect(SKRect.Create(0, 0, (float)TileW, (float)TileH), 4);
                canvas.ClipRoundRect(plate, antialias: true);
                using (var paint = new SKPaint { Color = Plate })
                    canvas.DrawRect(SKRect.Create(0, 0, (float)TileW, (float)TileH), paint);
                ShapeKindTile.Draw(canvas, ShapeKindTile.Box(shape.Kind, TileW, TileH), shape);
            }

            sheet.DrawBitmap(tile, x, y);
            return tile.Pixels;
        }

        private static void AssertInked(IReadOnlyList<SKColor> pixels, string what)
        {
            int inked = 0;
            foreach (var p in pixels)
            {
                if (p.Alpha == 255 && Math.Abs(p.Red - Plate.Red) + Math.Abs(p.Green - Plate.Green)
                    + Math.Abs(p.Blue - Plate.Blue) > 40)
                    inked++;
            }

            // a 2 px outline round a 16 px square is over 100 tile pixels at 1x; a quarter of that
            // at 4x is a floor no real tile comes near
            Assert.True(inked > 100, $"{what}: only {inked} inked pixels");
        }
    }
}
