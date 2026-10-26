using System;
using System.IO;
using System.Runtime.InteropServices;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="ObscureContent"/> through <see cref="FrameComposer"/>: what the box does to the
    /// rows beneath it, that it leaves everything outside it and above it alone, how its ramps fade
    /// it, and that it measures in output pixels — on the CPU factory, plus a GPU parity check
    /// where a GPU is available.
    ///
    /// The picture under the box is a test card whose every pixel is predictable: red climbs 2 per
    /// column, green 2 per row, and blue alternates 255/0 in 4px stripes. An untouched pixel is
    /// therefore known exactly, a mosaic turns red into steps and the stripes into their mean, and a
    /// blur leaves the (linear) gradients alone while flattening the stripes.
    /// </summary>
    public class ObscureComposeTests
    {
        private const long Sec = 10_000_000;
        private const int W = 128, H = 128;

        /// <summary>The box most tests use, [36, 100) each way: deliberately not on a multiple of
        /// the 8px cell, so a mosaic grid aligned to the canvas instead of the box would show.</summary>
        private const int BoxMin = 36, BoxMax = 100;

        private static readonly Lazy<string> TestCard = new Lazy<string>(WriteTestCard);

        private static string WriteTestCard()
        {
            // the card is deterministic, so one shared file serves every run: the composer's image
            // cache maps the file it decodes, which rules out both rewriting it and deleting it
            // while a run lasts, and a per-run name would leave one behind in temp every time
            var path = Path.Combine(Path.GetTempPath(), "prisc-obscure-test-card-v1.png");
            if (File.Exists(path))
                return path;

            using var bitmap = new SKBitmap(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                    bitmap.SetPixel(x, y, new SKColor(CardR(x), CardG(y), CardB(x)));
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            // written aside and moved into place, so a concurrent run never decodes half a file
            var staging = path + "." + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(staging, data.ToArray());
            try
            {
                File.Move(staging, path);
            }
            catch (IOException)
            {
                // another run got there first with the same bytes
                File.Delete(staging);
            }

            return path;
        }

        private static byte CardR(int x) => (byte)(x * 2);

        private static byte CardG(int y) => (byte)(y * 2);

        private static byte CardB(int x) => ((x / 4) & 1) == 0 ? (byte)255 : (byte)0;

        // ---------------------------------------------------------------------------- builders

        private static Project NewProject() => new Project
        {
            Output = new OutputSettings { WidthPx = W, HeightPx = H, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
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

        /// <summary>The box's transform: [36, 100) each way on the 128px canvas.</summary>
        private static Transform BoxTransform() => new Transform
        {
            X = (BoxMin + BoxMax) / 2.0 / W,
            Y = (BoxMin + BoxMax) / 2.0 / H,
            Scale = (BoxMax - BoxMin) / (double)W,
            ScaleY = (BoxMax - BoxMin) / (double)H,
        };

        /// <summary>The test card full-frame on the bottom row and an obscure box over it on the
        /// row above.</summary>
        private static Project CardUnderBox(ObscureKind kind, out Item box, double strength = 8)
        {
            var p = NewProject();
            AddItem(p, AddVideoTrack(p, 0), new ImageContent { Path = TestCard.Value });
            box = AddItem(p, AddVideoTrack(p, 1), new ObscureContent { Kind = kind, Strength = strength },
                BoxTransform());
            return p;
        }

        private static byte[] Render(Project p, long timeTicks, ISurfaceFactory factory = null,
            int width = W, int height = H)
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

        private static bool InBox(int x, int y, int margin = 0) =>
            x >= BoxMin - margin && x < BoxMax + margin && y >= BoxMin - margin && y < BoxMax + margin;

        private static void AssertCard(byte[] px, int x, int y)
        {
            var (r, g, b) = Px(px, x, y);
            Assert.True(r == CardR(x) && g == CardG(y) && b == CardB(x),
                $"pixel ({x},{y}) is ({r},{g},{b}), the card has ({CardR(x)},{CardG(y)},{CardB(x)})");
        }

        /// <summary>Every pixel a margin clear of the box is the untouched card.</summary>
        private static void AssertOutsideUntouched(byte[] px, int margin = 1)
        {
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    if (!InBox(x, y, margin))
                        AssertCard(px, x, y);
                }
            }
        }

        private static void AssertWholeFrameUntouched(byte[] px)
        {
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                    AssertCard(px, x, y);
            }
        }

        // ----------------------------------------------------------------------------- modes

        [Fact]
        public void Mosaic_turns_the_box_into_cells_aligned_to_its_corner()
        {
            var p = CardUnderBox(ObscureKind.Mosaic, out _);
            var px = Render(p, 5 * Sec);

            AssertOutsideUntouched(px);

            for (int y = BoxMin; y < BoxMax; y++)
            {
                int previousCellR = -1;
                for (int cell = BoxMin; cell < BoxMax; cell += 8)
                {
                    // one color across the cell's whole width — the grid starts at the box edge (36),
                    // not at a multiple of 8 on the canvas
                    var first = Px(px, cell, y);
                    for (int x = cell + 1; x < cell + 8; x++)
                        Assert.Equal(first, Px(px, x, y));

                    // the stripes average out to mid-blue, the gradient into rising steps
                    Assert.InRange(first.B, 100, 155);
                    Assert.True(first.R > previousCellR, $"cell at x={cell} row {y} does not step up");
                    previousCellR = first.R;
                }
            }

            // and the cells are square: the same color down a cell's height too
            Assert.Equal(Px(px, 50, BoxMin + 8), Px(px, 50, BoxMin + 15));
            Assert.NotEqual(Px(px, 50, BoxMin + 15).G, Px(px, 50, BoxMin + 16).G);
        }

        [Fact]
        public void Blur_flattens_detail_inside_the_box_only()
        {
            var p = CardUnderBox(ObscureKind.Blur, out _);
            var px = Render(p, 5 * Sec);

            AssertOutsideUntouched(px);

            for (int y = BoxMin + 2; y < BoxMax - 2; y++)
            {
                for (int x = BoxMin + 2; x < BoxMax - 2; x++)
                {
                    var (r, g, b) = Px(px, x, y);
                    // the 4px stripes are gone...
                    Assert.InRange(b, 100, 155);
                    // ...while the linear gradients survive a blur unchanged (it reads past the box
                    // edge, so even the rim keeps them rather than darkening against nothing)
                    Assert.InRange(r, CardR(x) - 4, CardR(x) + 4);
                    Assert.InRange(g, CardG(y) - 4, CardG(y) + 4);
                }
            }
        }

        [Fact]
        public void Blackout_paints_the_box_black()
        {
            var p = CardUnderBox(ObscureKind.Blackout, out _);
            var px = Render(p, 5 * Sec);

            AssertOutsideUntouched(px);
            for (int y = BoxMin; y < BoxMax; y++)
            {
                for (int x = BoxMin; x < BoxMax; x++)
                    Assert.Equal(((byte)0, (byte)0, (byte)0), Px(px, x, y));
            }
        }

        // ---------------------------------------------------------------------------- z-order

        [Theory]
        [InlineData(ObscureKind.Mosaic)]
        [InlineData(ObscureKind.Blur)]
        [InlineData(ObscureKind.Blackout)]
        public void An_item_on_a_higher_row_stays_crisp_over_the_box(ObscureKind kind)
        {
            var p = CardUnderBox(kind, out _);
            // a red square [56, 72) on top of everything
            AddItem(p, AddVideoTrack(p, 2), new SolidContent { Color = "#FFFF0000" },
                new Transform { Scale = 0.125 });

            var px = Render(p, 5 * Sec);

            for (int y = 57; y < 71; y++)
            {
                for (int x = 57; x < 71; x++)
                    Assert.Equal(((byte)255, (byte)0, (byte)0), Px(px, x, y));
            }

            // while the box around it is still obscured
            int ox = BoxMin + 2, oy = BoxMin + 2;
            Assert.NotEqual((CardR(ox), CardG(oy), CardB(ox)), Px(px, ox, oy));
        }

        [Fact]
        public void A_box_beneath_the_picture_hides_nothing()
        {
            // the box on the bottom row, the card on the row above it: the box only ever rewrites
            // what is under it, and nothing is
            var p = NewProject();
            AddItem(p, AddVideoTrack(p, 0), new ObscureContent { Kind = ObscureKind.Blackout }, BoxTransform());
            AddItem(p, AddVideoTrack(p, 1), new ImageContent { Path = TestCard.Value });

            AssertWholeFrameUntouched(Render(p, 5 * Sec));
        }

        // -------------------------------------------------------------------------- animation

        [Fact]
        public void Entry_ramp_starts_clear_and_fades_the_obscuring_in()
        {
            var p = CardUnderBox(ObscureKind.Blackout, out var box);
            box.Entry = new Transition { Kind = TransitionKind.Ramp, DurationTicks = 1 * Sec };

            AssertWholeFrameUntouched(Render(p, 0));

            // halfway: a linear fade puts half the black over the card
            var half = Render(p, Sec / 2);
            AssertOutsideUntouched(half);
            int x = 80, y = 60;
            Assert.InRange(Px(half, x, y).R, CardR(x) / 2 - 3, CardR(x) / 2 + 3);
            Assert.InRange(Px(half, x, y).G, CardG(y) / 2 - 3, CardG(y) / 2 + 3);

            // once the ramp is over, fully obscured
            Assert.Equal(((byte)0, (byte)0, (byte)0), Px(Render(p, 2 * Sec), x, y));
        }

        [Fact]
        public void Exit_ramp_fades_the_obscuring_back_out()
        {
            var p = CardUnderBox(ObscureKind.Mosaic, out var box);
            box.Exit = new Transition { Kind = TransitionKind.Ramp, DurationTicks = 1 * Sec };

            // before the ramp: obscured
            var before = Render(p, 8 * Sec);
            Assert.InRange(Px(before, 60, 60).B, 100, 155);

            // its last tick: as good as clear
            AssertWholeFrameUntouched(Render(p, box.TimelineEndTicks - 1));
        }

        [Fact]
        public void Zero_opacity_hides_nothing()
        {
            var p = CardUnderBox(ObscureKind.Blackout, out var box);
            box.Transform.Opacity = 0;

            AssertWholeFrameUntouched(Render(p, 5 * Sec));
        }

        // --------------------------------------------------------------------------- geometry

        [Fact]
        public void Rotation_turns_the_box()
        {
            var p = CardUnderBox(ObscureKind.Blackout, out var box);
            box.Transform.Rotation = 45;
            var px = Render(p, 5 * Sec);

            // the center is covered, a corner of the unrotated box is not, and a point past the
            // unrotated top edge (but inside the diamond) is
            Assert.Equal(((byte)0, (byte)0, (byte)0), Px(px, 68, 68));
            AssertCard(px, BoxMin + 2, BoxMin + 2);
            Assert.Equal(((byte)0, (byte)0, (byte)0), Px(px, 68, BoxMin - 4));
        }

        [Fact]
        public void Rotated_mosaic_and_blur_still_obscure_the_turned_box()
        {
            foreach (var kind in new[] { ObscureKind.Mosaic, ObscureKind.Blur })
            {
                var p = CardUnderBox(kind, out var box);
                box.Transform.Rotation = 45;
                var px = Render(p, 5 * Sec);

                // inside the diamond past the unrotated edge: the stripes are gone there too
                Assert.InRange(Px(px, 68, BoxMin - 4).B, 100, 155);
                AssertCard(px, BoxMin + 2, BoxMin + 2);
            }
        }

        [Fact]
        public void A_mosaic_cell_hanging_off_the_frame_still_hides_what_is_on_it()
        {
            // the box starts 4px left of the frame, so its first 8px cell is half off-canvas: the
            // half that is on it must still be one flat cell, not the picture showing through a
            // layer that came back empty where the cell's center fell outside the frame
            var p = NewProject();
            AddItem(p, AddVideoTrack(p, 0), new ImageContent { Path = TestCard.Value });
            AddItem(p, AddVideoTrack(p, 1), new ObscureContent { Kind = ObscureKind.Mosaic },
                new Transform { X = 28.0 / W, Y = 0.5, Scale = 64.0 / W, ScaleY = 0.5 });
            var px = Render(p, 5 * Sec);

            for (int y = 40; y < 88; y++)
            {
                var first = Px(px, 0, y);
                for (int x = 1; x < 4; x++)
                    Assert.Equal(first, Px(px, x, y));
            }
        }

        [Fact]
        public void Strength_is_output_pixels_on_a_scaled_canvas()
        {
            // composing at twice the output size (what a preview larger than the output does) must
            // draw the same picture twice as big: 8 output px cells are 16 canvas px wide
            var p = CardUnderBox(ObscureKind.Mosaic, out _);
            const int scale = 2;
            var px = Render(p, 5 * Sec, width: W * scale, height: H * scale);

            int y = 70 * scale;
            for (int cell = BoxMin * scale; cell < BoxMax * scale; cell += 8 * scale)
            {
                var first = Px(px, cell, y, W * scale);
                for (int x = cell + 1; x < cell + 8 * scale; x++)
                    Assert.Equal(first, Px(px, x, y, W * scale));
            }
        }

        /// <summary>
        /// The preview composes straight onto Avalonia's window canvas, clipped to the letterboxed
        /// video rect with the window's chrome already painted around it (PreviewDrawOperation).
        /// A blur at the frame's edge reads its backdrop past the box, and must find the frame's
        /// edge there — exactly as the render's own surface ends — never the chrome outside the
        /// clip, or the preview would show a fringe the export does not have.
        /// </summary>
        [Theory]
        [InlineData(ObscureKind.Blur)]
        [InlineData(ObscureKind.Mosaic)]
        public void Backdrop_stops_at_the_previews_clip(ObscureKind kind)
        {
            var p = NewProject();
            AddItem(p, AddVideoTrack(p, 0), new SolidContent { Color = "#FF000000" });
            // a box flush with the frame's top-left corner, strong enough to reach well past it
            AddItem(p, AddVideoTrack(p, 1), new ObscureContent { Kind = kind, Strength = 16 },
                new Transform { X = 0.125, Y = 0.125, Scale = 0.25, ScaleY = 0.25 });

            const int margin = 40, outer = W + 2 * margin;
            using var factory = new CpuSurfaceFactory();
            using var surface = factory.CreateSurface(outer, outer);
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(0, 255, 0)); // the "chrome": bright green around the frame
            canvas.Save();
            canvas.ClipRect(SKRect.Create(margin, margin, W, H));
            canvas.Translate(margin, margin);
            FrameComposer.Compose(p, 5 * Sec, null, canvas, W, H);
            canvas.Restore();

            int rowBytes = outer * 4;
            var native = Marshal.AllocHGlobal(rowBytes * outer);
            try
            {
                Assert.True(factory.TryReadPixels(surface, outer, outer, native, rowBytes));
                var pixels = new byte[rowBytes * outer];
                Marshal.Copy(native, pixels, 0, pixels.Length);
                for (int y = 0; y < 32; y++)
                {
                    for (int x = 0; x < 32; x++)
                    {
                        var (_, g, _) = Px(pixels, margin + x, margin + y, outer);
                        Assert.True(g < 8, $"chrome green bled into the frame at ({x},{y}): {g}");
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        // ------------------------------------------------------------------------------- GPU

        /// <summary>The backdrop filters run on the GPU in the render and the preview, and on the
        /// CPU here: the two must agree. Axis-aligned, to the byte (the blur's rounding aside);
        /// turned, only the anti-aliased rim may differ — the backends rasterize edge coverage
        /// differently, which no obscure code controls — so the bar is a mean over the frame.</summary>
        [Theory]
        [InlineData(ObscureKind.Mosaic, 0)]
        [InlineData(ObscureKind.Blur, 0)]
        [InlineData(ObscureKind.Blackout, 0)]
        [InlineData(ObscureKind.Mosaic, 30)]
        [InlineData(ObscureKind.Blur, 30)]
        [InlineData(ObscureKind.Blackout, 30)]
        public void Gpu_matches_cpu(ObscureKind kind, double rotation)
        {
            var gpu = GpuSurfaceFactory.TryCreate(out var reason);
            if (gpu == null)
                Assert.Skip("GPU backend unavailable: " + reason);

            try
            {
                var p = CardUnderBox(kind, out var box);
                box.Transform.Rotation = rotation;
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
                var detail = $"{kind} at {rotation}°: mean byte deviation {mean:0.###}, worst {worst}";
                if (rotation == 0)
                    Assert.True(worst <= 1, detail);
                else
                    Assert.True(mean < 0.5, detail);
            }
            finally
            {
                gpu.Dispose();
            }
        }
    }
}
