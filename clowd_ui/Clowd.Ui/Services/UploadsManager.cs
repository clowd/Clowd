using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    /// <summary>A single in-flight upload. Progress and status are surfaced on the Recent page
    /// (standalone uploads bind through <see cref="UploadsManager.Standalone"/>, session uploads
    /// through <see cref="SessionInfo.ActiveUpload"/>).</summary>
    public sealed class ActiveUpload : SimpleNotifyObject
    {
        public string Name { get; }

        // may be null for standalone uploads (clipboard / loose files) that have no session.
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

        public ActiveUpload(string name, SessionInfo session = null)
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
                    ? $"{PrettyBytes(completed)} / {PrettyBytes(total)}"
                    : $"{completed} / {total}";
            });
        }

        private static string PrettyBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return $"{size:0.#} {units[unit]}";
        }
    }

    /// <summary>Owns the in-flight uploads and finished standalone upload records surfaced on the
    /// Recent page. Replaces the tray-adjacent TaskWindow overlay: the page's Recent tab is opened
    /// whenever an upload starts or reaches a terminal state.</summary>
    public sealed class UploadsManager
    {
        /// <summary>Active uploads with no owning session.</summary>
        public ObservableCollection<ActiveUpload> Standalone { get; } = new();

        /// <summary>Finished standalone uploads, newest first. In-memory only — session uploads
        /// persist on the session itself.</summary>
        public ObservableCollection<UploadRecord> Completed { get; } = new();

        public ActiveUpload StartUpload(string name, SessionInfo session = null)
        {
            // constructed synchronously so the caller has CancelToken before the UI post runs.
            var item = new ActiveUpload(name, session);

            Dispatcher.UIThread.Post(() =>
            {
                if (session != null)
                    session.ActiveUpload = item;
                else
                    Standalone.Add(item);

                OpenRecentTab();
            });

            return item;
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

                if (upload.Session != null)
                {
                    var session = upload.Session;
                    session.Uploads = (session.Uploads ?? Array.Empty<UploadRecord>()).Append(record).ToArray();

                    // keep the legacy single-upload fields in sync for older readers of session.json.
                    session.UploadUrl = result.PublicUrl;
                    session.UploadFileKey = result.UploadKey;
                }
                else
                {
                    Completed.Insert(0, record);
                }

                OpenRecentTab();
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

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                OpenRecentTab();
                // spec: show the error first, then drop the row from the page.
                await NiceDialog.ShowNoticeAsync(null, NiceDialogIcon.Error, message, "Upload failed");
                Detach(upload);
            });
        }

        /// <summary>Removes an upload's row without any user-facing feedback (used for
        /// cancellation). Idempotent.</summary>
        public void DiscardUpload(ActiveUpload upload)
        {
            if (Dispatcher.UIThread.CheckAccess())
                Detach(upload);
            else
                Dispatcher.UIThread.Post(() => Detach(upload));
        }

        private void Detach(ActiveUpload upload)
        {
            if (upload == null)
                return;

            if (upload.Session != null && ReferenceEquals(upload.Session.ActiveUpload, upload))
                upload.Session.ActiveUpload = null;
            else
                Standalone.Remove(upload);
        }

        private static void OpenRecentTab()
        {
            PageManager.Current.GetSettingsPage().Open(SettingsPageTab.RecentSessions);
        }
    }
}
