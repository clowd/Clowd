using System;
using Clowd.Geometry2D;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// How a line kind's geometry lives in the model (<see cref="ShapeLineLayout"/>): canvas points
    /// written back as a tight padded box plus endpoint fractions and read out again, rotation
    /// baked, the pad in output pixels whatever the canvas, and every kind change.
    /// </summary>
    public class ShapeLineLayoutTests
    {
        private static readonly OutputSettings Output1080 =
            new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 };

        private static void AssertClose(Vec2 expected, Vec2 actual, double tol = 1e-6) =>
            Assert.True(Math.Abs(expected.X - actual.X) < tol && Math.Abs(expected.Y - actual.Y) < tol,
                $"expected {expected} actual {actual}");

        private static Item ShapeItem(ShapeKind kind, Transform transform = null, double strokeWidth = 6) => new Item
        {
            Id = Guid.NewGuid(),
            DurationTicks = TimeSpan.TicksPerSecond,
            Content = new ShapeContent { Kind = kind, StrokeWidth = strokeWidth },
            Transform = transform ?? new Transform { Scale = 0.25, ScaleY = 0.25 * 1920 / 1080 },
        };

        // ------------------------------------------------------------------- write / resolve

        [Theory]
        [InlineData(1920, 1080, 1.0)]
        [InlineData(640, 360, 1.0 / 3)]
        [InlineData(333, 777, 0.71)]
        public void Written_lines_read_back_where_they_were(double width, double height, double pxScale)
        {
            var lines = new[]
            {
                new ShapeLine(new Vec2(10, 20), new Vec2(300, 200), 0, 0),
                new ShapeLine(new Vec2(250, 40), new Vec2(30, 90), 0.35, -0.1),
                new ShapeLine(new Vec2(-40, 500), new Vec2(700, -80), -1.2, 0.25),
                new ShapeLine(new Vec2(100, 100), new Vec2(100, 100), 0, 0),
            };

            foreach (var line in lines)
            {
                var item = ShapeItem(ShapeKind.Arrow);
                var shape = (ShapeContent)item.Content;
                ShapeLineLayout.Write(item.Transform, shape, line, width, height, pxScale);

                Assert.True(ShapeLineLayout.TryResolve(item.Transform, shape, width, height, out var back));
                AssertClose(line.Start, back.Start);
                AssertClose(line.End, back.End);
                Assert.Equal(line.Bend, back.Bend);
                Assert.Equal(line.BendSkew, back.BendSkew);
                foreach (var f in new[] { shape.StartX, shape.StartY, shape.EndX, shape.EndY })
                    Assert.InRange(f, 0, 1);
            }
        }

        [Fact]
        public void A_rotation_is_baked_into_the_points()
        {
            var item = ShapeItem(ShapeKind.Arrow, new Transform { X = 0.4, Y = 0.6, Scale = 0.3, ScaleY = 0.2, Rotation = 30 });
            var shape = (ShapeContent)item.Content;
            shape.Bend = 0.2;
            Assert.True(ShapeLineLayout.TryResolve(item.Transform, shape, 1920, 1080, out var rotated));

            // the box center and the turned endpoints: rotating (0, 1) and (1, 0) of a 576×216 box
            // 30° clockwise about (768, 648)
            double cos = Math.Cos(Math.PI / 6), sin = Math.Sin(Math.PI / 6);
            var start = new Vec2(768 + (-288) * cos - 108 * sin, 648 + (-288) * sin + 108 * cos);
            AssertClose(start, rotated.Start);

            ShapeLineLayout.Write(item.Transform, shape, rotated, 1920, 1080, 1);
            Assert.Equal(0, item.Transform.Rotation);
            Assert.True(ShapeLineLayout.TryResolve(item.Transform, shape, 1920, 1080, out var baked));
            AssertClose(rotated.Start, baked.Start);
            AssertClose(rotated.End, baked.End);
            AssertClose(rotated.Handle, baked.Handle);
        }

        [Fact]
        public void The_box_is_the_curve_padded_by_the_ink_reach()
        {
            var item = ShapeItem(ShapeKind.Arrow);
            var shape = (ShapeContent)item.Content;
            var line = new ShapeLine(new Vec2(200, 700), new Vec2(900, 300), 0.4, 0.1);
            ShapeLineLayout.Write(item.Transform, shape, line, 1920, 1080, 1);

            var (minX, minY, maxX, maxY) = QuadraticCurve.Bounds(line.Start, line.Control.Value, line.End);
            double pad = ArrowGeometry.MaxInkRadius(6);
            var t = item.Transform;
            double left = t.X * 1920 - t.Scale * 1920 / 2, top = t.Y * 1080 - t.ScaleY.Value * 1080 / 2;
            Assert.Equal(minX - pad, left, 6);
            Assert.Equal(minY - pad, top, 6);
            Assert.Equal(maxX + pad, left + t.Scale * 1920, 6);
            Assert.Equal(maxY + pad, top + t.ScaleY.Value * 1080, 6);
        }

        [Fact]
        public void A_flat_thin_line_still_gets_a_box_with_height()
        {
            var item = ShapeItem(ShapeKind.Line, strokeWidth: 1);
            var shape = (ShapeContent)item.Content;
            ShapeLineLayout.Write(item.Transform, shape, new ShapeLine(new Vec2(100, 400), new Vec2(900, 400), 0, 0), 1920, 1080, 1);

            Assert.Equal(2 * ShapeLineLayout.MinLineBoxPadPx, item.Transform.ScaleY.Value * 1080, 9);
            Assert.Equal(800 + 2 * ShapeLineLayout.MinLineBoxPadPx, item.Transform.Scale * 1920, 9);
            Assert.Equal(0.5, shape.StartY, 12);
        }

        [Fact]
        public void A_preview_canvas_writes_the_same_fractions_as_the_output()
        {
            // the editor writes on the video rect with its pixel scale; the pad shrinks with it
            const double previewH = 405, k = previewH / 1080;
            var line = new ShapeLine(new Vec2(300, 800), new Vec2(1500, 200), -0.3, 0.05);
            var scaled = new ShapeLine(line.Start * k, line.End * k, line.Bend, line.BendSkew);

            var output = ShapeItem(ShapeKind.Arrow);
            var preview = ShapeItem(ShapeKind.Arrow);
            ShapeLineLayout.Write(output.Transform, (ShapeContent)output.Content, line, 1920, 1080, 1);
            ShapeLineLayout.Write(preview.Transform, (ShapeContent)preview.Content, scaled, 1920 * k, previewH, k);

            Assert.Equal(output.Transform.X, preview.Transform.X, 9);
            Assert.Equal(output.Transform.Y, preview.Transform.Y, 9);
            Assert.Equal(output.Transform.Scale, preview.Transform.Scale, 9);
            Assert.Equal(output.Transform.ScaleY.Value, preview.Transform.ScaleY.Value, 9);
            var a = (ShapeContent)output.Content;
            var b = (ShapeContent)preview.Content;
            Assert.Equal(a.StartX, b.StartX, 9);
            Assert.Equal(a.EndY, b.EndY, 9);
        }

        [Fact]
        public void The_bow_is_clamped_to_its_validated_range()
        {
            var item = ShapeItem(ShapeKind.Arrow);
            var shape = (ShapeContent)item.Content;
            ShapeLineLayout.Write(item.Transform, shape, new ShapeLine(new Vec2(0, 0), new Vec2(10, 0), 9, -3), 1920, 1080, 1);

            Assert.Equal(ShapeContent.MaxBend, shape.Bend);
            Assert.Equal(-ShapeContent.MaxBendSkew, shape.BendSkew);
        }

        [Fact]
        public void Refit_after_a_width_change_keeps_the_line_where_it_was()
        {
            var item = ShapeItem(ShapeKind.Arrow);
            var shape = (ShapeContent)item.Content;
            ShapeLineLayout.Write(item.Transform, shape, new ShapeLine(new Vec2(400, 700), new Vec2(1100, 350), 0.3, 0), 1920, 1080, 1);
            Assert.True(ShapeLineLayout.TryResolve(item.Transform, shape, 1920, 1080, out var before));
            double heightBefore = item.Transform.ScaleY.Value;

            shape.StrokeWidth = 20;
            ShapeLineLayout.Refit(item, Output1080);

            Assert.True(ShapeLineLayout.TryResolve(item.Transform, shape, 1920, 1080, out var after));
            AssertClose(before.Start, after.Start);
            AssertClose(before.End, after.End);
            Assert.Equal(before.Bend, after.Bend, 12);
            // the heavier head reaches further, so the box grew around the same line
            Assert.True(item.Transform.ScaleY.Value > heightBefore);
        }

        [Fact]
        public void A_shape_line_works_out_its_bow_from_what_it_holds()
        {
            var line = new ShapeLine(new Vec2(0, 0), new Vec2(100, 0), 0.3, 0);
            AssertClose(new Vec2(50, 30), line.Handle);
            AssertClose(new Vec2(50, 60), line.Control.Value);
            Assert.Equal(100, line.Chord, 12);

            // a fresh value with a new end re-bows around the new chord: nothing stale is carried
            var moved = new ShapeLine(line.Start, new Vec2(0, 200), line.Bend, line.BendSkew);
            AssertClose(new Vec2(-60, 100), moved.Handle);
            Assert.Null(new ShapeLine(line.Start, line.End, 0, 0.2).Control);
        }

        // ------------------------------------------------------------------------ kind change

        [Fact]
        public void A_rectangle_turned_into_an_arrow_keeps_its_box()
        {
            var item = ShapeItem(ShapeKind.Rectangle, new Transform { X = 0.3, Y = 0.6, Scale = 0.2, ScaleY = 0.4, Rotation = 15 });
            var shape = (ShapeContent)item.Content;

            ShapeLineLayout.ConvertKind(item, ShapeKind.Arrow, Output1080);

            Assert.Equal(ShapeKind.Arrow, shape.Kind);
            Assert.Equal((0.3, 0.6, 0.2, 0.4, 15.0),
                (item.Transform.X, item.Transform.Y, item.Transform.Scale, item.Transform.ScaleY.Value, item.Transform.Rotation));

            // the inset diagonal: bottom left to top right, the pad in from each side
            double pad = ShapeLineLayout.LinePadPx(shape);
            Assert.Equal(pad / 384, shape.StartX, 9);
            Assert.Equal(1 - pad / 432, shape.StartY, 9);
            Assert.Equal(1 - pad / 384, shape.EndX, 9);
            Assert.Equal(pad / 432, shape.EndY, 9);

            // and the arrow's ink stays inside the old box
            var box = new SKRect(0, 0, 384, 432);
            var ink = ShapeRenderer.InkBounds(box, shape, 1);
            Assert.True(ink.Left >= -0.01 && ink.Top >= -0.01 && ink.Right <= 384.01 && ink.Bottom <= 432.01, $"ink {ink}");
        }

        [Fact]
        public void An_arrow_turned_straight_back_gives_the_rectangle_back()
        {
            var original = new Transform { X = 0.45, Y = 0.55, Scale = 0.3, ScaleY = 0.35, Rotation = -20 };
            var item = ShapeItem(ShapeKind.Rectangle, new Transform
            {
                X = original.X, Y = original.Y, Scale = original.Scale, ScaleY = original.ScaleY, Rotation = original.Rotation,
            });

            ShapeLineLayout.ConvertKind(item, ShapeKind.Arrow, Output1080);
            ShapeLineLayout.ConvertKind(item, ShapeKind.Rectangle, Output1080);

            Assert.Equal(ShapeKind.Rectangle, ((ShapeContent)item.Content).Kind);
            Assert.Equal((original.X, original.Y, original.Scale, original.ScaleY, original.Rotation),
                (item.Transform.X, item.Transform.Y, item.Transform.Scale, item.Transform.ScaleY, item.Transform.Rotation));
        }

        [Fact]
        public void A_flat_arrow_turned_into_a_rectangle_becomes_a_square()
        {
            var item = ShapeItem(ShapeKind.Arrow);
            var shape = (ShapeContent)item.Content;
            ShapeLineLayout.Write(item.Transform, shape, new ShapeLine(new Vec2(500, 540), new Vec2(1100, 540), 0, 0), 1920, 1080, 1);
            double width = item.Transform.Scale * 1920;
            Assert.InRange(item.Transform.ScaleY.Value * 1080, 40, 42); // about 41 px: the head's reach either side
            var (x, y) = (item.Transform.X, item.Transform.Y);

            ShapeLineLayout.ConvertKind(item, ShapeKind.Rectangle, Output1080);

            Assert.Equal(width, item.Transform.Scale * 1920, 9);
            Assert.Equal(width, item.Transform.ScaleY.Value * 1080, 9);
            Assert.Equal((x, y), (item.Transform.X, item.Transform.Y));
        }

        [Fact]
        public void An_arrow_centred_off_the_canvas_turned_into_a_rectangle_comes_back_inside_it()
        {
            // pointing in from past the left edge: the box centre sits at about X = -0.05
            var item = ShapeItem(ShapeKind.Arrow);
            var shape = (ShapeContent)item.Content;
            ShapeLineLayout.Write(item.Transform, shape, new ShapeLine(new Vec2(-576, 540), new Vec2(384, 540), 0, 0), 1920, 1080, 1);
            Assert.InRange(item.Transform.X, -0.06, -0.04);
            double y = item.Transform.Y;

            ShapeLineLayout.ConvertKind(item, ShapeKind.Rectangle, Output1080);

            Assert.Equal(0, item.Transform.X);
            Assert.Equal(y, item.Transform.Y);
        }

        [Fact]
        public void An_arrow_turned_into_a_line_shrinks_its_box_round_the_same_line()
        {
            var item = ShapeItem(ShapeKind.Arrow);
            var shape = (ShapeContent)item.Content;
            ShapeLineLayout.Write(item.Transform, shape, new ShapeLine(new Vec2(300, 700), new Vec2(900, 300), 0.2, 0), 1920, 1080, 1);
            Assert.True(ShapeLineLayout.TryResolve(item.Transform, shape, 1920, 1080, out var before));
            double area = item.Transform.Scale * item.Transform.ScaleY.Value;

            ShapeLineLayout.ConvertKind(item, ShapeKind.Line, Output1080);

            Assert.Equal(ShapeKind.Line, shape.Kind);
            Assert.True(ShapeLineLayout.TryResolve(item.Transform, shape, 1920, 1080, out var after));
            AssertClose(before.Start, after.Start);
            AssertClose(before.End, after.End);
            Assert.True(item.Transform.Scale * item.Transform.ScaleY.Value < area);
        }

        [Fact]
        public void A_closed_kind_change_touches_nothing_but_the_kind()
        {
            var item = ShapeItem(ShapeKind.Rectangle, new Transform { X = 0.2, Y = 0.7, Scale = 0.15, ScaleY = 0.6, Rotation = 33 });
            var shape = (ShapeContent)item.Content;
            shape.StartX = 0.33; // stale line fields stay as they are
            string before = System.Text.Json.JsonSerializer.Serialize(item.Transform, ProjectJsonContext.Default.Transform);

            ShapeLineLayout.ConvertKind(item, ShapeKind.Star, Output1080);

            Assert.Equal(ShapeKind.Star, shape.Kind);
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(item.Transform, ProjectJsonContext.Default.Transform));
            Assert.Equal(0.33, shape.StartX);
        }

        [Fact]
        public void An_outline_of_no_width_becomes_a_line_of_one()
        {
            var item = ShapeItem(ShapeKind.Ellipse, strokeWidth: 0);
            ShapeLineLayout.ConvertKind(item, ShapeKind.Arrow, Output1080);

            Assert.Equal(ShapeContent.MinLineStrokeWidth, ((ShapeContent)item.Content).StrokeWidth);
        }
    }
}
