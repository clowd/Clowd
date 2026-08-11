using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clowd.VideoSDK;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;

namespace Clowd.UI.VideoEditor
{
    /// <summary>Wire form of the <b>version 1</b> <c>videoedit.json</c> — the flat trim/cut/webcam
    /// DTO the editor wrote before the composition model existed. Still read (and migrated), never
    /// written: <see cref="VideoEditPersistence"/> saves a v2 <see cref="Project"/> now.</summary>
    internal sealed class VideoEditDocumentDto
    {
        public int Version { get; set; } = CurrentVersion;

        public const int CurrentVersion = 1;

        public long TrimStartMs { get; set; }
        public long TrimEndMs { get; set; }

        public bool WebcamEnabled { get; set; }
        public string WebcamShape { get; set; }
        public double WebcamCornerRadius { get; set; }
        public double WebcamCenterX { get; set; }
        public double WebcamCenterY { get; set; }
        public double WebcamWidth { get; set; }

        public List<VideoEditCutDto> Cuts { get; set; }
    }

    /// <summary>One cut region, half-open <c>[StartMs, EndMs)</c> like <see cref="CutRegion"/>.</summary>
    internal sealed class VideoEditCutDto
    {
        public long StartMs { get; set; }
        public long EndMs { get; set; }
    }

    /// <summary>Just enough of any version of the file to tell which one it is.</summary>
    internal sealed class VideoEditVersionDto
    {
        public int Version { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(VideoEditDocumentDto))]
    [JsonSerializable(typeof(VideoEditVersionDto))]
    internal partial class VideoEditJsonContext : JsonSerializerContext
    { }

    /// <summary>
    /// Reads <c>videoedit.json</c>. The file is the v2 <see cref="Project"/> itself
    /// (<c>ProjectJsonContext</c>) — the same document the compositor plays and the renderer
    /// takes — and version 1 (the flat trim/cut/webcam DTO) is migrated <b>one way</b> on load:
    /// its values go through <see cref="VideoEditDocument"/>'s own math and the next save writes
    /// the project built from them. Saving is the session's business (a bare
    /// <c>Project.ToJson</c> through <c>EditorAutosave</c>); this class is just the way in.
    ///
    /// Files written by the retired single-row editor carry a legacy <c>EditorState</c> sibling
    /// block beside the project's own properties; it is not part of the model and deserialization
    /// reads straight past it.
    /// </summary>
    internal static class VideoEditPersistence
    {
        /// <summary>File name, stored beside session.json in the session directory.</summary>
        public const string FileName = "videoedit.json";

        private const long TicksPerMs = TimeSpan.TicksPerMillisecond;

        /// <summary>
        /// The project to edit for <paramref name="videoPath"/>: the saved edit when the file holds
        /// a usable one, else the whole recording as an identity project. This is the multi-track
        /// editor's way in — it hands the result to an <c>EditorSession</c> and never sees a
        /// <see cref="VideoEditDocument"/> again.
        ///
        /// <list type="bullet">
        /// <item>A <b>v2</b> file <i>is</i> the project: it is deserialized as it stands (the
        /// legacy <c>EditorState</c> block is not a <see cref="Project"/> member, so it is
        /// skipped), then the recording's own source is pointed back at
        /// <paramref name="videoPath"/> — session directories get moved and copied, and the file
        /// the caller opened is by definition the recording the edit describes. Imported media
        /// keeps the path it was imported from.</item>
        /// <item>A <b>v1</b> file goes through the same <see cref="VideoEditDocument"/> math it
        /// always did and is rebuilt as a project, keep segments and pixel-rounded webcam placement
        /// included, so a migrated edit composes exactly as it used to render — but now with one
        /// row per probed audio stream rather than the single one v1 knew about.</item>
        /// <item>Anything else — no file, a corrupt one, one from the future — starts a fresh edit
        /// of the whole recording. The recording is the authority; the edit is convenience.</item>
        /// </list>
        /// </summary>
        /// <param name="editJsonPath">The <c>videoedit.json</c> beside the recording; may be null
        /// (the dev harness opens a file with no session directory to save into).</param>
        /// <param name="videoPath">The recording being edited.</param>
        /// <param name="probe">Its probe — the output canvas, the streams and the duration.</param>
        /// <param name="audioTrackNames">Optional row names for the recording's audio streams,
        /// index-aligned (see <see cref="AudioTrackLabels"/>): the recorder knows which stream is the
        /// microphone, the probe does not. Decoration only — the probe still decides which rows exist
        /// — and it applies to a project being built, never to a saved one (which carries the names
        /// it was built with, and whatever the user renamed them to).</param>
        public static Project LoadOrCreate(string editJsonPath, string videoPath, MediaProbeResult probe,
            IReadOnlyList<string> audioTrackNames = null)
        {
            ArgumentNullException.ThrowIfNull(probe);

            return TryLoadProject(editJsonPath, videoPath, probe, audioTrackNames)
                   ?? BuildFromDocument(FreshDocument(), videoPath, probe, audioTrackNames);
        }

        /// <summary>
        /// The document a <b>fresh</b> edit starts from: the whole recording, with the webcam row
        /// showing. v1 defaulted the overlay off because the single-bar editor had nowhere to put a
        /// camera except on top of the screen, and the user had to opt in; the multi-track editor
        /// has a row for it, and a recording that carries a camera stream was made with the camera
        /// on purpose — opening it invisible reads as a lost track. The row's eye toggle still hides
        /// it, and a <b>migrated v1 file</b> is unaffected: it goes through
        /// <see cref="LoadLegacy"/>, which sets <c>Enabled</c> from the file, so an edit that said
        /// the overlay was off still opens with the row hidden.
        /// </summary>
        private static VideoEditDocument FreshDocument() =>
            new VideoEditDocument { Webcam = { Enabled = true } };

        /// <summary>The saved project, or null when there is nothing loadable — best-effort by
        /// design: a broken edit file must cost the edit, never the recording.</summary>
        private static Project TryLoadProject(string path, string videoPath, MediaProbeResult probe,
            IReadOnlyList<string> audioTrackNames)
        {
            try
            {
                if (String.IsNullOrEmpty(path) || !File.Exists(path))
                    return null;

                var bytes = File.ReadAllBytes(path);
                var version = JsonSerializer.Deserialize(bytes, VideoEditJsonContext.Default.VideoEditVersionDto);

                return version?.Version switch
                {
                    VideoEditDocumentDto.CurrentVersion => MigrateLegacy(bytes, videoPath, probe, audioTrackNames),
                    Project.CurrentVersion => LoadSaved(bytes, videoPath),
                    _ => null,
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to load videoedit.json: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "videoeditor.load-project");
                return null;
            }
        }

        private static Project LoadSaved(byte[] bytes, string videoPath)
        {
            var project = Project.FromJson(Encoding.UTF8.GetString(bytes));
            if (project == null)
                return null;

            project.Normalize();
            ReconcilePrimaryPath(project, videoPath);
            return project;
        }

        private static Project MigrateLegacy(byte[] bytes, string videoPath, MediaProbeResult probe,
            IReadOnlyList<string> audioTrackNames)
        {
            var document = new VideoEditDocument();
            return LoadLegacy(bytes, document) ? BuildFromDocument(document, videoPath, probe, audioTrackNames) : null;
        }

        /// <summary>
        /// Builds the project a document describes over the probed recording. Also the fresh-edit
        /// path, where the document is <see cref="FreshDocument"/> — untrimmed, uncut, every row
        /// the recording carries visible.
        ///
        /// An edit that keeps nothing yields a project with rows and no items rather than a
        /// failure: the session treats an empty project as legal (and undoable), and it is the
        /// window that refuses to hand one to the player or the renderer.
        /// </summary>
        private static Project BuildFromDocument(VideoEditDocument document, string videoPath,
            MediaProbeResult probe, IReadOnlyList<string> audioTrackNames)
        {
            var videoStreams = probe.VideoStreams ?? Array.Empty<VideoStreamProbe>();
            if (videoStreams.Count == 0)
                throw new InvalidOperationException("The recording has no video stream.");

            var screen = videoStreams[0];

            // the webcam is the second video stream, the same "track 1" rule the recorder writes
            // and the render tool reads.
            var cam = videoStreams.Count > 1 ? videoStreams[1] : null;
            if (cam is not { Width: > 0, Height: > 0 })
                cam = null;

            var audioStreams = probe.AudioStreams ?? Array.Empty<AudioStreamProbe>();

            // the container's duration, exactly as the export path has always taken it; a file
            // whose container declares none falls back to the screen stream's own.
            long durationTicks = probe.DurationTicks > 0 ? probe.DurationTicks : screen.DurationTicks;

            var keep = document.GetKeepSegments(durationTicks / TicksPerMs);
            var segments = new List<KeepSegment>(keep.Count);
            foreach (var region in keep)
                segments.Add(new KeepSegment(region.StartMs * TicksPerMs, region.DurationMs * TicksPerMs));

            var (fpsNum, fpsDen) = RecordingProject.ChooseFrameRate(screen);

            return RecordingProject.Build(new RecordingProjectSpec
            {
                InputPath = videoPath,
                Screen = screen,
                Webcam = cam,
                AudioStreams = audioStreams,
                // a v1 file predates separate audio tracks entirely, so in practice only a fresh
                // create has labels to apply — but they belong to the recording, not to the edit.
                AudioTrackNames = audioTrackNames,
                FpsNum = fpsNum,
                FpsDen = fpsDen,
                Segments = segments,
                WebcamTransform = cam != null ? WebcamTransformOf(document.Webcam, screen, cam) : null,
                WebcamHidden = !document.Webcam.Enabled,
                Ids = RecordingIds.New(audioStreams.Count),
            });
        }

        /// <summary>The webcam items' placement, taken through the very pixel rect the v1 render
        /// path was handed (<see cref="ComputeWebcamRect"/>) — its rounding and edge clamping are
        /// part of what a migrated edit composed to, so they have to survive the migration.</summary>
        private static Transform WebcamTransformOf(WebcamOverlay overlay, VideoStreamProbe screen, VideoStreamProbe cam)
        {
            var rect = ComputeWebcamRect(overlay, screen.Width, screen.Height, cam.Width, cam.Height);

            var mask = new Mask
            {
                Shape = overlay.Shape == WebcamOverlayShape.Circle ? MaskShape.Circle : MaskShape.RoundedRect,
                // kept even for a circle (where it has no effect) so switching shapes back and
                // forth — including across a save/reload — does not lose the user's radius.
                CornerRadius = overlay.CornerRadius,
            };

            return RecordingProject.WebcamTransform(rect.X, rect.Y, rect.W, rect.H,
                screen.Width, screen.Height, mask);
        }

        /// <summary>
        /// Turns the v1 document's normalized overlay geometry into output pixels. The width is a
        /// fraction of the screen frame; the height follows the webcam track's own aspect ratio
        /// (the document does not know it), and the whole rect is nudged back inside the frame
        /// rather than clipped.
        ///
        /// Pixel-exact rather than normalized because this is the rect the v1 render path handed
        /// the tool: rounding and edge-clamping are part of what a migrated edit composes to, so
        /// the transform derived from it has to keep them. (Relocated from the retired
        /// <c>EditorProject</c> — migration is the last thing that needs it.)
        /// </summary>
        internal static (int X, int Y, int W, int H) ComputeWebcamRect(WebcamOverlay overlay,
            int screenWidth, int screenHeight, int webcamWidth, int webcamHeight)
        {
            var frameW = Math.Max(1, screenWidth);
            var frameH = Math.Max(1, screenHeight);

            var w = (int)Math.Round(overlay.Width * frameW);
            w = Math.Clamp(w, 2, frameW);

            var aspect = (double)webcamHeight / webcamWidth;
            var h = (int)Math.Round(w * aspect);
            h = Math.Clamp(h, 2, frameH);

            var x = (int)Math.Round(overlay.CenterX * frameW - w / 2.0);
            var y = (int)Math.Round(overlay.CenterY * frameH - h / 2.0);
            x = Math.Clamp(x, 0, frameW - w);
            y = Math.Clamp(y, 0, frameH - h);

            return (x, y, w, h);
        }

        /// <summary>Points the recording's own source back at <paramref name="videoPath"/>. The
        /// primary source is the one the lowest video row's items reference — every other source in
        /// the project was imported and keeps its own path.</summary>
        private static void ReconcilePrimaryPath(Project project, string videoPath)
        {
            if (String.IsNullOrEmpty(videoPath))
                return;

            var primary = PrimarySource(project);
            if (primary != null)
                primary.Path = videoPath;
        }

        private static Source PrimarySource(Project project)
        {
            Track lowestVideo = null;
            foreach (var track in project.Tracks)
            {
                if (track.Kind == TrackKind.Video && (lowestVideo == null || track.Order < lowestVideo.Order))
                    lowestVideo = track;
            }

            if (lowestVideo != null)
            {
                foreach (var item in project.Items)
                {
                    if (item.TrackId != lowestVideo.Id || item.Content is not MediaContent media)
                        continue;

                    foreach (var source in project.Sources)
                    {
                        if (source.Id == media.SourceId)
                            return source;
                    }
                }
            }

            // no video row, or one whose items point at nothing: the first source is the best guess
            // left, and it is the recording's for every project this editor has ever written.
            return project.Sources.Count > 0 ? project.Sources[0] : null;
        }

        /// <summary>The one-way v1 migration: the legacy DTO is exactly the old editor's document,
        /// so applying it and rebuilding the project from it <i>is</i> the migration. Values pass
        /// through the document's own setters, so anything out of range in the file is clamped
        /// exactly as a live edit would have been.</summary>
        private static bool LoadLegacy(byte[] bytes, VideoEditDocument document)
        {
            var dto = JsonSerializer.Deserialize(bytes, VideoEditJsonContext.Default.VideoEditDocumentDto);
            if (dto == null)
                return false;

            document.TrimStartMs = dto.TrimStartMs;
            document.TrimEndMs = dto.TrimEndMs;

            document.Webcam.Enabled = dto.WebcamEnabled;
            if (Enum.TryParse<WebcamOverlayShape>(dto.WebcamShape, ignoreCase: true, out var shape))
                document.Webcam.Shape = shape;
            document.Webcam.CornerRadius = dto.WebcamCornerRadius;
            document.Webcam.CenterX = dto.WebcamCenterX;
            document.Webcam.CenterY = dto.WebcamCenterY;
            document.Webcam.Width = dto.WebcamWidth;

            if (dto.Cuts != null)
            {
                var cuts = new List<CutRegion>(dto.Cuts.Count);
                foreach (var c in dto.Cuts)
                    cuts.Add(new CutRegion(c.StartMs, c.EndMs));
                document.SetCuts(cuts);
            }

            return true;
        }
    }
}
