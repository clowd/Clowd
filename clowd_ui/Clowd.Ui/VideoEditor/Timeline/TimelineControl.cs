using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Clowd.UI.Controls;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>
    /// The multi-track timeline, assembled in code: a pinned <see cref="TimelineRuler"/> on top
    /// (with a corner cell over the header column), a shared vertical-only ScrollViewer holding
    /// the native <see cref="TrackHeaderPanel"/> beside the custom-drawn
    /// <see cref="TimelineSurface"/>, and a native horizontal ScrollBar at the bottom driving the
    /// <b>virtual</b> horizontal axis (a <see cref="TimelineViewport"/> — there is no
    /// million-pixel control).
    ///
    /// Wheel gestures are tunnel-handled here so the inner ScrollViewer never sees the modified
    /// ones: Ctrl+wheel = anchored zoom, Shift+wheel = horizontal scroll, plain wheel = the
    /// ScrollViewer's vertical scroll. Everything the timeline edits goes through
    /// <see cref="Session"/>; the control re-reads <c>Session.Project</c> on every
    /// <c>ProjectChanged</c> and rebuilds rows/headers only on Structural changes.
    /// </summary>
    public class TimelineControl : Decorator
    {
        internal const double HeaderWidth = 150;

        private const double ZoomStepPerNotch = 1.25;
        private const double WheelScrollPxPerNotch = 60;
        private const double FollowMarginPx = 40;

        public static readonly StyledProperty<TimeSpan> PositionProperty =
            AvaloniaProperty.Register<TimelineControl, TimeSpan>(nameof(Position),
                defaultBindingMode: BindingMode.TwoWay);

        private readonly TimelineViewport _viewport = new TimelineViewport();
        private readonly TimelineRuler _ruler;
        private readonly TimelineSurface _surface;
        private readonly TrackHeaderPanel _headers;
        private readonly ScrollBar _hscroll;
        private readonly Border _corner;
        private readonly ToolButton _zoomToFit;
        private readonly Border _spacer;
        private readonly Border _scrollHost;

        private EditorSession _session;
        private ITimelinePreviewProvider _previewProvider = NullTimelinePreviewProvider.Instance;
        private bool _scrubbing;
        private bool _syncingScrollBar;
        private bool _pendingZoomToFit;

        /// <summary>Raised when the user starts a scrub drag (playhead, ruler or empty row). The
        /// window pauses playback for the duration of the drag.</summary>
        public event EventHandler ScrubStarted;

        /// <summary>Raised for every position change while scrubbing, in timeline ticks.</summary>
        public event EventHandler<long> Scrubbed;

        /// <summary>Raised when the scrub drag ends, with the final position in timeline ticks.</summary>
        public event EventHandler<long> ScrubCompleted;

        /// <summary>Selection passthrough from <see cref="Session"/> — the session owns selection,
        /// so the timeline, inspector and gizmo cannot disagree about it.</summary>
        public event EventHandler SelectionChanged;

        public TimelineControl()
        {
            _ruler = new TimelineRuler(_viewport);
            _surface = new TimelineSurface(_viewport);
            _headers = new TrackHeaderPanel();

            // the corner cell sits over the header column, level with the ruler — the one piece of
            // timeline chrome with room for a button, and zoom-to-fit is the gesture users reach
            // for after a Ctrl+wheel has taken them somewhere they cannot find their way back from.
            _zoomToFit = new ToolButton
            {
                Width = 20,
                Height = 20,
                Padding = new Thickness(3),
                Margin = new Thickness(0, 0, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                IconPath = TimelineIcons.ZoomToFitGeometry,
            };
            ToolTip.SetTip(_zoomToFit, "Fit the whole project in view");
            _zoomToFit.Click += (_, _) => ZoomToFit();
            _corner = new Border { Child = _zoomToFit };

            _spacer = new Border();
            _hscroll = new ScrollBar
            {
                Orientation = Orientation.Horizontal,
                AllowAutoHide = false,
                Focusable = false,
                Minimum = 0,
            };

            var scrollContent = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions($"{HeaderWidth},*"),
            };
            Grid.SetColumn(_headers, 0);
            Grid.SetColumn(_surface, 1);
            scrollContent.Children.Add(_headers);
            scrollContent.Children.Add(_surface);

            _scrollHost = new Border
            {
                Child = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = scrollContent,
                },
            };

            var grid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                ColumnDefinitions = new ColumnDefinitions($"{HeaderWidth},*"),
            };
            Grid.SetRow(_corner, 0);
            Grid.SetColumn(_corner, 0);
            Grid.SetRow(_ruler, 0);
            Grid.SetColumn(_ruler, 1);
            Grid.SetRow(_scrollHost, 1);
            Grid.SetColumn(_scrollHost, 0);
            Grid.SetColumnSpan(_scrollHost, 2);
            Grid.SetRow(_spacer, 2);
            Grid.SetColumn(_spacer, 0);
            Grid.SetRow(_hscroll, 2);
            Grid.SetColumn(_hscroll, 1);
            grid.Children.Add(_corner);
            grid.Children.Add(_ruler);
            grid.Children.Add(_scrollHost);
            grid.Children.Add(_spacer);
            grid.Children.Add(_hscroll);
            Child = grid;

            // tunnel so the ScrollViewer never sees a modified wheel (it would eat Ctrl+wheel as
            // a plain vertical scroll before the zoom could run).
            AddHandler(PointerWheelChangedEvent, OnTunnelPointerWheel, RoutingStrategies.Tunnel);

            _viewport.Changed += Viewport_Changed;
            _hscroll.Scroll += HorizontalScrollBar_Scroll;

            _ruler.ScrubStarted += Child_ScrubStarted;
            _ruler.Scrubbed += Child_Scrubbed;
            _ruler.ScrubCompleted += Child_ScrubCompleted;
            _surface.ScrubStarted += Child_ScrubStarted;
            _surface.Scrubbed += Child_Scrubbed;
            _surface.ScrubCompleted += Child_ScrubCompleted;

            // the row context menu runs the very commands the keyboard does, rather than a second
            // copy of the ripple/group rules.
            _surface.DeleteSelection = DeleteSelection;
            _surface.SplitAtPlayhead = SplitAtPlayhead;

            ActualThemeVariantChanged += (_, _) =>
            {
                RefreshChrome();
                _headers.Rebuild();
            };

            RefreshChrome();
        }

        // ---------------------------------------------------------------------------- public API

        /// <summary>The editing session driving the timeline. The control subscribes to its
        /// change/selection events for the session's lifetime; setting a new session (or null)
        /// detaches from the previous one.</summary>
        public EditorSession Session
        {
            get => _session;
            set
            {
                if (ReferenceEquals(_session, value))
                    return;

                if (_session != null)
                {
                    _session.ProjectChanged -= Session_ProjectChanged;
                    _session.SelectionChanged -= Session_SelectionChanged;
                }

                _session = value;

                if (_session != null)
                {
                    _session.ProjectChanged += Session_ProjectChanged;
                    _session.SelectionChanged += Session_SelectionChanged;
                }

                _surface.Session = value;
                _headers.SetSession(value);
                _viewport.SetDuration(value?.DurationTicks ?? 0);

                // fit the whole project on open; deferred until the first layout pass when the
                // viewport width is still unknown.
                if (_viewport.ViewportWidth > 0)
                    _viewport.ZoomToFit();
                else
                    _pendingZoomToFit = true;
            }
        }

        /// <summary>Where filmstrip thumbnails and waveform peaks come from. Defaults to
        /// <see cref="NullTimelinePreviewProvider.Instance"/> so the timeline works (as plain
        /// bars) before the SDK's preview services are wired in.</summary>
        public ITimelinePreviewProvider PreviewProvider
        {
            get => _previewProvider;
            set
            {
                value ??= NullTimelinePreviewProvider.Instance;
                _previewProvider = value;
                _surface.PreviewProvider = value;
                value.SetViewport(_viewport.ScrollTicks, _viewport.ScrollEndTicks);
            }
        }

        /// <summary>Playhead position in timeline time (two-way; scrubbing writes it back).</summary>
        public TimeSpan Position
        {
            get => GetValue(PositionProperty);
            set => SetValue(PositionProperty, value);
        }

        /// <summary>When true the view scrolls to keep the playhead comfortably visible as
        /// <see cref="Position"/> advances. The window sets it while playing and clears it while
        /// paused, so the timeline never fights the user's own scrolling.</summary>
        public bool FollowPlayhead { get; set; }

        /// <summary>The selected item ids, primary first (delegated to <see cref="Session"/>).</summary>
        public IReadOnlyList<Guid> SelectedItemIds => _session?.SelectedItemIds ?? Array.Empty<Guid>();

        /// <summary>Deletes the primary selected item — ripple for recording segments (the gap
        /// closes on all tracks), a whole-group lift for an imported file's linked rows, plain
        /// removal for unlinked items. An import's link group means "streams of one file", not
        /// "contiguous recording segments", so rippling it would silently shift unrelated material
        /// (see <see cref="EditorSession.IsRippleGroup"/>). The window forwards the Delete key
        /// here. Returns false when nothing deletable is selected.</summary>
        public bool DeleteSelection()
        {
            // a drag in progress owns the model (a gesture is open): a delete would ride the
            // gesture as an un-undoable preview and be resurrected by the drag's next move.
            if (_session == null || _session.IsGestureActive)
                return false;

            var item = _session.PrimarySelectedItem;
            if (item == null)
                return false;

            var track = _session.Project.Tracks.FirstOrDefault(t => t.Id == item.TrackId);
            if (track is not { Locked: false })
                return false;

            if (item.LinkGroupId == null)
                _session.DeleteItem(item.Id, this);
            else if (_session.IsRippleGroup(item.Id))
                _session.RippleDeleteItem(item.Id, this);
            else
                _session.DeleteGroup(item.Id, this);
            return true;
        }

        /// <summary>Splits at the playhead (the selected item's group when it covers it, otherwise
        /// every covering group — see <see cref="EditorSession.SplitAtPlayhead"/>). The window's
        /// Ctrl+K. Returns true when anything split.</summary>
        public bool SplitAtPlayhead()
        {
            if (_session == null)
                return false;

            return _session.SplitAtPlayhead(Math.Clamp(Position.Ticks, 0, _session.DurationTicks), this);
        }

        /// <summary>Zooms out until the whole project fits and returns to the origin.</summary>
        public void ZoomToFit() => _viewport.ZoomToFit();

        /// <summary>Scrolls the minimum amount that brings a span into view — where the toolbar's
        /// add/import just put an item, which the user has to be able to see to believe. The end is
        /// asked for first so the start wins when the span is wider than the viewport.</summary>
        public void EnsureVisible(long startTicks, long durationTicks = 0)
        {
            _viewport.EnsureVisible(startTicks + Math.Max(0, durationTicks));
            _viewport.EnsureVisible(startTicks);
        }

        // ----------------------------------------------------------------------- session events

        private void Session_ProjectChanged(object sender, ProjectChangedEventArgs e)
        {
            // A mid-gesture (Preview) duration is provisional, and re-clamping the viewport to it
            // would move the tick<->pixel mapping the drag is being measured under: the surface
            // re-derives its target from XToTicks(x) on every move, so a trim/move that shortens
            // the project would feed on itself and collapse the item under a stationary pointer.
            // Growing only raises the zoom/scroll limits, so it is safe; a shrink lands once, when
            // the gesture commits (or cancels).
            if (e.Kind != ProjectChangeKind.Preview || _session.DurationTicks > _viewport.DurationTicks)
                _viewport.SetDuration(_session.DurationTicks);

            if (e.Kind == ProjectChangeKind.Structural)
            {
                _surface.RebuildRows();
                _headers.Rebuild();
            }
            else if (e.Kind == ProjectChangeKind.Mapping)
            {
                // unlink/relink are Mapping and can come from outside the headers (the
                // inspector's unlink button) — the link toggles re-read without a rebuild.
                _headers.SyncToggles();
            }

            _surface.InvalidateVisual();
        }

        private void Session_SelectionChanged(object sender, EventArgs e)
        {
            _surface.InvalidateVisual();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------------------ scrub plumbing

        private void Child_ScrubStarted(object sender, EventArgs e)
        {
            _scrubbing = true;
            ScrubStarted?.Invoke(this, EventArgs.Empty);
        }

        private void Child_Scrubbed(object sender, long ticks)
        {
            SetCurrentValue(PositionProperty, TimeSpan.FromTicks(ticks));
            Scrubbed?.Invoke(this, ticks);
        }

        private void Child_ScrubCompleted(object sender, long ticks)
        {
            _scrubbing = false;
            ScrubCompleted?.Invoke(this, ticks);
        }

        // -------------------------------------------------------------------- viewport plumbing

        private void Viewport_Changed(object sender, EventArgs e)
        {
            if (_pendingZoomToFit && _viewport.ViewportWidth > 0)
            {
                _pendingZoomToFit = false; // cleared before the re-entrant Changed this raises
                _viewport.ZoomToFit();
            }

            SyncScrollBar();
            _previewProvider.SetViewport(_viewport.ScrollTicks, _viewport.ScrollEndTicks);
        }

        private void SyncScrollBar()
        {
            _syncingScrollBar = true;
            try
            {
                var max = TimelineViewMath.MaxScrollTicks(_viewport.TicksPerPixel,
                    _viewport.DurationTicks, _viewport.ViewportWidth);
                _hscroll.Maximum = max;
                _hscroll.ViewportSize = _viewport.VisibleTicks;
                _hscroll.SmallChange = Math.Max(1, _viewport.VisibleTicks / 10.0);
                _hscroll.LargeChange = Math.Max(1, _viewport.VisibleTicks * 0.9);
                _hscroll.Value = _viewport.ScrollTicks;
                _hscroll.IsEnabled = max > 0;
            }
            finally
            {
                _syncingScrollBar = false;
            }
        }

        private void HorizontalScrollBar_Scroll(object sender, ScrollEventArgs e)
        {
            if (_syncingScrollBar)
                return;

            _viewport.ScrollToTicks((long)Math.Round(e.NewValue));
        }

        private void OnTunnelPointerWheel(object sender, PointerWheelEventArgs e)
        {
            var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
            if (delta == 0)
                return;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                // anchored zoom around the pointer — the anchor x is measured in the surface's
                // space so ruler and rows zoom identically; a wheel over the header column or the
                // vertical scroll bar lands outside [0, width] and the viewport clamps it to the
                // nearest surface edge (zooming around an off-screen tick would pan, not zoom).
                _viewport.SetZoomAnchored(_viewport.TicksPerPixel * Math.Pow(ZoomStepPerNotch, -delta),
                    e.GetPosition(_surface).X);
                e.Handled = true;
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                _viewport.ScrollByPixels(-delta * WheelScrollPxPerNotch);
                e.Handled = true;
            }
            // plain wheel bubbles on to the vertical ScrollViewer
        }

        // ------------------------------------------------------------------------------- chrome

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == PositionProperty)
            {
                var ticks = change.GetNewValue<TimeSpan>().Ticks;
                _surface.PositionTicks = ticks;
                _ruler.PositionTicks = ticks;

                if (FollowPlayhead && !_scrubbing)
                    _viewport.EnsureVisible(ticks, FollowMarginPx);
            }
        }

        private void RefreshChrome()
        {
            var palette = TimelinePalette.ForVariant(ActualThemeVariant);
            _corner.Background = palette.RulerBackground;
            _zoomToFit.Foreground = palette.LabelBrush;
            _spacer.Background = palette.RulerBackground;
            _scrollHost.Background = palette.SurfaceBackground;
        }
    }
}
