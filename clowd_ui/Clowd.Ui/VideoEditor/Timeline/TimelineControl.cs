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
using Avalonia.Media;
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
    /// Wheel gestures are tunnel-handled here so the inner ScrollViewer only sees the ones meant
    /// for it. What each one means is per-platform and lives in <see cref="TimelineScrollInput"/>:
    /// on Windows plain (and Ctrl) wheel = anchored zoom around the pointer, Shift+wheel =
    /// horizontal scroll, Alt+wheel = the ScrollViewer's vertical scroll; on macOS a two-finger
    /// scroll pans (vertically via that same ScrollViewer), Cmd/Ctrl+scroll zooms, and the pinch —
    /// a bubbling <c>PointerTouchPadGestureMagnify</c>, not a wheel — is the primary zoom.
    /// Everything the timeline edits goes through
    /// <see cref="Session"/>; the control re-reads <c>Session.Project</c> on every
    /// <c>ProjectChanged</c> and rebuilds rows/headers only on Structural changes.
    /// </summary>
    public class TimelineControl : Decorator
    {
        // just enough for the header rows' grip + kind icon + four-button cluster (and the corner
        // cell's four buttons above them) — wider only buys blank space between the two.
        internal const double HeaderWidth = 130;

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
        private readonly ToolButton _snap;
        private readonly ToolButton _split;
        private readonly ToolButton _zoomToFit;
        private readonly ToolButton _resetZoom;
        private readonly Border _spacer;
        private readonly Border _scrollHost;

        private EditorSession _session;
        private ITimelinePreviewProvider _previewProvider = NullTimelinePreviewProvider.Instance;
        private bool _scrubbing;
        private bool _syncingScrollBar;

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
            // timeline chrome with room for buttons, and these four are the ones that belong to
            // the timeline rather than to playback: the snap toggle, split at the playhead, and
            // the two escapes from a wheel-zoom that has taken the view somewhere the user cannot
            // find their way back from (fit everything / back to the default scale).
            _snap = NewCornerButton(TimelineIcons.SnapGeometry,
                "Snap dragged clips to other edges and the playhead (hold Alt to bypass once)",
                () => { });
            _snap.CanToggle = true;
            _snap.IsChecked = true;
            _snap.IsCheckedChanged += (_, _) =>
            {
                var on = _snap.IsChecked == true;
                _surface.SnapEnabled = on;
                // the state is the icon's weight, not the theme's checked veil (a white wash,
                // invisible over the light corner cell): lit when on, faded back when off.
                _snap.Opacity = on ? 1.0 : 0.4;
            };
            _split = NewCornerButton(TimelineIcons.SplitGeometry,
                "Split every track at playhead (Ctrl+K) — right-click a clip to split just that one",
                () => SplitAtPlayhead());
            _zoomToFit = NewCornerButton(TimelineIcons.ZoomToFitGeometry,
                "Fit the whole project in view", ZoomToFit);
            _resetZoom = NewCornerButton(TimelineIcons.ResetZoomGeometry,
                "Reset to the default zoom", ResetZoom);

            var cornerButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Margin = new Thickness(0, 0, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            cornerButtons.Children.Add(_snap);
            cornerButtons.Children.Add(_split);
            cornerButtons.Children.Add(_zoomToFit);
            cornerButtons.Children.Add(_resetZoom);
            _corner = new Border { Child = cornerButtons };

            _spacer = new Border();
            _hscroll = new ScrollBar
            {
                Orientation = Orientation.Horizontal,
                AllowAutoHide = false,
                Focusable = false,
                Minimum = 0,
            };

            // a touch taller than the theme's ordinary scrollbars: this bar is the timeline's main
            // navigation strip, not incidental chrome. The thumb grows via a scoped resource
            // override; the bar itself needs an explicit Height — the theme's thickness resource
            // sizes the thumb but not the control, so overriding it alone leaves the fatter thumb
            // clipped by the old container.
            if (Application.Current!.TryGetResource("ScrollBarThickness", null, out var barSize) &&
                barSize is double bar)
                _hscroll.Height = bar + 2;
            if (Application.Current!.TryGetResource("ScrollBarThumbThickness", null, out var thumbSize) &&
                thumbSize is double thumb)
                _hscroll.Resources["ScrollBarThumbThickness"] = thumb + 2;

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
            AddHandler(PointerTouchPadGestureMagnifyEvent, OnTouchPadMagnify);

            _viewport.Changed += Viewport_Changed;
            _hscroll.Scroll += HorizontalScrollBar_Scroll;

            _ruler.HoverTicksChanged += (_, ticks) => _surface.HoverTicks = ticks;
            _ruler.ScrubStarted += Child_ScrubStarted;
            _ruler.Scrubbed += Child_Scrubbed;
            _ruler.ScrubCompleted += Child_ScrubCompleted;
            _surface.ScrubStarted += Child_ScrubStarted;
            _surface.Scrubbed += Child_Scrubbed;
            _surface.ScrubCompleted += Child_ScrubCompleted;

            // the row context menu runs the very command the Delete key does, rather than a second
            // copy of the ripple/group rules. (Its two Split entries do NOT delegate here: they cut
            // the clicked clip alone, where this one cuts every row at once.)
            _surface.DeleteSelection = DeleteSelection;
            _surface.RippleDeleteSelection = RippleDeleteSelection;

            ActualThemeVariantChanged += (_, _) =>
            {
                RefreshChrome();
                _headers.Rebuild();
            };

            RefreshChrome();
        }

        private static ToolButton NewCornerButton(Geometry icon, string tip, Action click)
        {
            var button = new ToolButton
            {
                Width = 20,
                Height = 20,
                Padding = new Thickness(3),
                VerticalAlignment = VerticalAlignment.Center,
                IconPath = icon,
            };
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => click();
            return button;
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

                // a new project opens at the default scale, not fitted: one second is always the
                // same width, whatever the length of the recording or the size of the window.
                _viewport.ResetZoom();
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

        /// <summary>Deletes the primary selected item — just that clip, leaving its span blank.
        /// The rest of its link group (the recording's other rows, the row's other segments) stays
        /// exactly where it is: deleting the middle of a split audio row silences that stretch and
        /// moves nothing. The cross-track cut is <see cref="RippleDeleteSelection"/>, offered from
        /// the context menu. The window forwards the Delete key here. Returns false when nothing
        /// deletable is selected.</summary>
        public bool DeleteSelection()
        {
            var item = DeletableSelection();
            if (item == null)
                return false;

            _session.DeleteItem(item.Id, this);
            return true;
        }

        /// <summary>Cuts the selected clip's span out of its link group and closes the gap on
        /// <b>all</b> tracks (<see cref="EditorSession.RippleDeleteItem"/>) — the "remove this
        /// stretch of the recording" gesture, from the context menu's Ripple Delete. An unlinked
        /// item is a group of one: it is removed and everything at or after it slides left.
        /// Returns false when nothing deletable is selected.</summary>
        public bool RippleDeleteSelection()
        {
            var item = DeletableSelection();
            if (item == null)
                return false;

            _session.RippleDeleteItem(item.Id, this);
            return true;
        }

        /// <summary>The primary selected item when a delete may act on it, else null. A drag in
        /// progress owns the model (a gesture is open): a delete would ride the gesture as an
        /// un-undoable preview and be resurrected by the drag's next move.</summary>
        private Item DeletableSelection()
        {
            if (_session == null || _session.IsGestureActive)
                return null;

            var item = _session.PrimarySelectedItem;
            if (item == null)
                return null;

            var track = _session.Project.Tracks.FirstOrDefault(t => t.Id == item.TrackId);
            return track is { Locked: false } ? item : null;
        }

        /// <summary>Splits every row that covers the playhead — see
        /// <see cref="EditorSession.SplitAtPlayhead"/>. The window's Ctrl+K. Returns true when
        /// anything split.</summary>
        public bool SplitAtPlayhead()
        {
            if (_session == null)
                return false;

            return _session.SplitAtPlayhead(Math.Clamp(Position.Ticks, 0, _session.DurationTicks), this);
        }

        /// <summary>Zooms out until the whole project fits and returns to the origin.</summary>
        public void ZoomToFit() => _viewport.ZoomToFit();

        /// <summary>Back to the zoom the editor opens at (see
        /// <see cref="TimelineViewMath.DefaultTicksPerPixel"/>), keeping the left edge.</summary>
        public void ResetZoom() => _viewport.ResetZoom();

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
                // unlink/relink are Mapping and come from the inspector's unlink button — the
                // headers' link badges re-read without a rebuild.
                _headers.RefreshLinkBadges();
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
            // what the gesture means is decided by TimelineScrollInput (pure, and unit-tested for
            // both platforms); this method only carries it out.
            var decision = TimelineScrollInput.DecideWheel(e.Delta.X, e.Delta.Y,
                ToScrollModifiers(e.KeyModifiers), OperatingSystem.IsMacOS());

            switch (decision.Action)
            {
                case TimelineScrollAction.Zoom:
                    // The anchor x is measured in the surface's space so ruler and rows zoom
                    // identically; a wheel over the header column or the vertical scroll bar lands
                    // outside [0, width] and the viewport clamps it to the nearest surface edge
                    // (zooming around an off-screen tick would pan, not zoom).
                    _viewport.SetZoomAnchored(_viewport.TicksPerPixel * decision.ZoomFactor,
                        e.GetPosition(_surface).X);
                    e.Handled = true;
                    break;

                case TimelineScrollAction.PanHorizontal:
                    _viewport.ScrollByPixels(decision.PanPixels);
                    e.Handled = true;
                    break;

                // ScrollRows and None are left unhandled on purpose: the tunnel handler declining
                // the event is exactly how the inner ScrollViewer gets to see it.
            }
        }

        /// <summary>
        /// Trackpad pinch, macOS only (an X11/Win32 trackpad never raises this). Bubbling, not
        /// tunnelling: Avalonia registers the touch-pad gesture events with
        /// <c>RoutingStrategies.Bubble</c> alone, so a tunnel handler for it would never run —
        /// harmless here because nothing inside the timeline handles a magnify.
        ///
        /// This is the one gesture Avalonia's <c>PinchGestureRecognizer</c> does <i>not</i> cover on
        /// a Mac: that recognizer builds a pinch out of two touch/pen contacts, and a macOS trackpad
        /// delivers no contacts to the app at all — AppKit resolves the fingers itself and sends a
        /// single <c>magnifyWithEvent:</c>, which is what lands here.
        /// </summary>
        private void OnTouchPadMagnify(object sender, PointerDeltaEventArgs e)
        {
            var factor = TimelineScrollInput.ZoomFactorForMagnification(e.Delta.Y);
            if (factor == 1)
                return;

            _viewport.SetZoomAnchored(_viewport.TicksPerPixel * factor, e.GetPosition(_surface).X);
            e.Handled = true;
        }

        /// <summary>Avalonia's modifier flags as the pure decoder's own — spelled out rather than
        /// cast, so the decoder cannot silently disagree with Avalonia if either enum is ever
        /// renumbered.</summary>
        private static TimelineScrollModifiers ToScrollModifiers(KeyModifiers modifiers)
        {
            var result = TimelineScrollModifiers.None;
            if (modifiers.HasFlag(KeyModifiers.Alt)) result |= TimelineScrollModifiers.Alt;
            if (modifiers.HasFlag(KeyModifiers.Control)) result |= TimelineScrollModifiers.Control;
            if (modifiers.HasFlag(KeyModifiers.Shift)) result |= TimelineScrollModifiers.Shift;
            if (modifiers.HasFlag(KeyModifiers.Meta)) result |= TimelineScrollModifiers.Meta;
            return result;
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
            // full-weight text color, not LabelBrush: at the muted label weight these read as
            // disabled buttons rather than as controls.
            _snap.Foreground = palette.RulerLabelBrush;
            _split.Foreground = palette.RulerLabelBrush;
            _zoomToFit.Foreground = palette.RulerLabelBrush;
            _resetZoom.Foreground = palette.RulerLabelBrush;
            _spacer.Background = palette.RulerBackground;
            _scrollHost.Background = palette.SurfaceBackground;
        }
    }
}
