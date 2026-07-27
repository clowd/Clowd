using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Clowd.UI.Helpers;

namespace Clowd.UI
{
    public sealed class SessionGroupVm
    {
        public string Name { get; }
        public List<SessionInfo> Items { get; } = new();

        public SessionGroupVm(string name)
        {
            Name = name;
        }
    }

    public partial class RecentSessionsPage : UserControl
    {
        public ObservableCollection<SessionGroupVm> Groups { get; } = new();

        /// <summary>Call-to-action shown while there are no sessions.</summary>
        public string EmptyHint
        {
            get
            {
                var gesture = Clowd.Config.SettingsRoot.Current?.Hotkeys?.CaptureRegionShortcut?.ToString();
                return String.IsNullOrEmpty(gesture)
                    ? "Use the tray icon to take your first screenshot — it will show up here."
                    : $"Press {gesture} or use the tray icon to take your first screenshot — it will show up here.";
            }
        }

        private readonly DispatcherTimer _regroupTimer;

        public RecentSessionsPage()
        {
            InitializeComponent();
            DataContext = this;

            // regrouping is throttled to 250 ms (decision table #52 / §6) because
            // TrulyObservableCollection raises Reset for every item property change.
            _regroupTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _regroupTimer.Tick += (s, e) =>
            {
                _regroupTimer.Stop();
                RebuildGroups();
            };

            RebuildGroups();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            SessionManager.Current.Sessions.CollectionChanged += OnSessionsChanged;
            RebuildGroups();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            SessionManager.Current.Sessions.CollectionChanged -= OnSessionsChanged;
            _regroupTimer.Stop();
        }

        private void OnSessionsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            _regroupTimer.Stop();
            _regroupTimer.Start();
        }

        private void RebuildGroups()
        {
            var sessions = SessionManager.Current.Sessions
                                         .OrderByDescending(s => s.CreatedUtc)
                                         .ToArray();

            Groups.Clear();

            SessionGroupVm current = null;
            foreach (var session in sessions)
            {
                var key = GetTimeAgoGroupName(session.CreatedUtc);
                if (current == null || current.Name != key)
                {
                    current = new SessionGroupVm(key);
                    Groups.Add(current);
                }

                current.Items.Add(session);
            }
        }

        // group keys ported from the WPF TimeAgoConverter (PrettyTime approximated locally).
        // Bucketing happens in *local* time so "Today"/"Yesterday" match the user's clock.
        private static string GetTimeAgoGroupName(DateTime timeUtc)
        {
            if (timeUtc == default)
                return "Unknown";

            var time = ToLocalTime(timeUtc);

            if (time.Date == DateTime.Now.Date)
                return "Today";

            if (time.Date == DateTime.Now.Date.AddDays(-1))
                return "Yesterday";

            if (time.Date >= DateTime.Now.Date.AddDays(-7))
                return "This week";

            var days = Math.Abs((DateTime.Now - time).TotalDays);

            if (days < 32)
                return $"{Math.Max(2, (int)Math.Round(days / 7d))} weeks ago";

            if (days < 365)
            {
                var months = Math.Max(1, (int)Math.Round(days / 30.4));
                return months == 1 ? "A month ago" : $"{months} months ago";
            }

            var years = Math.Max(1, (int)Math.Round(days / 365.25));
            return years == 1 ? "A year ago" : $"{years} years ago";
        }

        /// <summary>Session timestamps are written with DateTime.UtcNow — normalize to local
        /// time for anything user-facing.</summary>
        internal static DateTime ToLocalTime(DateTime time)
        {
            return time.Kind == DateTimeKind.Local
                ? time
                : DateTime.SpecifyKind(time, DateTimeKind.Utc).ToLocalTime();
        }

        private static SessionInfo GetSessionFromEvent(object sender)
        {
            return (sender as Control)?.DataContext as SessionInfo;
        }

        private void OpenItemClicked(object sender, RoutedEventArgs e)
        {
            var session = GetSessionFromEvent(sender);
            if (session != null)
                SessionManager.Current.OpenSession(session);
        }

        private void CopyItemClicked(object sender, RoutedEventArgs e)
        {
            var session = GetSessionFromEvent(sender);
            if (session != null)
                SessionManager.Current.CopySession(session);
        }

        private async void DeleteItemClicked(object sender, RoutedEventArgs e)
        {
            var session = GetSessionFromEvent(sender);
            if (session != null)
                await DeleteWithConfirmation(session);
        }

        private async System.Threading.Tasks.Task DeleteWithConfirmation(SessionInfo session)
        {
            if (session.OpenEditor != null)
            {
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Information,
                    "The selected item is currently open and can not be deleted.");
                return;
            }

            // a recording's mp4 lives in the user's own output folder (issue #50), outside the
            // session directory this deletes — saying it "cannot be recovered" would be a lie.
            var videoKept = session.IsVideo
                            && !String.IsNullOrEmpty(session.VideoPath)
                            && !IsInsideSessionDirectory(session, session.VideoPath);

            var prompt = videoKept
                ? $"Remove this recording from Recents? The video file is kept at {session.VideoPath}."
                : "Delete this capture? It cannot be recovered afterwards.";

            if (await NiceDialog.ShowYesNoPromptAsync(this, NiceDialogIcon.Warning, prompt))
            {
                SessionManager.Current.DeleteSession(session);
            }
        }

        private static bool IsInsideSessionDirectory(SessionInfo session, string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(session.FilePath);
                return !String.IsNullOrEmpty(dir)
                       && Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true; // unreadable path: fall back to the original, more cautious wording
            }
        }

        private void ViewDoubleClick(object sender, TappedEventArgs e)
        {
            var session = GetSessionFromEvent(sender);
            if (session == null)
                return;

            if (session.IsVideo)
                PlayVideo(session);
            else if (!session.IsUploadOnly)
                SessionManager.Current.OpenSession(session);
        }

        private void PlayItemClicked(object sender, RoutedEventArgs e)
        {
            var session = GetSessionFromEvent(sender);
            if (session != null)
                PlayVideo(session);
        }

        private static void PlayVideo(SessionInfo session)
        {
            if (session == null || String.IsNullOrEmpty(session.VideoPath) || !File.Exists(session.VideoPath))
                return;

            try
            {
                // hand the file to the OS default player.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(session.VideoPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Play video failed: " + ex);
                SentryConfig.CaptureHandled(ex, "recents.play-video");
            }
        }

        private void ShowInFolderClicked(object sender, RoutedEventArgs e)
        {
            var session = GetSessionFromEvent(sender);
            if (session == null)
                return;

            var target = !String.IsNullOrEmpty(session.VideoPath) ? session.VideoPath : session.PreviewImgPath;
            Helpers.ShellHelper.RevealFileInFolder(target);
        }

        private async void SessionListKeyDown(object sender, KeyEventArgs e)
        {
            if ((sender as ListBox)?.SelectedItem is not SessionInfo session)
                return;

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (session.IsVideo)
                    PlayVideo(session);
                else if (!session.IsUploadOnly)
                    SessionManager.Current.OpenSession(session);
            }
            else if (e.Key == Key.Delete)
            {
                e.Handled = true;
                await DeleteWithConfirmation(session);
            }
        }

        private void CancelUploadClicked(object sender, RoutedEventArgs e)
        {
            ((sender as Control)?.DataContext as ActiveUpload)?.Cancel();
        }

        private async void CopyUrlClicked(object sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is not UploadRecord record || String.IsNullOrEmpty(record.Url))
                return;

            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                {
                    await clipboard.SetTextAsync(record.Url);
                    if (TopLevel.GetTopLevel(this) is Window window)
                        Toast.Show(window, "Upload URL Copied to Clipboard");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Copy upload link failed: " + ex);
                SentryConfig.CaptureHandled(ex, "recents.copy-link");
            }
        }

        private void OpenUrlClicked(object sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is not UploadRecord record || String.IsNullOrEmpty(record.Url))
                return;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(record.Url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Open upload link failed: " + ex);
                SentryConfig.CaptureHandled(ex, "recents.open-link");
            }
        }

        private async void DeleteUploadClicked(object sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is not UploadRecord record)
                return;

            if (!await NiceDialog.ShowYesNoPromptAsync(this, NiceDialogIcon.Warning,
                    "Delete this upload from the remote server? The link will stop working."))
                return;

            try
            {
                await UploadManager.DeleteUploadAsync(record);
            }
            catch (Exception ex)
            {
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Error, ex.Message, "Delete failed");
                SentryConfig.CaptureHandled(ex, "recents.delete");
                return;
            }

            // drop the record from its owning session (records are always session-owned now).
            var session = (sender as Control)?.GetLogicalAncestors()
                                              .OfType<Control>()
                                              .Select(c => c.DataContext)
                                              .OfType<SessionInfo>()
                                              .FirstOrDefault();

            if (session == null)
                return;

            session.Uploads = (session.Uploads ?? Array.Empty<UploadRecord>())
                              .Where(u => !ReferenceEquals(u, record))
                              .ToArray();

            if (record.Url == session.UploadUrl)
            {
                session.UploadUrl = null;
                session.UploadFileKey = null;
            }
        }
    }

    /// <summary>
    /// Loads (and caches) a thumbnail-sized Bitmap from a file path — replaces the WPF
    /// convCacheImage converter used by the recent sessions list. Full screenshots are decoded
    /// at thumbnail width (the list renders them at 110px logical / ~220px at 2x scale), which
    /// keeps decode time and memory per entry small; the cache is bounded as a backstop.
    /// </summary>
    public sealed class ImagePathToBitmapConverter : IValueConverter
    {
        private const int ThumbnailDecodeWidth = 220;
        private const int MaxCacheEntries = 256;

        private static readonly object _lock = new();
        private static readonly Dictionary<string, (DateTime Written, Bitmap Bitmap)> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string path || String.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                if (!File.Exists(path))
                    return null;

                var written = File.GetLastWriteTimeUtc(path);

                lock (_lock)
                {
                    if (_cache.TryGetValue(path, out var hit) && hit.Written == written)
                        return hit.Bitmap;

                    // decoded thumbnails are tiny — dropping the whole cache on overflow is
                    // cheaper than bookkeeping a true LRU.
                    if (_cache.Count >= MaxCacheEntries)
                        _cache.Clear();

                    using var stream = File.OpenRead(path);
                    var bmp = Bitmap.DecodeToWidth(stream, ThumbnailDecodeWidth);
                    _cache[path] = (written, bmp);
                    return bmp;
                }
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Maps a session's ContentKind to the placeholder icon geometry shown when an
    /// upload-only session has no preview image. Unknown/other non-empty kinds fall back to the
    /// generic file icon.</summary>
    public sealed class ContentKindToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var key = (value as string)?.ToLowerInvariant() switch
            {
                "video" => "IconVideo",
                "image" => "IconPhoto",
                "text" => "IconTextFile",
                _ => "IconFileGeneric",
            };

            if (Application.Current != null && Application.Current.TryGetResource(key, null, out var res) && res is Geometry geometry)
                return geometry;

            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Formats a (UTC) DateTime in local time using AppStyles.UiDateTimePattern.</summary>
    public sealed class UiDateTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // legacy synthesized records carry no upload date — hide it rather than show the epoch.
            if (value is DateTime time && time != default)
                return RecentSessionsPage.ToLocalTime(time).ToString(AppStyles.UiDateTimePattern, culture);

            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
