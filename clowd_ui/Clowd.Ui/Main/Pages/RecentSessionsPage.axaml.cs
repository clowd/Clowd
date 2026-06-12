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
        private static string GetTimeAgoGroupName(DateTime time)
        {
            if (time == default)
                return "Unknown";

            if (time.Date == DateTime.UtcNow.Date)
                return "Today";

            if (time.Date == DateTime.UtcNow.Date.AddDays(-1))
                return "Yesterday";

            if (time.Date >= DateTime.UtcNow.Date.AddDays(-7))
                return "This week";

            var days = Math.Abs((DateTime.UtcNow - time).TotalDays);

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
            if (session == null)
                return;

            if (session.OpenEditor != null)
            {
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Information,
                    "The selected item is currently open and can not be deleted.");
            }
            else
            {
                SessionManager.Current.DeleteSession(session);
            }
        }

        private void ViewDoubleClick(object sender, TappedEventArgs e)
        {
            var session = GetSessionFromEvent(sender);
            if (session != null)
                SessionManager.Current.OpenSession(session);
        }
    }

    /// <summary>
    /// Loads (and caches) a Bitmap from a file path — replaces the WPF convCacheImage
    /// converter used by the recent sessions list.
    /// </summary>
    public sealed class ImagePathToBitmapConverter : IValueConverter
    {
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

                    var bmp = new Bitmap(path);
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

    /// <summary>Formats a DateTime using AppStyles.UiDateTimePattern (WPF used StringFormat).</summary>
    public sealed class UiDateTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime time)
                return time.ToString(AppStyles.UiDateTimePattern, culture);

            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
