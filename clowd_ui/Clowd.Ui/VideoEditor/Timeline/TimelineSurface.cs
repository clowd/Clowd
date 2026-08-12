using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>
    /// The custom-drawn heart of the timeline: rows, items (filmstrips, waveforms, text/image
    /// cards), transition ramps, selection, the snap guide and the playhead line — plus the whole
    /// drag machine (scrub / move / trim, pointer-captured like the single-track control and the
    /// ColorSlider before it). Every edit goes through the <see cref="EditorSession"/> as a
    /// gesture-wrapped incremental mutation; the surface re-reads <c>Session.Project</c> on every
    /// change and never keeps an <see cref="Item"/> reference across one (undo replaces the Project
    /// instance — items are always re-resolved by id).
    /// </summary>
    internal sealed class TimelineSurface : Control
    {
        private const double ItemPadY = 2;          // vertical inset of item bodies within their row
        private const double ItemCornerRadius = 3;
        private const double TrimGripWidth = 3;
        private const double GlyphSize = 12;
        private const double OffscreenSlackPx = 50; // keep rects for items just off screen so a scrolled-out edge still hit-tests honestly

        /// <summary>Caps the cached waveform geometry per item; past this a bucket covers more
        /// than a pixel, which only happens when a single item spans thousands of on-screen pixels
        /// and is invisible at that width anyway.</summary>
        private const int MaxWaveformBuckets = 8192;

        private enum DragMode
        {
            None,
            Scrub,
            MoveItem,
            TrimStart,
            TrimEnd,
        }

        private readonly TimelineViewport _viewport;
        private readonly Dictionary<Guid, WaveformCache> _waveforms = new Dictionary<Guid, WaveformCache>();

        private EditorSession _session;
        private ITimelinePreviewProvider _previewProvider = NullTimelinePreviewProvider.Instance;
        private IReadOnlyList<TimelineRow> _rows = Array.Empty<TimelineRow>();
        private long _positionTicks;

        private DragMode _dragMode;
        private EditGesture _gesture;
        private Guid _dragItemId;
        private long _grabOffsetTicks;   // pointer ticks minus the dragged start/edge at press time
        private IPointer _dragPointer;
        private long? _snapGuideTicks;
        private Guid _hoverItemId;

        private DispatcherTimer _previewThrottle;
        private Cursor _cursorResize;
        private Cursor _cursorHand;
        private Guid _contextItemId; // the item the pending right-click landed on
        private long _contextTicks;  // …and where along the timeline it landed

        public event EventHandler ScrubStarted;

        public event EventHandler<long> Scrubbed;

        public event EventHandler<long> ScrubCompleted;

        public TimelineSurface(TimelineViewport viewport)
        {
            _viewport = viewport;
            _viewport.Changed += (_, _) => InvalidateVisual();
            _previewProvider.PreviewReady += OnPreviewReady;
            ActualThemeVariantChanged += (_, _) => InvalidateVisual();
            ClipToBounds = true;
            Focusable = true; // Esc-to-cancel needs key events mid-drag

            var menu = new ContextMenu();
            menu.Opening += ContextMenu_Opening;
            ContextMenu = menu;
        }

        /// <summary>Runs the parent control's <c>DeleteSelection</c> — the context menu must not
        /// carry a second copy of the ripple/group/lock rules the Delete key follows.</summary>
        public Func<bool> DeleteSelection { get; set; }

        // ------------------------------------------------------------------------------- wiring

        /// <summary>The editing session. The parent control owns the event subscriptions and calls
        /// <see cref="RebuildRows"/>/<c>InvalidateVisual</c>; the surface only reads.</summary>
        public EditorSession Session
        {
            get => _session;
            set
            {
                if (ReferenceEquals(_session, value))
                    return;

                if (_dragMode != DragMode.None)
                    CancelDrag();

                _session = value;
                _hoverItemId = Guid.Empty;
                _waveforms.Clear();
                RebuildRows();
            }
        }

        /// <summary>Where filmstrips and waveforms come from. Never null — defaults to
        /// <see cref="NullTimelinePreviewProvider.Instance"/>.</summary>
        public ITimelinePreviewProvider PreviewProvider
        {
            get => _previewProvider;
            set
            {
                value ??= NullTimelinePreviewProvider.Instance;
                if (ReferenceEquals(_previewProvider, value))
                    return;

                _previewProvider.PreviewReady -= OnPreviewReady;
                _previewProvider = value;
                _previewProvider.PreviewReady += OnPreviewReady;
                _waveforms.Clear();
                InvalidateVisual();
            }
        }

        /// <summary>Playhead position in timeline ticks; pushed by the parent control.</summary>
        public long PositionTicks
        {
            get => _positionTicks;
            set
            {
                if (_positionTicks == value)
                    return;

                _positionTicks = value;
                InvalidateVisual();
            }
        }

        /// <summary>Rebuilds the row layout from the live project — the parent calls this on
        /// Structural changes (and session swaps). Also prunes caches keyed by item ids that no
        /// longer exist.</summary>
        public void RebuildRows()
        {
            var project = _session?.Project;
            _rows = project == null ? Array.Empty<TimelineRow>() : TimelineRowLayout.Build(project);

            if (project == null)
            {
                _waveforms.Clear();
            }
            else if (_waveforms.Count > 0)
            {
                var live = project.Items.Select(i => i.Id).ToHashSet();
                foreach (var stale in _waveforms.Keys.Where(id => !live.Contains(id)).ToList())
                    _waveforms.Remove(stale);
            }

            InvalidateMeasure();
            InvalidateVisual();
        }

        /// <summary>PreviewReady arrives in bursts while the decoders catch up; one repaint every
        /// ~100ms is plenty for thumbnails appearing.</summary>
        private void OnPreviewReady(object sender, EventArgs e)
        {
            _previewThrottle ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
            {
                _previewThrottle.Stop();
                InvalidateVisual();
            });

            if (!_previewThrottle.IsEnabled)
                _previewThrottle.Start();
        }

        // ------------------------------------------------------------------------------- layout

        protected override Size MeasureOverride(Size availableSize) =>
            new Size(0, TimelineRowLayout.TotalHeight(_rows));

        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = base.ArrangeOverride(finalSize);
            // the surface is the one child whose width IS the drawing viewport (the ruler shares
            // its grid column), so this is where the shared viewport learns it.
            _viewport.SetViewportWidth(finalSize.Width);
            return size;
        }

        // -------------------------------------------------------------------------- interaction

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            var properties = e.GetCurrentPoint(this).Properties;
            if (properties.IsRightButtonPressed)
            {
                // the press decides what the menu (opened from the release that follows) acts on.
                PrepareContextMenu(e.GetPosition(this));
                return;
            }

            if (!properties.IsLeftButtonPressed || _session == null || _dragMode != DragMode.None)
                return;

            Focus();

            var pos = e.GetPosition(this);
            var hit = HitTestAt(pos);

            switch (hit.Kind)
            {
                case TimelineHitKind.Empty:
                case TimelineHitKind.Ruler: // unreachable (the ruler is a sibling), kept for the shared hit enum
                case TimelineHitKind.Playhead:
                    if (hit.Kind == TimelineHitKind.Empty)
                        _session.ClearSelection();

                    _dragMode = DragMode.Scrub;
                    _dragPointer = e.Pointer;
                    e.Pointer.Capture(this);
                    ScrubStarted?.Invoke(this, EventArgs.Empty);
                    Scrubbed?.Invoke(this, _viewport.XToTicksClamped(pos.X));
                    break;

                case TimelineHitKind.ItemBody:
                {
                    var item = FindItem(hit.ItemId);
                    if (item == null)
                        return;

                    _session.Select(item.Id);

                    // moving is the one edit recording sync forbids: a recording segment's (or a
                    // locked row's) body gets selection only — the missing drag affordance IS the
                    // sync cue, together with the header's link toggle. An import's group is just
                    // "streams of one file": dragging any member moves the whole group
                    // (TimelineOps.Move is group-scoped), which cannot desync anything.
                    var track = FindTrack(item.TrackId);
                    if (_session.IsRippleGroup(item.Id) || track is not { Locked: false })
                        return;

                    BeginDrag(DragMode.MoveItem, e, item.Id,
                        _viewport.XToTicks(pos.X) - item.TimelineStartTicks, "Move");
                    break;
                }

                case TimelineHitKind.ItemStart:
                case TimelineHitKind.ItemEnd:
                {
                    var item = FindItem(hit.ItemId);
                    if (item == null)
                        return;

                    _session.Select(item.Id);

                    // per-item trim is sync-safe, so synced items allow it; locked tracks do not.
                    var track = FindTrack(item.TrackId);
                    if (track is not { Locked: false })
                        return;

                    var isStart = hit.Kind == TimelineHitKind.ItemStart;
                    var edge = isStart ? item.TimelineStartTicks : item.TimelineEndTicks;
                    BeginDrag(isStart ? DragMode.TrimStart : DragMode.TrimEnd, e, item.Id,
                        _viewport.XToTicks(pos.X) - edge, "Trim");
                    break;
                }
            }
        }

        private void BeginDrag(DragMode mode, PointerPressedEventArgs e, Guid itemId, long grabOffsetTicks, string label)
        {
            // a second pointer (touch/pen) can land here while another control's drag (the
            // preview gizmo) owns the session; gestures do not nest, so this press stays
            // selection-only instead of throwing out of a pointer handler.
            if (_session.IsGestureActive)
                return;

            _gesture = _session.BeginGesture(label, this);
            _dragMode = mode;
            _dragItemId = itemId;
            _grabOffsetTicks = grabOffsetTicks;
            _dragPointer = e.Pointer;
            e.Pointer.Capture(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            var pos = e.GetPosition(this);
            if (_dragMode == DragMode.None || !Equals(e.Pointer.Captured, this))
            {
                UpdateHover(pos);
                return;
            }

            switch (_dragMode)
            {
                case DragMode.Scrub:
                    Scrubbed?.Invoke(this, _viewport.XToTicksClamped(pos.X));
                    break;
                case DragMode.MoveItem:
                    ApplyMove(pos.X, e.KeyModifiers);
                    break;
                case DragMode.TrimStart:
                case DragMode.TrimEnd:
                    ApplyTrim(pos.X, e.KeyModifiers);
                    break;
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (_dragMode == DragMode.None || !Equals(e.Pointer.Captured, this))
                return;

            var mode = _dragMode;
            var ticks = _viewport.XToTicksClamped(e.GetPosition(this).X);
            FinishDrag(commit: true); // before Capture(null): it re-enters OnPointerCaptureLost
            e.Pointer.Capture(null);

            if (mode == DragMode.Scrub)
                ScrubCompleted?.Invoke(this, ticks);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);

            if (_dragMode == DragMode.None)
                return;

            // losing capture without a release is an abort: restore the pre-drag state.
            var mode = _dragMode;
            FinishDrag(commit: false);

            if (mode == DragMode.Scrub)
                ScrubCompleted?.Invoke(this, Math.Clamp(_positionTicks, 0, Math.Max(0, _viewport.DurationTicks)));
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);

            if (_hoverItemId != Guid.Empty)
            {
                _hoverItemId = Guid.Empty;
                InvalidateVisual();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Key == Key.Escape &&
                _dragMode is DragMode.MoveItem or DragMode.TrimStart or DragMode.TrimEnd)
            {
                CancelDrag();
                e.Handled = true;
            }
        }

        /// <summary>Ends the drag. Commit pushes one undo entry for the whole gesture (none when
        /// nothing net-changed — a no-op drag costs nothing); cancel restores the pre-drag
        /// project.</summary>
        private void FinishDrag(bool commit)
        {
            var gesture = _gesture;
            _gesture = null;
            _dragMode = DragMode.None;
            _dragPointer = null;
            SetSnapGuide(null);

            if (gesture != null)
            {
                if (commit)
                    gesture.Commit();
                else
                    gesture.Cancel();
            }

            // a no-op commit (dragged out and back) raises no ProjectChanged, but the duration may
            // have grown mid-gesture and the viewport kept the growth (shrinks are deferred while a
            // gesture is open) — re-sync so the zoom/scroll limits match the real project again.
            if (_session != null)
                _viewport.SetDuration(_session.DurationTicks);

            InvalidateVisual();
        }

        private void CancelDrag()
        {
            var pointer = _dragPointer;
            FinishDrag(commit: false); // clears state first so the capture-lost re-entry is a no-op
            pointer?.Capture(null);
        }

        private void ApplyMove(double x, KeyModifiers modifiers)
        {
            var item = FindItem(_dragItemId);
            if (item == null)
            {
                CancelDrag();
                return;
            }

            var desired = Math.Max(0, _viewport.XToTicks(x) - _grabOffsetTicks);
            long? guide = null;

            if (!modifiers.HasFlag(KeyModifiers.Alt))
            {
                // both edges of the dragged item reach for targets; the nearer snap wins.
                var targets = BuildSnapTargets(item.Id);
                var tolerance = _viewport.ToleranceTicks;
                var snapStart = TimelineViewMath.Snap(desired, targets, tolerance);
                var snapEnd = TimelineViewMath.Snap(desired + item.DurationTicks, targets, tolerance);

                var startDistance = snapStart is long s ? Math.Abs(s - desired) : long.MaxValue;
                var endDistance = snapEnd is long en ? Math.Abs(en - (desired + item.DurationTicks)) : long.MaxValue;

                if (snapStart != null && startDistance <= endDistance)
                {
                    desired = Math.Max(0, snapStart.Value);
                    guide = snapStart;
                }
                else if (snapEnd != null)
                {
                    desired = Math.Max(0, snapEnd.Value - item.DurationTicks);
                    guide = snapEnd;
                }
            }

            var delta = desired - item.TimelineStartTicks;
            if (delta != 0)
                _session.MoveItem(item.Id, delta, this);

            // the move may have been clamped or rolled back (an occupied span) — only show the
            // guide when an edge actually landed on it.
            item = FindItem(_dragItemId);
            if (guide != null && (item == null ||
                (item.TimelineStartTicks != guide && item.TimelineEndTicks != guide)))
                guide = null;
            SetSnapGuide(guide);
        }

        private void ApplyTrim(double x, KeyModifiers modifiers)
        {
            var item = FindItem(_dragItemId);
            if (item == null)
            {
                CancelDrag();
                return;
            }

            var desired = _viewport.XToTicks(x) - _grabOffsetTicks;
            long? guide = null;

            if (!modifiers.HasFlag(KeyModifiers.Alt))
            {
                var snapped = TimelineViewMath.Snap(desired, BuildSnapTargets(item.Id), _viewport.ToleranceTicks);
                if (snapped != null)
                {
                    desired = snapped.Value;
                    guide = snapped;
                }
            }

            if (_dragMode == DragMode.TrimStart)
            {
                var delta = desired - item.TimelineStartTicks;
                if (delta != 0)
                    _session.TrimItemStart(item.Id, delta, this);
            }
            else
            {
                var delta = desired - item.TimelineEndTicks;
                if (delta != 0)
                    _session.TrimItemEnd(item.Id, delta, this);
            }

            item = FindItem(_dragItemId);
            var edge = item == null ? -1
                : _dragMode == DragMode.TrimStart ? item.TimelineStartTicks : item.TimelineEndTicks;
            SetSnapGuide(guide != null && edge == guide ? guide : null);
        }

        /// <summary>Snap targets in tie-break order: the origin, the playhead, then every other
        /// item's edges (see <see cref="TimelineViewMath.Snap"/> — ties go to the earlier entry).</summary>
        private List<long> BuildSnapTargets(Guid excludeItemId)
        {
            var targets = new List<long>
            {
                0,
                Math.Clamp(_positionTicks, 0, Math.Max(0, _viewport.DurationTicks)),
            };

            var project = _session?.Project;
            if (project != null)
            {
                foreach (var other in project.Items)
                {
                    if (other.Id == excludeItemId)
                        continue;

                    targets.Add(other.TimelineStartTicks);
                    targets.Add(other.TimelineEndTicks);
                }
            }

            return targets;
        }

        private void SetSnapGuide(long? ticks)
        {
            if (_snapGuideTicks == ticks)
                return;

            _snapGuideTicks = ticks;
            InvalidateVisual();
        }

        private void UpdateHover(Point pos)
        {
            var hit = HitTestAt(pos);
            var hover = hit.Kind is TimelineHitKind.ItemBody or TimelineHitKind.ItemStart or TimelineHitKind.ItemEnd
                ? hit.ItemId
                : Guid.Empty;
            if (hover != _hoverItemId)
            {
                _hoverItemId = hover;
                InvalidateVisual();
            }

            switch (hit.Kind)
            {
                case TimelineHitKind.Playhead:
                    Cursor = _cursorResize ??= new Cursor(StandardCursorType.SizeWestEast);
                    break;

                case TimelineHitKind.ItemStart:
                case TimelineHitKind.ItemEnd:
                {
                    var track = TrackOfItem(hit.ItemId);
                    Cursor = track is { Locked: false }
                        ? (_cursorResize ??= new Cursor(StandardCursorType.SizeWestEast))
                        : null;
                    break;
                }

                case TimelineHitKind.ItemBody:
                {
                    var item = FindItem(hit.ItemId);
                    var track = item == null ? null : FindTrack(item.TrackId);
                    var movable = item != null && track is { Locked: false } &&
                                  _session?.IsRippleGroup(item.Id) != true;
                    // Arrow (not Hand) on a recording-synced or locked body: no move affordance
                    // IS the cue. Import groups move as one, so their bodies keep the Hand.
                    Cursor = movable ? (_cursorHand ??= new Cursor(StandardCursorType.Hand)) : null;
                    break;
                }

                default:
                    Cursor = null;
                    break;
            }
        }

        // ------------------------------------------------------------------------- context menu

        /// <summary>Right-press: selects the item under the pointer (the menu acts on the
        /// selection, so what it will do has to be visible before it opens) and remembers it for
        /// <see cref="ContextMenu_Opening"/>, which builds the entries from it. The instant the
        /// pointer was over is remembered too — <c>Opening</c> is handed a bare
        /// <see cref="CancelEventArgs"/> with no pointer in it, and this is the only place the
        /// position is known. Nothing can zoom or scroll between the press and the open, so the
        /// cached tick is still true when the menu reads it.</summary>
        private void PrepareContextMenu(Point pos)
        {
            _contextItemId = Guid.Empty;
            _contextTicks = 0;
            if (_session == null || _dragMode != DragMode.None || _session.IsGestureActive)
                return;

            var hit = HitTestAt(pos);
            if (hit.Kind is not (TimelineHitKind.ItemBody or TimelineHitKind.ItemStart or TimelineHitKind.ItemEnd))
                return;

            var item = FindItem(hit.ItemId);
            if (item == null)
                return;

            _contextItemId = item.Id;
            _contextTicks = _viewport.XToTicksClamped(pos.X);
            _session.Select(item.Id);
        }

        private void ContextMenu_Opening(object sender, CancelEventArgs e)
        {
            var menu = (ContextMenu)sender;
            var item = _contextItemId == Guid.Empty ? null : FindItem(_contextItemId);
            var track = item == null ? null : FindTrack(item.TrackId);
            if (item == null || track == null || _session.IsGestureActive)
            {
                // empty rows and the ruler gutter have nothing to offer — no menu at all beats an
                // all-disabled one.
                e.Cancel = true;
                return;
            }

            menu.Items.Clear();

            // Both cuts act on THIS clip alone — not its recording, not the rows beside it. The
            // pointer picked one clip out; cutting its neighbours too would be an edit nobody
            // asked for. The toolbar's "Split every track at playhead" is the other gesture.
            // Splitting is only meaningful strictly inside the span: on an edge it would make a
            // zero-length half, which TimelineOps refuses anyway.
            var itemId = item.Id;
            var playhead = Math.Clamp(_positionTicks, 0, Math.Max(0, _viewport.DurationTicks));
            menu.Items.Add(NewMenuItem("Split at Playhead",
                !track.Locked && playhead > item.TimelineStartTicks && playhead < item.TimelineEndTicks,
                () => _session.SplitItemAt(itemId, playhead, this)));

            // …and the same cut where the user actually right-clicked, which is usually the frame
            // they were looking at.
            var cursorTicks = _contextTicks;
            menu.Items.Add(NewMenuItem("Split at Cursor",
                !track.Locked && cursorTicks > item.TimelineStartTicks && cursorTicks < item.TimelineEndTicks,
                () => _session.SplitItemAt(itemId, cursorTicks, this)));

            menu.Items.Add(NewMenuItem("Delete", !track.Locked, () => DeleteSelection?.Invoke()));

            var trackId = track.Id;

            // Z order. Row-level like Unlink below — the whole row moves, because stacking is a
            // property of the row and not of one clip on it. The timeline draws video rows highest
            // layer first, so moving up the panel and moving towards the viewer are the same
            // direction (see TimelineRowLayout.Build).
            if (track.Kind != TrackKind.Audio)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(NewMenuItem("Move Up",
                    !track.Locked && _session.CanMoveTrackLayer(trackId, towardsFront: true),
                    () => _session.MoveTrackLayer(trackId, towardsFront: true, this)));
                menu.Items.Add(NewMenuItem("Move Down",
                    !track.Locked && _session.CanMoveTrackLayer(trackId, towardsFront: false),
                    () => _session.MoveTrackLayer(trackId, towardsFront: false, this)));
            }

            // unlinking is a row-level action (it is the header's link toggle), offered here
            // because that toggle is easy to miss and this is where the sync is felt: a synced
            // item has no move affordance.
            if (_session.Project.Items.Any(i => i.TrackId == trackId && i.LinkGroupId != null))
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(NewMenuItem("Unlink Row", true, () => _session.UnlinkTrack(trackId, this)));
            }
        }

        private static MenuItem NewMenuItem(string header, bool enabled, Action execute)
        {
            var menuItem = new MenuItem { Header = header, IsEnabled = enabled };
            menuItem.Click += (_, _) => execute();
            return menuItem;
        }

        private TimelineHit HitTestAt(Point pos)
        {
            var duration = _viewport.DurationTicks;
            var playheadX = duration > 0
                ? _viewport.TickToX(Math.Clamp(_positionTicks, 0, duration))
                : Double.NaN; // NaN never matches — the old control's idiom for "not drawn"
            return TimelineHitTester.HitTest(pos.X, pos.Y, playheadX, 0, ComputeItemRects());
        }

        private List<TimelineItemRect> ComputeItemRects()
        {
            var rects = new List<TimelineItemRect>();
            var project = _session?.Project;
            if (project == null)
                return rects;

            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                foreach (var item in project.Items)
                {
                    if (item.TrackId != row.TrackId)
                        continue;

                    var x = _viewport.TickToX(item.TimelineStartTicks);
                    var w = Math.Max(1, item.DurationTicks / _viewport.TicksPerPixel);
                    if (x > Bounds.Width + OffscreenSlackPx || x + w < -OffscreenSlackPx)
                        continue;

                    rects.Add(new TimelineItemRect(item.Id, i, x, w, row.Top, row.Height));
                }
            }

            return rects;
        }

        private Item FindItem(Guid id) => _session?.Project.Items.FirstOrDefault(i => i.Id == id);

        private Track FindTrack(Guid trackId) => _session?.Project.Tracks.FirstOrDefault(t => t.Id == trackId);

        private Track TrackOfItem(Guid itemId)
        {
            var item = FindItem(itemId);
            return item == null ? null : FindTrack(item.TrackId);
        }

        // --------------------------------------------------------------------------- rendering

        public override void Render(DrawingContext context)
        {
            var palette = TimelinePalette.ForVariant(ActualThemeVariant);
            context.FillRectangle(palette.SurfaceBackground, new Rect(Bounds.Size));

            var project = _session?.Project;
            if (project == null)
                return;

            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                context.FillRectangle(i % 2 == 0 ? palette.RowBackground : palette.RowBackgroundAlt,
                    new Rect(0, row.Top, Bounds.Width, row.Height));
                context.DrawLine(palette.RowSeparatorPen,
                    new Point(0, row.Bottom - 0.5), new Point(Bounds.Width, row.Bottom - 0.5));
            }

            var selection = _session.SelectedItemIds;
            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                var track = FindTrack(row.TrackId);
                if (track == null)
                    continue;

                foreach (var item in project.Items)
                {
                    if (item.TrackId != row.TrackId)
                        continue;

                    var x = _viewport.TickToX(item.TimelineStartTicks);
                    var w = Math.Max(1, item.DurationTicks / _viewport.TicksPerPixel);
                    if (x > Bounds.Width + OffscreenSlackPx || x + w < -OffscreenSlackPx)
                        continue;

                    var body = new Rect(x, row.Top + ItemPadY, w, Math.Max(1, row.Height - ItemPadY * 2));
                    RenderItem(context, palette, project, track, row, item, body, selection.Contains(item.Id));
                }
            }

            if (_snapGuideTicks is long guide)
            {
                var gx = _viewport.TickToX(guide);
                context.DrawLine(palette.SnapGuidePen, new Point(gx, 0), new Point(gx, Bounds.Height));
            }

            // playhead line — the ruler above owns the triangle.
            var duration = _viewport.DurationTicks;
            if (duration > 0)
            {
                var px = _viewport.TickToX(Math.Clamp(_positionTicks, 0, duration));
                if (px >= -1 && px <= Bounds.Width + 1)
                    context.DrawLine(palette.PlayheadPen, new Point(px, 0), new Point(px, Bounds.Height));
            }
        }

        private void RenderItem(DrawingContext context, TimelinePalette palette, Project project,
            Track track, TimelineRow row, Item item, Rect body, bool selected)
        {
            context.DrawRectangle(palette.ItemFill(row.Kind), palette.ItemBorderPen, body,
                ItemCornerRadius, ItemCornerRadius);

            switch (item.Content)
            {
                case MediaContent media when row.Kind == TimelineRowKind.Audio:
                    RenderWaveform(context, palette, item, media, body);
                    break;
                case MediaContent media:
                    RenderFilmstrip(context, palette, project, item, media, body);
                    break;
                case TextContent text:
                    RenderGlyphLabel(context, palette, body, TimelineIcons.Find("IconToolText"), text.Text);
                    break;
                case ImageContent image:
                    RenderGlyphLabel(context, palette, body, TimelineIcons.Find("IconPhoto"),
                        System.IO.Path.GetFileName(image.Path));
                    break;
            }

            RenderTransitions(context, palette, item, body);

            var dimmed = row.Kind == TimelineRowKind.Audio ? track.Muted : track.Hidden;
            if (dimmed)
            {
                context.DrawRectangle(palette.DimFill, null, body, ItemCornerRadius, ItemCornerRadius);
                RenderHatch(context, palette.HatchPen, body);
            }

            if (track.Locked && body.Width > GlyphSize * 2)
            {
                DrawGlyph(context, TimelineIcons.Find("IconLock"), palette.ItemLabelBrush,
                    new Point(body.Right - GlyphSize - 4, body.Y + 2), GlyphSize * 0.85);
            }

            var hovered = _hoverItemId == item.Id;
            if (hovered && !selected)
                context.DrawRectangle(palette.HoverOverlay, null, body, ItemCornerRadius, ItemCornerRadius);

            if (selected)
                context.DrawRectangle(null, palette.SelectionPen, body.Deflate(0.75), ItemCornerRadius, ItemCornerRadius);

            if ((selected || hovered) && body.Width >= TimelineHitTester.MinEdgeGrabWidth)
            {
                var gripHeight = body.Height * 0.5;
                var gy = body.Y + (body.Height - gripHeight) / 2;
                context.DrawRectangle(palette.TrimGripBrush, null,
                    new Rect(body.X + 1.5, gy, TrimGripWidth, gripHeight), 1, 1);
                context.DrawRectangle(palette.TrimGripBrush, null,
                    new Rect(body.Right - TrimGripWidth - 1.5, gy, TrimGripWidth, gripHeight), 1, 1);
            }
        }

        private void RenderFilmstrip(DrawingContext context, TimelinePalette palette, Project project,
            Item item, MediaContent media, Rect body)
        {
            var source = project.Sources.FirstOrDefault(s => s.Id == media.SourceId);
            if (source == null)
                return;

            var stream = source.Streams?.FirstOrDefault(s => s.Index == media.StreamIndex);
            var aspect = stream is { Width: > 0, Height: > 0 }
                ? (double)stream.Width / stream.Height
                : 16.0 / 9;

            var tpp = _viewport.TicksPerPixel;
            var naturalSlotPx = Math.Max(8, body.Height * aspect);
            var strip = _previewProvider.GetThumbnails(new ThumbnailRequest(media.SourceId, media.StreamIndex,
                media.SourceInTicks, item.DurationTicks, (long)(naturalSlotPx * tpp), (int)Math.Round(body.Height)));

            var interval = Math.Max(1, strip.IntervalTicks);
            var slotWidth = interval / tpp;
            if (slotWidth <= 0.5)
                return;

            var thumbs = strip.Thumbnails;
            if (thumbs.Count == 0)
                return; // missing thumbnails leave the body fill visible

            using (context.PushClip(new RoundedRect(body, ItemCornerRadius)))
            {
                // slots sit on the interval grid anchored at source time 0 — the same anchoring
                // the provider quantizes to, so a zoom change reuses decoded thumbnails.
                var visStartX = Math.Max(body.X, 0);
                var visEndX = Math.Min(body.Right, Bounds.Width);
                var firstSlot = Math.Max(0, (media.SourceInTicks + (long)((visStartX - body.X) * tpp)) / interval);

                for (var n = firstSlot; ; n++)
                {
                    var slotSource = n * interval;
                    var x = body.X + (slotSource - media.SourceInTicks) / tpp;
                    if (x >= visEndX)
                        break;

                    var dest = new Rect(x, body.Y, slotWidth, body.Height);
                    // target the slot instant itself — the cell's left edge maps to slotSource, and
                    // refined thumbs are published AT the slot ("the frame covering it"); targeting
                    // the cell midpoint would tie exactly between two grid thumbs and systematically
                    // draw every cell one interval late.
                    if (NearestThumb(thumbs, slotSource, interval * 2) is { Image: { } bitmap })
                    {
                        // centered crop: a quantized interval makes the slot narrower than the
                        // thumb's natural width, and squeezing would distort every frame.
                        var size = bitmap.Size;
                        var srcWidth = Math.Min(size.Width, size.Height * (slotWidth / body.Height));
                        context.DrawImage(bitmap, new Rect((size.Width - srcWidth) / 2, 0, srcWidth, size.Height), dest);
                    }
                    else
                    {
                        context.FillRectangle(palette.FilmstripPlaceholderFill, dest.Deflate(0.5));
                    }
                }
            }
        }

        private static TimelineThumbnail? NearestThumb(IReadOnlyList<TimelineThumbnail> thumbs,
            long sourceTicks, long maxDistance)
        {
            if (thumbs.Count == 0)
                return null;

            int lo = 0, hi = thumbs.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (thumbs[mid].SourceTicks < sourceTicks)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            var best = thumbs[lo];
            if (lo > 0 && Math.Abs(thumbs[lo - 1].SourceTicks - sourceTicks) < Math.Abs(best.SourceTicks - sourceTicks))
                best = thumbs[lo - 1];

            return Math.Abs(best.SourceTicks - sourceTicks) <= maxDistance ? best : null;
        }

        private void RenderWaveform(DrawingContext context, TimelinePalette palette, Item item,
            MediaContent media, Rect body)
        {
            var tpp = _viewport.TicksPerPixel;
            var perBucket = Math.Max((long)Math.Round(tpp), Math.Max(1, item.DurationTicks / MaxWaveformBuckets));
            var bucketCount = (int)Math.Min((item.DurationTicks + perBucket - 1) / perBucket, Int32.MaxValue / 2);
            if (bucketCount <= 0)
                return;

            // cached per item, keyed (tpp, sourceIn, duration, bucketCount): scrolling never
            // rebuilds (the geometry is drawn under a translate), zooming and trimming do. An
            // incomplete waveform rebuilds on each (throttled) repaint until the provider is done.
            if (!_waveforms.TryGetValue(item.Id, out var cache) ||
                !cache.Matches(tpp, media.SourceInTicks, item.DurationTicks, bucketCount) ||
                !cache.Complete)
            {
                var peaks = _previewProvider.GetAudioPeaks(new AudioPeaksRequest(media.SourceId,
                    media.StreamIndex, media.SourceInTicks, item.DurationTicks, perBucket));
                cache = WaveformCache.Build(peaks, tpp, media.SourceInTicks, item.DurationTicks,
                    bucketCount, perBucket, body.Height);
                _waveforms[item.Id] = cache;
            }

            if (cache.Geometry == null)
                return;

            using (context.PushClip(new RoundedRect(body, ItemCornerRadius)))
            using (context.PushTransform(Matrix.CreateTranslation(body.X, body.Y)))
                context.DrawGeometry(palette.WaveformBrush, null, cache.Geometry);
        }

        private void RenderGlyphLabel(DrawingContext context, TimelinePalette palette, Rect body,
            Geometry glyph, string label)
        {
            using (context.PushClip(new RoundedRect(body, ItemCornerRadius)))
            {
                var x = body.X + 6;
                if (glyph != null)
                {
                    DrawGlyph(context, glyph, palette.ItemLabelBrush,
                        new Point(x, body.Center.Y - GlyphSize / 2), GlyphSize);
                    x += GlyphSize + 5;
                }

                if (String.IsNullOrEmpty(label))
                    return;

                var maxWidth = body.Right - 4 - x;
                if (maxWidth <= 8)
                    return;

                var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default), 11, palette.ItemLabelBrush)
                {
                    MaxTextWidth = maxWidth,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                context.DrawText(text, new Point(x, body.Center.Y - text.Height / 2));
            }
        }

        private void RenderTransitions(DrawingContext context, TimelinePalette palette, Item item, Rect body)
        {
            if (item.Entry is { Kind: not TransitionKind.None } entry)
                RenderRamp(context, palette, body, Math.Min(entry.DurationTicks, item.DurationTicks), isEntry: true);
            if (item.Exit is { Kind: not TransitionKind.None } exit)
                RenderRamp(context, palette, body, Math.Min(exit.DurationTicks, item.DurationTicks), isEntry: false);
        }

        private void RenderRamp(DrawingContext context, TimelinePalette palette, Rect body,
            long durationTicks, bool isEntry)
        {
            var w = Math.Min(body.Width, durationTicks / _viewport.TicksPerPixel);
            if (w < 2)
                return;

            // the translucent triangle covers what the transition attenuates; its hypotenuse rises
            // from silence/blank at the item edge to full at the ramp's end.
            Point top, corner, bottom;
            if (isEntry)
            {
                corner = new Point(body.X, body.Y);
                top = new Point(body.X + w, body.Y);
                bottom = new Point(body.X, body.Bottom);
            }
            else
            {
                corner = new Point(body.Right, body.Y);
                top = new Point(body.Right - w, body.Y);
                bottom = new Point(body.Right, body.Bottom);
            }

            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                gc.BeginFigure(corner, true);
                gc.LineTo(top);
                gc.LineTo(bottom);
                gc.EndFigure(true);
            }

            using (context.PushClip(new RoundedRect(body, ItemCornerRadius)))
            {
                context.DrawGeometry(palette.TransitionFill, null, geo);
                context.DrawLine(palette.TransitionEdgePen, top, bottom);
            }
        }

        private static void RenderHatch(DrawingContext context, Pen pen, Rect rect)
        {
            using (context.PushClip(rect))
            {
                const double spacing = 6;
                for (var x = rect.X - rect.Height; x < rect.Right; x += spacing)
                    context.DrawLine(pen, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Y));
            }
        }

        private static void DrawGlyph(DrawingContext context, Geometry glyph, IBrush brush, Point topLeft, double size)
        {
            if (glyph == null)
                return;

            var bounds = glyph.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            var scale = size / Math.Max(bounds.Width, bounds.Height);
            var matrix = Matrix.CreateTranslation(-bounds.X, -bounds.Y)
                * Matrix.CreateScale(scale, scale)
                * Matrix.CreateTranslation(
                    topLeft.X + (size - bounds.Width * scale) / 2,
                    topLeft.Y + (size - bounds.Height * scale) / 2);

            using (context.PushTransform(matrix))
                context.DrawGeometry(brush, null, glyph);
        }

        /// <summary>One item's cached waveform geometry, in item-local coordinates (0,0 = the
        /// item body's top-left) so scrolling only moves a translate.</summary>
        private sealed class WaveformCache
        {
            public double Tpp;
            public long SourceIn;
            public long Duration;
            public int BucketCount;
            public bool Complete;
            public StreamGeometry Geometry;

            public bool Matches(double tpp, long sourceIn, long duration, int bucketCount) =>
                Tpp == tpp && SourceIn == sourceIn && Duration == duration && BucketCount == bucketCount;

            public static WaveformCache Build(AudioPeaks peaks, double tpp, long sourceIn,
                long duration, int bucketCount, long perBucket, double height)
            {
                var mid = height / 2;
                var half = Math.Max(1, mid - 1);
                var bucketPx = perBucket / tpp;

                // min/max are floats in [-1,1]; ±0.5px floor keeps silence visible as a hairline.
                var geometry = new StreamGeometry();
                using (var gc = geometry.Open())
                {
                    gc.BeginFigure(new Point(0, mid - 0.5), true);
                    for (var i = 0; i < bucketCount; i++)
                    {
                        peaks.TryGetBucket(i, out _, out var max);
                        gc.LineTo(new Point(i * bucketPx, mid - Math.Max(0.5, max * half)));
                    }

                    for (var i = bucketCount - 1; i >= 0; i--)
                    {
                        peaks.TryGetBucket(i, out var min, out _);
                        gc.LineTo(new Point(i * bucketPx, mid - Math.Min(-0.5, min * half)));
                    }

                    gc.EndFigure(true);
                }

                return new WaveformCache
                {
                    Tpp = tpp,
                    SourceIn = sourceIn,
                    Duration = duration,
                    BucketCount = bucketCount,
                    Complete = peaks.IsComplete,
                    Geometry = geometry,
                };
            }
        }
    }
}
