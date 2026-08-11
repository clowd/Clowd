using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Clowd.UI.Services;
using Clowd.UI.VideoEditor;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
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

        private static string WriteTemp(string json)
        {
            var path = Path.Combine(Path.GetTempPath(), "clowd-videoedit-" + Guid.NewGuid().ToString("N") + ".json");
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
    }
}
