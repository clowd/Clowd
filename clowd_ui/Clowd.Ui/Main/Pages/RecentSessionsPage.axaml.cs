using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
// Avalonia 12 moved SetTextAsync and friends off IClipboard into extension methods here.
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clowd.UI.Controls;
using Clowd.UI.Helpers;
using Clowd.UI.Services;

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

    /// <summary>What the Recent list can be narrowed to (issue #62). Not a partition of the list: a
    /// screenshot that has been uploaded is both an <see cref="Image"/> and an <see cref="Upload"/>,
    /// and a text or file upload is only ever the latter.</summary>
    public enum RecentFilter
    {
        All,
        Image,
        Recording,
        Upload,
    }

    public partial class RecentSessionsPage : UserControl, IPageHeaderContent
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

        // the filter strip shown beside the page title, and what it currently says. The page owns
        // the control but does not host it — the window's header does (see IPageHeaderContent).
        private readonly CollapsingSegmentedBar _filterBar;
        private RecentFilter _filter = RecentFilter.All;

        // the row to re-select after a rebuild; see RebuildGroups.
        private SessionInfo _focusedSession;

        // guards the fan-out in SessionSelectionChanged, which clears every other group's ListBox
        // and so re-enters this handler once per group.
        private bool _syncingSelection;

        // the newest session already auto-selected, so that only happens once per created session
        // (static: the page is rebuilt whenever the Recent tab is re-created).
        private static SessionInfo _autoSelected;

        // the row waiting for its attention pulse. Only ever set for a selection the app made on the
        // user's behalf — a row the user clicked is not pulsed at them.
        private SessionInfo _pulseSession;

        public RecentSessionsPage()
        {
            InitializeComponent();
            DataContext = this;

            _filterBar = BuildFilterBar();

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

        /// <summary>The filter strip, shown by the window to the right of the "Recent" title.</summary>
        public Control HeaderContent => _filterBar;

        private CollapsingSegmentedBar BuildFilterBar()
        {
            var bar = new CollapsingSegmentedBar
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    // the labels are what the strip has to fit side by side, and what the collapsed
                    // dropdown carries as its own label.
                    Segment("All", RecentFilter.All, "Show everything"),
                    Segment("Images", RecentFilter.Image, "Screenshots and images"),
                    Segment("Recordings", RecentFilter.Recording, "Recordings and GIFs"),
                    Segment("Uploads", RecentFilter.Upload, "Anything uploaded, or waiting to be"),
                },
            };

            // hooked after the segments are in, so the strip settling on its first segment is not
            // reported as the user changing the filter.
            bar.SelectionChanged += FilterChanged;
            return bar;

            static ToggleButton Segment(string label, RecentFilter filter, string tip)
            {
                var segment = new ToggleButton { Content = label, Tag = filter };
                ToolTip.SetTip(segment, tip);
                return segment;
            }
        }

        private void FilterChanged(object sender, EventArgs e)
        {
            var selected = _filterBar.SelectedValue is RecentFilter filter ? filter : RecentFilter.All;
            if (selected == _filter)
                return;

            _filter = selected;

            // the user is waiting on this one: regroup now rather than on the throttle.
            _regroupTimer.Stop();
            RebuildGroups();
        }

        /// <summary>Moves the filter, keeping the strip and the list in step. A no-op when it is
        /// already there.</summary>
        private void SetFilter(RecentFilter filter)
        {
            if (_filter == filter)
                return;

            // _filter first, so the strip's own change notification finds nothing left to do.
            _filter = filter;
            _filterBar.SelectedValue = filter;

            _regroupTimer.Stop();
            RebuildGroups();
        }

        private bool MatchesFilter(SessionInfo session)
        {
            return _filter switch
            {
                // a capture or editor session carries no ContentKind at all; an entry the upload
                // path created names what it holds, and only "image" is one.
                RecentFilter.Image => !session.IsVideo
                                      && (!session.IsUploadOnly
                                          || String.Equals(session.ContentKind, "image", StringComparison.OrdinalIgnoreCase)),
                RecentFilter.Recording => IsRecording(session),
                // anything with a link, plus everything the upload path started even if it has no
                // link (yet, or ever — a failed or cancelled upload still belongs here).
                RecentFilter.Upload => session.ActiveUpload != null
                                       || session.AllUploads.Length > 0
                                       || IsUploadEntry(session),
                _ => true,
            };
        }

        /// <summary>A recording Clowd made — or the GIF converted from one. Both are "video" entries
        /// carrying the file they wrote; a video *file* someone uploaded is one too, but has no
        /// VideoPath of its own, which is what tells the two apart.</summary>
        private static bool IsRecording(SessionInfo session)
        {
            return session.IsVideo && !String.IsNullOrEmpty(session.VideoPath);
        }

        /// <summary>An entry started by uploading something rather than by capturing it: the upload
        /// path is the only one that sets ContentKind without also writing a VideoPath.</summary>
        private static bool IsUploadEntry(SessionInfo session)
        {
            return session.IsUploadOnly && String.IsNullOrEmpty(session.VideoPath);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            SessionManager.Current.Sessions.CollectionChanged += OnSessionsChanged;
            SessionManager.Current.PropertyChanged += OnSessionManagerPropertyChanged;
            RebuildGroups();

            // a capture, recording or upload creates its session and then opens this page — so by the
            // time we get here the entry the user just made already exists, and is what they came to
            // see. This covers the page not existing yet when it was created; if it did already exist,
            // OnSessionManagerPropertyChanged got there first.
            SelectNewSession();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            SessionManager.Current.Sessions.CollectionChanged -= OnSessionsChanged;
            SessionManager.Current.PropertyChanged -= OnSessionManagerPropertyChanged;
            _regroupTimer.Stop();
        }

        private void OnSessionsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            _regroupTimer.Stop();
            _regroupTimer.Start();
        }

        /// <summary>A session created while the page is already open — a recording finishing, a tray
        /// upload — selects itself here.</summary>
        private void OnSessionManagerPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SessionManager.LastCreated))
                return;

            // the clipboard / file upload paths create their session off the UI thread.
            if (Dispatcher.UIThread.CheckAccess())
                SelectNewSession();
            else
                Dispatcher.UIThread.Post(SelectNewSession);
        }

        /// <summary>Selects the session most recently created by this app instance, once. Does nothing
        /// when there is none, when it has already been selected, or when it has since been
        /// deleted.</summary>
        private void SelectNewSession()
        {
            var created = SessionManager.Current.LastCreated;
            if (created == null || ReferenceEquals(created, _autoSelected))
                return;

            if (!SessionManager.Current.Sessions.Contains(created))
                return;

            _autoSelected = created;
            FocusSession(created);
        }

        private void RebuildGroups()
        {
            var sessions = SessionManager.Current.Sessions
                                         .Where(MatchesFilter)
                                         .OrderByDescending(s => s.CreatedUtc)
                                         .ToArray();

            Groups.Clear();
            UpdateEmptyState(sessions.Length);

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

            // every group (and so every ListBox, and so every selection) is thrown away here, and
            // TrulyObservableCollection raises Reset for any session property change — without this
            // the selected row silently deselects itself moments after the user picked it.
            if (_focusedSession != null)
                Dispatcher.UIThread.Post(() => TrySelectSession(_focusedSession, scrollIntoView: false), DispatcherPriority.Loaded);
        }

        /// <summary>Words the zero state for whichever kind of empty this is: nothing captured yet
        /// (where the call to action belongs) or a filter that happens to match nothing.</summary>
        private void UpdateEmptyState(int matched)
        {
            // the zero state is bound to Groups being empty, so there is nothing to say otherwise.
            if (matched > 0)
                return;

            if (_filter == RecentFilter.All || SessionManager.Current.Sessions.Count == 0)
            {
                EmptyTitle.Text = "No captures yet";
                EmptyDetail.Text = EmptyHint;
                return;
            }

            EmptyTitle.Text = _filter switch
            {
                RecentFilter.Image => "No images",
                RecentFilter.Recording => "No recordings",
                RecentFilter.Upload => "No uploads",
                _ => "Nothing to show",
            };

            EmptyDetail.Text = "Nothing in your recent captures matches this filter — choose \"All\" to see everything.";
        }

        /// <summary>Selects a session's row and scrolls it into view — used to walk the user to the
        /// entry a gif conversion just created. Safe for a session that is not on the page. The row
        /// pulses once it settles, because the user did not ask for this selection and needs pointing
        /// at it.</summary>
        public void FocusSession(SessionInfo session)
        {
            if (session == null)
                return;

            _focusedSession = session;
            _pulseSession = session;

            // the app is walking the user to this entry, so a filter that hides it has to give way —
            // otherwise a screenshot taken while the list shows Recordings lands on an empty page.
            if (!MatchesFilter(session))
                SetFilter(RecentFilter.All);

            // a session created moments ago isn't grouped yet: the rebuild is throttled to 250 ms.
            if (!Groups.Any(g => g.Items.Contains(session)))
            {
                _regroupTimer.Stop();
                RebuildGroups();
            }

            // containers for a freshly rebuilt group only exist after the next layout pass.
            Dispatcher.UIThread.Post(() => TrySelectSession(session, scrollIntoView: true), DispatcherPriority.Loaded);
        }

        /// <summary>Selects <paramref name="session"/> in whichever group's ListBox holds it. Returns
        /// false when it is on no list (deleted, or not grouped yet).</summary>
        private bool TrySelectSession(SessionInfo session, bool scrollIntoView)
        {
            if (session == null)
                return false;

            try
            {
                foreach (var listBox in this.GetVisualDescendants().OfType<ListBox>())
                {
                    if (listBox.ItemsSource?.OfType<SessionInfo>().Any(s => ReferenceEquals(s, session)) != true)
                        continue;

                    listBox.SelectedItem = session;

                    // a rebuild is pending: it throws these containers away, and with them any
                    // animation running on one. Its own re-select lands here again once it is done.
                    if (ReferenceEquals(_pulseSession, session) && !_regroupTimer.IsEnabled)
                    {
                        _pulseSession = null;
                        PulseSelection(listBox, session);
                    }

                    if (scrollIntoView)
                    {
                        listBox.ScrollIntoView(session);
                        // the container only exists once the scroll has realized it; bringing it into
                        // view a second time is what scrolls the *outer* viewer down to its group.
                        Dispatcher.UIThread.Post(
                            () => (listBox.ContainerFromItem(session) as Control)?.BringIntoView(),
                            DispatcherPriority.Loaded);
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                // purely cosmetic — never let a torn-down list take the page down with it.
                System.Diagnostics.Debug.WriteLine("Focus session failed: " + ex);
            }

            return false;
        }

        /// <summary>Washes the row's overlay orange and back twice, to point out a selection the user
        /// did not make. Cosmetic throughout: a row whose container or overlay cannot be found (or an
        /// animation that is interrupted) just means no pulse.</summary>
        private static void PulseSelection(ListBox listBox, SessionInfo session)
        {
            // one hop, so the container exists even when the list was only just given its items.
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (listBox.ContainerFromItem(session) is not Control container)
                        return;

                    var overlay = container.GetVisualDescendants()
                                           .OfType<Border>()
                                           .FirstOrDefault(b => b.Name == "PulseOverlay");
                    if (overlay == null)
                        return;

                    var pulse = new Animation
                    {
                        Duration = TimeSpan.FromMilliseconds(450),
                        IterationCount = new IterationCount(2),
                        Easing = new SineEaseInOut(),
                        Children =
                        {
                            new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OpacityProperty, 0d) } },
                            // a tint, not a flash: the wash reads as the selection warming up rather
                            // than the row changing colour.
                            new KeyFrame { Cue = new Cue(0.5d), Setters = { new Setter(OpacityProperty, 0.2d) } },
                            new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OpacityProperty, 0d) } },
                        },
                    };

                    _ = pulse.RunAsync(overlay);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Pulse selection failed: " + ex);
                }
            }, DispatcherPriority.Loaded);
        }

        private void SessionSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox list || list.SelectedItem is not SessionInfo session)
                return;

            _focusedSession = session;

            // each date group is its own ListBox, so "SelectionMode=Single" is only single *within* a
            // group — the page has to clear the others itself to keep one selected row overall.
            if (_syncingSelection)
                return;

            _syncingSelection = true;
            try
            {
                foreach (var other in this.GetVisualDescendants().OfType<ListBox>())
                {
                    if (!ReferenceEquals(other, list))
                        other.SelectedItem = null;
                }
            }
            finally
            {
                _syncingSelection = false;
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

        private async void UploadItemClicked(object sender, RoutedEventArgs e)
        {
            var session = GetSessionFromEvent(sender);
            if (session == null)
                return;

            try
            {
                // reports its own failures (provider selection, transfer, missing file) — the row's
                // progress and resulting link come back through the session.
                await UploadManager.UploadSession(session);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Upload session failed: " + ex);
                SentryConfig.CaptureHandled(ex, "recents.upload");
            }
        }

        private async void DeleteItemClicked(object sender, RoutedEventArgs e)
        {
            var session = GetSessionFromEvent(sender);
            if (session != null)
                await DeleteWithConfirmation(session);
        }

        private async System.Threading.Tasks.Task DeleteWithConfirmation(SessionInfo session)
        {
            // a conversion owns the (incomplete) entry it is writing into: cancelling is what removes
            // the row, so there is nothing to confirm and nothing finished to lose.
            var conversion = session.ActiveGifConversion;
            if (conversion != null)
            {
                conversion.Cancel();
                return;
            }

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

            // a converted gif is written next to the recording it came from, so it takes the same
            // "kept on disk" wording — just not the word "video".
            var isGif = !String.IsNullOrEmpty(session.VideoPath)
                        && session.VideoPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

            string prompt;
            if (!videoKept)
                prompt = "Delete this capture? It cannot be recovered afterwards.";
            else if (isGif)
                prompt = $"Remove this GIF from Recents? The file is kept at {session.VideoPath}.";
            else
                prompt = $"Remove this recording from Recents? The video file is kept at {session.VideoPath}.";

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
            if (session == null || session.ActiveGifConversion != null)
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
                if (session.ActiveGifConversion != null)
                    return; // still being written — there is nothing to open yet

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

        private async void CreateGifClicked(object sender, RoutedEventArgs e)
        {
            var session = GetSessionFromEvent(sender);
            if (session == null)
                return;

            try
            {
                // a recording converts once — asking again just walks the user to the entry it made.
                var existing = GifConversionManager.FindExisting(session);
                if (existing != null)
                {
                    FocusSession(existing);
                    return;
                }

                var created = await GifConversionManager.StartConversionAsync(session);
                if (created != null)
                    FocusSession(created);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Create gif failed: " + ex);
                SentryConfig.CaptureHandled(ex, "recents.create-gif");
            }
        }

        private void CancelGifClicked(object sender, RoutedEventArgs e)
        {
            ((sender as Control)?.DataContext as GifConversion)?.Cancel();
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
