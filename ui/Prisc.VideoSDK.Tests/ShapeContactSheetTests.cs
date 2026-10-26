using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The shapes' look, for a person to judge: every <see cref="ShapeKind"/> composed through
    /// <see cref="FrameComposer"/> on the CPU in a handful of styles, laid out as labelled contact
    /// sheets in the temp folder (the paths are printed to the test output). The asserts only check
    /// that every cell drew something; whether the corners, dashes, heads and curves look right is
    /// a matter of opening the PNGs, which anyone changing <see cref="ShapeRenderer"/> should do.
    /// </summary>
    public class ShapeContactSheetTests
    {
        private const int CellW = 240, CellH = 180, LabelH = 22, Columns = 4;

        private static readonly SKColor Backdrop = new SKColor(0x80, 0x80, 0x80);

        /// <summary>One cell: a label, the edits that style its shape, and the time it is composed
        /// at (its item runs for <see cref="ItemDuration"/>).</summary>
        private sealed record Cell(string Label, ShapeKind Kind, Action<Item, ShapeContent> Style, double Extent = 0.6,
            long At = 0);

        private static readonly long ItemDuration = 10 * TimeSpan.TicksPerSecond;

        /// <summary>The length of the draw sheets' entry and exit.</summary>
        private static readonly long DrawDuration = TimeSpan.TicksPerSecond;

        [Fact]
        public void Every_kind_in_every_style()
        {
            var cells = new List<Cell>();
            foreach (var kind in Enum.GetValues<ShapeKind>())
            {
                bool line = ShapeContent.IsLineKind(kind);
                cells.Add(new Cell($"{kind}", kind, (_, _) => { }));
                cells.Add(new Cell(line ? $"{kind} dashed" : $"{kind} fill r24 dashed", kind, (_, s) =>
                {
                    s.Fill = true;
                    s.CornerRadius = 24;
                    s.Dash = ShapeDash.Dashed;
                }));
                cells.Add(new Cell(line ? $"{kind} dotted w14 bend" : $"{kind} dotted w14", kind, (item, s) =>
                {
                    s.Dash = ShapeDash.Dotted;
                    s.StrokeWidth = 14;
                    if (line)
                    {
                        s.Bend = 0.35;
                        ShapeLineLayout.Refit(item, OutputOf());
                    }
                }));
                cells.Add(new Cell(line ? $"{kind} 25deg bend shadow" : $"{kind} 25deg fill shadow", kind, (item, s) =>
                {
                    s.Fill = true;
                    s.FillColor = "#FFFFD54F";
                    s.StrokeColor = "#FF1E3A8A";
                    s.CornerRadius = 10;
                    if (line)
                    {
                        s.Bend = -0.3;
                        ShapeLineLayout.Refit(item, OutputOf());
                    }
                    item.Transform.Rotation = 25;
                    item.Surround = new Surround { Kind = SurroundKind.Shadow, Size = 0.06, Distance = 0.06 };
                }, Extent: 0.45));
            }

            WriteSheet("prisc-shape-contact-sheet.png", cells);
        }

        [Fact]
        public void Small_and_thin_and_translucent()
        {
            var cells = new List<Cell>();
            foreach (var kind in Enum.GetValues<ShapeKind>())
            {
                cells.Add(new Cell($"{kind} small w2", kind, (_, s) => s.StrokeWidth = 2, Extent: 0.2));
                cells.Add(new Cell($"{kind} w1 fill no stroke", kind, (_, s) =>
                {
                    s.StrokeWidth = ShapeContent.IsLineKind(kind) ? 1 : 0;
                    s.Fill = true;
                    s.FillColor = "#FF2E7D32";
                    s.StrokeColor = "#FF2E7D32";
                }, Extent: 0.35));
                cells.Add(new Cell($"{kind} 50% ink", kind, (item, s) =>
                {
                    s.StrokeColor = "#80FFFFFF";
                    s.FillColor = "#80000000";
                    s.Fill = true;
                    s.StrokeWidth = 10;
                    s.Bend = 0.25;
                    ShapeLineLayout.Refit(item, OutputOf());
                }));
                cells.Add(new Cell($"{kind} glow w30 r60", kind, (item, s) =>
                {
                    s.StrokeWidth = 30;
                    s.CornerRadius = 60;
                    s.StrokeColor = "#FFFFFFFF";
                    ShapeLineLayout.Refit(item, OutputOf());
                    item.Surround = new Surround { Kind = SurroundKind.Glow, Color = 0xC0FF00FF, Size = 0.15 };
                }));
            }

            WriteSheet("prisc-shape-contact-sheet-2.png", cells);
        }

        [Fact]
        public void The_draw_transition_on_closed_kinds()
        {
            WriteSheet("prisc-shape-draw-closed.png", DrawCells(
            [
                ("Rectangle", ShapeKind.Rectangle, (_, _) => { }),
                ("Rect fill", ShapeKind.Rectangle, (_, s) => s.Fill = true),
                ("Rect r24 dashed", ShapeKind.Rectangle, (_, s) =>
                {
                    s.CornerRadius = 24;
                    s.Dash = ShapeDash.Dashed;
                }),
                ("Ellipse dotted", ShapeKind.Ellipse, (_, s) =>
                {
                    s.Dash = ShapeDash.Dotted;
                    s.StrokeWidth = 10;
                }),
                ("Star fill", ShapeKind.Star, (_, s) => s.Fill = true),
                ("Heart fill", ShapeKind.Heart, (_, s) => s.Fill = true),
                ("Bubble dashed fill", ShapeKind.Bubble, (_, s) =>
                {
                    s.Fill = true;
                    s.CornerRadius = 12;
                    s.Dash = ShapeDash.Dashed;
                }),
                ("Triangle shadow", ShapeKind.Triangle, (item, _) =>
                    item.Surround = new Surround { Kind = SurroundKind.Shadow, Size = 0.06, Distance = 0.06 }),
            ]), DrawColumns);
        }

        [Fact]
        public void The_draw_transition_on_line_kinds()
        {
            WriteSheet("prisc-shape-draw-lines.png", DrawCells(
            [
                ("Arrow", ShapeKind.Arrow, (_, _) => { }),
                ("Arrow bend", ShapeKind.Arrow, (item, s) => Bend(item, s, 0.35)),
                ("Arrow dashed bend", ShapeKind.Arrow, (item, s) =>
                {
                    s.Dash = ShapeDash.Dashed;
                    Bend(item, s, -0.3);
                }),
                ("Double bend", ShapeKind.DoubleArrow, (item, s) => Bend(item, s, -0.25)),
                ("Double dashed", ShapeKind.DoubleArrow, (_, s) => s.Dash = ShapeDash.Dashed),
                ("Line dotted bend w10", ShapeKind.Line, (item, s) =>
                {
                    s.Dash = ShapeDash.Dotted;
                    s.StrokeWidth = 10;
                    Bend(item, s, 0.4);
                }),
                ("Arrow 50% shadow", ShapeKind.Arrow, (item, s) =>
                {
                    s.StrokeColor = "#80FFFFFF";
                    Bend(item, s, 0.25);
                    item.Surround = new Surround { Kind = SurroundKind.Shadow, Size = 0.06, Distance = 0.06 };
                }),
            ]), DrawColumns);
        }

        private const int DrawColumns = 6;

        private static void Bend(Item item, ShapeContent shape, double bend)
        {
            shape.Bend = bend;
            ShapeLineLayout.Refit(item, OutputOf());
        }

        /// <summary>Each style in a row of <see cref="DrawColumns"/> moments: a quarter, half and
        /// three quarters into a draw entry, at rest, and 40% and 80% into a draw exit.</summary>
        private static List<Cell> DrawCells(IEnumerable<(string Label, ShapeKind Kind, Action<Item, ShapeContent> Style)> styles)
        {
            var moments = new (string Name, long At)[]
            {
                ("in 25%", DrawDuration / 4),
                ("in 50%", DrawDuration / 2),
                ("in 75%", DrawDuration * 3 / 4),
                ("rest", ItemDuration / 2),
                ("out 40%", ItemDuration - DrawDuration * 6 / 10),
                ("out 80%", ItemDuration - DrawDuration * 2 / 10),
            };

            var cells = new List<Cell>();
            foreach (var (label, kind, style) in styles)
            {
                foreach (var (name, at) in moments)
                {
                    cells.Add(new Cell($"{label} {name}", kind, (item, s) =>
                    {
                        style(item, s);
                        item.Entry = new Transition { Kind = TransitionKind.Draw, DurationTicks = DrawDuration };
                        item.Exit = new Transition { Kind = TransitionKind.Draw, DurationTicks = DrawDuration };
                    }, At: at));
                }
            }
            return cells;
        }

        private static OutputSettings OutputOf() =>
            new OutputSettings { WidthPx = CellW, HeightPx = CellH, FpsNum = 30, FpsDen = 1, SampleRate = 48000 };

        private static void WriteSheet(string name, IReadOnlyList<Cell> cells, int columns = Columns)
        {
            int rows = (cells.Count + columns - 1) / columns;
            int sheetW = columns * CellW, sheetH = rows * (CellH + LabelH);
            using var sheet = new SKBitmap(sheetW, sheetH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var sheetCanvas = new SKCanvas(sheet);
            sheetCanvas.Clear(new SKColor(0x30, 0x30, 0x30));

            using var typeface = SKTypeface.CreateDefault();
            using var font = new SKFont(typeface, 13);
            using var labelPaint = new SKPaint { IsAntialias = true, Color = SKColors.White };

            for (int i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                var pixels = RenderCell(cell);
                AssertHasInk(cell, pixels);

                int x = i % columns * CellW, y = i / columns * (CellH + LabelH);
                using (var image = SKImage.FromPixelCopy(new SKImageInfo(CellW, CellH, SKColorType.Bgra8888, SKAlphaType.Premul), pixels, CellW * 4))
                    sheetCanvas.DrawImage(image, x, y + LabelH);
                sheetCanvas.DrawText(cell.Label, x + 6, y + 16, SKTextAlign.Left, font, labelPaint);
            }

            var path = Path.Combine(Path.GetTempPath(), name);
            using (var data = SKImage.FromBitmap(sheet).Encode(SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(path, data.ToArray());
            TestContext.Current.TestOutputHelper?.WriteLine($"contact sheet: {path}");
        }

        /// <summary>The cell's shape, built the way the editor adds one (a centered square box,
        /// converted to a line kind by the session's rule), styled, then composed over a grey
        /// backdrop.</summary>
        private static byte[] RenderCell(Cell cell)
        {
            var project = new Project { Output = OutputOf() };
            var backdrop = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0 };
            var row = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 1 };
            project.Tracks.Add(backdrop);
            project.Tracks.Add(row);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = backdrop.Id,
                DurationTicks = ItemDuration,
                Content = new SolidContent { Color = "#FF808080" },
            });

            var shape = new ShapeContent();
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = row.Id,
                DurationTicks = ItemDuration,
                Content = shape,
                Transform = new Transform { Scale = cell.Extent, ScaleY = cell.Extent * CellW / CellH },
            };
            ShapeLineLayout.ConvertKind(item, cell.Kind, project.Output);
            cell.Style(item, shape);
            project.Items.Add(item);
            Assert.Empty(project.Validate());

            using var factory = new CpuSurfaceFactory();
            using var surface = factory.CreateSurface(CellW, CellH);
            FrameComposer.Compose(project, cell.At, null, surface.Canvas, CellW, CellH);

            int rowBytes = CellW * 4;
            var native = Marshal.AllocHGlobal(rowBytes * CellH);
            try
            {
                Assert.True(factory.TryReadPixels(surface, CellW, CellH, native, rowBytes));
                var pixels = new byte[rowBytes * CellH];
                Marshal.Copy(native, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        private static void AssertHasInk(Cell cell, byte[] bgra)
        {
            int inked = 0;
            for (int i = 0; i < bgra.Length; i += 4)
            {
                if (Math.Abs(bgra[i] - Backdrop.Blue) + Math.Abs(bgra[i + 1] - Backdrop.Green)
                    + Math.Abs(bgra[i + 2] - Backdrop.Red) > 24)
                    inked++;
            }
            Assert.True(inked > 20, $"cell '{cell.Label}' drew {inked} pixels");
        }
    }
}
