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

        public bool IsCanceled { get; private set; }

        // Set by UploadsManager.SetEarlyUrl for accelerated uploads: the shareable link surfaced
        // (copied to the clipboard, written to session.UploadUrl) before any bytes transferred, and
        // the session's UploadUrl from just before it was overwritten. If the upload then fails or is
        // canceled the server aborts the session and the link 410s, so these let the UI roll the dead
        // link back rather than leave it on the Recent page and blocking junk-row cleanup.
        internal string EarlyUrl { get; set; }
        internal string PreEarlyUploadUrl { get; set; }
        internal bool WasAccelerated { get; set; }

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
            IsCanceled = true;
            _source.Cancel();
            PageManager.Current.Uploads.DiscardUpload(this);
        }

        internal void SetStatus(string status)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsCanceled)
                    return;
                Status = status;
            });
        }

        internal void SetProgress(long completed, long total, bool isBytes)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsCanceled)
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
                if (upload.IsCanceled)
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
                    Accelerated = upload.WasAccelerated,
                };

                var session = upload.Session;

                // an accelerated upload already surfaced this exact URL (and copied/toasted it) the
                // moment the session was created — don't copy or toast it a second time here.
                var alreadySurfaced = String.Equals(session.UploadUrl, result.PublicUrl, StringComparison.Ordinal);

                session.Uploads = (session.Uploads ?? Array.Empty<UploadRecord>()).Append(record).ToArray();

                // keep the legacy single-upload fields in sync for older readers of session.json.
                session.UploadUrl = result.PublicUrl;
                session.UploadFileKey = result.UploadKey;

                // always copy the resulting URL to the clipboard and notify the user.
                if (!alreadySurfaced)
                    CopyUrlAndToast(session, result.PublicUrl);
            });
        }

        /// <summary>Surfaces a shareable URL for an in-flight upload before it finishes — used by
        /// accelerated uploads, whose download link is live the instant the server session is
        /// created. Sets the Recent page's link and copies/toasts exactly as completion would, so
        /// the completion handler can then skip the duplicate.</summary>
        public void SetEarlyUrl(ActiveUpload upload, string url)
        {
            if (String.IsNullOrEmpty(url))
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (upload.IsCanceled)
                    return;

                upload.WasAccelerated = true;

                var session = upload.Session;
                if (String.Equals(session.UploadUrl, url, StringComparison.Ordinal))
                    return; // already surfaced (idempotent)

                // remember what to restore if this (still-in-flight) upload later fails/cancels and the
                // server invalidates the link.
                upload.PreEarlyUploadUrl = session.UploadUrl;
                upload.EarlyUrl = url;

                session.UploadUrl = url;
                CopyUrlAndToast(session, url);
            });
        }

        public async Task FailUpload(ActiveUpload upload, Exception ex)
        {
            if (upload.IsCanceled)
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

                // the server aborted the session on failure, so any early (pre-transfer) link is now
                // dead — roll it off the session and warn that the copied link no longer works.
                var invalidatedLink = RollbackEarlyUrl(upload);
                var notice = invalidatedLink
                    ? message + Environment.NewLine + Environment.NewLine
                              + "The shareable link copied to your clipboard is no longer valid."
                    : message;

                // spec: show the error first, then drop the row from the page.
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, notice, "Upload failed");
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
            // a cancel also aborts the server session, invalidating any early link — roll it back so
            // it doesn't linger on the Recent page and so CleanupEmptyUploadOnly can drop the junk row.
            RollbackEarlyUrl(upload);
            Detach(upload);
            CleanupEmptyUploadOnly(session);
        }

        /// <summary>Undoes an early (pre-transfer) URL the server has now invalidated by aborting the
        /// session (the link 410s). Restores <see cref="SessionInfo.UploadUrl"/> to its pre-upload
        /// value, but only when the early URL is still the one on the session and no completed upload
        /// record captured it — so a genuinely-completed upload is never disturbed. Returns true when a
        /// dead link was rolled back.</summary>
        private static bool RollbackEarlyUrl(ActiveUpload upload)
        {
            var session = upload?.Session;
            if (session == null || String.IsNullOrEmpty(upload.EarlyUrl))
                return false;

            // a completed upload replaces UploadUrl with its record's URL and appends a record; if
            // either happened, this is not a dead early link and must be left alone.
            if (!String.Equals(session.UploadUrl, upload.EarlyUrl, StringComparison.Ordinal))
                return false;
            if (session.Uploads != null && session.Uploads.Any(u => String.Equals(u?.Url, upload.EarlyUrl, StringComparison.Ordinal)))
                return false;

            session.UploadUrl = upload.PreEarlyUploadUrl;
            return true;
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
                catch (Exception ex)
                {
                    Debug.WriteLine("failed to copy upload url to clipboard: " + ex);
                    SentryConfig.CaptureHandled(ex, "uploads.copy-url");
                }
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
        /// canceled/failed clipboard or file upload doesn't linger as a junk row.</summary>
        private static void CleanupEmptyUploadOnly(SessionInfo session)
        {
            if (session == null || !session.IsUploadOnly)
                return;

            // never delete a session that owns real content: a video recording is IsUploadOnly
            // too, and a failed or canceled upload must not silently drop the recording out of
            // Recents. This cleanup exists only for the ephemeral
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
                SentryConfig.CaptureHandled(ex, "uploads.delete-empty");
            }
        }

        private static void OpenRecentTab()
        {
            PageManager.Current.GetSettingsPage().Open(SettingsPageTab.RecentSessions);
        }
    }
}
