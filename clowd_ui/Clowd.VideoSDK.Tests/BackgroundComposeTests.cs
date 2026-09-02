using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// A <see cref="BackgroundContent"/> item through <see cref="FrameComposer.Compose"/> on the
    /// CPU factory: it draws, it is boxed like a solid and clipped like a picture, it obeys
    /// hidden tracks and item spans, and its loop phase is a pure function of the project ticks
    /// the composer receives (the same instant twice is byte-identical; a loop moves between
    /// instants and repeats one period later; the composer agrees with the renderer's own
    /// entry point at the same time).
    /// </summary>
    public class BackgroundComposeTests
    {
        private const long Sec = 10_000_000;
        private const int W = 64, H = 64;

        // ---------------------------------------------------------------------------- builders

        private static Project NewProject(int w = W, int h = H) => new Project
        {
            Output = new OutputSettings { WidthPx = w, HeightPx = h, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
        };

        private static Track AddVideoTrack(Project p, int order = 0, bool hidden = false)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = order, Hidden = hidden };
            p.Tracks.Add(track);
            return track;
        }

        private static Item AddItem(Project p, Track track, ItemContent content,
            long start = 0, long duration = 200 * Sec)
        {
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = start,
                DurationTicks = duration,
                Content = content,
            };
            p.Items.Add(item);
            return item;
        }

        /// <summary>A project holding exactly one background item on one visible track.</summary>
        private static (Project Project, Item Item) OneBackground(string style, string theme = null,
            double speed = 1.0, int w = W, int h = H, string color = null)
        {
            var p = NewProject(w, h);
            var item = AddItem(p, AddVideoTrack(p), new BackgroundContent
            {
                Style = style,
                Theme = theme,
                AnimationSpeed = speed,
                Color = color ?? BackgroundContent.DefaultColor,
            });
            return (p, item);
        }

        private static byte[] Render(Project p, long timeTicks, int w = W, int h = H)
        {
            using var factory = new CpuSurfaceFactory();
            return Render(factory, p, timeTicks, w, h);
        }

        private static byte[] Render(ISurfaceFactory factory, Project p, long timeTicks, int w, int h)
        {
            using var surface = factory.CreateSurface(w, h);
            FrameComposer.Compose(p, timeTicks, null, surface.Canvas, w, h);

            int rowBytes = w * 4;
            var native = Marshal.AllocHGlobal(rowBytes * h);
            try
            {
                Assert.True(factory.TryReadPixels(surface, w, h, native, rowBytes));
                var pixels = new byte[rowBytes * h];
                Marshal.Copy(native, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        private static (byte B, byte G, byte R, byte A) Px(byte[] bgra, int x, int y, int w = W)
        {
            int i = y * w * 4 + x * 4;
            return (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
        }

        private static bool IsBlack((byte B, byte G, byte R, byte A) px, int tolerance = 2)
            => px.R <= tolerance && px.G <= tolerance && px.B <= tolerance;

        private static int CountNonBlack(byte[] bgra, int w, int h)
        {
            int n = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (!IsBlack(Px(bgra, x, y, w)))
                        n++;
            return n;
        }

        private static int MaxChannelDifference(byte[] a, byte[] b)
        {
            Assert.Equal(a.Length, b.Length);
            int max = 0;
            for (int i = 0; i < a.Length; i++)
                max = Math.Max(max, Math.Abs(a[i] - b[i]));
            return max;
        }

        public static IEnumerable<object[]> AllStyles()
            => BackgroundCatalog.Styles.Select(s => new object[] { s.Id });

        /// <summary>The styles that draw artwork — everything but the solid fill, whose whole
        /// picture is one color and which therefore fails every "this is a picture" sweep by
        /// design (see <see cref="Solid_style_fills_the_box_with_the_items_color"/>).</summary>
        public static IEnumerable<object[]> ArtStyles()
            => BackgroundCatalog.Styles.Where(s => !s.IsSolid).Select(s => new object[] { s.Id });

        public static IEnumerable<object[]> AnimatedStyles()
            => BackgroundCatalog.Styles.Where(s => s.IsAnimated).Select(s => new object[] { s.Id });

        public static IEnumerable<object[]> StaticStyles()
            => BackgroundCatalog.Styles.Where(s => !s.IsAnimated).Select(s => new object[] { s.Id });

        // ------------------------------------------------------------------------------ drawing

        [Theory]
        [MemberData(nameof(ArtStyles))]
        public void Default_transform_fills_the_whole_canvas(string style)
        {
            var (p, _) = OneBackground(style);
            var px = Render(p, 5 * Sec);

            // every pixel opaque and not one flat colour; the frame is cleared to black first,
            // and the dark grounds (abyss, layered-steps) legitimately hold a few near-black
            // pixels, so allow under a tenth of the frame at the cleared colour
            for (int i = 3; i < px.Length; i += 4)
                Assert.Equal(255, px[i]);
            int nearBlack = W * H - CountNonBlack(px, W, H);
            Assert.True(nearBlack < W * H / 10, $"{style}: {nearBlack} near-black pixels of {W * H}");
            var distinct = new HashSet<uint>();
            for (int i = 0; i < px.Length; i += 4)
                distinct.Add(BitConverter.ToUInt32(px, i));
            Assert.True(distinct.Count >= 8, $"{style}: only {distinct.Count} distinct colours");
        }

        [Fact]
        public void One_item_project_produces_a_non_blank_frame()
        {
            var (p, _) = OneBackground("big-sur");
            var px = Render(p, 0);
            Assert.True(CountNonBlack(px, W, H) > W * H * 9 / 10);
        }

        [Fact]
        public void Scale_half_paints_only_the_centered_box()
        {
            var (p, item) = OneBackground("stacked-waves");
            item.Transform.Scale = 0.5; // 32x32 centered on 64x64

            var px = Render(p, 0);
            // the outer ring stays at the cleared black; the box is painted edge to edge
            Assert.True(IsBlack(Px(px, 2, 2)));
            Assert.True(IsBlack(Px(px, W - 3, H - 3)));
            Assert.True(IsBlack(Px(px, 10, 32)));
            Assert.True(IsBlack(Px(px, 32, 10)));
            Assert.False(IsBlack(Px(px, 17, 17)));
            Assert.False(IsBlack(Px(px, 46, 46)));
            Assert.False(IsBlack(Px(px, 32, 32)));
            int painted = CountNonBlack(px, W, H);
            Assert.InRange(painted, 32 * 32 - 40, 32 * 32 + 8);
        }

        [Fact]
        public void ScaleY_stretches_the_box_independently_of_the_width()
        {
            var (p, item) = OneBackground("gradient", "sunset");
            item.Transform.Scale = 1.0;
            item.Transform.ScaleY = 0.25; // rows 24..40

            var px = Render(p, 0);
            Assert.False(IsBlack(Px(px, 2, H / 2)));
            Assert.False(IsBlack(Px(px, W - 3, H / 2)));
            Assert.True(IsBlack(Px(px, W / 2, 2)));
            Assert.True(IsBlack(Px(px, W / 2, H - 3)));
        }

        /// <summary>
        /// The whole point of a wallpaper's free resize is a box of a ratio the canvas does not
        /// have, and that box is two independent numbers in the file (Scale for the width, ScaleY
        /// for the height, no ratio field to re-derive either from). Squash one through the
        /// session the way an edge-handle drag writes it, save it, load it back, and the composed
        /// frame has to show the squashed box, not a canvas-filling one; then the same in the
        /// other axis from the reloaded project.
        /// </summary>
        [Fact]
        public void A_non_uniform_resize_survives_a_save_and_reload()
        {
            var session = new EditorSession(NewProject(), null, null);
            var added = session.AddBackground(0, 200 * Sec);
            Assert.NotNull(added);
            session.EditItem(added.Id, i =>
            {
                var background = (BackgroundContent)i.Content;
                background.Style = "gradient";
                background.Theme = "sunset";
            });

            // a bottom-edge drag: full width, a quarter of the height (rows 24..40 of 64)
            session.EditItem(added.Id, i => i.Transform.ScaleY = 0.25, "gizmo:resize");

            var squashed = Project.FromJson(session.Project.ToJson());
            var squashedItem = Assert.Single(squashed.Items);
            Assert.Equal(1.0, squashedItem.Transform.Scale);
            Assert.Equal(0.25, squashedItem.Transform.ScaleY);
            Assert.Null(squashedItem.Transform.Aspect);
            Assert.False(squashedItem.Transform.AspectStretch);

            var px = Render(squashed, 0);
            Assert.False(IsBlack(Px(px, 2, H / 2)));
            Assert.False(IsBlack(Px(px, W - 3, H / 2)));
            Assert.True(IsBlack(Px(px, W / 2, 2)));
            Assert.True(IsBlack(Px(px, W / 2, H - 3)));

            // then a corner drag on the reloaded project: a quarter of the width, the full height
            // (columns 24..40): both axes rewritten apart, neither pulled along by the other
            var again = new EditorSession(squashed, null, null);
            again.EditItem(squashedItem.Id, i =>
            {
                i.Transform.Scale = 0.25;
                i.Transform.ScaleY = 1.0;
            }, "gizmo:resize");

            var narrowed = Project.FromJson(again.Project.ToJson());
            var narrowedItem = Assert.Single(narrowed.Items);
            Assert.Equal(0.25, narrowedItem.Transform.Scale);
            Assert.Equal(1.0, narrowedItem.Transform.ScaleY);

            px = Render(narrowed, 0);
            Assert.False(IsBlack(Px(px, W / 2, 2)));
            Assert.False(IsBlack(Px(px, W / 2, H - 3)));
            Assert.True(IsBlack(Px(px, 2, H / 2)));
            Assert.True(IsBlack(Px(px, W - 3, H / 2)));
        }

        /// <summary>
        /// The composed counterpart of the placement test: Width % and Height % are canvas
        /// fractions, so a 120% x 80% wallpaper paints the full width and the middle 80% of the
        /// rows on a 64x64 output, and after the output is changed to 64x32 (the item untouched)
        /// the same two numbers paint the full width and the middle 80% of the new, shorter frame.
        /// </summary>
        [Fact]
        public void Canvas_fractions_are_reinterpreted_against_a_new_output_size()
        {
            var (p, item) = OneBackground("gradient", "sunset");
            item.Transform.Scale = 1.2;
            item.Transform.ScaleY = 0.8;

            // 64x64: box rows 6.4..57.6, columns -6.4..70.4 (clipped to the frame)
            var px = Render(p, 0);
            Assert.False(IsBlack(Px(px, 0, H / 2)));
            Assert.False(IsBlack(Px(px, W - 1, H / 2)));
            Assert.False(IsBlack(Px(px, W / 2, 9)));
            Assert.False(IsBlack(Px(px, W / 2, 54)));
            Assert.True(IsBlack(Px(px, W / 2, 3)));
            Assert.True(IsBlack(Px(px, W / 2, 60)));

            // the resolution picker's edit: only the output changes
            p.Output.WidthPx = W;
            p.Output.HeightPx = H / 2;
            Assert.Equal(1.2, item.Transform.Scale);
            Assert.Equal(0.8, item.Transform.ScaleY);

            // 64x32: box rows 3.2..28.8, still edge to edge horizontally
            const int h2 = H / 2;
            px = Render(p, 0, W, h2);
            Assert.False(IsBlack(Px(px, 0, h2 / 2)));
            Assert.False(IsBlack(Px(px, W - 1, h2 / 2)));
            Assert.False(IsBlack(Px(px, W / 2, 6)));
            Assert.False(IsBlack(Px(px, W / 2, 26)));
            Assert.True(IsBlack(Px(px, W / 2, 1)));
            Assert.True(IsBlack(Px(px, W / 2, 30)));
        }

        [Fact]
        public void Cover_semantics_fill_a_box_of_any_aspect()
        {
            // a 900x600 wallpaper in a tall 16x64 box must still paint every pixel of the box
            var (p, item) = OneBackground("stacked-waves");
            item.Transform.Scale = 0.25;
            item.Transform.ScaleY = 1.0;

            var px = Render(p, 0);
            for (int y = 0; y < H; y++)
            {
                Assert.False(IsBlack(Px(px, 24, y)), $"row {y} unpainted inside the box");
                Assert.False(IsBlack(Px(px, 39, y)), $"row {y} unpainted inside the box");
            }
            Assert.True(IsBlack(Px(px, 10, H / 2)));
            Assert.True(IsBlack(Px(px, 54, H / 2)));
        }

        [Fact]
        public void Composed_placement_is_resolution_independent()
        {
            // the 128 render box-downsampled 2x2 matches the 64 render: the item box is
            // canvas-relative and the cover matrix inside it scales with the box
            foreach (var (style, theme) in new[] { ("gradient", "sunrise"), ("stacked-waves", "source"), ("explode", null) })
            {
                var (small, _) = OneBackground(style, theme);
                var (large, _) = OneBackground(style, theme, w: 128, h: 128);
                var s = Render(small, 0);
                var l = Render(large, 0, 128, 128);
                int worst = 0;
                long total = 0;
                for (int y = 0; y < 64; y++)
                {
                    for (int x = 0; x < 64; x++)
                    {
                        var sp = Px(s, x, y, 64);
                        var a = Px(l, 2 * x, 2 * y, 128);
                        var b = Px(l, 2 * x + 1, 2 * y, 128);
                        var c = Px(l, 2 * x, 2 * y + 1, 128);
                        var d = Px(l, 2 * x + 1, 2 * y + 1, 128);
                        int r = (a.R + b.R + c.R + d.R) / 4, g = (a.G + b.G + c.G + d.G) / 4, bl = (a.B + b.B + c.B + d.B) / 4;
                        int diff = Math.Max(Math.Abs(r - sp.R), Math.Max(Math.Abs(g - sp.G), Math.Abs(bl - sp.B)));
                        worst = Math.Max(worst, diff);
                        total += diff;
                    }
                }
                double mean = total / (64.0 * 64.0);
                Assert.True(worst <= 96 && mean < 2.0, $"{style}/{theme}: 128 differs from 64 by up to {worst}, mean {mean:F2}");
            }
        }

        // --------------------------------------------------------------------------- visibility

        [Fact]
        public void Hidden_track_draws_nothing()
        {
            var p = NewProject();
            AddItem(p, AddVideoTrack(p, hidden: true), new BackgroundContent { Style = "big-sur" });
            var px = Render(p, 0);
            Assert.Equal(0, CountNonBlack(px, W, H));
        }

        [Fact]
        public void Item_outside_its_span_does_not_draw()
        {
            var p = NewProject();
            AddItem(p, AddVideoTrack(p), new BackgroundContent { Style = "gradient", Theme = "glacier" },
                start: 2 * Sec, duration: 2 * Sec);

            Assert.Equal(0, CountNonBlack(Render(p, 1 * Sec), W, H));
            Assert.True(CountNonBlack(Render(p, 3 * Sec), W, H) > W * H * 9 / 10);
            Assert.Equal(0, CountNonBlack(Render(p, 4 * Sec), W, H)); // exclusive end
        }

        [Fact]
        public void Zero_opacity_draws_nothing_and_half_opacity_halves_the_colour()
        {
            var (p, item) = OneBackground("layered-steps");
            var full = Render(p, 0);

            item.Transform.Opacity = 0;
            Assert.Equal(0, CountNonBlack(Render(p, 0), W, H));

            item.Transform.Opacity = 0.5;
            var half = Render(p, 0);
            // the ground of layered-steps is #140021 (nearly black); test the bright step pixels
            int checkedPixels = 0;
            for (int y = 0; y < H; y += 4)
            {
                for (int x = 0; x < W; x += 4)
                {
                    var f = Px(full, x, y);
                    if (f.R + f.G + f.B < 120)
                        continue;
                    var h = Px(half, x, y);
                    Assert.InRange(h.R, f.R / 2 - 2, f.R / 2 + 2);
                    Assert.InRange(h.G, f.G / 2 - 2, f.G / 2 + 2);
                    Assert.InRange(h.B, f.B / 2 - 2, f.B / 2 + 2);
                    checkedPixels++;
                }
            }
            Assert.True(checkedPixels > 20);
        }

        [Fact]
        public void Rotated_and_masked_background_stays_inside_its_rect()
        {
            var (p, item) = OneBackground("gradient", "peach-cream");
            item.Transform.Scale = 0.5;
            item.Transform.Rotation = 30;
            item.Transform.Mask = new Mask { Shape = MaskShape.Squircle };

            var px = Render(p, 0);
            // a 32x32 squircle rotated 30 degrees about the centre stays within the 45 px
            // circumscribed circle; the canvas corners are well outside it
            Assert.True(IsBlack(Px(px, 1, 1)));
            Assert.True(IsBlack(Px(px, W - 2, 1)));
            Assert.True(IsBlack(Px(px, 1, H - 2)));
            Assert.True(IsBlack(Px(px, W - 2, H - 2)));
            Assert.False(IsBlack(Px(px, W / 2, H / 2)));
            int painted = CountNonBlack(px, W, H);
            // fewer pixels than the unmasked 32x32 box: the squircle rounds the corners off
            Assert.InRange(painted, 600, 32 * 32 - 1);
        }

        [Fact]
        public void Circle_mask_leaves_the_box_corners_unpainted()
        {
            var (p, item) = OneBackground("monterey", "light");
            item.Transform.Scale = 0.5;
            item.Transform.Mask = new Mask { Shape = MaskShape.Circle };

            var px = Render(p, 0);
            Assert.True(IsBlack(Px(px, 17, 17)));  // box corner, outside the inscribed circle
            Assert.True(IsBlack(Px(px, 46, 46)));
            Assert.False(IsBlack(Px(px, 32, 32)));
            Assert.False(IsBlack(Px(px, 32, 18)));  // on the circle's vertical axis, inside
        }

        [Fact]
        public void Wipe_entry_paints_only_the_revealed_band()
        {
            var (p, item) = OneBackground("gradient", "orchid");
            item.Entry = new Transition { Kind = TransitionKind.Wipe, DurationTicks = 2 * Sec };

            var px = Render(p, 1 * Sec); // halfway through the wipe: the left half is revealed
            Assert.False(IsBlack(Px(px, 4, H / 2)));
            Assert.True(IsBlack(Px(px, W - 4, H / 2)));
        }

        [Fact]
        public void Background_composites_beneath_a_higher_order_solid()
        {
            var p = NewProject();
            AddItem(p, AddVideoTrack(p, order: 0), new BackgroundContent { Style = "big-sur" });
            var over = AddItem(p, AddVideoTrack(p, order: 1), new SolidContent { Color = "#FF00FF00" });
            over.Transform.Scale = 0.5;

            var px = Render(p, 0);
            var centre = Px(px, W / 2, H / 2);
            Assert.Equal((0, 255, 0), (centre.R, centre.G, centre.B));
            Assert.False(IsBlack(Px(px, 2, 2))); // the wallpaper shows around the solid
        }

        /// <summary>The solid style draws no artwork at all: the very box a wallpaper would have
        /// covered, filled flat with the item's own color. A missing or unparseable color is
        /// Clowd blue rather than nothing, and an alpha composites over what is behind.</summary>
        [Fact]
        public void Solid_style_fills_the_box_with_the_items_color()
        {
            var (p, item) = OneBackground(BackgroundCatalog.SolidStyle, color: "#FFFF0000");
            var px = Render(p, 5 * Sec);
            for (int i = 0; i < px.Length; i += 4)
                Assert.Equal((0, 0, 255, 255), (px[i], px[i + 1], px[i + 2], px[i + 3]));

            // the default color is Clowd blue, and an unparseable one falls back to it
            var (blue, _) = OneBackground(BackgroundCatalog.SolidStyle);
            var (bad, _) = OneBackground(BackgroundCatalog.SolidStyle, color: "not-a-color");
            var expected = Render(blue, 0);
            var centre = Px(expected, W / 2, H / 2);
            Assert.Equal((0x00, 0xAF, 0xF0), (centre.R, centre.G, centre.B));
            Assert.Equal(expected, Render(bad, 0));

            // and it is boxed like any other background: half scale paints the centered half
            ((BackgroundContent)item.Content).Color = "#FFFF0000";
            item.Transform.Scale = 0.5;
            item.Transform.ScaleY = 0.5;
            var half = Render(p, 0);
            Assert.True(IsBlack(Px(half, 2, 2)));
            var inside = Px(half, 32, 32);
            Assert.Equal((0, 0, 255), (inside.B, inside.G, inside.R));

            // a translucent fill composites rather than replacing: half red over black
            ((BackgroundContent)item.Content).Color = "#80FF0000";
            item.Transform.Scale = 1.0;
            item.Transform.ScaleY = 1.0;
            var faded = Px(Render(p, 0), 32, 32);
            Assert.InRange(faded.R, 126, 130);
            Assert.Equal(0, faded.G);
        }

        // -------------------------------------------------------------------------------- time

        [Theory]
        [MemberData(nameof(AllStyles))]
        public void Same_timeline_position_is_byte_identical_twice(string style)
        {
            var (p, _) = OneBackground(style);
            long t = 17 * Sec + 1234567;
            Assert.Equal(Render(p, t), Render(p, t));
        }

        [Theory]
        [MemberData(nameof(AnimatedStyles))]
        public void Animated_background_differs_between_two_positions_and_repeats_after_a_period(string styleId)
        {
            var style = BackgroundCatalog.Find(styleId);
            var (p, _) = OneBackground(styleId, w: 96, h: 96);
            long t = 3 * Sec;

            var at = Render(p, t, 96, 96);
            var later = Render(p, t + style.PeriodTicks / 3, 96, 96);
            var period = Render(p, t + style.PeriodTicks, 96, 96);

            Assert.True(MaxChannelDifference(at, later) > 32, $"{styleId}: no motion across a third of a period");
            Assert.Equal(at, period);
        }

        [Theory]
        [MemberData(nameof(StaticStyles))]
        public void Static_background_ignores_time(string styleId)
        {
            var (p, _) = OneBackground(styleId);
            Assert.Equal(Render(p, 0), Render(p, 37 * Sec));
        }

        [Fact]
        public void Animation_speed_two_at_t_equals_speed_one_at_two_t()
        {
            var (fast, _) = OneBackground("moving-blob", speed: 2.0);
            var (normal, _) = OneBackground("moving-blob", speed: 1.0);
            Assert.Equal(Render(fast, 7 * Sec), Render(normal, 14 * Sec));
        }

        /// <summary>The composer's phase clock is global project time, not time since the
        /// item's start: moving the item's start does not move the animation.</summary>
        [Fact]
        public void Phase_is_global_project_time_not_item_local()
        {
            var (a, ia) = OneBackground("moving-corners");
            var (b, ib) = OneBackground("moving-corners");
            ia.TimelineStartTicks = 0;
            ib.TimelineStartTicks = 9 * Sec;
            long t = 20 * Sec;
            Assert.Equal(Render(a, t), Render(b, t));
        }

        /// <summary>The composer feeds the renderer exactly the ticks it received: the composed
        /// frame equals <see cref="BackgroundRenderer.Draw"/> at the same project seconds, so an
        /// inspector tile driven by the playhead shows the preview's (and the export's) frame.</summary>
        [Theory]
        [MemberData(nameof(AnimatedStyles))]
        public void Composer_agrees_with_the_renderer_entry_point_at_the_same_time(string styleId)
        {
            var (p, _) = OneBackground(styleId);
            long t = 41 * Sec + 5_000_000; // 41.5 s

            var composed = Render(p, t);
            using var bitmap = new SKBitmap(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Black);
                BackgroundRenderer.Draw(canvas, SKRect.Create(0, 0, W, H), styleId, null, 41.5);
                canvas.Flush();
            }
            Assert.Equal(composed, bitmap.Bytes.ToArray());
        }

        // ----------------------------------------------------------------------------- fallback

        [Fact]
        public void Unknown_style_and_theme_draw_the_defaults()
        {
            var (unknown, _) = OneBackground("no-such-style", "no-such-theme");
            var (bigSur, _) = OneBackground("big-sur", "default");
            Assert.Equal(Render(bigSur, 0), Render(unknown, 0));

            var (unknownTheme, _) = OneBackground("gradient", "no-such-theme");
            var (sunrise, _) = OneBackground("gradient", "sunrise");
            Assert.Equal(Render(sunrise, 0), Render(unknownTheme, 0));
        }

        [Fact]
        public void Null_transform_is_the_default_transform()
        {
            var (p, item) = OneBackground("explode");
            var withDefault = Render(p, 0);
            item.Transform = null;
            Assert.Equal(withDefault, Render(p, 0));
        }

        // ------------------------------------------------------------------------------ parity

        /// <summary>
        /// GPU and CPU compose the same background. The bar is per route, from measurement at
        /// 64x64, 128x128 and 256x256 on D3D12 and on Metal against raster:
        /// <list type="bullet">
        /// <item>Breathing Field computes its blur once on a CPU raster, identically for both
        /// backends, then each backend resamples that snapshot into the box; their bilinear
        /// filters round differently by 2 on a fraction of bytes and never more. With the
        /// float blur the snapshot is a smooth ramp everywhere (the earlier 8-bit blur had
        /// plateaus, where the two filters trivially agree), so the fraction rose from 3 to
        /// 40 of 16384; sizing the raster by sigma (<c>SvgGroup.BlurSigmaWorkingPx</c>, a
        /// 115x93 snapshot in place of 823x663) put several levels between neighbouring
        /// texels, so the two filters' rounding lands a 2 more often still: 139 of 16384 at
        /// 64x64 and the same 0.86 percent at 256x256, mean 0.35, worst still 2. The gate is
        /// 2 on under two percent of bytes;</item>
        /// <item>the vector styles draw gradients and antialiased paths straight to the canvas,
        /// and here Skia's two rasterisers genuinely differ: gradient ramps by up to 4 levels
        /// (the interpolation is float on Ganesh, fixed-point on raster) and path EDGE pixels by
        /// up to 94 (Ganesh's analytic coverage against raster's supersampled coverage), with
        /// the mean channel difference under 0.5. That is the same primitive-level difference
        /// the cursor glyphs already live with; the interior of every shape agrees, and the
        /// divergent bytes trace the shape outlines and nothing else.
        /// <para>How many bytes that is depends on how much of the frame is outline, which is a
        /// property of the style and of the size, not of the backend: for a given style the
        /// fraction falls as the box grows (Explode is 2.10 percent of bytes beyond 4 levels at
        /// 64x64, 1.19 at 128x128, 0.68 at 256x256), and at one size it ranges over the styles
        /// from 0 (Gradient, which is one ramp with no path edge at all) to that 2.10. Metal
        /// resolves those edges further from raster than D3D12 does, which is why the 1 percent
        /// bar measured on D3D12 alone failed on macOS for Explode; the widest measured on
        /// either backend is Explode's 2.10 percent at 64x64, with Stacked Waves next at 2.02.
        /// The gate is therefore a mean under 1 level and at most 3 percent of bytes beyond 4
        /// levels. The mean is the term that carries the real weight here: a shifted, mistimed
        /// or misplaced background moves whole regions rather than outlines, and the measured
        /// mean is at most 0.31 across every style and size, so a structural divergence has
        /// three times the room it needs to trip it.</para></item>
        /// </list>
        /// None of these is a time or placement divergence: the CPU tests above hold the same
        /// tick to byte-identical output, and the same code with the same ticks runs on both.
        /// </summary>
        [Fact]
        public void Gpu_matches_cpu_for_backgrounds()
        {
            var gpu = GpuSurfaceFactory.TryCreate(out var reason);
            if (gpu == null)
                Assert.Skip("GPU backend unavailable: " + reason);

            try
            {
                using var cpu = new CpuSurfaceFactory();
                foreach (var (style, theme, route) in new[]
                         {
                             ("big-sur", "teal", "vector"),
                             ("breathing-field", "source", "snapshot"),
                             ("gradient", "sunrise", "vector"),
                             ("monterey", "dark", "vector"),
                             ("explode", null, "vector"),
                             ("moving-corners", "source", "vector"),
                         })
                {
                    var (p, _) = OneBackground(style, theme);
                    long t = 12 * Sec;
                    var g = Render(gpu, p, t, W, H);
                    var c = Render(cpu, p, t, W, H);
                    int worst = 0, beyondOne = 0, beyondFour = 0;
                    long total = 0;
                    for (int i = 0; i < g.Length; i++)
                    {
                        int d = Math.Abs(g[i] - c[i]);
                        worst = Math.Max(worst, d);
                        total += d;
                        if (d > 1)
                            beyondOne++;
                        if (d > 4)
                            beyondFour++;
                    }
                    double mean = total / (double)g.Length;
                    string report = $"{style}/{theme}: GPU and CPU differ by up to {worst}, mean {mean:F3}, {beyondOne} bytes beyond 1, {beyondFour} beyond 4";
                    switch (route)
                    {
                        case "snapshot":
                            Assert.True(worst <= 2 && beyondOne <= g.Length / 50, report);
                            break;
                        default:
                            Assert.True(mean < 1.0 && beyondFour <= g.Length * 3 / 100, report);
                            break;
                    }
                }
            }
            finally
            {
                gpu.Dispose();
            }
        }
    }
}
