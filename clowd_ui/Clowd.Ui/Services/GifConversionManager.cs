using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clowd.Config;
using Clowd.UI.Helpers;

namespace Clowd.UI.Services
{
    /// <summary>An in-flight video → GIF conversion, surfaced on the Recent page through
    /// <see cref="SessionInfo.ActiveGifConversion"/> exactly as <see cref="ActiveUpload"/> is. The
    /// conversion is owned by the GIF session it will fill in, not by the recording it reads.</summary>
    public sealed class GifConversion : SimpleNotifyObject
    {
        // the session this conversion is producing (the "GIF" row), not the source recording.
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

        private readonly Vid2GifRunner _runner;
        private string _status = "Converting…";
        private double _progress;

        internal GifConversion(SessionInfo session, Vid2GifRunner runner)
        {
            Session = session;
            _runner = runner;
        }

        /// <summary>Asks the conversion to stop. The row stays on the page (showing "Cancelling…")
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
                // means the conversion may finish normally instead.
                Debug.WriteLine("Failed to cancel gif conversion: " + ex);
                SentryConfig.CaptureHandled(ex, "gif.cancel");
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
                Status = $"Converting… {percent}%";
            });
        }

        private void PostStatus(string status)
        {
            Dispatcher.UIThread.Post(() => Status = status);
        }
    }

    /// <summary>
    /// Turns a finished recording into a GIF beside it (<c>video.mp4</c> → <c>video.gif</c>) using
    /// the external vid2gif tool. The conversion is surfaced as its own Recent-page entry named
    /// "GIF" carrying an <see cref="SessionInfo.ActiveGifConversion"/> while it runs; when it
    /// finishes the entry drops the conversion and behaves like any other video entry, and when it
    /// is cancelled or fails the entry is removed again.
    /// </summary>
    public static class GifConversionManager
    {
        /// <summary>The name every GIF entry carries; also how <see cref="FindExisting"/> spots one.</summary>
        public const string GifSessionName = "GIF";

        /// <summary>The GIF entry already made (or being made) from <paramref name="source"/>, or
        /// null. Callers use this to focus the existing entry instead of starting a second
        /// conversion of the same recording.</summary>
        public static SessionInfo FindExisting(SessionInfo source)
        {
            var videoPath = source?.VideoPath;
            if (String.IsNullOrEmpty(videoPath))
                return null;

            return SessionManager.Current.Sessions.FirstOrDefault(s =>
                String.Equals(s.Name, GifSessionName, StringComparison.Ordinal) &&
                !String.IsNullOrEmpty(s.SourceVideoPath) &&
                String.Equals(s.SourceVideoPath, videoPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Creates the GIF entry for <paramref name="source"/> and returns it immediately — the
        /// conversion itself continues in the background and reports through the entry's
        /// <see cref="SessionInfo.ActiveGifConversion"/>. Returns null when there is nothing to
        /// convert (the user has been told why), and the existing entry when this recording has
        /// already been converted. Must be called on the UI thread.
        /// </summary>
        public static async Task<SessionInfo> StartConversionAsync(SessionInfo source)
        {
            if (source == null)
                return null;

            // a GIF entry's own VideoPath is already a gif; converting it again would overwrite it.
            if (!String.IsNullOrEmpty(source.SourceVideoPath))
                return null;

            var videoPath = source.VideoPath;
            if (String.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            {
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Warning,
                    "The recording this GIF would be made from could not be found. It may have been moved or deleted.",
                    "Can't create a GIF");
                return null;
            }

            var existing = FindExisting(source);
            if (existing != null)
                return existing;

            var gifPath = Path.ChangeExtension(videoPath, ".gif");

            SessionInfo session;
            try
            {
                session = CreateGifSession(source, gifPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to create gif session: " + ex);
                SentryConfig.CaptureHandled(ex, "gif.create-session");
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, ex.Message, "Can't create a GIF");
                return null;
            }

            var runner = new Vid2GifRunner();
            var conversion = new GifConversion(session, runner);
            runner.ProgressChanged += (s, percent) => conversion.SetProgress(percent);
            session.ActiveGifConversion = conversion;

            // a snapshot: settings edited while the conversion runs apply to the next one.
            var recording = SettingsRoot.Current?.Recording;
            var quality = (recording?.GifQuality ?? GifQuality.Good).ToString().ToLowerInvariant();
            var maxWidth = recording?.GifMaxWidth ?? 0;
            var maxHeight = recording?.GifMaxHeight ?? 0;

            _ = ConvertAsync(session, conversion, runner, videoPath, gifPath, quality, maxWidth, maxHeight);
            return session;
        }

        /// <summary>Builds the Recent-page entry the conversion will fill in: a video session whose
        /// VideoPath is the gif that does not exist yet, tagged with the recording it came from so
        /// the entry can be found again and so it never offers a GIF button of its own.</summary>
        private static SessionInfo CreateGifSession(SessionInfo source, string gifPath)
        {
            var dir = SessionManager.Current.GetNextSessionDirectory();
            Directory.CreateDirectory(dir); // CreateSessionInDirectory expects the directory to exist

            var session = SessionManager.Current.CreateSessionInDirectory(dir);
            session.Name = GifSessionName;
            session.CreatedUtc = DateTime.UtcNow;
            session.ContentKind = "video";
            session.VideoPath = gifPath;
            session.SourceVideoPath = source.VideoPath;
            session.DurationMs = source.DurationMs;
            session.PreviewImgPath = CopyPreview(source.PreviewImgPath, dir);
            return session;
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
                Debug.WriteLine("Failed to copy gif session preview: " + ex);
                SentryConfig.CaptureHandled(ex, "gif.copy-preview");
                return null;
            }
        }

        private static async Task ConvertAsync(SessionInfo session, GifConversion conversion, Vid2GifRunner runner,
            string inputPath, string gifPath, string quality, int maxWidth, int maxHeight)
        {
            Vid2GifResult result;
            try
            {
                result = await runner.RunAsync(inputPath, gifPath, quality, maxWidth, maxHeight);
            }
            catch (Exception ex)
            {
                // the process could not be started at all (missing binary, denied) — the run never
                // began, so there is no protocol result to report.
                Debug.WriteLine("Gif conversion could not run: " + ex);
                result = Vid2GifResult.Error(ex.Message);
            }
            finally
            {
                runner.Dispose();
            }

            await Dispatcher.UIThread.InvokeAsync(() => FinishAsync(session, conversion, gifPath, result));
        }

        private static async Task FinishAsync(SessionInfo session, GifConversion conversion, string gifPath, Vid2GifResult result)
        {
            // the entry stops being an in-progress row here whatever happened next.
            if (ReferenceEquals(session.ActiveGifConversion, conversion))
                session.ActiveGifConversion = null;

            switch (result.Outcome)
            {
                case Vid2GifOutcome.Success:
                    // the tool reports the path it actually wrote; trust it over the one we asked for.
                    if (!String.IsNullOrEmpty(result.OutputPath) && IsLive(session))
                    {
                        try
                        {
                            session.VideoPath = result.OutputPath;
                        }
                        catch (ObjectDisposedException)
                        {
                            // the entry was deleted while the conversion was finishing.
                        }
                    }

                    Toast.Show(Toast.GetActiveOrMainWindow(), "GIF saved");
                    break;

                case Vid2GifOutcome.Cancelled:
                    DeleteQuietly(session);
                    DeletePartialOutput(gifPath);
                    break;

                default:
                    DeleteQuietly(session);

                    var message = String.IsNullOrEmpty(result.Message) ? "The GIF conversion failed." : result.Message;
                    var report = String.IsNullOrEmpty(result.Diagnostics)
                        ? message
                        : message + Environment.NewLine + result.Diagnostics;
                    SentryConfig.CaptureHandled(new InvalidOperationException(report), "gif.convert-failed");

                    await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, message, "GIF conversion failed");
                    break;
            }
        }

        private static bool IsLive(SessionInfo session) =>
            SessionManager.Current.Sessions.Any(s => ReferenceEquals(s, session));

        private static void DeleteQuietly(SessionInfo session)
        {
            try
            {
                if (IsLive(session))
                    SessionManager.Current.DeleteSession(session);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to delete gif session: " + ex);
                SentryConfig.CaptureHandled(ex, "gif.delete-session");
            }
        }

        /// <summary>vid2gif removes its own half-written gif when it honors a <c>quit</c>; this only
        /// matters on the path where it had to be killed instead, which leaves the file behind.</summary>
        private static void DeletePartialOutput(string gifPath)
        {
            try
            {
                if (!String.IsNullOrEmpty(gifPath) && File.Exists(gifPath))
                    File.Delete(gifPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to delete partial gif: " + ex);
                SentryConfig.CaptureHandled(ex, "gif.delete-partial");
            }
        }
    }
}
