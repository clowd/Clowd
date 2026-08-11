using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Clowd.UI.Services;
using Clowd.UI.VideoEditor;
using Clowd.VideoSDK.Composition;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using SkiaSharp;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The editor's document ↔ project projection (Clowd.Ui exposes its internals to this project),
    /// exercised without Avalonia: <see cref="EditorProject"/> only does integer/geometry math over
    /// a probe, and <see cref="VideoEditPersistence"/> only touches JSON and the filesystem.
    /// </summary>
    public class EditorProjectTests
    {
        private const long Ms = TimeSpan.TicksPerMillisecond;
        private const long DurationMs = 10_000;

        // A real videoedit.json written by the shipped v1 editor (session_20260810_162448_241.0),
        // copied in verbatim so the migration is tested against the actual file format rather than
        // against a hand-written approximation, and without depending on that machine's path.
        private const string RealV1Json = """
            {
              "Version": 1,
              "TrimStartMs": 2151,
              "TrimEndMs": 0,
              "WebcamEnabled": false,
              "WebcamShape": "Circle",
              "WebcamCornerRadius": 0.25,
              "WebcamCenterX": 0.82,
              "WebcamCenterY": 0.78,
              "WebcamWidth": 0.2,
              "Cuts": []
            }
            """;

        // ------------------------------------------------------------------ probe fixture

        private static MediaProbeResult Probe(bool withWebcam = true, bool withAudio = true) =>
            new MediaProbeResult
            {
                Path = @"C:\recordings\video.mp4",
                DurationTicks = DurationMs * Ms,
                VideoStreams = withWebcam
                    ? new[] { ScreenStream(), WebcamStream() }
                    : new[] { ScreenStream() },
                AudioStreams = withAudio
                    ? new[] { new AudioStreamProbe { StreamIndex = 2, SampleRate = 48_000, Channels = 2, DurationTicks = DurationMs * Ms } }
                    : Array.Empty<AudioStreamProbe>(),
                HasAudio = withAudio,
            };

        private static VideoStreamProbe ScreenStream() => new VideoStreamProbe
        {
            StreamIndex = 0,
            Width = 1920,
            Height = 1080,
            AvgFrameRateNum = 30,
            AvgFrameRateDen = 1,
            RFrameRateNum = 30,
            RFrameRateDen = 1,
            DurationTicks = DurationMs * Ms,
        };

        private static VideoStreamProbe WebcamStream() => new VideoStreamProbe
        {
            StreamIndex = 1,
            Width = 640,
            Height = 480,
            AvgFrameRateNum = 30,
            AvgFrameRateDen = 1,
            RFrameRateNum = 30,
            RFrameRateDen = 1,
            DurationTicks = DurationMs * Ms,
        };

        private static EditorProject NewEdit(VideoEditDocument document, bool withWebcam = true, bool withAudio = true) =>
            new EditorProject(@"C:\recordings\video.mp4", Probe(withWebcam, withAudio), document);

        private static List<Item> ItemsOn(Project project, string trackName)
        {
            var track = project.Tracks.Single(t => t.Name == trackName);
            return project.Items.Where(i => i.TrackId == track.Id)
                          .OrderBy(i => i.TimelineStartTicks)
                          .ToList();
        }

        private static string TempPath() =>
            Path.Combine(Path.GetTempPath(), "clowd-videoedit-" + Guid.NewGuid().ToString("N") + ".json");

        private static string WriteTemp(string json)
        {
            var path = TempPath();
            File.WriteAllText(path, json);
            return path;
        }

        // ------------------------------------------------------------------ v1 migration

        [Fact]
        public void Real_v1_file_migrates_into_the_project_it_describes()
        {
            var path = WriteTemp(RealV1Json);
            try
            {
                var document = new VideoEditDocument();
                Assert.True(VideoEditPersistence.TryLoadInto(path, document, DurationMs));

                // the document values the file carried
                Assert.Equal(2151, document.TrimStartMs);
                Assert.Equal(0, document.TrimEndMs);
                Assert.Empty(document.Cuts);
                Assert.False(document.Webcam.Enabled);
                Assert.Equal(WebcamOverlayShape.Circle, document.Webcam.Shape);
                Assert.Equal(0.25, document.Webcam.CornerRadius, 9);
                Assert.Equal(0.82, document.Webcam.CenterX, 9);
                Assert.Equal(0.78, document.Webcam.CenterY, 9);
                Assert.Equal(0.2, document.Webcam.Width, 9);

                var edit = NewEdit(document);
                Assert.True(edit.Rebuild());
                var project = edit.Current;

                Assert.Empty(project.Validate());
                Assert.Equal(Project.CurrentVersion, project.Version);
                Assert.Equal(1920, project.Output.WidthPx);
                Assert.Equal(1080, project.Output.HeightPx);
                Assert.Equal(30, project.Output.FpsNum);
                Assert.Equal(1, project.Output.FpsDen);
                Assert.Equal(48_000, project.Output.SampleRate);

                // one source, three rows, one link group
                var source = Assert.Single(project.Sources);
                Assert.Equal(@"C:\recordings\video.mp4", source.Path);
                Assert.Equal(new[] { 0, 1, 2 }, source.Streams.Select(s => s.Index).ToArray());
                Assert.Equal(3, project.Tracks.Count);
                Assert.Single(project.Items.Select(i => i.LinkGroupId).Distinct());

                // the trim is one item per row, starting 2151ms into the source and running to the end
                var screen = Assert.Single(ItemsOn(project, "Screen"));
                Assert.Equal(0, screen.TimelineStartTicks);
                Assert.Equal((DurationMs - 2151) * Ms, screen.DurationTicks);
                Assert.Equal(2151 * Ms, ((MediaContent)screen.Content).SourceInTicks);
                Assert.Equal(0, ((MediaContent)screen.Content).StreamIndex);

                // "webcam overlay off" hides the row rather than dropping it, so the placement
                // survives turning it back on.
                Assert.True(project.Tracks.Single(t => t.Name == "Webcam").Hidden);
                var cam = Assert.Single(ItemsOn(project, "Webcam"));
                Assert.Equal(screen.TimelineStartTicks, cam.TimelineStartTicks);
                Assert.Equal(screen.DurationTicks, cam.DurationTicks);
                Assert.Equal(MaskShape.Circle, cam.Transform.Mask.Shape);

                var audio = Assert.Single(ItemsOn(project, "Audio"));
                Assert.Equal(2151 * Ms, ((MediaContent)audio.Content).SourceInTicks);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Migrated_v1_edit_saves_and_reloads_as_a_v2_project()
        {
            var path = WriteTemp(RealV1Json);
            try
            {
                var document = new VideoEditDocument();
                Assert.True(VideoEditPersistence.TryLoadInto(path, document, DurationMs));
                var edit = NewEdit(document);
                Assert.True(edit.Rebuild());

                // saving writes the project, not the legacy DTO
                File.WriteAllBytes(path, VideoEditPersistence.Serialize(edit.Current));
                Assert.Contains("\"Version\": 2", File.ReadAllText(path));

                var reloaded = new VideoEditDocument();
                Assert.True(VideoEditPersistence.TryLoadInto(path, reloaded, DurationMs));

                Assert.Equal(document.TrimStartMs, reloaded.TrimStartMs);
                Assert.Equal(document.TrimEndMs, reloaded.TrimEndMs); // untrimmed tail keeps the 0 sentinel
                Assert.Equal(document.Webcam.Enabled, reloaded.Webcam.Enabled);
                Assert.Equal(document.Webcam.Shape, reloaded.Webcam.Shape);
                Assert.Equal(document.Webcam.CornerRadius, reloaded.Webcam.CornerRadius, 9);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Unknown_versions_leave_the_document_untouched()
        {
            var path = WriteTemp("""{ "Version": 99, "TrimStartMs": 500 }""");
            try
            {
                var document = new VideoEditDocument();
                Assert.False(VideoEditPersistence.TryLoadInto(path, document, DurationMs));
                Assert.Equal(0, document.TrimStartMs);
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ------------------------------------------------------- document/project round trip

        /// <summary>A trim + cut + overlay edit must survive the trip through the v2 project
        /// unchanged, because the export path still writes v1 render-args from the document: the
        /// keep segments (and therefore the args' segment list) and the overlay rect handed to the
        /// render tool have to come back bit-for-bit.</summary>
        [Fact]
        public void Trim_cut_and_overlay_round_trip_to_identical_render_args()
        {
            var document = new VideoEditDocument
            {
                TrimStartMs = 1_000,
                TrimEndMs = 9_000,
            };
            document.AddCut(3_000, 4_500);
            document.AddCut(6_000, 6_800);
            document.Webcam.Enabled = true;
            document.Webcam.Shape = WebcamOverlayShape.RoundedRect;
            document.Webcam.CornerRadius = 0.3;
            document.Webcam.CenterX = 0.7;
            document.Webcam.CenterY = 0.65;
            document.Webcam.Width = 0.25;

            var edit = NewEdit(document);
            Assert.True(edit.Rebuild());

            var expectedSegments = RenderArgs.ToSegments(document.GetKeepSegments(DurationMs));
            var expectedRect = VideoRenderManager.ComputeWebcamRect(document.Webcam, edit.RenderSource);

            // the project's screen row IS the keep-segment list, placed back to back
            var screenItems = ItemsOn(edit.Current, "Screen");
            Assert.Equal(expectedSegments.Count, screenItems.Count);
            long timeline = 0;
            for (int i = 0; i < screenItems.Count; i++)
            {
                var item = screenItems[i];
                Assert.Equal(timeline, item.TimelineStartTicks);
                Assert.Equal(expectedSegments[i].StartMs * Ms, ((MediaContent)item.Content).SourceInTicks);
                Assert.Equal((expectedSegments[i].EndMs - expectedSegments[i].StartMs) * Ms, item.DurationTicks);
                timeline += item.DurationTicks;
            }

            // …and the webcam transform is that very rect, normalized
            var expectedTransform = RecordingProject.WebcamTransform(
                expectedRect.X, expectedRect.Y, expectedRect.W, expectedRect.H, 1920, 1080, null);
            var camTransform = ItemsOn(edit.Current, "Webcam")[0].Transform;
            Assert.Equal(expectedTransform.X, camTransform.X, 9);
            Assert.Equal(expectedTransform.Y, camTransform.Y, 9);
            Assert.Equal(expectedTransform.Scale, camTransform.Scale, 9);
            Assert.Equal(MaskShape.RoundedRect, camTransform.Mask.Shape);
            Assert.Equal(0.3, camTransform.Mask.CornerRadius, 9);

            // save → load → the render args the export path would write are unchanged
            var reloaded = new VideoEditDocument();
            EditorProject.ApplyToDocument(Project.FromJson(edit.Current.ToJson()), reloaded, DurationMs);

            var actualSegments = RenderArgs.ToSegments(reloaded.GetKeepSegments(DurationMs));
            Assert.Equal(expectedSegments.Count, actualSegments.Count);
            for (int i = 0; i < expectedSegments.Count; i++)
            {
                Assert.Equal(expectedSegments[i].StartMs, actualSegments[i].StartMs);
                Assert.Equal(expectedSegments[i].EndMs, actualSegments[i].EndMs);
            }

            var reloadedEdit = NewEdit(reloaded);
            var actualRect = VideoRenderManager.ComputeWebcamRect(reloaded.Webcam, reloadedEdit.RenderSource);
            Assert.Equal(expectedRect.X, actualRect.X);
            Assert.Equal(expectedRect.Y, actualRect.Y);
            Assert.Equal(expectedRect.W, actualRect.W);
            Assert.Equal(expectedRect.H, actualRect.H);
        }

        // ------------------------------------------------------------- editor-state round trip

        /// <summary>A cut the trim range currently excludes leaves no gap between two items, so the
        /// v2 project alone cannot carry it: without the editor-state block, merely opening and
        /// closing the editor would drop it, and widening the trim afterwards would silently bring
        /// the cut-out material back into the preview and the render.</summary>
        [Fact]
        public void A_cut_outside_the_trim_range_survives_save_and_reload()
        {
            var document = new VideoEditDocument();
            document.AddCut(1_000, 3_000);
            document.TrimStartMs = 5_000;

            var edit = NewEdit(document);
            Assert.True(edit.Rebuild());

            // the project really is lossy here: the edit is one unbroken item from 5000ms on.
            var item = Assert.Single(ItemsOn(edit.Current, "Screen"));
            Assert.Equal(5_000 * Ms, ((MediaContent)item.Content).SourceInTicks);

            var path = TempPath();
            try
            {
                File.WriteAllBytes(path, VideoEditPersistence.Serialize(edit.Current));

                var reloaded = new VideoEditDocument();
                Assert.True(VideoEditPersistence.TryLoadInto(path, reloaded, DurationMs));

                Assert.Equal(5_000, reloaded.TrimStartMs);
                var cut = Assert.Single(reloaded.Cuts);
                Assert.Equal(1_000, cut.StartMs);
                Assert.Equal(3_000, cut.EndMs);

                // what is rendered is unchanged…
                Assert.Equal(document.GetKeepSegments(DurationMs), reloaded.GetKeepSegments(DurationMs));

                // …and dragging the trim back to the head recovers the cut instead of the material
                reloaded.TrimStartMs = 0;
                Assert.Equal(
                    new[] { new CutRegion(0, 1_000), new CutRegion(3_000, DurationMs) },
                    reloaded.GetKeepSegments(DurationMs));
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>The same loss, one step subtler: a cut that straddles the trim start is only
        /// half expressible as items, so the project reads back as a later trim with no cut.</summary>
        [Fact]
        public void A_cut_straddling_the_trim_start_survives_save_and_reload()
        {
            var document = new VideoEditDocument { TrimStartMs = 1_000 };
            document.AddCut(500, 3_000);

            var edit = NewEdit(document);
            Assert.True(edit.Rebuild());

            var path = TempPath();
            try
            {
                File.WriteAllBytes(path, VideoEditPersistence.Serialize(edit.Current));

                var reloaded = new VideoEditDocument();
                Assert.True(VideoEditPersistence.TryLoadInto(path, reloaded, DurationMs));

                Assert.Equal(1_000, reloaded.TrimStartMs);
                var cut = Assert.Single(reloaded.Cuts);
                Assert.Equal(500, cut.StartMs);
                Assert.Equal(3_000, cut.EndMs);
                Assert.Equal(document.GetKeepSegments(DurationMs), reloaded.GetKeepSegments(DurationMs));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void The_editor_block_round_trips_an_ordinary_edit_and_stays_a_valid_project()
        {
            var document = new VideoEditDocument { TrimStartMs = 1_000, TrimEndMs = 9_000 };
            document.AddCut(3_000, 4_500);

            var edit = NewEdit(document);
            Assert.True(edit.Rebuild());

            var json = System.Text.Encoding.UTF8.GetString(VideoEditPersistence.Serialize(edit.Current));
            Assert.Contains("\"" + VideoEditPersistence.EditorStateProperty + "\"", json);

            // the file is still exactly the project the compositor and the render tool read
            var project = Project.FromJson(json);
            Assert.Empty(project.Validate());
            Assert.Equal(edit.Current.Items.Count, project.Items.Count);

            var reloaded = new VideoEditDocument();
            var path = TempPath();
            try
            {
                File.WriteAllText(path, json);
                Assert.True(VideoEditPersistence.TryLoadInto(path, reloaded, DurationMs));
            }
            finally
            {
                File.Delete(path);
            }

            Assert.Equal(1_000, reloaded.TrimStartMs);
            Assert.Equal(9_000, reloaded.TrimEndMs);
            Assert.Equal(document.GetCutRanges(), reloaded.GetCutRanges());
        }

        [Fact]
        public void A_v2_file_without_an_editor_block_loads_exactly_as_before()
        {
            var document = new VideoEditDocument { TrimStartMs = 1_000 };
            document.AddCut(3_000, 4_500);

            var edit = NewEdit(document);
            Assert.True(edit.Rebuild());

            // a project this process did not build carries no state, so nothing is written —
            // which is also what every file saved before the block existed looks like.
            var plain = Project.FromJson(edit.Current.ToJson());
            Assert.Null(EditorProject.StateOf(plain));

            var path = TempPath();
            try
            {
                File.WriteAllBytes(path, VideoEditPersistence.Serialize(plain));
                Assert.DoesNotContain(VideoEditPersistence.EditorStateProperty, File.ReadAllText(path));

                var reloaded = new VideoEditDocument();
                Assert.True(VideoEditPersistence.TryLoadInto(path, reloaded, DurationMs));

                Assert.Equal(1_000, reloaded.TrimStartMs);
                Assert.Equal(0, reloaded.TrimEndMs); // untrimmed tail keeps the sentinel
                Assert.Equal(document.GetCutRanges(), reloaded.GetCutRanges());
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>The block may only add back what the project cannot express. One that would
        /// change the edit (hand-edited, or left over from a different project) is discarded.</summary>
        [Fact]
        public void An_editor_block_that_disagrees_with_the_project_is_ignored()
        {
            var document = new VideoEditDocument { TrimStartMs = 1_000 };
            var edit = NewEdit(document);
            Assert.True(edit.Rebuild());

            var json = edit.Current.ToJson();
            json = json.TrimEnd().TrimEnd('}') +
                   ", \"" + VideoEditPersistence.EditorStateProperty + "\": " +
                   """{ "Version": 1, "TrimStartMs": 8000, "TrimEndMs": 0, "Cuts": [] } }""";

            var path = WriteTemp(json);
            try
            {
                var reloaded = new VideoEditDocument();
                Assert.True(VideoEditPersistence.TryLoadInto(path, reloaded, DurationMs));

                Assert.Equal(1_000, reloaded.TrimStartMs); // the project's own trim, not the block's
                Assert.Empty(reloaded.Cuts);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Rebuilding_keeps_the_source_and_track_identities()
        {
            // CompositionPlayer only reopens decoders when the referenced (source, stream) set
            // changes, so an edit that mints new ids would tear the pipeline down on every drag.
            var document = new VideoEditDocument();
            var edit = NewEdit(document);
            Assert.True(edit.Rebuild());
            var before = edit.Current;

            document.TrimStartMs = 500;
            document.AddCut(2_000, 3_000);
            Assert.True(edit.Rebuild());
            var after = edit.Current;

            Assert.NotSame(before, after);
            Assert.Equal(before.Sources[0].Id, after.Sources[0].Id);
            Assert.Equal(
                before.Tracks.Select(t => t.Id).ToArray(),
                after.Tracks.Select(t => t.Id).ToArray());
            // the cut split the row in two, without changing which streams it references
            Assert.Equal(2, ItemsOn(after, "Screen").Count);
        }

        [Fact]
        public void An_edit_that_keeps_nothing_leaves_the_last_project_in_place()
        {
            var document = new VideoEditDocument();
            var edit = NewEdit(document);
            Assert.True(edit.Rebuild());
            var last = edit.Current;

            document.AddCut(0, DurationMs);
            Assert.False(edit.Rebuild());
            Assert.Same(last, edit.Current);
        }

        [Fact]
        public void A_recording_without_a_webcam_has_no_webcam_row()
        {
            var document = new VideoEditDocument();
            document.Webcam.Enabled = true;

            var edit = NewEdit(document, withWebcam: false, withAudio: false);
            Assert.False(edit.HasWebcam);
            Assert.True(edit.Rebuild());

            Assert.Single(edit.Current.Tracks);
            Assert.Equal(RecordingProject.FallbackSampleRate, edit.Current.Output.SampleRate);
            Assert.Empty(edit.Current.Validate());
        }

        // ------------------------------------------------------------------ time mapping

        [Fact]
        public void Time_map_folds_cuts_out_of_the_timeline()
        {
            var document = new VideoEditDocument { TrimStartMs = 1_000 };
            document.AddCut(3_000, 5_000);

            var edit = NewEdit(document);
            Assert.True(edit.Rebuild());
            var map = edit.TimeMap;

            // kept: [1000,3000) + [5000,10000) => 7000ms of output
            Assert.Equal(7_000, map.TimelineDurationMs);

            Assert.Equal(1_000, map.ToSourceMs(0));
            Assert.Equal(2_500, map.ToSourceMs(1_500));
            Assert.Equal(5_000, map.ToSourceMs(2_000)); // first instant after the cut
            Assert.Equal(10_000, map.ToSourceMs(999_999)); // past the end clamps

            Assert.Equal(0, map.ToTimelineMs(0));        // inside the trimmed head
            Assert.Equal(0, map.ToTimelineMs(1_000));
            Assert.Equal(1_500, map.ToTimelineMs(2_500));
            Assert.Equal(2_000, map.ToTimelineMs(4_000)); // inside the cut => the seam
            Assert.Equal(2_000, map.ToTimelineMs(5_000));
            Assert.Equal(7_000, map.ToTimelineMs(10_000));
        }

        [Fact]
        public void Time_map_of_an_unedited_recording_is_the_identity()
        {
            var edit = NewEdit(new VideoEditDocument());
            Assert.True(edit.Rebuild());

            for (long ms = 0; ms <= DurationMs; ms += 250)
            {
                Assert.Equal(ms, edit.TimeMap.ToSourceMs(ms));
                Assert.Equal(ms, edit.TimeMap.ToTimelineMs(ms));
            }
        }

        // -------------------------------------------------------------- webcam gizmo placement

        /// <summary>
        /// The webcam gizmo (outline, mask preview and corner handles) is arranged on
        /// <see cref="WebcamPlacement.Compose"/>, so that rect has to be where
        /// <see cref="FrameComposer"/> actually draws the camera — including the case the composer
        /// does not bound: a 4:3 camera at 90% width on a 16:9 recording is drawn taller than the
        /// frame and merely clipped. Ground truth here is composed pixels, not a repeat of the
        /// formula: the gizmo used to clamp its height to the frame, which put the outline 90px
        /// short of the picture (its ellipse converged to a point where the composed one was still
        /// ~400px wide).
        /// </summary>
        [Fact]
        public void Webcam_gizmo_rect_follows_the_composed_picture_when_the_overlay_is_taller_than_the_frame()
        {
            const int CanvasW = 800, CanvasH = 450;

            var document = new VideoEditDocument();
            document.Webcam.Enabled = true;
            document.Webcam.Shape = WebcamOverlayShape.Circle;
            document.Webcam.Width = 0.9;
            document.Webcam.CenterY = 0.1; // the frame-clamped rect pins the composed centre to 0.5

            var edit = NewEdit(document);
            Assert.True(edit.Rebuild());

            var transform = EditorProject.WebcamTransformOf(edit.Current);
            Assert.NotNull(transform);

            var gizmo = WebcamPlacement.Compose(transform, edit.WebcamAspect, CanvasW, CanvasH);
            Assert.Equal(720, gizmo.W, 3);
            Assert.Equal(540, gizmo.H, 3);   // 4:3 of 720 — taller than the 450 frame
            Assert.Equal(-45, gizmo.Y, 3);   // and therefore hanging off both edges

            // what the composer draws, measured
            var pixels = ComposeWebcamOnly(edit.Current, CanvasW, CanvasH);

            foreach (int y in new[] { 0, 60, CanvasH / 2, CanvasH - 1 })
            {
                var (drawnLeft, drawnRight) = DrawnSpan(pixels, CanvasW, y);

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
            var (topLeft, topRight) = DrawnSpan(pixels, CanvasW, 0);
            Assert.InRange(topRight - topLeft, 380, 420);
        }

        /// <summary>Composes the project's webcam row only (the screen row's frames are withheld)
        /// onto a canvas of the given size, and returns its BGRA pixels.</summary>
        private static byte[] ComposeWebcamOnly(Project project, int width, int height)
        {
            using var camera = SolidImage(64, 48, SKColors.White); // 4:3, like the probe fixture
            var frames = new WebcamOnlyFrameSource(camera, streamIndex: 1);

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
        private static (double Left, double Right) DrawnSpan(byte[] bgra, int width, int y)
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

        private sealed class WebcamOnlyFrameSource : IFrameSource
        {
            private readonly SKImage _image;
            private readonly int _streamIndex;

            public WebcamOnlyFrameSource(SKImage image, int streamIndex)
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
