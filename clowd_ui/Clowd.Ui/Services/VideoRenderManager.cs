using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.UI.Helpers;
using Clowd.Video;

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

        public bool IsCancelled { get; private set; }

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
                CanExecute = _ => !IsCancelled,
            };
        }

        /// <summary>Asks the render to stop. The row stays on the page (showing "Cancelling…")
        /// until the process is actually gone, at which point the manager removes it — unlike an
        /// upload, there is a child process that must be given a chance to clean up its partial
        /// output first.</summary>
        public void Cancel()
        {
            if (IsCancelled)
                return;

            IsCancelled = true;
            PostStatus("Cancelling…");
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
                if (IsCancelled)
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
    /// Everything about the source recording the edit document deliberately does not know: how long
    /// it is, how big track 0 (the screen) is, and which stream — if any — carries the webcam and
    /// at what size. Comes from the <c>tracks</c> object the recorder reports
    /// (<see cref="ObsTracks"/>), or from probing the file when the recording predates it.
    /// </summary>
    public sealed record VideoRenderSource(
        long DurationMs,
        int ScreenWidth,
        int ScreenHeight,
        int? WebcamStreamIndex,
        int WebcamWidth,
        int WebcamHeight)
    {
        /// <summary>Whether an overlay can be rendered at all: there must be a webcam stream and a
        /// usable aspect ratio for it.</summary>
        public bool HasWebcam => WebcamStreamIndex.HasValue && WebcamWidth > 0 && WebcamHeight > 0;

        /// <summary>Builds the source description from a recorder <c>tracks</c> report.</summary>
        public static VideoRenderSource FromTracks(long durationMs, ObsTracks tracks)
        {
            if (tracks?.Screen == null)
                return new VideoRenderSource(durationMs, 0, 0, null, 0, 0);

            var webcam = tracks.Webcam;
            return new VideoRenderSource(
                durationMs,
                tracks.Screen.Width,
                tracks.Screen.Height,
                webcam?.Index,
                webcam?.Width ?? 0,
                webcam?.Height ?? 0);
        }
    }

    /// <summary>
    /// Renders an edited recording beside its source (<c>video.mp4</c> → <c>video-edited.mp4</c>)
    /// using the external vid-render tool. The render is surfaced as its own Recent-page entry
    /// named "Edited" carrying a <see cref="SessionInfo.ActiveRender"/> while it runs; when it
    /// finishes the entry drops the render and behaves like any other video entry, and when it is
    /// cancelled or fails the entry is removed again. Re-rendering the same recording replaces the
    /// entry it made last time rather than accumulating them.
    /// </summary>
    public static class VideoRenderManager
    {
        /// <summary>The name every rendered entry carries; also how <see cref="FindExisting"/>
        /// spots one.</summary>
        public const string EditedSessionName = "Edited";

        /// <summary>Name of the render-args file written into the session directory.</summary>
        public const string RenderArgsFileName = "render-args.json";

        /// <summary>Name of the webcam overlay mask written into the session directory.</summary>
        public const string MaskFileName = "mask.png";

        /// <summary>The edited entry already made (or being made) from <paramref name="source"/>,
        /// or null.</summary>
        public static SessionInfo FindExisting(SessionInfo source) => FindExisting(source?.VideoPath);

        /// <summary>Overload for callers that only have the recording path.</summary>
        public static SessionInfo FindExisting(string sourceVideoPath)
        {
            if (String.IsNullOrEmpty(sourceVideoPath))
                return null;

            return SessionManager.Current.Sessions.FirstOrDefault(s =>
                String.Equals(s.Name, EditedSessionName, StringComparison.Ordinal) &&
                !String.IsNullOrEmpty(s.EditSourceVideoPath) &&
                String.Equals(s.EditSourceVideoPath, sourceVideoPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Creates the edited entry for <paramref name="source"/> and returns it immediately — the
        /// render itself continues in the background and reports through the entry's
        /// <see cref="SessionInfo.ActiveRender"/>. Returns null when there is nothing to render
        /// (the user has been told why), and the existing entry unchanged when a render of this
        /// same recording is already in flight. Any *finished* entry for this recording is replaced,
        /// file and all. Must be called on the UI thread.
        /// </summary>
        public static async Task<SessionInfo> StartRenderAsync(SessionInfo source, VideoEditDocument document, VideoRenderSource sourceInfo)
        {
            if (source == null || document == null)
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

            var durationMs = sourceInfo?.DurationMs > 0 ? sourceInfo.DurationMs : source.DurationMs;
            var segments = document.GetKeepSegments(durationMs);
            if (segments.Count == 0)
            {
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Warning,
                    "This edit keeps nothing of the recording — trim or cut less and try again.",
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
                session = CreateEditedSession(source, outputPath, SumDuration(segments));
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
                renderArgsPath = WriteRenderArgs(session, document, sourceInfo, videoPath, outputPath, segments);
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

        /// <summary>Writes render-args.json (and, when the overlay is on, mask.png beside it) into
        /// the session directory and returns the args path. Runs on the UI thread — the mask needs
        /// a renderer.</summary>
        private static string WriteRenderArgs(SessionInfo session, VideoEditDocument document, VideoRenderSource sourceInfo,
            string inputPath, string outputPath, System.Collections.Generic.IReadOnlyList<CutRegion> segments)
        {
            var dir = Path.GetDirectoryName(session.FilePath);

            var args = new RenderArgs
            {
                Input = inputPath,
                Output = outputPath,
                Segments = RenderArgs.ToSegments(segments),
                // a snapshot: settings edited while the render runs apply to the next one. The
                // VideoQuality enum members are the CRF values (Low=29, Medium=23, High=16).
                Crf = (int)(SettingsRoot.Current?.Recording?.Quality ?? VideoQuality.Medium),
            };

            var overlay = document.Webcam;
            if (overlay.Enabled && sourceInfo != null && sourceInfo.HasWebcam)
            {
                var rect = ComputeWebcamRect(overlay, sourceInfo);
                var maskPath = Path.Combine(dir, MaskFileName);
                WebcamMaskRenderer.WriteMask(maskPath, rect.W, rect.H, overlay);

                args.Webcam = new RenderWebcam
                {
                    StreamIndex = sourceInfo.WebcamStreamIndex.Value,
                    Rect = rect,
                    MaskPng = maskPath,
                };
            }

            var argsPath = Path.Combine(dir, RenderArgsFileName);
            File.WriteAllText(argsPath, args.ToJson());
            return argsPath;
        }

        /// <summary>
        /// Turns the document's normalized overlay geometry into output pixels. The width is a
        /// fraction of the screen frame; the height follows the webcam track's own aspect ratio
        /// (the document does not know it), and the whole rect is nudged back inside the frame
        /// rather than clipped, so a mask rendered at rect.w x rect.h always lands whole.
        /// </summary>
        internal static RenderRect ComputeWebcamRect(WebcamOverlay overlay, VideoRenderSource source)
        {
            var frameW = Math.Max(1, source.ScreenWidth);
            var frameH = Math.Max(1, source.ScreenHeight);

            var w = (int)Math.Round(overlay.Width * frameW);
            w = Math.Clamp(w, 2, frameW);

            var aspect = (double)source.WebcamHeight / source.WebcamWidth;
            var h = (int)Math.Round(w * aspect);
            h = Math.Clamp(h, 2, frameH);

            var x = (int)Math.Round(overlay.CenterX * frameW - w / 2.0);
            var y = (int)Math.Round(overlay.CenterY * frameH - h / 2.0);
            x = Math.Clamp(x, 0, frameW - w);
            y = Math.Clamp(y, 0, frameH - h);

            return new RenderRect { X = x, Y = y, W = w, H = h };
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

        private static long SumDuration(System.Collections.Generic.IReadOnlyList<CutRegion> segments)
        {
            long total = 0;
            foreach (var s in segments)
                total += s.DurationMs;

            return total;
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

                case VidRenderOutcome.Cancelled:
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
