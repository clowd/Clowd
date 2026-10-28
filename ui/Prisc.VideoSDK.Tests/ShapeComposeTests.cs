using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Prisc.Geometry2D;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="ShapeContent"/> through <see cref="FrameComposer"/>: the outline drawn inside its
    /// box, fills, corners, dashes, the arrow's parts, the standard transitions (the wipe sweeping
    /// the ink rather than the box), the surround, output-pixel sizing, and GPU parity where a GPU
    /// is available. The canvas starts black (the composer's clear) unless a test lays a backdrop.
    /// </summary>
    public class ShapeComposeTests
    {
        private const long Sec = 10_000_000;
        private const int W = 256, H = 256;

        private static readonly SKColor Red = new SKColor(255, 0, 0);

        // ---------------------------------------------------------------------------- builders

        private static Project NewProject(int width = W, int height = H) => new Project
        {
            Output = new OutputSettings { WidthPx = width, HeightPx = height, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
        };

        private static Track AddVideoTrack(Project p, int order)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = order };
            p.Tracks.Add(track);
            return track;
        }

        private static Item AddItem(Project p, Track track, ItemContent content, Transform transform = null,
            long start = 0, long duration = 10 * Sec)
        {
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = start,
                DurationTicks = duration,
                Content = content,
                Transform = transform ?? new Transform(),
            };
            p.Items.Add(item);
            return item;
        }

        /// <summary>The transform of a box [left, right) × [top, bottom) on a W×H canvas.</summary>
        private static Transform Box(double left, double top, double right, double bottom, double width = W, double height = H) =>
            new Transform
            {
                X = (left + right) / 2 / width,
                Y = (top + bottom) / 2 / height,
                Scale = (right - left) / width,
                ScaleY = (bottom - top) / height,
            };

        /// <summary>One shape on a black canvas (an optional white backdrop row under it).</summary>
        private static (Project Project, Item Item) OneShape(ShapeContent shape, Transform transform, bool whiteBackdrop = false,
            int width = W, int height = H)
        {
            var p = NewProject(width, height);
            if (whiteBackdrop)
                AddItem(p, AddVideoTrack(p, 0), new SolidContent { Color = "#FFFFFFFF" });
            var item = AddItem(p, AddVideoTrack(p, 1), shape, transform);
            Assert.Empty(p.Validate());
            return (p, item);
        }

        /// <summary>A line kind placed by its canvas points, the way the editor's handles write one.</summary>
        private static (Project Project, Item Item) OneLine(ShapeContent shape, Vec2 start, Vec2 end, double bend = 0,
            bool whiteBackdrop = false, int width = W, int height = H)
        {
            var transform = new Transform();
            ShapeLineLayout.Write(transform, shape, new ShapeLine(start, end, bend, 0), width, height, 1);
            return OneShape(shape, transform, whiteBackdrop, width, height);
        }

        private static byte[] Render(Project p, long timeTicks, ISurfaceFactory factory = null, int width = W, int height = H)
        {
            using var owned = factory == null ? new CpuSurfaceFactory() : null;
            factory ??= owned;
            using var surface = factory.CreateSurface(width, height);
            FrameComposer.Compose(p, timeTicks, null, surface.Canvas, width, height);

            int rowBytes = width * 4;
            var native = Marshal.AllocHGlobal(rowBytes * height);
            try
            {
                Assert.True(factory.TryReadPixels(surface, width, height, native, rowBytes));
                var pixels = new byte[rowBytes * height];
                Marshal.Copy(native, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        private static (byte R, byte G, byte B) Px(byte[] bgra, int x, int y, int width = W)
        {
            int i = (y * width + x) * 4;
            return (bgra[i + 2], bgra[i + 1], bgra[i]);
        }

        private static bool IsBlack(byte[] px, int x, int y, int width = W)
        {
            var (r, g, b) = Px(px, x, y, width);
            return r < 8 && g < 8 && b < 8;
        }

        private static bool IsRed(byte[] px, int x, int y, int width = W)
        {
            var (r, g, b) = Px(px, x, y, width);
            return r > 240 && g < 16 && b < 16;
        }

        private static bool IsInked(byte[] px, int x, int y, int width = W) => !IsBlack(px, x, y, width);

        private static int InkedInColumn(byte[] px, int x, int width = W, int height = H)
        {
            int count = 0;
            for (int y = 0; y < height; y++)
            {
                if (Px(px, x, y, width).R > 128)
                    count++;
            }
            return count;
        }

        private static ShapeContent Outline(ShapeKind kind, double strokeWidth = 8) =>
            new ShapeContent { Kind = kind, StrokeWidth = strokeWidth, StrokeColor = "#FFFF0000" };

        // ------------------------------------------------------------------------- rectangle

        [Fact]
        public void The_outline_is_stroked_inside_the_box()
        {
            var (p, _) = OneShape(Outline(ShapeKind.Rectangle), Box(64, 64, 192, 192));
            var px = Render(p, 5 * Sec);

            Assert.True(IsBlack(px, 63, 128), "1 px outside the left edge");
            Assert.True(IsRed(px, 64, 128), "the edge's first column");
            Assert.True(IsRed(px, 71, 128), "the stroke's last column (8 px)");
            Assert.True(IsBlack(px, 72, 128), "just past the stroke");
            Assert.True(IsBlack(px, 128, 128), "an unfilled interior");
            Assert.True(IsBlack(px, 128, 192), "1 px below the bottom edge");
            Assert.True(IsRed(px, 128, 191));
        }

        [Fact]
        public void A_fill_paints_the_interior_and_no_outline_draws_without_a_width()
        {
            var shape = Outline(ShapeKind.Rectangle);
            shape.Fill = true;
            shape.FillColor = "#FF00FF00";
            var (p, _) = OneShape(shape, Box(64, 64, 192, 192));
            var px = Render(p, 5 * Sec);
            Assert.Equal((0, 255, 0), Px(px, 128, 128));
            Assert.True(IsRed(px, 66, 128));

            shape.StrokeWidth = 0;
            px = Render(p, 5 * Sec);
            Assert.Equal((0, 255, 0), Px(px, 66, 128));
            Assert.Equal((0, 255, 0), Px(px, 64, 64));
            Assert.True(IsBlack(px, 63, 128));
        }

        [Fact]
        public void The_stroke_is_output_pixels_on_a_scaled_canvas()
        {
            // a 128 px output composed at twice its size (a preview larger than the output) draws
            // the same picture twice as big: the 6 px stroke is 12 canvas px
            var (p, _) = OneShape(Outline(ShapeKind.Rectangle, 6), Box(32, 32, 96, 96, 128, 128), width: 128, height: 128);
            var once = Render(p, 5 * Sec, width: 128, height: 128);
            var twice = Render(p, 5 * Sec, width: 256, height: 256);

            int Thickness(byte[] px, int width, int row)
            {
                int count = 0;
                for (int x = 0; x < width / 2; x++)
                {
                    if (Px(px, x, row, width).R > 128)
                        count++;
                }
                return count;
            }

            Assert.Equal(6, Thickness(once, 128, 64));
            Assert.Equal(12, Thickness(twice, 256, 128));
        }

        [Fact]
        public void A_large_corner_radius_rounds_the_corners_off()
        {
            var shape = Outline(ShapeKind.Rectangle);
            shape.CornerRadius = 40;
            var (p, _) = OneShape(shape, Box(64, 64, 192, 192));
            var px = Render(p, 5 * Sec);

            Assert.True(IsBlack(px, 65, 65), "the box's corner is cut away");
            Assert.True(IsRed(px, 66, 128), "the edge's middle is still stroked");
            Assert.True(IsRed(px, 128, 66));
        }

        [Fact]
        public void A_rotation_of_ninety_degrees_swaps_the_box_aspect()
        {
            var shape = Outline(ShapeKind.Rectangle);
            shape.Fill = true;
            shape.FillColor = "#FF00FF00";
            var (p, item) = OneShape(shape, Box(48, 98, 208, 158)); // 160 × 60
            var px = Render(p, 5 * Sec);
            Assert.True(IsInked(px, 60, 128));
            Assert.True(IsBlack(px, 128, 60));

            item.Transform.Rotation = 90;
            px = Render(p, 5 * Sec);
            Assert.True(IsBlack(px, 60, 128));
            Assert.True(IsInked(px, 128, 60));
        }

        // ------------------------------------------------------------------ the other kinds

        [Fact]
        public void An_ellipse_leaves_its_box_corners_empty()
        {
            var (p, _) = OneShape(Outline(ShapeKind.Ellipse), Box(64, 64, 192, 192));
            var px = Render(p, 5 * Sec);

            Assert.True(IsBlack(px, 68, 68));
            Assert.True(IsBlack(px, 188, 188));
            Assert.True(IsRed(px, 66, 128));
            Assert.True(IsRed(px, 128, 66));
            Assert.True(IsBlack(px, 128, 128));
        }

        [Fact]
        public void A_star_fills_its_middle_and_inks_its_tip_but_not_between_its_tips()
        {
            var shape = Outline(ShapeKind.Star, 4);
            shape.Fill = true;
            shape.FillColor = "#FF00FF00";
            var (p, _) = OneShape(shape, Box(48, 48, 208, 208));
            var px = Render(p, 5 * Sec);

            Assert.Equal((0, 255, 0), Px(px, 128, 140));
            Assert.True(IsRed(px, 128, 56), "just under the top tip");
            Assert.True(IsBlack(px, 60, 60), "between the top and the left tips");
            Assert.True(IsBlack(px, 128, 205), "between the two bottom tips");
        }

        [Fact]
        public void A_soft_star_rounds_its_inner_corners_too()
        {
            // the radius far exceeds what any vertex can carry: every vertex is clamped, and the
            // concave ones round outward, which fills the star's inner corners
            var shape = Outline(ShapeKind.Star, 4);
            shape.Fill = true;
            shape.FillColor = "#FF00FF00";
            shape.CornerRadius = 1000;
            var (p, _) = OneShape(shape, Box(48, 48, 208, 208));
            var px = Render(p, 5 * Sec);

            // the inner vertices of a 5-point star at ratio 0.5, fitted to the box
            var (minX, minY, maxX, maxY) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
            var vertices = new (double X, double Y)[10];
            for (int k = 0; k < 10; k++)
            {
                double a = -Math.PI / 2 + k * Math.PI / 5, r = k % 2 == 1 ? 0.5 : 1;
                vertices[k] = (r * Math.Cos(a), r * Math.Sin(a));
                minX = Math.Min(minX, vertices[k].X);
                maxX = Math.Max(maxX, vertices[k].X);
                minY = Math.Min(minY, vertices[k].Y);
                maxY = Math.Max(maxY, vertices[k].Y);
            }

            for (int k = 1; k < 10; k += 2)
            {
                int x = (int)(48 + (vertices[k].X - minX) / (maxX - minX) * 160);
                int y = (int)(48 + (vertices[k].Y - minY) / (maxY - minY) * 160);
                Assert.True(IsInked(px, x, y), $"inner vertex {k} at ({x},{y})");
            }
        }

        [Fact]
        public void A_four_sided_polygon_is_a_diamond()
        {
            var (p, _) = OneShape(new ShapeContent { Kind = ShapeKind.Polygon, PolygonSides = 4, StrokeWidth = 6 }, Box(64, 64, 192, 192));
            var px = Render(p, 5 * Sec);

            Assert.True(IsRed(px, 128, 67), "top vertex");
            Assert.True(IsRed(px, 189, 128), "right vertex");
            Assert.True(IsRed(px, 128, 189), "bottom vertex");
            Assert.True(IsRed(px, 67, 128), "left vertex");
            Assert.True(IsBlack(px, 68, 68), "the box's corner");
        }

        [Theory]
        [InlineData(ShapeKind.Triangle)]
        [InlineData(ShapeKind.Diamond)]
        [InlineData(ShapeKind.Heart)]
        [InlineData(ShapeKind.Bubble)]
        public void Every_closed_kind_stays_inside_its_box(ShapeKind kind)
        {
            var shape = Outline(kind);
            shape.Fill = true;
            shape.CornerRadius = 12;
            shape.Dash = ShapeDash.Dashed;
            var (p, _) = OneShape(shape, Box(64, 80, 192, 176));
            var px = Render(p, 5 * Sec);

            int inside = 0;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    bool inBox = x >= 64 && x < 192 && y >= 80 && y < 176;
                    if (!inBox)
                        Assert.True(IsBlack(px, x, y), $"{kind} inked ({x},{y}) outside its box");
                    else if (IsInked(px, x, y))
                        inside++;
                }
            }
            Assert.True(inside > 1000, $"{kind} drew {inside} px");
        }

        [Theory]
        [InlineData(ShapeDash.Dashed)]
        [InlineData(ShapeDash.Dotted)]
        public void A_dashed_outline_alternates_along_an_edge(ShapeDash dash)
        {
            var shape = Outline(ShapeKind.Rectangle, 6);
            shape.Dash = dash;
            var (p, _) = OneShape(shape, Box(16, 96, 240, 160));
            var px = Render(p, 5 * Sec);

            int changes = 0;
            bool previous = IsInked(px, 40, 99);
            for (int x = 41; x < 216; x++)
            {
                bool inked = IsInked(px, x, 99);
                if (inked != previous)
                    changes++;
                previous = inked;
            }
            Assert.True(changes >= 8, $"{dash}: {changes} changes along the top edge");
            // the corners are always inked: the pattern is fitted to them
            Assert.True(IsRed(px, 18, 98), $"{dash}: top left corner");
            Assert.True(IsRed(px, 237, 157), $"{dash}: bottom right corner");
        }

        [Theory]
        [InlineData(ShapeKind.Triangle, 0)]
        [InlineData(ShapeKind.Triangle, 24)]
        [InlineData(ShapeKind.Diamond, 0)]
        [InlineData(ShapeKind.Polygon, 0)]
        public void Dots_are_spaced_evenly_from_the_corner_dots(ShapeKind kind, double radius)
        {
            // a sharp corner's dot is wedged well along both edges, so the dots beside it must be
            // spaced from it, not from the vertex
            var shape = Outline(kind, 14);
            shape.Dash = ShapeDash.Dotted;
            shape.CornerRadius = radius;
            var (p, _) = OneShape(shape, Box(16, 16, 240, 240));
            var dots = Blobs(Render(p, 5 * Sec));
            Assert.True(dots.Count >= 9, $"{kind}: {dots.Count} dots");

            // in order round the outline, every dot on a straight run sits as far from the dot
            // before it as from the one after, the corner's own dot included; only the corner
            // dots, where the run turns, may differ
            using var outline = ShapeRenderer.BuildOutline(new SKRect(16, 16, 240, 240), shape, 1);
            dots = AlongOutline(outline, dots);
            int straight = 0;
            for (int i = 0; i < dots.Count; i++)
            {
                var dot = dots[i];
                var before = dots[(i + dots.Count - 1) % dots.Count] - dot;
                var after = dots[(i + 1) % dots.Count] - dot;
                double cos = (before.X * after.X + before.Y * after.Y) / (before.Length * after.Length);
                if (cos > Math.Cos(Math.PI * 5 / 6))
                    continue;
                straight++;
                double ratio = Math.Min(before.Length, after.Length) / Math.Max(before.Length, after.Length);
                Assert.True(ratio > 0.85,
                    $"{kind} r{radius}: the dot at ({dot.X:0},{dot.Y:0}) is {before.Length:0.0} and {after.Length:0.0} px from its neighbours");
            }
            Assert.True(straight >= 6, $"{kind} r{radius}: {straight} dots on straight runs");
        }

        /// <summary><paramref name="points"/> ordered by where they lie nearest along
        /// <paramref name="outline"/>.</summary>
        private static List<Vec2> AlongOutline(SKPath outline, List<Vec2> points)
        {
            using var measure = new SKPathMeasure(outline, true);
            var samples = new List<(double At, Vec2 Point)>();
            for (float at = 0; at < measure.Length; at += 0.5f)
            {
                measure.GetPosition(at, out var position);
                samples.Add((at, new Vec2(position.X, position.Y)));
            }

            var ordered = new List<(double At, Vec2 Point)>();
            foreach (var point in points)
            {
                var nearest = samples[0];
                foreach (var sample in samples)
                {
                    if ((sample.Point - point).Length < (nearest.Point - point).Length)
                        nearest = sample;
                }
                ordered.Add((nearest.At, point));
            }
            ordered.Sort((x, y) => x.At.CompareTo(y.At));
            return ordered.ConvertAll(o => o.Point);
        }

        /// <summary>The centroids of the separate inked spots on a W×H render.</summary>
        private static List<Vec2> Blobs(byte[] px)
        {
            var seen = new bool[W * H];
            var blobs = new List<Vec2>();
            var stack = new Stack<int>();
            for (int start = 0; start < W * H; start++)
            {
                if (seen[start] || Px(px, start % W, start / W).R <= 128)
                    continue;

                double sx = 0, sy = 0;
                int count = 0;
                seen[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    int x = i % W, y = i / W;
                    sx += x;
                    sy += y;
                    count++;
                    foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                    {
                        if (nx < 0 || ny < 0 || nx >= W || ny >= H || seen[ny * W + nx] || Px(px, nx, ny).R <= 128)
                            continue;
                        seen[ny * W + nx] = true;
                        stack.Push(ny * W + nx);
                    }
                }
                blobs.Add(new Vec2(sx / count, sy / count));
            }
            return blobs;
        }

        // ------------------------------------------------------------------------ line kinds

        private static ShapeContent Ink(ShapeKind kind, double width = 6, string color = "#FFFF0000") =>
            new ShapeContent { Kind = kind, StrokeWidth = width, StrokeColor = color };

        [Fact]
        public void An_arrow_has_its_head_at_the_end_and_a_tail_thinner_than_its_shaft()
        {
            var (p, _) = OneLine(Ink(ShapeKind.Arrow), new Vec2(40, 128), new Vec2(216, 128));
            var px = Render(p, 5 * Sec);

            // the rounded tip lands on the end point
            Assert.True(IsRed(px, 208, 128), "just inside the tip");
            Assert.True(IsBlack(px, 217, 128), "past the tip");
            int tail = InkedInColumn(px, 46), shaft = InkedInColumn(px, 170), head = InkedInColumn(px, 195);
            Assert.True(tail < shaft, $"tail {tail} px, shaft {shaft} px");
            Assert.True(shaft < head, $"shaft {shaft} px, head {head} px");
        }

        [Fact]
        public void A_curved_arrow_follows_its_bow()
        {
            var shape = Ink(ShapeKind.Arrow);
            var (p, item) = OneLine(shape, new Vec2(40, 100), new Vec2(216, 100), bend: 0.3);
            var px = Render(p, 5 * Sec);

            Assert.True(ShapeLineLayout.TryResolve(item.Transform, shape, W, H, out var line));
            var handle = line.Handle;
            Assert.True(IsRed(px, (int)handle.X, (int)handle.Y), $"the bend handle at {handle}");
            Assert.True(IsBlack(px, 128, 100), "the chord's middle");
        }

        [Fact]
        public void A_translucent_arrow_is_one_even_wash()
        {
            // shaft, head and the head's corner stroke overlap; drawn one by one at half alpha the
            // seams would double up
            var (p, _) = OneLine(Ink(ShapeKind.Arrow, 8, "#80FF0000"), new Vec2(40, 128), new Vec2(216, 128));
            var px = Render(p, 5 * Sec);

            int strongest = 0, solid = 0;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int r = Px(px, x, y).R;
                    strongest = Math.Max(strongest, r);
                    if (r >= 126)
                        solid++;
                }
            }
            Assert.InRange(strongest, 126, 130);
            Assert.True(solid > 500, $"{solid} px at the full wash");
        }

        [Fact]
        public void A_dashed_arrow_breaks_its_shaft()
        {
            var shape = Ink(ShapeKind.Arrow);
            shape.Dash = ShapeDash.Dashed;
            var (p, _) = OneLine(shape, new Vec2(30, 128), new Vec2(226, 128));
            var px = Render(p, 5 * Sec);

            int gaps = 0;
            bool previous = IsInked(px, 30, 128);
            for (int x = 31; x < 180; x++)
            {
                bool inked = IsInked(px, x, 128);
                if (previous && !inked)
                    gaps++;
                previous = inked;
            }
            Assert.True(gaps >= 3, $"{gaps} gaps along the shaft");
        }

        [Fact]
        public void A_double_arrow_has_a_head_at_each_end()
        {
            var (p, _) = OneLine(Ink(ShapeKind.DoubleArrow), new Vec2(30, 128), new Vec2(226, 128));
            var px = Render(p, 5 * Sec);

            int start = InkedInColumn(px, 45), middle = InkedInColumn(px, 128), end = InkedInColumn(px, 211);
            Assert.True(start > 2 * middle, $"start {start} px, middle {middle} px");
            Assert.True(end > 2 * middle, $"end {end} px, middle {middle} px");
            Assert.True(IsRed(px, 37, 128) && IsRed(px, 218, 128), "both tips");
            Assert.True(IsBlack(px, 28, 128) && IsBlack(px, 227, 128), "nothing past them");
        }

        [Fact]
        public void A_line_is_a_round_capped_stroke_through_its_endpoints()
        {
            var (p, _) = OneLine(Ink(ShapeKind.Line, 10), new Vec2(40, 128), new Vec2(216, 128));
            var px = Render(p, 5 * Sec);

            Assert.Equal(10, InkedInColumn(px, 128));
            Assert.True(IsRed(px, 37, 128), "the round cap past the start");
            Assert.True(IsBlack(px, 35, 123), "...which is round, not square");
        }

        // ---------------------------------------------------------------------- transitions

        private static ShapeContent WhiteBlock() =>
            new ShapeContent { StrokeWidth = 0, Fill = true, FillColor = "#FFFFFFFF" };

        [Fact]
        public void A_fade_entry_at_its_midpoint_is_half_strength()
        {
            var (p, item) = OneShape(WhiteBlock(), Box(96, 96, 160, 160));
            item.Entry = new Transition { Kind = TransitionKind.Fade, DurationTicks = Sec, Easing = TransitionEasing.Linear };

            var px = Render(p, Sec / 2);
            Assert.InRange(Px(px, 128, 128).R, 124, 132);
        }

        [Fact]
        public void A_slide_moves_the_box_by_its_own_width()
        {
            var (p, item) = OneShape(WhiteBlock(), Box(96, 96, 160, 160));
            item.Entry = new Transition { Kind = TransitionKind.SlideLeft, DurationTicks = Sec, Easing = TransitionEasing.Linear };

            // halfway: half the 64 px box still to travel, entering from the right
            var px = Render(p, Sec / 2);
            Assert.True(IsBlack(px, 100, 128));
            Assert.True(IsInked(px, 130, 128));
            Assert.True(IsInked(px, 190, 128));
            Assert.True(IsBlack(px, 194, 128));
        }

        [Fact]
        public void A_wipe_sweeps_the_arrow_from_its_tail_to_its_head()
        {
            var (p, item) = OneLine(Ink(ShapeKind.Arrow), new Vec2(40, 128), new Vec2(216, 128));
            item.Entry = new Transition { Kind = TransitionKind.Wipe, DurationTicks = Sec, Easing = TransitionEasing.Linear };

            var px = Render(p, Sec / 2);
            Assert.True(IsRed(px, 60, 128), "the tail is revealed");
            Assert.True(IsBlack(px, 205, 128), "the head is not yet");
        }

        [Fact]
        public void A_wipe_reaches_a_head_that_pokes_out_of_its_box()
        {
            // a stretch squashed the box under the head's wings: the band must span the ink, or
            // the wipe would crop the wings it is revealing
            var shape = Ink(ShapeKind.Arrow);
            var (p, item) = OneLine(shape, new Vec2(40, 128), new Vec2(216, 128));
            item.Transform.ScaleY /= 2;
            item.Entry = new Transition { Kind = TransitionKind.Wipe, DurationTicks = Sec, Easing = TransitionEasing.Linear };
            double boxBottom = 128 + item.Transform.ScaleY.Value * H / 2;

            var px = Render(p, (long)(0.98 * Sec));
            int wing = 128 + 16;
            Assert.True(wing > boxBottom + 2, $"the wing row {wing} is inside the box (bottom {boxBottom})");
            Assert.True(IsInked(px, 186, wing), "the head's wing below the box");
        }

        // ----------------------------------------------------------------------------- draw

        private static Transition Draw(long duration = Sec) =>
            new Transition { Kind = TransitionKind.Draw, DurationTicks = duration, Easing = TransitionEasing.Linear };

        /// <summary>The red pixels of a frame, by index (R above half, G low).</summary>
        private static HashSet<int> RedPixels(byte[] px, Func<int, int, bool> where = null)
        {
            var red = new HashSet<int>();
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    var (r, g, _) = Px(px, x, y);
                    if (r > 128 && g < 64 && (where == null || where(x, y)))
                        red.Add(y * W + x);
                }
            }
            return red;
        }

        [Fact]
        public void A_draw_entry_at_its_start_draws_nothing()
        {
            var shape = Outline(ShapeKind.Rectangle);
            shape.Fill = true;
            var (p, item) = OneShape(shape, Box(64, 64, 192, 192));
            item.Entry = Draw();

            Assert.Empty(RedPixels(Render(p, 0)));
            Assert.NotEmpty(RedPixels(Render(p, Sec)));
        }

        [Fact]
        public void An_arrow_drawing_in_grows_from_its_tail_with_the_head_on_the_drawn_tip()
        {
            var (p, item) = OneLine(Ink(ShapeKind.Arrow), new Vec2(40, 128), new Vec2(216, 128));
            item.Entry = Draw();

            var px = Render(p, Sec / 2); // drawn to x = 128
            Assert.True(IsRed(px, 60, 128), "the tail is drawn");
            Assert.True(IsBlack(px, 140, 128), "past the drawn tip");
            Assert.True(IsBlack(px, 205, 128), "the far end is not drawn yet");
            // the head rides the tip: as tall there as the finished arrow's head, far taller than the shaft
            var rest = Render(p, 5 * Sec);
            int head = InkedInColumn(px, 128 - 21), restHead = InkedInColumn(rest, 216 - 21), shaft = InkedInColumn(px, 80);
            Assert.True(head > 3 * shaft, $"head {head} px, shaft {shaft} px");
            Assert.InRange(head, restHead - 2, restHead + 2);
            Assert.True(IsRed(px, 120, 128), "just inside the drawn tip");
        }

        [Fact]
        public void An_arrow_drawing_out_retracts_its_tail_into_the_head()
        {
            var (p, item) = OneLine(Ink(ShapeKind.Arrow), new Vec2(40, 128), new Vec2(216, 128));
            item.Exit = Draw();

            var px = Render(p, 10 * Sec - Sec / 2); // erased to x = 128
            Assert.True(IsBlack(px, 60, 128), "the tail is gone");
            Assert.True(IsBlack(px, 124, 128), "up to the erased point");
            Assert.True(IsRed(px, 150, 128), "the shaft beyond it is still drawn");
            Assert.True(IsRed(px, 208, 128), "and so is the tip");
            Assert.True(InkedInColumn(px, 195) > 3 * InkedInColumn(px, 160), "the head is whole");
        }

        [Fact]
        public void A_curved_arrow_drawing_in_follows_its_bow()
        {
            var shape = Ink(ShapeKind.Arrow);
            var (p, item) = OneLine(shape, new Vec2(40, 100), new Vec2(216, 100), bend: 0.3);
            item.Entry = Draw();
            Assert.True(ShapeLineLayout.TryResolve(item.Transform, shape, W, H, out var line));

            // three quarters drawn: the bow's middle is inked, the end is not
            var px = Render(p, Sec * 3 / 4);
            var handle = line.Handle;
            Assert.True(IsRed(px, (int)handle.X, (int)handle.Y), $"the bend handle at {handle}");
            Assert.True(IsBlack(px, 212, 100), "the end of the path");
            Assert.True(IsBlack(px, 128, 100), "the chord's middle");
        }

        [Fact]
        public void A_double_arrow_drawing_in_has_both_heads()
        {
            var (p, item) = OneLine(Ink(ShapeKind.DoubleArrow), new Vec2(30, 128), new Vec2(226, 128));
            item.Entry = Draw();

            var px = Render(p, Sec * 3 / 4); // drawn to x = 177
            Assert.True(IsRed(px, 37, 128), "the start tip stays put");
            int start = InkedInColumn(px, 45), middle = InkedInColumn(px, 100), end = InkedInColumn(px, 162);
            Assert.True(start > 2 * middle, $"start {start} px, middle {middle} px");
            Assert.True(end > 2 * middle, $"end {end} px, middle {middle} px");
            Assert.True(IsBlack(px, 190, 128), "past the drawn tip");
        }

        [Theory]
        [InlineData(ShapeKind.Line)]
        [InlineData(ShapeKind.Arrow)]
        [InlineData(ShapeKind.DoubleArrow)]
        public void A_dashed_line_drawing_on_and_off_keeps_its_dashes_in_place(ShapeKind kind)
        {
            var shape = Ink(kind);
            shape.Dash = ShapeDash.Dashed;
            var (p, item) = OneLine(shape, new Vec2(30, 128), new Vec2(226, 128), bend: 0.2);
            item.Entry = Draw();
            item.Exit = Draw();
            var rest = RedPixels(Render(p, 5 * Sec));
            Assert.True(ShapeLineLayout.TryResolve(item.Transform, shape, W, H, out var line));

            // the path finely sampled, so a pixel can be placed along it by its nearest sample
            const int samples = 512;
            var control = line.Control.Value;
            var points = new Vec2[samples + 1];
            var along = new double[samples + 1];
            for (int k = 0; k <= samples; k++)
            {
                points[k] = QuadraticCurve.Eval(line.Start, control, line.End, (double)k / samples);
                if (k > 0)
                    along[k] = along[k - 1] + QuadraticCurve.Distance(points[k - 1], points[k]);
            }
            double length = along[samples];

            // the spine's own pixels between two arc lengths, clear of the heads: within a few px
            // of the path, so a head's wings (which reach far off it) never count
            Func<int, int, bool> Spine(double from, double to) => (x, y) =>
            {
                var p0 = new Vec2(x + 0.5, y + 0.5);
                int nearest = 0;
                for (int k = 1; k <= samples; k++)
                {
                    if (QuadraticCurve.Distance(points[k], p0) < QuadraticCurve.Distance(points[nearest], p0))
                        nearest = k;
                }
                return QuadraticCurve.Distance(points[nearest], p0) < 4 && along[nearest] > from && along[nearest] < to;
            };

            // the heads move or resize while the path grows and shrinks; the spine between them
            // may not. A curved path, so the phase is measured along the arc, not the chord. Early
            // in the entry a double arrow's start head is still small (capped by the short path),
            // so its spine starts nearer the tail than at rest.
            const double clear = 40; // a head's length and its tip's inset
            const double shortClear = 33; // the same for the smaller heads of a piece under 100 px
            var moments = new (long At, double From, double To)[]
            {
                (Sec * 4 / 10, shortClear, 0.4 * length - shortClear),
                (10 * Sec - Sec * 7 / 10, 0.3 * length + clear, length - clear),
                (10 * Sec - Sec * 4 / 10, 0.6 * length + shortClear, length - shortClear),
            };
            foreach (var (at, from, to) in moments)
            {
                var frame = RedPixels(Render(p, at), Spine(from, to));
                Assert.True(frame.Count > 20, $"{frame.Count} red px drawn at {at}");
                int moved = frame.Count(i => !rest.Contains(i) && !rest.Contains(i - 1) && !rest.Contains(i + 1));
                Assert.True(moved < 4, $"{moved} red px at {at} where the dashes at rest have none");
            }
        }

        [Fact]
        public void A_rectangle_drawing_in_traces_half_its_outline_while_its_fill_fades_in()
        {
            var shape = Outline(ShapeKind.Rectangle);
            shape.Fill = true;
            shape.FillColor = "#FF0000FF";
            var (p, item) = OneShape(shape, Box(64, 64, 192, 192));
            item.Entry = Draw();

            int whole = RedPixels(Render(p, 5 * Sec)).Count;
            var px = Render(p, Sec / 2);
            int half = RedPixels(px).Count;
            Assert.InRange(half, whole * 0.45, whole * 0.55);
            // traced clockwise from the top's middle: the right side is drawn, the left is not
            Assert.True(IsRed(px, 188, 128), "the right edge");
            Assert.True(IsRed(px, 170, 189), "the bottom's right half");
            Assert.False(IsRed(px, 67, 128), "the left edge");
            Assert.False(IsRed(px, 90, 66), "the top's left half");
            Assert.InRange(Px(px, 128, 128).B, 120, 136);
        }

        [Theory]
        [InlineData(ShapeDash.Dashed)]
        [InlineData(ShapeDash.Dotted)]
        public void A_dashed_rectangle_drawing_out_keeps_its_pattern_in_place(ShapeDash dash)
        {
            var shape = Outline(ShapeKind.Rectangle);
            shape.Dash = dash;
            var (p, item) = OneShape(shape, Box(64, 64, 192, 192));
            item.Exit = Draw();
            var rest = RedPixels(Render(p, 5 * Sec));

            var early = RedPixels(Render(p, 10 * Sec - Sec * 7 / 10)); // 30% erased
            var late = RedPixels(Render(p, 10 * Sec - Sec * 4 / 10));  // 60% erased
            Assert.True(late.Count < early.Count && early.Count < rest.Count);
            // every pixel a later frame inks, the earlier frames ink too (a few antialiased ones
            // at the cut aside): the trace moves, the dashes do not
            Assert.True(late.Count(i => !early.Contains(i)) < 16, "late outside early");
            Assert.True(early.Count(i => !rest.Contains(i)) < 16, "early outside rest");
        }

        // ------------------------------------------------------------------------- surround

        [Fact]
        public void A_shadow_falls_down_and_right()
        {
            var shape = WhiteBlock();
            shape.FillColor = "#FFFF0000";
            var (p, item) = OneShape(shape, Box(96, 96, 160, 160), whiteBackdrop: true);
            item.Surround = new Surround { Kind = SurroundKind.Shadow, Color = 0xFF000000, Size = 0, Distance = 0.2 };
            var px = Render(p, 5 * Sec);

            // 0.2 of the 64 px box along the diagonal: about 9 px each way
            Assert.True(Px(px, 164, 164).R < 64, "shadow below right");
            Assert.Equal((255, 255, 255), Px(px, 92, 92));
            Assert.Equal((255, 255, 255), Px(px, 164, 100));
        }

        [Fact]
        public void A_line_kinds_shadow_scales_exactly_with_the_canvas()
        {
            // measured from the stroke in output pixels, so composing at twice the output size
            // doubles the shadow's offset and spread, the 16 px floor included
            const int size = 128;
            int ShadowBelow(int scale, Surround surround)
            {
                var shape = Ink(ShapeKind.Line, 1);
                var (p, item) = OneLine(shape, new Vec2(20, 40), new Vec2(108, 40), whiteBackdrop: true, width: size, height: size);
                item.Surround = surround;
                int canvas = size * scale;
                var px = Render(p, 5 * Sec, width: canvas, height: canvas);

                // the lowest row in the middle column the shadow darkens
                int column = canvas / 2, lowest = -1;
                for (int y = 0; y < canvas; y++)
                {
                    var (r, g, _) = Px(px, column, y, canvas);
                    if (g < 250 && r < 250)
                        lowest = y;
                }
                return lowest - 40 * scale;
            }

            var hard = new Surround { Kind = SurroundKind.Shadow, Color = 0xFF000000, Size = 0, Distance = 0.5 };
            int offset1 = ShadowBelow(1, hard), offset2 = ShadowBelow(2, hard);
            Assert.InRange(offset1, 5, 8); // 0.5 · 16 · cos 45° ≈ 5.7 px beyond the half-pixel line
            Assert.InRange(offset2, 2 * offset1 - 1, 2 * offset1 + 1);

            var soft = new Surround { Kind = SurroundKind.Glow, Color = 0xFF000000, Size = 0.5 };
            int spread1 = ShadowBelow(1, soft), spread2 = ShadowBelow(2, soft);
            Assert.True(spread1 > 4, $"spread {spread1}");
            Assert.InRange(spread2, 2 * spread1 - 2, 2 * spread1 + 2);
        }

        // ------------------------------------------------------------------- renderer direct

        [Fact]
        public void The_renderer_draws_a_tile_on_its_own()
        {
            // the inspector's picker tiles call the renderer directly with their own scale
            using var bitmap = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
            var box = new SKRect(8, 8, 56, 56);
            ShapeRenderer.Draw(canvas, box, Outline(ShapeKind.Ellipse, 4), 0.5);

            Assert.Equal(Red, bitmap.GetPixel(9, 32)); // 2 tile px of the 4 output px stroke
            Assert.Equal(SKColors.Black, bitmap.GetPixel(11, 32));
            Assert.Equal(box, ShapeRenderer.InkBounds(box, Outline(ShapeKind.Ellipse), 0.5));
            Assert.Null(ShapeRenderer.BuildOutline(box, Ink(ShapeKind.Arrow), 1));
            using var outline = ShapeRenderer.BuildOutline(box, Outline(ShapeKind.Heart), 1);
            Assert.True(outline.Bounds.Left >= box.Left - 0.01 && outline.Bounds.Bottom <= box.Bottom + 0.01, $"{outline.Bounds}");
        }

        // ------------------------------------------------------------------------------- GPU

        /// <summary>The render and the preview draw on the GPU, the tests on the CPU: the two must
        /// agree. Only antialiased edges may differ (the backends rasterize slanted and curved edge
        /// coverage differently, which no shape code controls), so the bar is a mean over the
        /// frame: a mismatch in what is drawn would move it by whole percents.</summary>
        [Theory]
        [InlineData(ShapeKind.Star, 0)]
        [InlineData(ShapeKind.Arrow, 0)]
        [InlineData(ShapeKind.Star, 30)]
        [InlineData(ShapeKind.Arrow, 30)]
        public void Gpu_matches_cpu(ShapeKind kind, double rotation)
        {
            var gpu = GpuSurfaceFactory.TryCreate(out var reason);
            if (gpu == null)
                Assert.Skip("GPU backend unavailable: " + reason);

            try
            {
                var shape = new ShapeContent { Kind = kind, StrokeWidth = 6, Fill = true, Dash = ShapeDash.Dashed, CornerRadius = 6 };
                var (p, item) = ShapeContent.IsLineKind(kind)
                    ? OneLine(shape, new Vec2(40, 180), new Vec2(210, 70), bend: 0.2)
                    : OneShape(shape, Box(48, 48, 208, 208));
                item.Transform.Rotation = rotation;
                var cpuPixels = Render(p, 5 * Sec);
                var gpuPixels = Render(p, 5 * Sec, gpu);

                long total = 0;
                int worst = 0;
                for (int i = 0; i < cpuPixels.Length; i++)
                {
                    int d = Math.Abs(cpuPixels[i] - gpuPixels[i]);
                    total += d;
                    worst = Math.Max(worst, d);
                }

                double mean = total / (double)cpuPixels.Length;
                var detail = $"{kind} at {rotation}°: mean byte deviation {mean:0.####}, worst {worst}";
                Assert.True(mean < (rotation == 0 ? 0.05 : 0.5), detail);
            }
            finally
            {
                gpu.Dispose();
            }
        }
    }
}
