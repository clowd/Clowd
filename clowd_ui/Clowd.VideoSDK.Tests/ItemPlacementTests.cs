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
