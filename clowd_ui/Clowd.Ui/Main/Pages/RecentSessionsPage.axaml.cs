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
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clowd.UI.Controls;
using Clowd.UI.Dialogs;
using Clowd.UI.Helpers;
using Clowd.UI.Preview;
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
        Starred,
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
                    Segment("Starred", RecentFilter.Starred, "Items you starred — and whatever they are linked to"),
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
                RecentFilter.Image => !session.IsVideo && !session.IsProject
                                      && (!session.IsUploadOnly
                                          || String.Equals(session.ContentKind, "image", StringComparison.OrdinalIgnoreCase)),
                RecentFilter.Recording => IsRecording(session),
                // anything with a link, plus everything the upload path started even if it has no
                // link (yet, or ever — a failed or canceled upload still belongs here).
                RecentFilter.Upload => session.ActiveUpload != null
                                       || session.AllUploads.Length > 0
                                       || IsUploadEntry(session),
                // deliberately the narrow answer: the list itself widens this to the whole linked
                // chain (see SessionLinks.CollectStarredChains), but callers here ask about one
                // entry on its own — "is this row's own reason for being here still true?".
                RecentFilter.Starred => session.Starred,
                _ => true,
            };
        }

        /// <summary>A recording Clowd made — or the project, render or GIF that came out of one.
        /// All of them are "video" entries carrying the file they wrote; a video *file* someone
        /// uploaded is one too, but has no VideoPath of its own, which is what tells the two apart.
        /// A blank video project has no file at all and is caught by IsProject.</summary>
        private static bool IsRecording(SessionInfo session)
        {
            return session.IsProject || (session.IsVideo && !String.IsNullOrEmpty(session.VideoPath));
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
            SessionScroller.ScrollChanged += OnSessionsScrolled;
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
            SessionScroller.ScrollChanged -= OnSessionsScrolled;
            _regroupTimer.Stop();
        }

        /// <summary>During a fling the rows under the pointer change faster than any of them can be
        /// produced, so the cheap preview lane is gated shut for a beat rather than made to chase
        /// them. Already-decoded previews are unaffected — this pauses dequeue, not drawing — and
        /// requests still arrive and are still ordered while the gate is down, so whatever the user
        /// lands on is at the head of the queue when it lifts.</summary>
        private void OnSessionsScrolled(object sender, ScrollChangedEventArgs e)
        {
            SessionPreviewEngine.Current.SuspendFor(TimeSpan.FromMilliseconds(120));
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
            var newestFirst = SessionManager.Current.Sessions
                                            .OrderByDescending(s => s.CreatedUtc)
                                            .ToArray();

            // Starred is the one filter not decided one entry at a time: a star anywhere on a chain
            // keeps the whole chain on the list, so it has to be resolved against every session
            // before anything is dropped.
            var starred = _filter == RecentFilter.Starred ? SessionLinks.CollectStarredChains(newestFirst) : null;

            var sessions = starred != null
                ? newestFirst.Where(starred.Contains).ToArray()
                : newestFirst.Where(MatchesFilter).ToArray();

            var rows = OrderWithLinkedEntries(sessions);

            Groups.Clear();
            UpdateEmptyState(rows.Count);

            SessionGroupVm current = null;
            foreach (var row in rows)
            {
                // the whole chain is grouped by the entry it grew from, not by its own timestamp —
                // a render made this morning belongs with the recording it came from.
                var key = GetTimeAgoGroupName(row.GroupTime);
                if (current == null || current.Name != key)
                {
                    current = new SessionGroupVm(key);
                    Groups.Add(current);
                }

                current.Items.Add(row.Session);
            }

            // every group (and so every ListBox, and so every selection) is thrown away here, and
            // TrulyObservableCollection raises Reset for any session property change — without this
            // the selected row silently deselects itself moments after the user picked it.
            if (_focusedSession != null)
                Dispatcher.UIThread.Post(() => TrySelectSession(_focusedSession, scrollIntoView: false), DispatcherPriority.Loaded);
        }

        /// <summary>One row of the list: the entry, and the timestamp its whole chain is grouped
        /// by (its own, or the entry it was made from).</summary>
        private readonly record struct SessionRow(SessionInfo Session, DateTime GroupTime);

        /// <summary>
        /// Lays <paramref name="newestFirst"/> out so that an entry made <i>from</i> another one —
        /// a render from its project, a GIF from the video it was converted from — sits directly
        /// above it, whatever the two were created at, and travels with it into its time group.
        /// A chain of them (project → render → GIF) reads top to bottom as output first, origin
        /// last. Also refreshes the two row properties only this layout knows: the chain-link
        /// marker, and a project's render status line.
        /// </summary>
        private static List<SessionRow> OrderWithLinkedEntries(IReadOnlyList<SessionInfo> newestFirst)
        {
            var (parents, children) = SessionLinks.BuildGraph(newestFirst);

            var rows = new List<SessionRow>(newestFirst.Count);
            var placed = new HashSet<SessionInfo>();

            void Emit(SessionInfo session, DateTime groupTime)
            {
                if (!placed.Add(session))
                    return; // already placed, or a cycle of source paths pointing at each other

                if (children.TryGetValue(session, out var siblings))
                {
                    foreach (var child in siblings)
                        Emit(child, groupTime);
                }

                rows.Add(new SessionRow(session, groupTime));
            }

            foreach (var session in newestFirst)
            {
                if (!parents.ContainsKey(session))
                    Emit(session, session.CreatedUtc);
            }

            // only a cycle can leave anything behind; it keeps its own place rather than vanishing.
            foreach (var session in newestFirst)
            {
                if (placed.Add(session))
                    rows.Add(new SessionRow(session, session.CreatedUtc));
            }

            ApplyRowLinks(rows, parents);
            return rows;
        }

        /// <summary>Points each row's chain-link marker and render status line at what the layout
        /// just decided. Every session is written, including the ones this filter hides, so nothing
        /// carries a marker from a list it is no longer part of.</summary>
        /// <remarks>Each session is assigned exactly once, from a value worked out first: writing
        /// these properties announces a change, and an announced change is what schedules the next
        /// rebuild — a clear-then-set pass would toggle every linked row on every rebuild and never
        /// settle.</remarks>
        private static void ApplyRowLinks(List<SessionRow> rows, Dictionary<SessionInfo, SessionInfo> parents)
        {
            var marks = new Dictionary<SessionInfo, (bool Previous, bool Next, string RenderStatus)>();

            foreach (var row in rows)
                marks[row.Session] = (false, false, row.Session.IsProject ? DescribeRender(row.Session) : null);

            for (var i = 0; i + 1 < rows.Count; i++)
            {
                // the bracket joins two adjacent rows, and each of them draws its own half of it
                // (see SessionInfo.LinkedToPrevious). It belongs there only when the upper row was
                // made from the lower one — never across a group boundary, nor between two siblings.
                if (!parents.TryGetValue(rows[i].Session, out var parent) || !ReferenceEquals(parent, rows[i + 1].Session))
                    continue;

                var upper = rows[i].Session;
                var lower = rows[i + 1].Session;
                marks[upper] = (marks[upper].Previous, true, marks[upper].RenderStatus);
                marks[lower] = (true, marks[lower].Next, marks[lower].RenderStatus);
            }

            foreach (var session in SessionManager.Current.Sessions)
            {
                var mark = marks.GetValueOrDefault(session);
                session.LinkedToPrevious = mark.Previous;
                session.LinkedToNext = mark.Next;
                session.RenderStatusText = mark.RenderStatus;
            }
        }

        /// <summary>What a project row says in place of "Not uploaded". It is only rendered once
        /// the output entry has actually landed — while the render is still running that entry,
        /// chained directly above, is the one showing the progress bar. The date is the output
        /// entry's own, so the two rows agree on when it was made.</summary>
        private static string DescribeRender(SessionInfo project)
        {
            var output = VideoRenderManager.FindExisting(project);
            if (output == null || output.ActiveRender != null)
                return "Not rendered";

            return "Rendered on " + ToLocalTime(output.CreatedUtc)
                                    .ToString(AppStyles.UiDateTimePattern, CultureInfo.CurrentCulture);
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
                RecentFilter.Starred => "Nothing starred",
                _ => "Nothing to show",
            };

            // the starred list is the one an empty page can be *fixed* from, so it says how rather
            // than pointing back at "All".
            EmptyDetail.Text = _filter == RecentFilter.Starred
                ? "Hover an item in Recent and click its star to keep it here — starred items are never removed by the automatic cleanup."
                : "Nothing in your recent captures matches this filter — choose \"All\" to see everything.";
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
                            () =>
                            {
                                if (listBox.ContainerFromItem(session) is not Control container)
                                    return;

                                var scroller = container.FindAncestorOfType<FadeEdgeScrollViewer>();

                                // the newest row sits just under the group header — scrolling the
                                // page all the way up shows it with its context instead of leaving
                                // the header cut off above the row.
                                if (ReferenceEquals(Groups.FirstOrDefault()?.Items.FirstOrDefault(), session)
                                    && scroller != null)
                                {
                                    scroller.Offset = scroller.Offset.WithY(0);
                                    return;
                                }

                                // ask for the row plus the fade band: a minimal scroll parks the row
                                // at the viewport edge, exactly under the scroller's dissolve mask.
                                var fade = scroller?.FadeSize ?? 0;
                                container.BringIntoView(
                                    new Rect(container.Bounds.Size).Inflate(new Thickness(0, fade + 4)));
                            },
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
                            // than the row changing color.
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

        private void ToggleStarClicked(object sender, RoutedEventArgs e)
        {
            var session = GetSessionFromEvent(sender);
            if (session == null)
                return;

            session.Starred = !session.Starred;

            // under the Starred filter the click changes which rows belong on the page, and that is
            // the whole of its feedback — the throttled rebuild is too slow to read as a response.
            if (_filter != RecentFilter.Starred)
                return;

            // the row keeps its place when the chain it sits on is still starred elsewhere, so this
            // is a rebuild rather than a removal.
            _regroupTimer.Stop();
            RebuildGroups();
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
            // a conversion or a render owns the (incomplete) entry it is writing into: canceling is
            // what removes the row, so there is nothing to confirm and nothing finished to lose.
            var conversion = session.ActiveGifConversion;
            if (conversion != null)
            {
                conversion.Cancel();
                return;
            }

            var render = session.ActiveRender;
            if (render != null)
            {
                render.Cancel();
                return;
            }

            if (session.OpenEditor != null)
            {
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Information,
                    "The selected item is currently open and can not be deleted.");
                return;
            }

            var noun = DescribeEntry(session);

            // the one file the entry owns that deleting its session directory would leave behind
            // (a recording saved to the user's output folder, a GIF, a render). Null when the entry
            // keeps everything with itself, which is the only case with nothing to choose between.
            var external = GetExternalContentPath(session);

            if (external == null)
            {
                // a project's mp4 carries one stream per track and lives in the session directory
                // beside the composition that gives it meaning — the two are one thing, and there
                // is no version of deleting the entry that keeps the video.
                string content;
                if (session.IsVideoProject)
                    content = "The project is stored with the entry and will be deleted with it. "
                              + "Media you imported into it stays where it is on disk.";
                else if (session.IsProject)
                    content = "Its video file is stored with the entry and will be deleted too. This cannot be undone.";
                else
                    content = $"This {noun} is stored with the entry. Deleting it cannot be undone.";

                if (await NiceDialog.ShowDialogAsync(this, NiceDialogIcon.Warning, content,
                        $"Delete this {noun}?", "Delete", "Cancel"))
                {
                    SessionManager.Current.DeleteSession(session);
                }

                return;
            }

            var fileNoun = DescribeExternalFile(session, external);
            var choice = await NiceDialog.ShowThreeWayPromptAsync(this, NiceDialogIcon.Warning,
                $"The {fileNoun} is saved outside this entry, at {external}. You can remove the entry "
                + $"from Recents and keep the file, or delete both.",
                $"Delete this {noun}?",
                "Remove from Recents", "Delete both");

            if (choice == MessageDialogChoice.Cancel)
                return;

            if (choice == MessageDialogChoice.Alternate)
                await TryDeleteExternalFileAsync(external);

            SessionManager.Current.DeleteSession(session);
        }

        /// <summary>What the confirmation calls this entry: the same word the Recent list has
        /// taught the user for it, lowercased to sit inside a sentence.</summary>
        private static string DescribeEntry(SessionInfo session)
        {
            if (session.IsVideoProject)
                return "video project";

            // a composition capture: a project too, but one the user thinks of as their recording.
            if (session.IsProject)
                return "screen recording";

            if (!String.IsNullOrEmpty(session.SourceVideoPath))
                return "GIF";

            if (!String.IsNullOrEmpty(session.EditSourceVideoPath))
                return "rendered video";

            if (session.IsVideo)
                return "video";

            return session.ContentKind?.ToLowerInvariant() switch
            {
                "image" => "image",
                "text" => "text upload",
                "file" => "file upload",
                _ => "capture", // a screenshot or a scrolling capture
            };
        }

        /// <summary>
        /// The file this entry owns that does <i>not</i> live in its session directory, and so
        /// would survive the session being deleted. Null when there is none — a project (whose
        /// media is inseparable from it), a capture, an upload — and null when the file has since
        /// gone missing, because then there is nothing left to offer to delete.
        /// </summary>
        private static string GetExternalContentPath(SessionInfo session)
        {
            if (session.IsProject)
                return null;

            // the recording/GIF/render first, then the image: a video entry's PreviewImgPath is
            // only its poster frame, which lives in the session directory either way.
            foreach (var path in new[] { session.VideoPath, session.PreviewImgPath })
            {
                if (String.IsNullOrEmpty(path) || session.IsInsideSessionDirectory(path))
                    continue;

                try
                {
                    if (File.Exists(path))
                        return path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // an unreachable path is one this dialog cannot promise to delete either.
                }
            }

            return null;
        }

        /// <summary>What the confirmation calls the file kept outside the entry.</summary>
        private static string DescribeExternalFile(SessionInfo session, string path)
        {
            if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                return "GIF file";

            return session.IsVideo ? "video file" : "image file";
        }

        /// <summary>Deletes the kept-outside file the user asked to go with the entry. A failure
        /// is reported and then let go: the entry itself is still removed, which is the part of
        /// the request that can be honoured.</summary>
        private async System.Threading.Tasks.Task TryDeleteExternalFileAsync(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Delete session file failed: " + ex);
                SentryConfig.CaptureHandled(ex, "recents.delete-file");
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Error,
                    $"{path} could not be deleted: {ex.Message}", "Delete failed");
            }
        }

        private void ViewDoubleClick(object sender, TappedEventArgs e)
        {
            var session = GetSessionFromEvent(sender);
            if (session == null || !session.IsIdle)
                return;

            DefaultAction(session);
        }

        /// <summary>What double-click and Enter do to a row: open it in the editor that owns it —
        /// the video editor for a recording, the image editor for a capture. A video with no editor
        /// behind it (a GIF, a single-track capture, or any recording on a platform the video editor
        /// does not ship on) plays instead, which is what those rows have always done.</summary>
        private static void DefaultAction(SessionInfo session)
        {
            if (session.CanEditVideo)
                Clowd.UI.VideoEditor.VideoEditorWindow.ShowSession(session);
            else if (session.IsVideo)
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
                if (!session.IsIdle)
                    return; // still being written — there is nothing to open yet

                DefaultAction(session);
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
            if (session == null || !session.IsIdle || !session.CanCreateGif)
                return; // a rendering edit is still being written — there is no usable mp4 yet

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

        private async void RenderVideoClicked(object sender, RoutedEventArgs e)
        {
            var session = GetSessionFromEvent(sender);
            if (session == null || !session.IsIdle || !session.ShowRender)
                return;

            try
            {
                // a render already running for this project owns its output entry; StartRender
                // hands that same entry back, so either way this walks the user to the row.
                var created = await VideoRenderManager.StartRenderFromSessionAsync(session);
                if (created != null)
                    FocusSession(created);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Render video failed: " + ex);
                SentryConfig.CaptureHandled(ex, "recents.render-video");
            }
        }

        private void EditVideoClicked(object sender, RoutedEventArgs e)
        {
            var session = GetSessionFromEvent(sender);
            if (session == null || !session.CanEditVideo)
                return;

            Clowd.UI.VideoEditor.VideoEditorWindow.ShowSession(session);
        }

        private void CancelGifClicked(object sender, RoutedEventArgs e)
        {
            ((sender as Control)?.DataContext as GifConversion)?.Cancel();
        }

        private void CancelRenderClicked(object sender, RoutedEventArgs e)
        {
            ((sender as Control)?.DataContext as VideoRender)?.Cancel();
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
