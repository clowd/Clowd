using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    /// <summary>A single in-flight upload. Progress and status are surfaced on the Recent page (and
    /// on an editor window) through <see cref="SessionInfo.ActiveUpload"/>. Every upload is owned by
    /// a session.</summary>
    public sealed class ActiveUpload : SimpleNotifyObject
    {
        public string Name { get; }

        // the owning session; every upload has one.
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

        public CancellationToken CancelToken => _source.Token;

        public bool IsCancelled { get; private set; }

        private readonly CancellationTokenSource _source = new();
        private string _status;
        private double _progress;

        public ActiveUpload(string name, SessionInfo session)
        {
            Name = name;
            Session = session;
        }

        /// <summary>Aborts the upload and removes its row from the page immediately.</summary>
        public void Cancel()
        {
            IsCancelled = true;
            _source.Cancel();
            PageManager.Current.Uploads.DiscardUpload(this);
        }

        internal void SetStatus(string status)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsCancelled)
                    return;
                Status = status;
            });
        }

        internal void SetProgress(long completed, long total, bool isBytes)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsCancelled)
                    return;

                Progress = total > 0 ? Math.Min(100, completed / (double)total * 100d) : 0;
                Status = isBytes
                    ? FixedWidthMegabytes(completed, total)
                    : $"{completed} / {total}";
            });
        }

        // always whole MB, zero-padded to 3 digits, so the recents-page status text keeps a stable
        // width as the upload progresses ("000 / 001 MB"; multi-GB uploads grow to 4+ digits).
        private static string FixedWidthMegabytes(long completed, long total)
        {
            const double mb = 1024d * 1024d;
            var totalMb = Math.Max(1, (long)Math.Ceiling(total / mb));
            var doneMb = Math.Min(totalMb, (long)Math.Round(completed / mb));
            return $"{doneMb:000} / {totalMb:000} MB";
        }
    }

    /// <summary>Owns the in-flight uploads surfaced on the Recent page and on editor windows. Every
    /// upload is attached to a session (an upload-only session for clipboard / file / tray uploads);
    /// there is no standalone upload list any more. Replaces the tray-adjacent TaskWindow overlay:
    /// the page's Recent tab is opened when an upload starts or reaches a terminal state, unless the
    /// session is already open in an editor (which shows its own progress).</summary>
    public sealed class UploadsManager
    {
        /// <summary>Begins an upload owned by <paramref name="session"/>. The single-active-upload
        /// check and the <see cref="SessionInfo.ActiveUpload"/> assignment are done atomically on the
        /// UI thread (marshalled synchronously when a caller resumes on a background thread). Returns
        /// null when the session already has an in-flight upload — callers must handle null.</summary>
        public ActiveUpload StartUpload(string name, SessionInfo session)
        {
            return Dispatcher.UIThread.Invoke(() =>
            {
                if (session.ActiveUpload != null)
                    return null;

                var item = new ActiveUpload(name, session);
                session.ActiveUpload = item;

                // an open editor shows its own progress — don't steal focus to the recents tab.
                if (EditorWindow.FindWindowForSession(session) == null)
                    OpenRecentTab();

                return item;
            });
        }

        public void CompleteUpload(ActiveUpload upload, UploadResult result)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (upload.IsCancelled)
                {
                    Detach(upload);
                    return;
                }

                Detach(upload);

                var record = new UploadRecord
                {
                    Provider = result.Provider?.GetType().Name,
                    Url = result.PublicUrl,
                    FileName = result.FileName,
                    UploadKey = result.UploadKey,
                    DeleteKey = result.DeleteKey,
                    UploadedUtc = DateTime.UtcNow,
                };

                var session = upload.Session;
                session.Uploads = (session.Uploads ?? Array.Empty<UploadRecord>()).Append(record).ToArray();

                // keep the legacy single-upload fields in sync for older readers of session.json.
                session.UploadUrl = result.PublicUrl;
                session.UploadFileKey = result.UploadKey;

                // always copy the resulting URL to the clipboard and notify the user.
                CopyUrlAndToast(session, result.PublicUrl);
            });
        }

        public async Task FailUpload(ActiveUpload upload, Exception ex)
        {
            if (upload.IsCancelled)
            {
                DiscardUpload(upload);
                return;
            }

            var message = (ex is AggregateException agg ? agg.GetBaseException() : ex)?.Message ?? "Unknown error";
            var session = upload.Session;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                // an open editor shows its own progress — don't steal focus to the recents tab.
                if (EditorWindow.FindWindowForSession(session) == null)
                    OpenRecentTab();
                // spec: show the error first, then drop the row from the page.
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, message, "Upload failed");
                Detach(upload);
                CleanupEmptyUploadOnly(session);
            });
        }

        /// <summary>Removes an upload's row without any user-facing feedback (used for
        /// cancellation). Idempotent.</summary>
        public void DiscardUpload(ActiveUpload upload)
        {
            if (Dispatcher.UIThread.CheckAccess())
                DetachAndCleanup(upload);
            else
                Dispatcher.UIThread.Post(() => DetachAndCleanup(upload));
        }

        private void DetachAndCleanup(ActiveUpload upload)
        {
            if (upload == null)
                return;

            var session = upload.Session;
            Detach(upload);
            CleanupEmptyUploadOnly(session);
        }

        private static void Detach(ActiveUpload upload)
        {
            if (upload == null)
                return;

            var session = upload.Session;
            if (session != null && ReferenceEquals(session.ActiveUpload, upload))
                session.ActiveUpload = null;
        }

        /// <summary>Copies the finished upload's URL to the clipboard and shows a confirmation toast:
        /// in the editor window when the session is open there, otherwise on the recents/settings
        /// window (which is opened first).</summary>
        private static void CopyUrlAndToast(SessionInfo session, string url)
        {
            if (!String.IsNullOrEmpty(url))
            {
                try { _ = ClipboardImpl.SetClipboardText(Toast.GetPrimaryClipboard(), url); }
                catch (Exception ex) { Debug.WriteLine("failed to copy upload url to clipboard: " + ex); }
            }

            const string message = "Upload URL Copied to Clipboard";

            var editor = EditorWindow.FindWindowForSession(session);
            if (editor != null)
            {
                Toast.Show(editor, message);
            }
            else
            {
                OpenRecentTab();
                Toast.Show(PageManager.Current.GetSettingsPage() as Window, message);
            }
        }

        /// <summary>Deletes an upload-only session that never produced a completed upload, so a
        /// cancelled/failed clipboard or file upload doesn't linger as a junk row.</summary>
        private static void CleanupEmptyUploadOnly(SessionInfo session)
        {
            if (session == null || !session.IsUploadOnly)
                return;

            // never delete a session that owns real content: a video recording is IsUploadOnly
            // too, and its session dir is the only copy of video.mp4 — a failed or cancelled
            // upload must not destroy the recording. This cleanup exists only for the ephemeral
            // sessions UploadManager creates around a payload *copy* (clipboard/file/text).
            if (!String.IsNullOrEmpty(session.VideoPath) && File.Exists(session.VideoPath))
                return;

            var hasUpload = (session.Uploads != null && session.Uploads.Length > 0) || !String.IsNullOrEmpty(session.UploadUrl);
            if (hasUpload)
                return;

            try
            {
                // upload-only sessions are never open in an editor, so DeleteSession won't refuse.
                SessionManager.Current.DeleteSession(session);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("failed to delete empty upload session: " + ex);
            }
        }

        private static void OpenRecentTab()
        {
            PageManager.Current.GetSettingsPage().Open(SettingsPageTab.RecentSessions);
        }
    }
}
