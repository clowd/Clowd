using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.UI.Helpers;
using Clowd.UI.VideoEditor;
using Clowd.VideoSDK;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;

namespace Clowd.UI.Services
{
    /// <summary>An in-flight video render, surfaced on the Recent page through
    /// <see cref="SessionInfo.ActiveRender"/> exactly as <see cref="GifConversion"/> is. The render
    /// is owned by the "Edited" session it will fill in, not by the recording it reads.</summary>
    public sealed class VideoRender : SimpleNotifyObject
    {
        // the session this render is producing (the "Edited" row), not the source recording.
        public SessionInfo Session { get; }

        public string Status
        {
            get => _status;
            private set => Set(ref _status, value);
        }

        public double Progress
        {
            get => _progress;
            private set => Set(ref _progress, value, nameof(Progress), nameof(ProgressText));
        }

        public string ProgressText => $"{Progress:0}%";

        public bool IsCanceled { get; private set; }

        /// <summary>Bindable form of <see cref="Cancel"/>, for hosts that prefer a command to a
        /// click handler (the Recent page uses the handler, like the gif row does).</summary>
        public RelayCommand CancelCommand { get; }

        private readonly VidRenderRunner _runner;
        private string _status = "Rendering…";
        private double _progress;

        internal VideoRender(SessionInfo session, VidRenderRunner runner)
        {
            Session = session;
            _runner = runner;
            CancelCommand = new RelayCommand
            {
                Text = "Cancel",
                Executed = _ => Cancel(),
                CanExecute = _ => !IsCanceled,
            };
        }

        /// <summary>Asks the render to stop. The row stays on the page (showing "Canceling…")
        /// until the process is actually gone, at which point the manager removes it — unlike an
        /// upload, there is a child process that must be given a chance to clean up its partial
        /// output first.</summary>
        public void Cancel()
        {
            if (IsCanceled)
                return;

            IsCanceled = true;
            PostStatus("Canceling…");
            _ = CancelCoreAsync();
        }

        private async Task CancelCoreAsync()
        {
            try
            {
                await _runner.CancelAsync();
            }
            catch (Exception ex)
            {
                // the run still resolves through its own completion path, so a failed cancel only
                // means the render may finish normally instead.
                Debug.WriteLine("Failed to cancel video render: " + ex);
                SentryConfig.CaptureHandled(ex, "render.cancel");
            }
        }

        internal void SetProgress(int percent)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsCanceled)
                    return;

                Progress = Math.Clamp(percent, 0, 100);
                // the template shows a bar plus this text, so it carries the number too.
                Status = $"Rendering… {percent}%";
            });
        }

        private void PostStatus(string status)
        {
            Dispatcher.UIThread.Post(() => Status = status);
        }
    }

    /// <summary>
    /// Renders an edited recording beside its source (<c>video.mp4</c> → <c>video-edited.mp4</c>)
    /// in the out-of-process <c>Clowd.VideoRender</c> tool. The render is surfaced as its own Recent-page entry
    /// named "Rendered Video" carrying a <see cref="SessionInfo.ActiveRender"/> while it runs; when it
    /// finishes the entry drops the render and behaves like any other video entry, and when it is
    /// canceled or fails the entry is removed again. Re-rendering the same recording replaces the
    /// entry it made last time rather than accumulating them.
    /// </summary>
    public static class VideoRenderManager
    {
        /// <summary>The name every rendered entry carries in Recents, to tell it apart from the
        /// multi-track "Screen Capture Session" it was rendered from.</summary>
        public const string EditedSessionName = "Rendered Video";

        /// <summary>Name of the render-args file written into the session directory. Unchanged
        /// from the v1 days: the tool dispatches on the file's version, not on its name.</summary>
        public const string RenderArgsFileName = "render-args.json";

        /// <summary>The edited entry already made (or being made) from <paramref name="source"/>,
        /// or null.</summary>
        public static SessionInfo FindExisting(SessionInfo source) => FindExisting(source?.VideoPath);

        /// <summary>Overload for callers that only have the recording path.</summary>
        public static SessionInfo FindExisting(string sourceVideoPath)
        {
            if (String.IsNullOrEmpty(sourceVideoPath))
                return null;

            // matched on EditSourceVideoPath alone: it is set on rendered entries and nothing else,
            // so it identifies them without tying the lookup to the display name (entries written
            // before that name last changed would otherwise never be found, and would pile up).
            return SessionManager.Current.Sessions.FirstOrDefault(s =>
                !String.IsNullOrEmpty(s.EditSourceVideoPath) &&
                String.Equals(s.EditSourceVideoPath, sourceVideoPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Renders <paramref name="source"/> with no editor in the loop — the "Render automatically
        /// when capture finished" setting. The project is the identity edit the editor itself would
        /// open the recording with (whole recording, webcam row visible), built through the same
        /// <see cref="VideoEditPersistence.LoadOrCreate"/>, so the file this produces is what the
        /// user would have got by opening the editor and pressing Render without touching anything.
        ///
        /// This runs unattended, so the failures it can foresee — no FFmpeg, an unprobeable file, a
        /// recording with no video stream — are logged and dropped rather than raised as a dialog
        /// over whatever the user has since moved on to. The capture itself is safe either way, and
        /// the Recents row is still there to render by hand. Must be called on the UI thread.
        /// </summary>
        public static async Task<SessionInfo> StartAutoRenderAsync(SessionInfo source)
        {
            if (source == null || !source.CanEditVideo)
                return null;

            var videoPath = source.VideoPath;
            if (String.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
                return null;

            if (!FFmpegLoader.TryInitialize(FFmpegDirectory))
            {
                Debug.WriteLine("Auto-render skipped, no FFmpeg: " + FFmpegLoader.FailureReason);
                return null;
            }

            Project project;
            try
            {
                var probe = await Task.Run(() => MediaProbe.ProbeDetailed(videoPath));
                if (probe.VideoStreams == null || probe.VideoStreams.Count == 0)
                    return null;

                var sessionDir = Path.GetDirectoryName(source.FilePath);
                var editDocPath = String.IsNullOrEmpty(sessionDir)
                    ? null
                    : Path.Combine(sessionDir, VideoEditPersistence.FileName);

                project = VideoEditPersistence.LoadOrCreate(editDocPath, videoPath, probe,
                    AudioTrackLabels.From(source.AudioTracks), RecordingTrackHints.From(source));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Auto-render could not build a project: " + ex);
                SentryConfig.CaptureHandled(ex, "render.auto-build");
                return null;
            }

            return await StartRenderAsync(source, project);
        }

        /// <summary>Where the FFmpeg natives live in a shipped build: beside the obs-express binary.
        /// Dev machines set CLOWD_FFMPEG_PATH, which FFmpegLoader checks before calling this.</summary>
        private static string FFmpegDirectory()
        {
            var obs = ObsBinaryLocator.Resolve();
            return obs != null ? Path.GetDirectoryName(obs) : null;
        }

        /// <summary>
        /// Creates the edited entry for <paramref name="source"/> and returns it immediately — the
        /// render itself continues in the background and reports through the entry's
        /// <see cref="SessionInfo.ActiveRender"/>. Returns null when there is nothing to render
        /// (the user has been told why), and the existing entry unchanged when a render of this
        /// same recording is already in flight. Any *finished* entry for this recording is replaced,
        /// file and all. Must be called on the UI thread.
        /// </summary>
        public static async Task<SessionInfo> StartRenderAsync(SessionInfo source, Project project)
        {
            if (source == null || project == null)
                return null;

            // an edited entry's own video is the render output; re-editing it is fine, but it is
            // never itself a gif.
            if (!String.IsNullOrEmpty(source.SourceVideoPath))
                return null;

            var videoPath = source.VideoPath;
            if (String.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            {
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Warning,
                    "The recording this edit would be rendered from could not be found. It may have been moved or deleted.",
                    "Can't render the video");
                return null;
            }

            var durationTicks = project.GetDurationTicks();
            if (durationTicks <= 0)
            {
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Warning,
                    "This edit keeps nothing of the recording — trim or cut less and try again.",
                    "Can't render the video");
                return null;
            }

            var problems = project.Validate();
            if (problems.Count > 0)
            {
                // the model is checked here rather than in the tool's error line because only the
                // editor can say what to do about it; the whole list goes to Sentry, the first
                // problem to the user.
                SentryConfig.CaptureHandled(
                    new InvalidOperationException(String.Join(Environment.NewLine, problems)), "render.invalid-project");
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Warning,
                    "This edit can't be rendered: " + problems[0],
                    "Can't render the video");
                return null;
            }

            var missing = FindMissingSource(project);
            if (missing != null)
            {
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Warning,
                    "A file this edit uses could not be found. It may have been moved or deleted:" +
                    Environment.NewLine + missing,
                    "Can't render the video");
                return null;
            }

            // a render already running for this recording owns the output path; asking again just
            // walks the user to the row that is already working on it.
            var existing = FindExisting(source);
            if (existing?.ActiveRender != null)
                return existing;

            // …a finished one is replaced, so re-rendering does not pile up "-edited-2.mp4" files.
            if (existing != null)
                DeleteEntryAndOutput(existing);

            var outputPath = GetOutputPath(videoPath);

            SessionInfo session;
            try
            {
                session = CreateEditedSession(source, outputPath, durationTicks / TimeSpan.TicksPerMillisecond);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to create edited session: " + ex);
                SentryConfig.CaptureHandled(ex, "render.create-session");
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, ex.Message, "Can't render the video");
                return null;
            }

            string renderArgsPath;
            try
            {
                renderArgsPath = WriteProjectArgs(session, project, outputPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to write render args: " + ex);
                SentryConfig.CaptureHandled(ex, "render.write-args");
                DeleteQuietly(session);
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, ex.Message, "Can't render the video");
                return null;
            }

            var runner = new VidRenderRunner();
            var render = new VideoRender(session, runner);
            runner.ProgressChanged += (s, percent) => render.SetProgress(percent);
            session.ActiveRender = render;

            _ = RenderAsync(session, render, runner, renderArgsPath, outputPath);
            return session;
        }

        /// <summary>Builds the Recent-page entry the render will fill in: a video session whose
        /// VideoPath is the mp4 that does not exist yet, tagged with the recording it was edited
        /// from so the entry can be found (and replaced) again.</summary>
        private static SessionInfo CreateEditedSession(SessionInfo source, string outputPath, long durationMs)
        {
            var dir = SessionManager.Current.GetNextSessionDirectory();
            Directory.CreateDirectory(dir); // CreateSessionInDirectory expects the directory to exist

            var session = SessionManager.Current.CreateSessionInDirectory(dir);
            session.Name = EditedSessionName;
            session.CreatedUtc = DateTime.UtcNow;
            session.ContentKind = "video";
            session.VideoPath = outputPath;
            session.EditSourceVideoPath = source.VideoPath;
            session.DurationMs = durationMs;
            session.PreviewImgPath = CopyPreview(source.PreviewImgPath, dir);
            return session;
        }

        /// <summary>Writes the render job — the project itself, plus the output path and the
        /// encoder quality it cannot carry — into the session directory and returns its path.</summary>
        private static string WriteProjectArgs(SessionInfo session, Project project, string outputPath)
        {
            var argsPath = Path.Combine(Path.GetDirectoryName(session.FilePath), RenderArgsFileName);

            // a snapshot: settings edited while the render runs apply to the next one. The
            // VideoQuality enum members are the CRF values (Low=29, Medium=23, High=16).
            var crf = (int)(SettingsRoot.Current?.Recording?.Quality ?? VideoQuality.Medium);
            return ProjectFileWriter.Write(argsPath, project, outputPath, crf);
        }

        /// <summary>The path of the first media file the project <b>references</b> that is not on
        /// disk, or null when they are all there. The tool would fail on it too, but only after the
        /// entry has been created and the user has watched a render start. A source no item plays —
        /// an import whose items were all deleted — is never opened, so its file being gone is not
        /// a reason to refuse (the same definition the editor's missing-media prompt uses).</summary>
        private static string FindMissingSource(Project project)
        {
            if (project.Sources == null)
                return null;

            foreach (var media in project.Sources)
            {
                if (!EditorSession.IsSourceReferenced(project, media.Id))
                    continue;

                if (String.IsNullOrEmpty(media.Path))
                    return "(no path)";
                if (!File.Exists(media.Path))
                    return media.Path;
            }

            return null;
        }

        /// <summary>"<c>name</c>-edited.mp4" beside the source, uniquified with a counter when that
        /// is taken (an earlier render the user kept, or a file they made themselves).</summary>
        internal static string GetOutputPath(string sourceVideoPath)
        {
            var dir = Path.GetDirectoryName(sourceVideoPath);
            var stem = Path.GetFileNameWithoutExtension(sourceVideoPath) + "-edited";

            var candidate = Path.Combine(dir, stem + ".mp4");
            for (var i = 2; File.Exists(candidate); i++)
                candidate = Path.Combine(dir, stem + "-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".mp4");

            return candidate;
        }

        /// <summary>Copies the source recording's thumbnail into the new session directory so the
        /// entry has a preview from the moment it appears. Best-effort: a missing or unreadable
        /// preview just means no thumbnail.</summary>
        private static string CopyPreview(string previewPath, string dir)
        {
            if (String.IsNullOrEmpty(previewPath) || !File.Exists(previewPath))
                return null;

            try
            {
                var dest = Path.Combine(dir, "preview" + Path.GetExtension(previewPath));
                File.Copy(previewPath, dest, overwrite: true);
                return dest;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to copy edited session preview: " + ex);
                SentryConfig.CaptureHandled(ex, "render.copy-preview");
                return null;
            }
        }

        private static async Task RenderAsync(SessionInfo session, VideoRender render, VidRenderRunner runner,
            string renderArgsPath, string outputPath)
        {
            VidRenderResult result;
            try
            {
                result = await runner.RunAsync(renderArgsPath);
            }
            catch (Exception ex)
            {
                // the process could not be started at all (missing binary, denied) — the run never
                // began, so there is no protocol result to report.
                Debug.WriteLine("Video render could not run: " + ex);
                result = VidRenderResult.Error(ex.Message);
            }
            finally
            {
                runner.Dispose();
            }

            await Dispatcher.UIThread.InvokeAsync(() => FinishAsync(session, render, outputPath, result));
        }

        private static async Task FinishAsync(SessionInfo session, VideoRender render, string outputPath, VidRenderResult result)
        {
            // the entry stops being an in-progress row here whatever happened next.
            if (ReferenceEquals(session.ActiveRender, render))
                session.ActiveRender = null;

            switch (result.Outcome)
            {
                case VidRenderOutcome.Success:
                    // the tool reports the path it actually wrote; trust it over the one we asked for.
                    if (!String.IsNullOrEmpty(result.OutputPath) && IsLive(session))
                    {
                        try
                        {
                            session.VideoPath = result.OutputPath;
                        }
                        catch (ObjectDisposedException)
                        {
                            // the entry was deleted while the render was finishing.
                        }
                    }

                    Toast.Show(Toast.GetActiveOrMainWindow(), "Video saved");
                    break;

                case VidRenderOutcome.Canceled:
                    DeleteQuietly(session);
                    DeletePartialOutput(outputPath);
                    break;

                default:
                    DeleteQuietly(session);
                    DeletePartialOutput(outputPath);

                    var message = String.IsNullOrEmpty(result.Message) ? "The video render failed." : result.Message;
                    var report = String.IsNullOrEmpty(result.Diagnostics)
                        ? message
                        : message + Environment.NewLine + result.Diagnostics;
                    SentryConfig.CaptureHandled(new InvalidOperationException(report), "render.failed");

                    await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, message, "Video render failed");
                    break;
            }
        }

        private static bool IsLive(SessionInfo session) =>
            SessionManager.Current.Sessions.Any(s => ReferenceEquals(s, session));

        /// <summary>Drops a finished edited entry and the file it produced, so the replacement
        /// render can take the same output name back.</summary>
        private static void DeleteEntryAndOutput(SessionInfo session)
        {
            var output = session.VideoPath;
            DeleteQuietly(session);
            DeletePartialOutput(output);
        }

        private static void DeleteQuietly(SessionInfo session)
        {
            try
            {
                if (IsLive(session))
                    SessionManager.Current.DeleteSession(session);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to delete edited session: " + ex);
                SentryConfig.CaptureHandled(ex, "render.delete-session");
            }
        }

        /// <summary>vid-render removes its own half-written mp4 when it honors a <c>quit</c>; this
        /// only matters on the paths where it had to be killed or died mid-write, which leave the
        /// file behind.</summary>
        private static void DeletePartialOutput(string outputPath)
        {
            try
            {
                if (!String.IsNullOrEmpty(outputPath) && File.Exists(outputPath))
                    File.Delete(outputPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to delete partial render: " + ex);
                SentryConfig.CaptureHandled(ex, "render.delete-partial");
            }
        }
    }
}
