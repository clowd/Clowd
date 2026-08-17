using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Clowd.UI.VideoEditor;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using SkiaSharp;
using Xunit;
using ModelTransform = Clowd.VideoSDK.Model.Transform;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="ItemPlacement"/> is what the preview arranges the transform gizmo on (Clowd.Ui
    /// exposes its internals to this project), so its rect has to be where <c>FrameComposer</c>
    /// actually draws the item — verified against composed pixels, not a repeat of the formula.
    /// </summary>
    public class ItemPlacementTests
    {
        private const long Ms = TimeSpan.TicksPerMillisecond;
        private const long DurationMs = 10_000;
        private const string VideoPath = @"C:\recordings\video.mp4";

        // A v1 edit with the overlay on at 90% width near the top edge — the case the composer
        // does not bound (see the test below). Migrated through LoadOrCreate so the transform is
        // the same pixel-rounded one a real edit carries.
        private const string TallOverlayV1Json = """
            {
              "Version": 1,
              "TrimStartMs": 0,
              "TrimEndMs": 0,
              "WebcamEnabled": true,
              "WebcamShape": "Circle",
              "WebcamCornerRadius": 0.25,
              "WebcamCenterX": 0.82,
              "WebcamCenterY": 0.1,
              "WebcamWidth": 0.9,
              "Cuts": []
            }
            """;

        private static MediaProbeResult Probe() => new MediaProbeResult
        {
            Path = VideoPath,
            DurationTicks = DurationMs * Ms,
            VideoStreams = new[]
            {
                new VideoStreamProbe
                {
                    StreamIndex = 0,
                    Width = 1920,
                    Height = 1080,
                    AvgFrameRateNum = 30,
                    AvgFrameRateDen = 1,
                    DurationTicks = DurationMs * Ms,
                },
                new VideoStreamProbe
                {
                    StreamIndex = 1,
                    Width = 640,
                    Height = 480,
                    AvgFrameRateNum = 30,
                    AvgFrameRateDen = 1,
                    DurationTicks = DurationMs * Ms,
                },
            },
            AudioStreams = Array.Empty<AudioStreamProbe>(),
        };

        /// <summary>
        /// The gizmo (outline, mask preview and corner handles) is arranged on
        /// <see cref="ItemPlacement.Compose"/>, so that rect has to be where
        /// <see cref="FrameComposer"/> actually draws the picture — including the case the composer
        /// does not bound: a 4:3 camera at 90% width on a 16:9 recording is drawn taller than the
        /// frame and merely clipped. Ground truth here is composed pixels, not a repeat of the
        /// formula: the gizmo used to clamp its height to the frame, which put the outline 90px
        /// short of the picture (its ellipse converged to a point where the composed one was still
        /// ~400px wide).
        /// </summary>
        [Fact]
        public void Gizmo_rect_follows_the_composed_picture_when_the_overlay_is_taller_than_the_frame()
        {
            const int CanvasW = 800, CanvasH = 450;
            const double CamAspect = 480.0 / 640.0;

            var path = Path.Combine(Path.GetTempPath(), "clowd-webcam-placement-" + Guid.NewGuid().ToString("N") + ".json");
            Project project;
            try
            {
                File.WriteAllText(path, TallOverlayV1Json);
                project = VideoEditPersistence.LoadOrCreate(path, VideoPath, Probe());
            }
            finally
            {
                File.Delete(path);
            }

            Assert.Empty(project.Validate());

            // the transform the composer draws the webcam row with
            var camItem = project.Items
                .Where(i => ((MediaContent)i.Content).StreamIndex == 1)
                .OrderBy(i => i.TimelineStartTicks)
                .First();
            Assert.NotNull(camItem.Transform);

            var gizmo = ItemPlacement.Compose(camItem.Transform, CamAspect, CanvasW, CanvasH);
            Assert.Equal(720, gizmo.W, 3);
            Assert.Equal(540, gizmo.H, 3);   // 4:3 of 720 — taller than the 450 frame
            Assert.Equal(-45, gizmo.Y, 3);   // and therefore hanging off both edges

            // and the resolver reaches the same rect from the project alone: the camera's aspect
            // comes from the source stream the item references (640x480), not from a caller.
            Assert.True(ItemPlacement.TryResolve(project, camItem, CanvasW, CanvasH, out var resolved));
            Assert.Equal(gizmo.X, resolved.X, 3);
            Assert.Equal(gizmo.Y, resolved.Y, 3);
            Assert.Equal(gizmo.W, resolved.W, 3);
            Assert.Equal(gizmo.H, resolved.H, 3);
            Assert.Equal(CamAspect, resolved.Aspect, 6);
            Assert.Equal(CanvasW, resolved.ScaleDenominatorPx, 6); // pictures scale against the canvas

            // what the composer draws, measured
            var pixels = ComposeStreamOnly(project, CanvasW, CanvasH, streamIndex: 1, SolidImage(64, 48, SKColors.White));

            foreach (int y in new[] { 0, 60, CanvasH / 2, CanvasH - 1 })
            {
                var (drawnLeft, drawnRight) = DrawnSpanX(pixels, CanvasW, y);

                // the mask is the ellipse inscribed in the item rect, so the gizmo rect predicts
                // the drawn span on every scanline
                double dy = (y + 0.5) - (gizmo.Y + gizmo.H / 2);
                double half = gizmo.W / 2 * Math.Sqrt(Math.Max(0, 1 - dy * dy / (gizmo.H / 2 * (gizmo.H / 2))));
                double expectedLeft = Math.Max(0, gizmo.X + gizmo.W / 2 - half);
                double expectedRight = Math.Min(CanvasW, gizmo.X + gizmo.W / 2 + half);

                Assert.InRange(drawnLeft, expectedLeft - 2, expectedLeft + 2);
                Assert.InRange(drawnRight, expectedRight - 2, expectedRight + 2);
            }

            // the discriminating scanline: the frame-clamped rect the gizmo used to be arranged on
            // predicts a ~48px span at the top edge where the composer draws ~400px.
            var (topLeft, topRight) = DrawnSpanX(pixels, CanvasW, 0);
            Assert.InRange(topRight - topLeft, 380, 420);
        }

        // ------------------------------------------------------------------------ cropped aspect

        /// <summary>
        /// Crop is applied <b>before</b> Scale: the composer keeps the dest width at
        /// <c>Scale * canvasWidth</c> and derives the height from the <i>cropped</i> region, so
        /// cropping the sides of a 16:9 frame makes the drawn rect taller for the same Scale. The
        /// gizmo has to follow, or the handles sit inside the picture the moment a crop inset is
        /// touched — verified against composed pixels.
        /// </summary>
        [Fact]
        public void Cropped_media_placement_matches_the_composed_picture()
        {
            const int CanvasW = 800, CanvasH = 450;

            var project = MediaProject(1920, 1080, out var item);
            item.Transform = new ModelTransform
            {
                X = 0.5,
                Y = 0.5,
                Scale = 0.4,
                Crop = new CropRect { Left = 0.25, Right = 0.25 },
            };

            Assert.Empty(project.Validate());
            Assert.True(ItemPlacement.TryResolve(project, item, CanvasW, CanvasH, out var placed));

            // cropped source is 960x1080 → aspect 1.125; the width is still 0.4 of the canvas.
            Assert.Equal(1080.0 / 960.0, placed.Aspect, 6);
            Assert.Equal(320, placed.W, 3);
            Assert.Equal(360, placed.H, 3);
            Assert.Equal(240, placed.X, 3);
            Assert.Equal(45, placed.Y, 3);

            var pixels = ComposeStreamOnly(project, CanvasW, CanvasH, streamIndex: 0, SolidImage(192, 108, SKColors.White));

            var (left, right) = DrawnSpanX(pixels, CanvasW, CanvasH / 2);
            Assert.InRange(left, placed.X - 2, placed.X + 2);
            Assert.InRange(right, placed.Right - 2, placed.Right + 2);

            var (top, bottom) = DrawnSpanY(pixels, CanvasW, CanvasH, CanvasW / 2);
            Assert.InRange(top, placed.Y - 2, placed.Y + 2);
            Assert.InRange(bottom, placed.Bottom - 2, placed.Bottom + 2);
        }

        /// <summary>The same picture without a crop is half as tall — the guard that the test above
        /// is measuring the crop and not the default aspect.</summary>
        [Fact]
        public void Uncropped_media_placement_uses_the_full_stream_aspect()
        {
            var project = MediaProject(1920, 1080, out var item);
            item.Transform = new ModelTransform { Scale = 0.4 };

            Assert.True(ItemPlacement.TryResolve(project, item, 800, 450, out var placed));
            Assert.Equal(1080.0 / 1920.0, placed.Aspect, 6);
            Assert.Equal(320, placed.W, 3);
            Assert.Equal(180, placed.H, 3);
        }

        /// <summary>A crop that removes everything is what the composer refuses to draw — the gizmo
        /// must vanish with it rather than sit on a zero-area rect.</summary>
        [Fact]
        public void Placement_is_refused_when_the_crop_removes_everything()
        {
            var project = MediaProject(1920, 1080, out var item);
            item.Transform = new ModelTransform { Crop = new CropRect { Left = 0.6, Right = 0.6 } };

            Assert.False(ItemPlacement.TryResolve(project, item, 800, 450, out _));
        }

        /// <summary>An audio item (or any stream the project has no dimensions for) has no picture
        /// to place, so no gizmo.</summary>
        [Fact]
        public void Placement_is_refused_for_a_stream_without_dimensions()
        {
            var project = MediaProject(1920, 1080, out var item);
            project.Sources[0].Streams.Add(new SourceStream { Index = 1, Kind = StreamKind.Audio });
            ((MediaContent)item.Content).StreamIndex = 1;

            Assert.False(ItemPlacement.TryResolve(project, item, 800, 450, out _));
        }

        // --------------------------------------------------------------------------- text sizing

        /// <summary>
        /// Text is the one content whose Scale multiplies a <i>measured</i> natural size instead of
        /// mapping to a canvas-width fraction, so the gizmo's rect can only come from the composer's
        /// own measurement (<see cref="FrameComposer.MeasureText"/>, which <c>DrawText</c> now
        /// consumes — the parity is structural). This checks the other half: that the measured block
        /// really does bound the drawn ink, and comfortably fills it.
        /// </summary>
        [Fact]
        public void Text_placement_bounds_the_composed_ink()
        {
            const int CanvasW = 640, CanvasH = 360;

            var project = TextProject("Hello\nWorld", size: 48, out var item);
            item.Transform = new ModelTransform { X = 0.5, Y = 0.5, Scale = 1 };

            Assert.True(ItemPlacement.TryResolve(project, item, CanvasW, CanvasH, out var placed));
            Assert.Equal(placed.H / placed.W, placed.Aspect, 6);
            Assert.Equal(placed.W, placed.ScaleDenominatorPx, 6); // Scale 1 == the natural block

            var pixels = Compose(project, CanvasW, CanvasH, frames: null);
            var ink = InkBounds(pixels, CanvasW, CanvasH);
            Assert.NotNull(ink);
            var (left, top, right, bottom) = ink.Value;

            // the drawn ink sits inside the placed block (antialiasing may bleed a pixel)…
            Assert.InRange(left, placed.X - 1.5, placed.Right);
            Assert.InRange(right, placed.X, placed.Right + 1.5);
            Assert.InRange(top, placed.Y - 1.5, placed.Bottom);
            Assert.InRange(bottom, placed.Y, placed.Bottom + 1.5);

            // …and fills it: a block that were twice the ink would put the handles nowhere near
            // the text. (Height stays looser: SKFont.Spacing includes the font's leading.)
            Assert.True(right - left >= placed.W * 0.75, $"ink width {right - left} vs block {placed.W}");
            Assert.True(bottom - top >= placed.H * 0.5, $"ink height {bottom - top} vs block {placed.H}");
        }

        /// <summary>Scale multiplies the measured block for text — so does the drawn ink. (The
        /// picture rule, <c>Scale * canvasWidth</c>, would give the same rect for both canvases
        /// here; this pins the text rule specifically.)</summary>
        [Fact]
        public void Text_placement_scales_with_the_transform_exactly_as_the_ink_does()
        {
            const int CanvasW = 640, CanvasH = 360;

            var project = TextProject("Hello", size: 40, out var item);
            item.Transform = new ModelTransform { Scale = 1 };

            Assert.True(ItemPlacement.TryResolve(project, item, CanvasW, CanvasH, out var single));
            var singleInk = InkBounds(Compose(project, CanvasW, CanvasH, null), CanvasW, CanvasH);

            item.Transform.Scale = 2;
            Assert.True(ItemPlacement.TryResolve(project, item, CanvasW, CanvasH, out var doubled));
            var doubledInk = InkBounds(Compose(project, CanvasW, CanvasH, null), CanvasW, CanvasH);

            Assert.Equal(single.W * 2, doubled.W, 3);
            Assert.Equal(single.H * 2, doubled.H, 3);

            Assert.NotNull(singleInk);
            Assert.NotNull(doubledInk);
            var inkRatio = (doubledInk.Value.Right - doubledInk.Value.Left) /
                           (singleInk.Value.Right - singleInk.Value.Left);
            Assert.InRange(inkRatio, 1.9, 2.1);
        }

        /// <summary>
        /// Text must be the same <i>fraction</i> of every canvas: the preview composes at the
        /// letterboxed window rect while the render composes at Output, and TextContent.Size is in
        /// output pixels — before the composer scaled the font by canvasHeight/outputHeight, a
        /// default-window preview drew titles ~2.3x larger relative to the frame than the export
        /// did (and resizing the window changed them again).
        /// </summary>
        [Fact]
        public void Text_draws_the_same_fraction_of_every_canvas_size()
        {
            // the exact card EditorSession.AddText creates on a 1080p recording
            var project = TextProject("Title", size: 1080 * 0.08, out var item);
            item.Transform = new ModelTransform { X = 0.5, Y = 0.5, Scale = 1 };

            var large = InkBounds(Compose(project, 1920, 1080, null), 1920, 1080);
            var small = InkBounds(Compose(project, 835, 470, null), 835, 470); // default 1100x720 window
            Assert.NotNull(large);
            Assert.NotNull(small);

            var largeWFrac = (large.Value.Right - large.Value.Left) / 1920.0;
            var smallWFrac = (small.Value.Right - small.Value.Left) / 835.0;
            Assert.InRange(smallWFrac, largeWFrac - 0.01, largeWFrac + 0.01);

            var largeHFrac = (large.Value.Bottom - large.Value.Top) / 1080.0;
            var smallHFrac = (small.Value.Bottom - small.Value.Top) / 470.0;
            Assert.InRange(smallHFrac, largeHFrac - 0.01, largeHFrac + 0.01);

            // and the gizmo's resolver mirrors the rule: the same normalized rect at both sizes,
            // so the chrome lands on the drawn text whatever the window measures.
            Assert.True(ItemPlacement.TryResolve(project, item, 1920, 1080, out var big));
            Assert.True(ItemPlacement.TryResolve(project, item, 835, 470, out var lil));
            Assert.InRange(lil.W / 835.0, big.W / 1920.0 - 0.01, big.W / 1920.0 + 0.01);
            Assert.InRange(lil.H / 470.0, big.H / 1080.0 - 0.01, big.H / 1080.0 + 0.01);
        }

        /// <summary>Text that draws nothing measures to nothing (and therefore places nothing).</summary>
        [Fact]
        public void Empty_text_measures_to_zero()
        {
            Assert.Equal((0.0, 0.0), FrameComposer.MeasureText(null));
            Assert.Equal((0.0, 0.0), FrameComposer.MeasureText(new TextContent { Text = "", Size = 40 }));

            var project = TextProject("", size: 40, out var item);
            Assert.False(ItemPlacement.TryResolve(project, item, 640, 360, out _));
        }

        /// <summary>Two lines are twice as tall as one and no wider than the widest — the block is a
        /// block, not a single run.</summary>
        [Fact]
        public void Multi_line_text_measures_the_widest_line_and_every_line_height()
        {
            var one = FrameComposer.MeasureText(new TextContent { Text = "Hello", Size = 40 });
            var two = FrameComposer.MeasureText(new TextContent { Text = "Hello\nHi", Size = 40 });

            Assert.Equal(one.Width, two.Width, 3);
            Assert.Equal(one.Height * 2, two.Height, 3);
        }

        // ------------------------------------------------------------------------ keyboard block

        /// <summary>
        /// A keyboard overlay is the one content the transform anchors by its <b>bottom</b> centre:
        /// the rows stack upward from it (<c>FrameComposer.DrawKeyboard</c>), so the gizmo's rect
        /// hangs above the anchor rather than being centred on it. Ground truth is composed pixels
        /// — the pills darken a white background, and their bounding box is the block the gizmo
        /// must land on, three nominal rows tall.
        /// </summary>
        [Fact]
        public void Keyboard_placement_boxes_the_composed_block_above_its_anchor()
        {
            const int CanvasW = 800, CanvasH = 450;

            var capture = WriteCapture();
            try
            {
                var project = KeyboardProject(capture, out var item, out _);

                Assert.True(ItemPlacement.TryResolve(project, item, CanvasW, CanvasH, out var placed));
                Assert.Equal(400, placed.W, 3);                 // Scale 0.5 of the canvas width
                Assert.Equal(0.85 * CanvasH, placed.Bottom, 3); // …anchored by its bottom, not its centre
                Assert.Equal(placed.H / placed.W, placed.Aspect, 6);
                Assert.Equal(CanvasW, placed.ScaleDenominatorPx, 6);
                Assert.Equal(placed.H, placed.ScaleDenominatorYPx, 6);

                // the three pills the capture's three runs draw, measured off the canvas
                var pixels = Compose(project, CanvasW, CanvasH, frames: null);
                var pills = DarkBounds(pixels, CanvasW, CanvasH);
                Assert.NotNull(pills);
                var (left, top, right, bottom) = pills.Value;

                Assert.InRange(bottom, placed.Bottom - 2, placed.Bottom + 2);
                Assert.InRange(top, placed.Y - 2, placed.Y + 2);

                // the box is the wrap width, deliberately wider than the pills a short run draws:
                // it is what the user sizes (text wraps at it), not the ink of the moment.
                Assert.True(right - left < placed.W, $"pills {right - left} vs wrap box {placed.W}");
                Assert.InRange((left + right) / 2, placed.X + placed.W / 2 - 2, placed.X + placed.W / 2 + 2);
            }
            finally
            {
                File.Delete(capture);
            }
        }

        /// <summary>The block's width is the wrap box <c>Scale</c> sets, but its height is the
        /// font's — so widening it must not make it taller, which is exactly where the picture rule
        /// (height derived from the width and an aspect) would be wrong.</summary>
        [Fact]
        public void Keyboard_width_scales_with_the_transform_and_the_height_does_not()
        {
            var capture = WriteCapture();
            try
            {
                var project = KeyboardProject(capture, out var item, out _);

                Assert.True(ItemPlacement.TryResolve(project, item, 800, 450, out var half));
                item.Transform.Scale = 1.0;
                Assert.True(ItemPlacement.TryResolve(project, item, 800, 450, out var full));

                Assert.Equal(400, half.W, 3);
                Assert.Equal(800, full.W, 3);
                Assert.Equal(half.H, full.H, 6);   // the font's height, untouched by the width
                Assert.Equal(half.Bottom, full.Bottom, 6);
            }
            finally
            {
                File.Delete(capture);
            }
        }

        /// <summary>
        /// <see cref="KeyboardContent.FontSize"/> is in output pixels like <see cref="TextContent"/>'s,
        /// so the block is the same fraction of the preview's letterboxed canvas as of the exported
        /// frame — the WYSIWYG rule the gizmo has to inherit, or its box would drift with the
        /// window.
        /// </summary>
        [Fact]
        public void Keyboard_block_is_the_same_fraction_of_every_canvas_size()
        {
            var capture = WriteCapture();
            try
            {
                var project = KeyboardProject(capture, out var item, out _);

                Assert.True(ItemPlacement.TryResolve(project, item, 1920, 1080, out var big));
                Assert.True(ItemPlacement.TryResolve(project, item, 835, 470, out var lil));

                Assert.InRange(lil.H / 470.0, big.H / 1080.0 - 0.01, big.H / 1080.0 + 0.01);
                Assert.InRange(lil.W / 835.0, big.W / 1920.0 - 0.01, big.W / 1920.0 + 0.01);
            }
            finally
            {
                File.Delete(capture);
            }
        }

        /// <summary>A keyboard block is clickable on the preview: the whole wrap box selects it,
        /// and the picture beneath keeps every other point.</summary>
        [Fact]
        public void Hit_test_picks_a_keyboard_block_over_the_picture_beneath()
        {
            const int CanvasW = 800, CanvasH = 450;

            var capture = WriteCapture();
            try
            {
                var project = KeyboardProject(capture, out var item, out var background);
                Assert.True(ItemPlacement.TryResolve(project, item, CanvasW, CanvasH, out var placed));

                Assert.Equal(item.Id, ItemPlacement.HitTest(project, TimeSpan.TicksPerSecond,
                    placed.X + placed.W / 2, placed.Bottom - 1, CanvasW, CanvasH)?.Id);

                // just under the anchored bottom edge is outside the block
                Assert.Equal(background.Id, ItemPlacement.HitTest(project, TimeSpan.TicksPerSecond,
                    placed.X + placed.W / 2, placed.Bottom + 1, CanvasW, CanvasH)?.Id);
            }
            finally
            {
                File.Delete(capture);
            }
        }

        /// <summary>
        /// A cursor item is deliberately unplaceable: its position comes from the capture, not from
        /// its <c>Transform</c> (the composer ignores it), so there is nothing a gizmo could move.
        /// No aspect, no placement — and therefore no chrome and no click of its own, which is what
        /// keeps the picture underneath selectable through a full-frame cursor row.
        /// </summary>
        [Fact]
        public void Cursor_items_are_excluded_from_placement_and_the_hit_test()
        {
            const int CanvasW = 800, CanvasH = 450;

            var project = MediaProject(1920, 1080, out var background);
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Cursor", Order = 5 };
            var cursor = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = 5 * TimeSpan.TicksPerSecond,
                Content = new CursorContent { SourceId = project.Sources[0].Id },
                Transform = new ModelTransform(),
                LinkGroupId = Guid.NewGuid(),
            };
            project.Tracks.Add(track);
            project.Items.Add(cursor);

            Assert.Null(ItemPlacement.ContentAspect(project, cursor, CanvasW, CanvasH));
            Assert.False(ItemPlacement.TryResolve(project, cursor, CanvasW, CanvasH, out _));

            // the topmost row is the cursor's, and the click still lands on the picture below it
            Assert.Equal(background.Id,
                ItemPlacement.HitTest(project, 0, 400, 225, CanvasW, CanvasH)?.Id);
        }

        // ---------------------------------------------------------------------------- hit-testing

        /// <summary>The preview's click-to-select walks the composed stack from the top down, so an
        /// overlay covering a full-frame picture wins the click where they overlap, and the picture
        /// underneath wins everywhere else.</summary>
        [Fact]
        public void Hit_test_picks_the_topmost_item_at_the_playhead()
        {
            const int CanvasW = 800, CanvasH = 450;

            var project = MediaProject(1920, 1080, out var background);
            var overlayTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Overlay", Order = 5 };
            project.Tracks.Add(overlayTrack);
            var overlay = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = overlayTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = 5 * TimeSpan.TicksPerSecond,
                Content = new MediaContent { SourceId = project.Sources[0].Id, StreamIndex = 0 },
                Transform = new ModelTransform { X = 0.8, Y = 0.8, Scale = 0.25 },
            };
            project.Items.Add(overlay);
            project.Normalize();
            Assert.Empty(project.Validate());

            // dead centre of the overlay (0.8, 0.8 of the canvas)
            Assert.Equal(overlay.Id, ItemPlacement.HitTest(project, 0, 640, 360, CanvasW, CanvasH)?.Id);
            // top-left corner: only the background is there
            Assert.Equal(background.Id, ItemPlacement.HitTest(project, 0, 20, 20, CanvasW, CanvasH)?.Id);

            // hidden rows are not composed, so they cannot be clicked
            overlayTrack.Hidden = true;
            Assert.Equal(background.Id, ItemPlacement.HitTest(project, 0, 640, 360, CanvasW, CanvasH)?.Id);
        }

        /// <summary>Only items covering the playhead are on screen to be clicked; a click on bare
        /// canvas clears the selection instead.</summary>
        [Fact]
        public void Hit_test_ignores_items_that_do_not_cover_the_playhead()
        {
            var project = MediaProject(1920, 1080, out var item);
            item.Transform = new ModelTransform { Scale = 0.5 }; // 400x225 centred on an 800x450 canvas

            Assert.NotNull(ItemPlacement.HitTest(project, 0, 400, 225, 800, 450));
            Assert.Null(ItemPlacement.HitTest(project, item.TimelineEndTicks, 400, 225, 800, 450));
            Assert.Null(ItemPlacement.HitTest(project, 0, 10, 10, 800, 450)); // outside the picture
        }

        // ----------------------------------------------------------------------------- gizmo math

        [Fact]
        public void Move_translates_the_pointer_delta_into_normalized_centre_and_clamps()
        {
            var (x, y) = GizmoMath.Move(0.5, 0.5, 80, -45, 800, 450);
            Assert.Equal(0.6, x, 6);
            Assert.Equal(0.4, y, 6);

            var (clampedX, clampedY) = GizmoMath.Move(0.5, 0.5, 10_000, -10_000, 800, 450);
            Assert.Equal(1.0, clampedX, 6);
            Assert.Equal(0.0, clampedY, 6);

            var (nanX, _) = GizmoMath.Move(0.5, 0.5, Double.NaN, 0, 800, 450);
            Assert.Equal(0.0, nanX, 6); // NaN must not reach the model
        }

        /// <summary>Anchored uniform resize: the dragged corner follows the pointer, the opposite
        /// corner stays exactly where it was, and the height is derived from the content aspect
        /// (never dragged).</summary>
        [Fact]
        public void Resize_keeps_the_opposite_corner_anchored()
        {
            // canvas at (100,50), 800x450; dragging the bottom-right corner away from a top-left
            // anchor at the canvas origin.
            var (scale, x, y) = GizmoMath.Resize(
                pointerX: 500, pointerY: 250, anchorX: 100, anchorY: 50,
                draggingRight: true, draggingDown: true, aspect: 0.5, scaleDenominatorPx: 800,
                canvasX: 100, canvasY: 50, canvasWidth: 800, canvasHeight: 450,
                minScale: 0.01, maxScale: 4);

            Assert.Equal(0.5, scale, 6);          // 400px of an 800px canvas
            Assert.Equal(0.25, x, 6);             // centre 200px right of the anchor
            Assert.Equal(100 / 450.0, y, 6);      // …and 100px below it (400 * 0.5 / 2)

            // the anchor really is stationary: rebuild the rect from the result
            var rect = ItemPlacement.Compose(new ModelTransform { X = x, Y = y, Scale = scale }, 0.5, 800, 450);
            Assert.Equal(0, rect.X, 6);
            Assert.Equal(0, rect.Y, 6);
        }

        /// <summary>The pointer axis that is pulling hardest sets the size — dragging straight down
        /// still widens the item, because the aspect is fixed.</summary>
        [Fact]
        public void Resize_takes_the_larger_of_the_two_axes()
        {
            var (scale, _, _) = GizmoMath.Resize(
                pointerX: 100, pointerY: 450, anchorX: 100, anchorY: 50,
                draggingRight: true, draggingDown: true, aspect: 0.5, scaleDenominatorPx: 800,
                canvasX: 100, canvasY: 50, canvasWidth: 800, canvasHeight: 450,
                minScale: 0.01, maxScale: 4);

            Assert.Equal(1.0, scale, 6); // 400px of height / 0.5 aspect = 800px of width
        }

        /// <summary>A clamped scale still anchors: the centre is computed from the width the clamp
        /// actually allowed, so the fixed corner does not creep while the pointer keeps going.</summary>
        [Fact]
        public void Resize_anchors_the_clamped_size()
        {
            var (scale, x, _) = GizmoMath.Resize(
                pointerX: 101, pointerY: 51, anchorX: 100, anchorY: 50,
                draggingRight: true, draggingDown: true, aspect: 0.5, scaleDenominatorPx: 800,
                canvasX: 100, canvasY: 50, canvasWidth: 800, canvasHeight: 450,
                minScale: 0.05, maxScale: 4);

            Assert.Equal(0.05, scale, 6);
            var rect = ItemPlacement.Compose(new ModelTransform { X = x, Scale = scale }, 0.5, 800, 450);
            Assert.Equal(0, rect.X, 6); // still starts at the anchor, at the clamped width
        }

        /// <summary>Text resizes against its own natural width (Scale multiplies the block), which
        /// is exactly what <see cref="PlacedItem.ScaleDenominatorPx"/> carries out of the
        /// resolver.</summary>
        [Fact]
        public void Resize_scales_text_against_its_natural_width()
        {
            var (scale, _, _) = GizmoMath.Resize(
                pointerX: 300, pointerY: 50, anchorX: 100, anchorY: 50,
                draggingRight: true, draggingDown: true, aspect: 0.5, scaleDenominatorPx: 100,
                canvasX: 100, canvasY: 50, canvasWidth: 800, canvasHeight: 450,
                minScale: 0.01, maxScale: 4);

            Assert.Equal(2.0, scale, 6); // 200px drawn / a 100px natural block
        }

        // ------------------------------------------------------------- unlocked aspect ratio

        /// <summary>With ScaleY set the height stops following the content: the composer reads it as
        /// a fraction of the canvas height, and the placement the gizmo lands on must agree.</summary>
        [Fact]
        public void ScaleY_overrides_the_content_aspect()
        {
            var transform = new ModelTransform { Scale = 0.5, ScaleY = 0.25 };

            var rect = ItemPlacement.Compose(transform, pictureAspect: 1.0, 800, 400);

            Assert.Equal(400, rect.W, 6);  // 0.5 of the canvas width
            Assert.Equal(100, rect.H, 6);  // 0.25 of the canvas height, NOT 400 (aspect 1.0)
            Assert.Equal(200, rect.X, 6);  // still centred
            Assert.Equal(150, rect.Y, 6);
        }

        /// <summary>Text scales off its own natural block on both axes, so ScaleY multiplies the
        /// natural height exactly as Scale multiplies the natural width.</summary>
        [Fact]
        public void ScaleY_multiplies_the_natural_block_for_text()
        {
            var transform = new ModelTransform { Scale = 2.0, ScaleY = 0.5 };

            var rect = ItemPlacement.ComposeNatural(transform, naturalWidth: 100, naturalHeight: 40, 800, 400);

            Assert.Equal(200, rect.W, 6);
            Assert.Equal(20, rect.H, 6);
        }

        /// <summary>A free corner drag puts the dragged corner under the pointer on BOTH axes —
        /// unlike the locked resize, which follows whichever axis is pulling hardest.</summary>
        [Fact]
        public void ResizeFree_follows_the_pointer_on_both_axes()
        {
            var (scaleX, scaleY, x, y) = GizmoMath.ResizeFree(
                pointerX: 500, pointerY: 150, anchorX: 100, anchorY: 50,
                draggingRight: true, draggingDown: true,
                scaleDenominatorPx: 800, scaleDenominatorYPx: 450,
                canvasX: 100, canvasY: 50, canvasWidth: 800, canvasHeight: 450,
                minScale: 0.01, maxScale: 4);

            Assert.Equal(0.5, scaleX, 6);        // 400px of an 800px canvas
            Assert.Equal(100 / 450.0, scaleY, 6); // 100px of a 450px canvas

            // the anchor is still stationary on both axes
            var rect = ItemPlacement.Compose(
                new ModelTransform { X = x, Y = y, Scale = scaleX, ScaleY = scaleY }, 1.0, 800, 450);
            Assert.Equal(0, rect.X, 6);
            Assert.Equal(0, rect.Y, 6);
        }

        /// <summary>An edge handle moves one axis and leaves the other alone — which is the whole
        /// point of offering them only once the aspect ratio is unlocked.</summary>
        [Fact]
        public void ResizeAxis_anchors_the_opposite_edge()
        {
            var (scale, center) = GizmoMath.ResizeAxis(
                pointer: 500, anchor: 100, draggingPositive: true, denominatorPx: 800,
                canvasOrigin: 100, canvasExtent: 800, minScale: 0.01, maxScale: 4);

            Assert.Equal(0.5, scale, 6);
            Assert.Equal(0.25, center, 6); // centre 200px right of an anchor at the canvas origin
        }

        /// <summary>…and a clamped edge drag still anchors, for the same reason the corner one
        /// does: the centre is computed from the size the clamp allowed, not the pointer.</summary>
        [Fact]
        public void ResizeAxis_anchors_the_clamped_size()
        {
            var (scale, center) = GizmoMath.ResizeAxis(
                pointer: 101, anchor: 100, draggingPositive: true, denominatorPx: 800,
                canvasOrigin: 100, canvasExtent: 800, minScale: 0.05, maxScale: 4);

            Assert.Equal(0.05, scale, 6);
            Assert.Equal(0.025, center, 6); // half of the clamped 40px width, off the anchor
        }

        // ---------------------------------------------------------------------------- rotation

        /// <summary>Clockwise rotation, matching the composer's <c>RotateDegrees</c> sense (+90°
        /// sends the point right of the centre to below it), and unrotating by the negated angle
        /// round-trips.</summary>
        [Fact]
        public void RotateAbout_matches_the_composer_and_round_trips()
        {
            var (x, y) = GizmoMath.RotateAbout(110, 50, 100, 50, 90);
            Assert.Equal(100, x, 6);
            Assert.Equal(60, y, 6);

            var (bx, by) = GizmoMath.RotateAbout(x, y, 100, 50, -90);
            Assert.Equal(110, bx, 6);
            Assert.Equal(50, by, 6);
        }

        /// <summary>The click hit-test unrotates the point about the item centre, so a rotated item
        /// owns the pixels it actually covers — not its unrotated rect. A 90°-rotated wide overlay
        /// covers points above/below its centre that the unrotated rect misses, and no longer
        /// covers the left/right ends of that rect.</summary>
        [Fact]
        public void Hit_test_follows_a_rotated_item()
        {
            var project = MediaProject(1920, 1080, out var item);
            // 400x225 centred on an 800x450 canvas: unrotated rect (200,112.5)-(600,337.5)
            item.Transform = new ModelTransform { Scale = 0.5, Rotation = 90 };

            // rotated 90° the item is 225 wide and 400 tall about (400,225): x 287.5..512.5, y 25..425
            Assert.NotNull(ItemPlacement.HitTest(project, 0, 400, 50, 800, 450));   // above the AABB's top
            Assert.Null(ItemPlacement.HitTest(project, 0, 210, 225, 800, 450));     // inside the unrotated rect, but empty now
        }

        /// <summary>The centre a rotated resize writes puts the anchor's drawn position exactly
        /// back where it was: composing the result and rotating the anchored corner about the new
        /// centre lands on the original visual anchor.</summary>
        [Fact]
        public void AnchoredCenter_pins_the_drawn_anchor_of_a_rotated_resize()
        {
            const double rotation = 30;
            // item: 400x200 centred at (400,225) on an 800x450 canvas at origin (0,0).
            // anchor = unrotated top-left corner (200,125); its drawn position:
            var (avx, avy) = GizmoMath.RotateAbout(200, 125, 400, 225, rotation);

            // user drags the bottom-right corner out to a 600x300 size (still anchored top-left)
            const double w = 600, h = 300;
            var (x, y) = GizmoMath.AnchoredCenter(avx, avy, -w / 2, -h / 2, rotation,
                0, 0, 800, 450);

            // rebuild the drawn anchor from the result: centre + rotated centre-to-anchor vector
            double cx = x * 800, cy = y * 450;
            var (rx, ry) = GizmoMath.RotateAbout(cx - w / 2, cy - h / 2, cx, cy, rotation);
            Assert.Equal(avx, rx, 6);
            Assert.Equal(avy, ry, 6);
        }

        // ------------------------------------------------------------------------------- helpers

        private static Project NewProject() => new Project
        {
            Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
        };

        /// <summary>A one-source, one-video-row project with a single full-length item.</summary>
        private static Project MediaProject(int width, int height, out Item item)
        {
            var project = NewProject();
            var source = new Source
            {
                Id = Guid.NewGuid(),
                Path = VideoPath,
                Streams = new List<SourceStream>
                {
                    new SourceStream
                    {
                        Index = 0,
                        Kind = StreamKind.Video,
                        Width = width,
                        Height = height,
                        AvgFrameRateNum = 30,
                        AvgFrameRateDen = 1,
                        DurationTicks = 5 * TimeSpan.TicksPerSecond,
                    },
                },
            };
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = 5 * TimeSpan.TicksPerSecond,
                Content = new MediaContent { SourceId = source.Id, StreamIndex = 0 },
            };

            project.Sources.Add(source);
            project.Tracks.Add(track);
            project.Items.Add(item);
            return project;
        }

        /// <summary>A capture sidecar holding three single-key runs 100ms apart — three display
        /// rows once <see cref="KeyboardContent.PauseBreakMs"/> is below that gap. Written to a
        /// unique path each time: <c>InputCapture</c> and <c>KeyboardLayout</c> both cache by
        /// path for the life of the process.</summary>
        private static string WriteCapture()
        {
            var path = Path.Combine(Path.GetTempPath(),
                "clowd-input-capture-" + Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllText(path, """
                {"type":"header","version":1,"region":[0,0,1920,1080],"fps_num":30,"fps_den":1,"platform":"windows","monitors":[{"x":0,"y":0,"w":1920,"h":1080,"scale":1.0}]}
                {"type":"event","t":0,"kind":"kd","vk":65,"ch":"a"}
                {"type":"event","t":100,"kind":"kd","vk":66,"ch":"b"}
                {"type":"event","t":200,"kind":"kd","vk":67,"ch":"c"}
                """);
            return path;
        }

        /// <summary>A white full-frame background with a keyboard overlay row over it, at the
        /// transform <c>EditorSession.AddKeyboardTrack</c> creates (bottom centre at 0.85, half the
        /// canvas wide). The runs linger long enough that all three are visible at the one second
        /// <see cref="Compose"/> draws.</summary>
        private static Project KeyboardProject(string capturePath, out Item item, out Item background)
        {
            var project = NewProject();
            var source = new Source
            {
                Id = Guid.NewGuid(),
                Path = VideoPath,
                InputCapturePath = capturePath,
                Streams = new List<SourceStream>(),
            };
            project.Sources.Add(source);

            var backTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Back", Order = 0 };
            background = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = backTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = 5 * TimeSpan.TicksPerSecond,
                Content = new SolidContent { Color = "#FFFFFFFF" },
            };

            var keyTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Keys", Order = 1 };
            item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = keyTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = 5 * TimeSpan.TicksPerSecond,
                Content = new KeyboardContent
                {
                    SourceId = source.Id,
                    FontSize = 60,
                    LingerMs = 5000,
                    PauseBreakMs = 10,
                },
                Transform = new ModelTransform { X = 0.5, Y = 0.85, Scale = 0.5 },
                LinkGroupId = Guid.NewGuid(),
            };

            project.Tracks.Add(backTrack);
            project.Tracks.Add(keyTrack);
            project.Items.Add(background);
            project.Items.Add(item);
            return project;
        }

        private static Project TextProject(string text, double size, out Item item)
        {
            var project = NewProject();
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Titles", Order = 0 };
            item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = 5 * TimeSpan.TicksPerSecond,
                Content = new TextContent { Text = text, Size = size, Color = "#FFFFFFFF", Align = TextAlign.Center },
            };

            project.Tracks.Add(track);
            project.Items.Add(item);
            return project;
        }

        /// <summary>Composes only the given stream (every other item's frames are withheld) onto a
        /// canvas of the given size, and returns its BGRA pixels.</summary>
        private static byte[] ComposeStreamOnly(Project project, int width, int height, int streamIndex, SKImage image)
        {
            using (image)
                return Compose(project, width, height, new SingleStreamFrameSource(image, streamIndex));
        }

        private static byte[] Compose(Project project, int width, int height, IFrameSource frames)
        {
            using var factory = new CpuSurfaceFactory();
            using var surface = factory.CreateSurface(width, height);
            FrameComposer.Compose(project, TimeSpan.TicksPerSecond, frames, surface.Canvas, width, height);

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

        private static SKImage SolidImage(int width, int height, SKColor color)
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(color);
            return surface.Snapshot();
        }

        /// <summary>Leftmost and rightmost drawn (non-black) pixel on one scanline, as canvas
        /// coordinates of the outer edges. (0, 0) when the scanline is empty.</summary>
        private static (double Left, double Right) DrawnSpanX(byte[] bgra, int width, int y)
        {
            int left = -1, right = -1;
            for (int x = 0; x < width; x++)
            {
                // half-covered antialiased edge pixels count as outside
                if (bgra[(y * width + x) * 4 + 1] < 128)
                    continue;

                if (left < 0)
                    left = x;
                right = x;
            }

            return left < 0 ? (0, 0) : (left, right + 1);
        }

        /// <summary>Topmost and bottommost drawn pixel in one column.</summary>
        private static (double Top, double Bottom) DrawnSpanY(byte[] bgra, int width, int height, int x)
        {
            int top = -1, bottom = -1;
            for (int y = 0; y < height; y++)
            {
                if (bgra[(y * width + x) * 4 + 1] < 128)
                    continue;

                if (top < 0)
                    top = y;
                bottom = y;
            }

            return top < 0 ? (0, 0) : (top, bottom + 1);
        }

        /// <summary>Bounding box of everything <i>darker</i> than the canvas, or null when nothing
        /// is — the inverse of <see cref="InkBounds"/>, for the keyboard's semi-transparent black
        /// pills over a white background (which the ink scan cannot see, both being bright).</summary>
        private static (double Left, double Top, double Right, double Bottom)? DarkBounds(
            byte[] bgra, int width, int height)
        {
            int left = Int32.MaxValue, top = Int32.MaxValue, right = -1, bottom = -1;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (bgra[(y * width + x) * 4 + 1] > 200)
                        continue;

                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }

            return right < 0 ? null : (left, top, right + 1, bottom + 1);
        }

        /// <summary>Bounding box of everything drawn on the canvas (any non-black pixel), or null
        /// when nothing was.</summary>
        private static (double Left, double Top, double Right, double Bottom)? InkBounds(byte[] bgra, int width, int height)
        {
            int left = Int32.MaxValue, top = Int32.MaxValue, right = -1, bottom = -1;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (bgra[(y * width + x) * 4 + 1] < 64)
                        continue;

                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }

            return right < 0 ? null : (left, top, right + 1, bottom + 1);
        }

        private sealed class SingleStreamFrameSource : IFrameSource
        {
            private readonly SKImage _image;
            private readonly int _streamIndex;

            public SingleStreamFrameSource(SKImage image, int streamIndex)
            {
                _image = image;
                _streamIndex = streamIndex;
            }

            public bool TryGetFrame(Guid sourceId, int streamIndex, long sourceTimeTicks, out FrameRef frame)
            {
                if (streamIndex != _streamIndex)
                {
                    frame = default;
                    return false;
                }

                frame = new FrameRef(_image, sourceTimeTicks);
                return true;
            }
        }
    }
}
