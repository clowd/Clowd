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

    /// <summary>
    /// What the recording's session knows about its video streams beyond what a probe can see:
    /// which stream is the webcam (by index, from the recorder's own tracks report), and where the
    /// input-capture jsonl lives. Decoration over the probe, in the exact spirit of the audio
    /// labels — the probe still decides which streams exist, these only classify them — and null
    /// (or a null member) simply falls back to the loader's heuristics. Built from a
    /// <see cref="SessionInfo"/> rather than passed as one so the dev harness and the tests, which
    /// have no session, can construct it (or skip it) directly.
    /// </summary>
    internal sealed class RecordingTrackHints
    {
        public SessionVideoTrack Webcam { get; set; }
        public string InputCapturePath { get; set; }

        /// <summary>The rounded corners the recorded window was composited with, as a fraction of
        /// the region's height — the units <see cref="Mask.CornerRadius"/> is in. 0 = square, which
        /// is every dragged region and every recording made before the capturer reported one.
        /// A fraction rather than pixels because the recording's own frame size need not match the
        /// region's (a Retina region is recorded at 2x), and a ratio is the same in either space.
        /// </summary>
        public double ScreenCornerRadius { get; set; }

        public static RecordingTrackHints From(SessionInfo session) => session == null
            ? null
            : new RecordingTrackHints
            {
                Webcam = session.WebcamTrack,
                InputCapturePath = session.InputCapturePath,
                ScreenCornerRadius = ScreenCornerRadiusOf(session),
            };

        /// <summary>The session's capture-space corner radius as a fraction of the region's
        /// height, clamped to the half that is a fully rounded end. A session with no region to
        /// measure against (an imported file, a project) has no radius to express.</summary>
        private static double ScreenCornerRadiusOf(SessionInfo session)
        {
            var bounds = session.OriginalBounds;
            if (session.CornerRadius <= 0 || bounds == null || bounds.Height <= 0)
                return 0;

            return Math.Min(session.CornerRadius / bounds.Height, 0.5);
        }
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
        /// <param name="hints">The session's classification of the recording's video streams and
        /// its input-capture file (see <see cref="RecordingTrackHints"/>); null when there is no
        /// session. Like the labels, decoration over the probe and build-time only — a saved
        /// project already knows which of its streams is which.</param>
        public static Project LoadOrCreate(string editJsonPath, string videoPath, MediaProbeResult probe,
            IReadOnlyList<string> audioTrackNames = null, RecordingTrackHints hints = null)
        {
            ArgumentNullException.ThrowIfNull(probe);

            return TryLoadProject(editJsonPath, videoPath, probe, audioTrackNames, hints)
                   ?? BuildFromDocument(FreshDocument(), videoPath, probe, audioTrackNames, hints,
                       freshEdit: true);
        }

        /// <summary>The canvas a project with nothing in it yet composes onto. Provisional: the
        /// first media imported into an empty project adopts its own size and rate (see
        /// <c>EditorSession.ImportMedia</c>), so these values only ever survive a project the user
        /// puts nothing but text and shapes into.</summary>
        public const int BlankWidthPx = 1920;
        public const int BlankHeightPx = 1080;
        public const int BlankFpsNum = 30;

        /// <summary>
        /// The project for a <b>blank</b> video edit — one with no recording behind it, started from
        /// the Video button rather than opened onto a capture. The saved v2 document when the
        /// session already has one (this is how such a project is reopened), otherwise an empty
        /// project on the default canvas: no sources, no tracks, no items, which the session treats
        /// as a legal (and undoable) state and the window shows as "import something".
        ///
        /// There is no v1 to migrate and no recording to reconcile paths against — every source in
        /// one of these projects was imported by path and keeps it.
        /// </summary>
        public static Project LoadOrCreateBlank(string editJsonPath)
        {
            var saved = TryLoadBlank(editJsonPath);
            if (saved != null)
                return saved;

            return new Project
            {
                Output = new OutputSettings
                {
                    WidthPx = BlankWidthPx,
                    HeightPx = BlankHeightPx,
                    FpsNum = BlankFpsNum,
                    FpsDen = 1,
                    SampleRate = RecordingProject.FallbackSampleRate,
                },
            };
        }

        /// <summary>The saved project of a blank edit, or null when there is nothing loadable —
        /// best-effort on the same terms as <see cref="TryLoadProject"/>: a broken edit file costs
        /// the edit, never the ability to open the editor.</summary>
        private static Project TryLoadBlank(string path)
        {
            try
            {
                if (String.IsNullOrEmpty(path) || !File.Exists(path))
                    return null;

                var bytes = File.ReadAllBytes(path);
                var version = JsonSerializer.Deserialize(bytes, VideoEditJsonContext.Default.VideoEditVersionDto);
                if (version?.Version != Project.CurrentVersion)
                    return null;

                var project = Project.FromJson(Encoding.UTF8.GetString(bytes));
                project?.Normalize();
                return project;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to load videoedit.json: " + ex.Message);
                SentryConfig.CaptureHandled(ex, "videoeditor.load-project");
                return null;
            }
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
            IReadOnlyList<string> audioTrackNames, RecordingTrackHints hints)
        {
            try
            {
                if (String.IsNullOrEmpty(path) || !File.Exists(path))
                    return null;

                var bytes = File.ReadAllBytes(path);
                var version = JsonSerializer.Deserialize(bytes, VideoEditJsonContext.Default.VideoEditVersionDto);

                return version?.Version switch
                {
                    VideoEditDocumentDto.CurrentVersion => MigrateLegacy(bytes, videoPath, probe, audioTrackNames, hints),
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
            IReadOnlyList<string> audioTrackNames, RecordingTrackHints hints)
        {
            var document = new VideoEditDocument();
            return LoadLegacy(bytes, document)
                ? BuildFromDocument(document, videoPath, probe, audioTrackNames, hints)
                : null;
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
            MediaProbeResult probe, IReadOnlyList<string> audioTrackNames, RecordingTrackHints hints,
            bool freshEdit = false)
        {
            var videoStreams = probe.VideoStreams ?? Array.Empty<VideoStreamProbe>();
            if (videoStreams.Count == 0)
                throw new InvalidOperationException("The recording has no video stream.");

            // the screen is always the first video stream — the one rule every recorder version
            // shares, and the one the session never needs to hint at.
            var screen = videoStreams[0];

            // the webcam by the session's reported index when it has one; the fallback is the old
            // "track 1" positional rule, which is all a session written before the report existed
            // — or a dev-harness open with no session — has.
            var cam = FindHintedStream(videoStreams, hints?.Webcam)
                      ?? FindPositionalWebcam(videoStreams);
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
                // only ever from the session: the jsonl's location is nothing a probe can see, and
                // an old recording without one must not be pointed at a path that was never there.
                InputCapturePath = hints?.InputCapturePath,
                AudioStreams = audioStreams,
                // a v1 file predates separate audio tracks entirely, so in practice only a fresh
                // create has labels to apply — but they belong to the recording, not to the edit.
                AudioTrackNames = audioTrackNames,
                FpsNum = fpsNum,
                FpsDen = fpsDen,
                Segments = segments,
                WebcamTransform = cam == null ? null
                    : freshEdit ? FreshWebcamTransform(document.Webcam)
                    : WebcamTransformOf(document.Webcam, screen, cam),
                WebcamSurround = cam != null && freshEdit
                    ? Surround.Create(SurroundKind.Shadow, cursor: false)
                    : null,
                WebcamHidden = !document.Webcam.Enabled,
                // the picked window's rounded corners: the recorder captured the raw region, so
                // the curve travels as metadata and the composition puts it back. Fresh edits
                // only — a saved project carries whatever shape the user settled on, including a
                // deliberately square one.
                ScreenMask = freshEdit && hints?.ScreenCornerRadius > 0
                    ? new Mask { Shape = MaskShape.RoundedRect, CornerRadius = hints.ScreenCornerRadius }
                    : null,
                Ids = RecordingIds.New(audioStreams.Count),
            });
        }

        /// <summary>The probed stream a session hint names, matched by mp4 stream index — never the
        /// screen (a corrupt hint claiming stream 0 is dropped rather than believed). A hint with
        /// no dimensions is not a track anything can lay out and says nothing.</summary>
        private static VideoStreamProbe FindHintedStream(IReadOnlyList<VideoStreamProbe> videoStreams,
            SessionVideoTrack hint)
        {
            if (hint is not { Width: > 0, Height: > 0 })
                return null;

            for (var i = 1; i < videoStreams.Count; i++)
            {
                if (videoStreams[i].StreamIndex == hint.Index)
                    return videoStreams[i];
            }

            return null;
        }

        /// <summary>The legacy positional rule: the webcam is the second video stream. The screen
        /// and the camera are the only video tracks a recording carries, so nothing else can sit
        /// there.</summary>
        private static VideoStreamProbe FindPositionalWebcam(IReadOnlyList<VideoStreamProbe> videoStreams)
        {
            if (videoStreams.Count < 2)
                return null;

            return videoStreams[1];
        }

        /// <summary>
        /// The placement a <b>fresh</b> edit's webcam starts at: bottom-right at 80%, a 1:1 fill
        /// crop in a squircle, at the document's default width. Presentation seeding only — the
        /// model's own defaults (and the inspector's "reset" affordances) are untouched, so the
        /// user can strip any of it back off. A migrated v1 edit never comes here: its placement
        /// is what the file says (<see cref="WebcamTransformOf"/>).
        /// </summary>
        private static Transform FreshWebcamTransform(WebcamOverlay overlay) => new Transform
        {
            X = 0.8,
            Y = 0.8,
            Scale = overlay.Width,
            Aspect = 1.0,
            AspectStretch = false,
            // the radius rides along unused by the squircle, so switching to a rounded rect
            // starts from the overlay's default rather than a square corner.
            Mask = new Mask { Shape = MaskShape.Squircle, CornerRadius = overlay.CornerRadius },
        };

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
