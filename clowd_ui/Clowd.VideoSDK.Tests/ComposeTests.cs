using System;
using System.IO;
using System.Runtime.InteropServices;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // FrameComposer pixel tests on the CPU factory. Solid-color scenes assert exact regions;
    // text/image are smoke-level (they draw, they land roughly where placed).
    public class ComposeTests
    {
        private const long Sec = 10_000_000;
        private const int W = 64, H = 64;

        // ---------------------------------------------------------------------------- builders

        private static Project NewProject() => new Project
        {
            Output = new OutputSettings { WidthPx = W, HeightPx = H, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
        };

        private static Track AddVideoTrack(Project p, int order = 0, bool hidden = false)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = order, Hidden = hidden };
            p.Tracks.Add(track);
            return track;
        }

        private static Item AddItem(Project p, Track track, ItemContent content,
            long start = 0, long duration = 10 * Sec)
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

        private static byte[] Render(Project p, long timeTicks, IFrameSource frames = null)
        {
            using var factory = new CpuSurfaceFactory();
            using var surface = factory.CreateSurface(W, H);
            FrameComposer.Compose(p, timeTicks, frames, surface.Canvas, W, H);

            int rowBytes = W * 4;
            var native = Marshal.AllocHGlobal(rowBytes * H);
            try
            {
                Assert.True(factory.TryReadPixels(surface, W, H, native, rowBytes));
                var pixels = new byte[rowBytes * H];
                Marshal.Copy(native, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        private static (byte B, byte G, byte R, byte A) Px(byte[] bgra, int x, int y)
        {
            int i = y * W * 4 + x * 4;
            return (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
        }

        private static void AssertColor((byte B, byte G, byte R, byte A) actual,
            byte b, byte g, byte r, int tolerance = 1)
        {
            Assert.InRange(actual.B, Math.Max(0, b - tolerance), Math.Min(255, b + tolerance));
            Assert.InRange(actual.G, Math.Max(0, g - tolerance), Math.Min(255, g + tolerance));
            Assert.InRange(actual.R, Math.Max(0, r - tolerance), Math.Min(255, r + tolerance));
        }

        // ------------------------------------------------------------------------------- solid

        [Fact]
        public void Solid_default_transform_fills_canvas()
        {
            var p = NewProject();
            AddItem(p, AddVideoTrack(p), new SolidContent { Color = "#FFFF0000" });

            var px = Render(p, 5 * Sec);
            AssertColor(Px(px, 0, 0), 0, 0, 255);
            AssertColor(Px(px, W - 1, 0), 0, 0, 255);
            AssertColor(Px(px, 0, H - 1), 0, 0, 255);
            AssertColor(Px(px, W - 1, H - 1), 0, 0, 255);
            AssertColor(Px(px, W / 2, H / 2), 0, 0, 255);
        }

        [Fact]
        public void Solid_scaled_draws_centred_region_only()
        {
            var p = NewProject();
            var item = AddItem(p, AddVideoTrack(p), new SolidContent { Color = "#FFFF0000" });
            item.Transform.Scale = 0.5; // 32x32 centred on the 64x64 canvas

            var px = Render(p, 5 * Sec);
            AssertColor(Px(px, W / 2, H / 2), 0, 0, 255); // centre red
            AssertColor(Px(px, 2, 2), 0, 0, 0);           // corners background
            AssertColor(Px(px, W - 3, H - 3), 0, 0, 0);
            AssertColor(Px(px, 20, 32), 0, 0, 255);       // inside the region (x in [16,48))
            AssertColor(Px(px, 10, 32), 0, 0, 0);         // outside it
        }

        [Fact]
        public void Item_outside_its_time_span_does_not_draw()
        {
            var p = NewProject();
            AddItem(p, AddVideoTrack(p), new SolidContent { Color = "#FFFF0000" }, start: 2 * Sec, duration: 2 * Sec);

            AssertColor(Px(Render(p, 1 * Sec), W / 2, H / 2), 0, 0, 0);   // before
            AssertColor(Px(Render(p, 3 * Sec), W / 2, H / 2), 0, 0, 255); // inside
            AssertColor(Px(Render(p, 4 * Sec), W / 2, H / 2), 0, 0, 0);   // at the exclusive end
        }

        // ------------------------------------------------------------------------------ layers

        [Fact]
        public void Tracks_layer_in_ascending_order()
        {
            var p = NewProject();
            AddItem(p, AddVideoTrack(p, order: 0), new SolidContent { Color = "#FFFF0000" });
            AddItem(p, AddVideoTrack(p, order: 1), new SolidContent { Color = "#FF00FF00" });

            // higher order draws on top
            AssertColor(Px(Render(p, 5 * Sec), W / 2, H / 2), 0, 255, 0);

            var p2 = NewProject();
            AddItem(p2, AddVideoTrack(p2, order: 1), new SolidContent { Color = "#FFFF0000" });
            AddItem(p2, AddVideoTrack(p2, order: 0), new SolidContent { Color = "#FF00FF00" });
            AssertColor(Px(Render(p2, 5 * Sec), W / 2, H / 2), 0, 0, 255);
        }

        [Fact]
        public void Hidden_track_is_skipped()
        {
            var p = NewProject();
            AddItem(p, AddVideoTrack(p, order: 0), new SolidContent { Color = "#FFFF0000" });
            AddItem(p, AddVideoTrack(p, order: 1, hidden: true), new SolidContent { Color = "#FF00FF00" });

            AssertColor(Px(Render(p, 5 * Sec), W / 2, H / 2), 0, 0, 255);
        }

        // ----------------------------------------------------------------------------- opacity

        [Fact]
        public void Transform_opacity_blends_with_background()
        {
            var p = NewProject();
            var item = AddItem(p, AddVideoTrack(p), new SolidContent { Color = "#FFFF0000" });
            item.Transform.Opacity = 0.5;

            // 50% red over the black canvas ≈ (128, 0, 0)
            AssertColor(Px(Render(p, 5 * Sec), W / 2, H / 2), 0, 0, 128, tolerance: 3);
        }

        [Fact]
        public void Fade_entry_at_half_duration_yields_half_alpha()
        {
            var p = NewProject();
            var item = AddItem(p, AddVideoTrack(p), new SolidContent { Color = "#FFFF0000" });
            item.Entry = new Transition { Kind = TransitionKind.Fade, DurationTicks = 1 * Sec, Easing = TransitionEasing.Linear };

            AssertColor(Px(Render(p, Sec / 2), W / 2, H / 2), 0, 0, 128, tolerance: 3);
            AssertColor(Px(Render(p, 1 * Sec), W / 2, H / 2), 0, 0, 255); // complete
        }

        // -------------------------------------------------------------------------------- mask

        [Fact]
        public void Circle_mask_zeroes_corners()
        {
            var p = NewProject();
            var item = AddItem(p, AddVideoTrack(p), new SolidContent { Color = "#FFFFFFFF" });
            item.Transform.Mask = new Mask { Shape = MaskShape.Circle };

            var px = Render(p, 5 * Sec);
            AssertColor(Px(px, W / 2, H / 2), 255, 255, 255);
            AssertColor(Px(px, 1, 1), 0, 0, 0);
            AssertColor(Px(px, W - 2, 1), 0, 0, 0);
            AssertColor(Px(px, 1, H - 2), 0, 0, 0);
            AssertColor(Px(px, W - 2, H - 2), 0, 0, 0);
        }

        [Fact]
        public void Rounded_rect_mask_rounds_corners_only()
        {
            var p = NewProject();
            var item = AddItem(p, AddVideoTrack(p), new SolidContent { Color = "#FFFFFFFF" });
            item.Transform.Mask = new Mask { Shape = MaskShape.RoundedRect, CornerRadius = 0.25 }; // r = 16px

            var px = Render(p, 5 * Sec);
            AssertColor(Px(px, W / 2, H / 2), 255, 255, 255);
            AssertColor(Px(px, W / 2, 0), 255, 255, 255); // edge midpoints stay
            AssertColor(Px(px, 0, H / 2), 255, 255, 255);
            AssertColor(Px(px, 1, 1), 0, 0, 0);           // corners clipped
            AssertColor(Px(px, W - 2, H - 2), 0, 0, 0);
        }

        // -------------------------------------------------------------------------------- wipe

        [Fact]
        public void Wipe_entry_reveals_left_band()
        {
            var p = NewProject();
            var item = AddItem(p, AddVideoTrack(p), new SolidContent { Color = "#FFFF0000" });
            item.Entry = new Transition { Kind = TransitionKind.Wipe, DurationTicks = 1 * Sec, Easing = TransitionEasing.Linear };

            var px = Render(p, Sec / 2); // visible band [0, 0.5] → x < 32
            AssertColor(Px(px, 8, H / 2), 0, 0, 255);
            AssertColor(Px(px, 56, H / 2), 0, 0, 0);
        }

        // ------------------------------------------------------------------------------- media

        private sealed class FakeFrameSource : IFrameSource, IDisposable
        {
            private readonly SKImage _image;
            public int Requests;
            public long LastRequestedTicks = long.MinValue;

            public FakeFrameSource(SKColor color, int size = 16)
            {
                using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
                surface.Canvas.Clear(color);
                _image = surface.Snapshot();
            }

            public bool TryGetFrame(Guid sourceId, int streamIndex, long sourceTimeTicks, out FrameRef frame)
            {
                Requests++;
                LastRequestedTicks = sourceTimeTicks;
                frame = new FrameRef(_image, sourceTimeTicks);
                return true;
            }

            public void Dispose() => _image.Dispose();
        }

        [Fact]
        public void Media_item_draws_source_frame_and_maps_timeline_to_source_time()
        {
            var p = NewProject();
            var item = AddItem(p, AddVideoTrack(p),
                new MediaContent { SourceId = Guid.NewGuid(), StreamIndex = 0, SourceInTicks = 3 * Sec },
                start: 2 * Sec, duration: 6 * Sec);

            using var frames = new FakeFrameSource(new SKColor(0, 0, 255)); // blue, square aspect
            var px = Render(p, 4 * Sec, frames);

            // source time = SourceIn + (t - TimelineStart) = 3s + 2s
            Assert.Equal(5 * Sec, frames.LastRequestedTicks);
            AssertColor(Px(px, W / 2, H / 2), 255, 0, 0); // blue fills the canvas (Scale 1)
            AssertColor(Px(px, 1, 1), 255, 0, 0);
        }

        [Fact]
        public void Media_item_with_null_source_is_skipped()
        {
            var p = NewProject();
            AddItem(p, AddVideoTrack(p), new MediaContent { SourceId = Guid.NewGuid() });

            var px = Render(p, 5 * Sec, frames: null); // must not throw
            AssertColor(Px(px, W / 2, H / 2), 0, 0, 0);
        }

        [Fact]
        public void Media_item_crop_shows_selected_region()
        {
            // left half red, right half green; crop away the left half → green fills the item.
            using var surface = SKSurface.Create(new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(new SKColor(255, 0, 0));
            using (var paint = new SKPaint { Color = new SKColor(0, 255, 0) })
                surface.Canvas.DrawRect(SKRect.Create(8, 0, 8, 16), paint);
            using var image = surface.Snapshot();

            var p = NewProject();
            var item = AddItem(p, AddVideoTrack(p), new MediaContent { SourceId = Guid.NewGuid() });
            item.Transform.Crop = new CropRect { Left = 0.5 };
            item.Transform.Scale = 0.5;

            var stub = new StubSource(image);
            var px = Render(p, 5 * Sec, stub);
            AssertColor(Px(px, W / 2, H / 2), 0, 255, 0); // green: the un-cropped half
        }

        private sealed class StubSource : IFrameSource
        {
            private readonly SKImage _image;
            public StubSource(SKImage image) => _image = image;

            public bool TryGetFrame(Guid sourceId, int streamIndex, long sourceTimeTicks, out FrameRef frame)
            {
                frame = new FrameRef(_image, sourceTimeTicks);
                return true;
            }
        }

        // -------------------------------------------------------------------------- text/image

        [Fact]
        public void Text_item_draws_something()
        {
            var p = NewProject();
            AddItem(p, AddVideoTrack(p), new TextContent
            {
                Text = "AB",
                Size = 32,
                Color = "#FFFFFFFF",
                Align = TextAlign.Center,
            });

            var px = Render(p, 5 * Sec);
            bool anyInk = false;
            for (int i = 0; i < px.Length && !anyInk; i += 4)
                anyInk = px[i] != 0 || px[i + 1] != 0 || px[i + 2] != 0;
            Assert.True(anyInk, "text item drew nothing");
        }

        [Fact]
        public void Image_item_draws_decoded_file()
        {
            string path = Path.Combine(Path.GetTempPath(), $"clowd-compose-test-{Guid.NewGuid():N}.png");
            try
            {
                using (var surface = SKSurface.Create(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul)))
                {
                    surface.Canvas.Clear(new SKColor(0, 255, 0));
                    using var img = surface.Snapshot();
                    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(path, data.ToArray());
                }

                var p = NewProject();
                AddItem(p, AddVideoTrack(p), new ImageContent { Path = path });

                var px = Render(p, 5 * Sec);
                AssertColor(Px(px, W / 2, H / 2), 0, 255, 0);
                AssertColor(Px(px, 2, 2), 0, 255, 0); // square aspect fills the square canvas
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Image_item_with_missing_file_is_skipped()
        {
            var p = NewProject();
            AddItem(p, AddVideoTrack(p), new ImageContent { Path = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".png") });

            var px = Render(p, 5 * Sec); // must not throw
            AssertColor(Px(px, W / 2, H / 2), 0, 0, 0);
        }
    }
}
