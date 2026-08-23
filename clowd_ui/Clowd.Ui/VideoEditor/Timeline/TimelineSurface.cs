using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Clowd.Drawing;
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
        private const double ItemCornerRadius = 6;
        private const double TrimHandleWidth = 9;   // full-height slab at each edge, drawn inside the body's rounded clip
        private const double GlyphSize = 12;
        private const double OffscreenSlackPx = 50; // keep rects for items just off screen so a scrolled-out edge still hit-tests honestly
        private const double EdgeFadeWidth = 24;    // fade-to-row-background over a viewport-cut item end (see FadeEdgeScrollViewer)
        private const double JumpGlyphSize = 10;    // the chevron inside the fade that jumps to the cut-off end
        private const double AiChipSize = 15;       // the AI badge's chip (the star inside it is smaller still)
        private const double AiStarSize = 10;

        /// <summary>The AI badge's tip. One line covering every AI-backed feature an item can
        /// carry (the speech enhancer, background blur/removal): naming the specific one would
        /// have to be rewritten every time the set grows, and the properties panel is one click
        /// away with the real answer.</summary>
        private const string AiFeaturesTip = "This track has AI features enabled";

        /// <summary>Caps the cached waveform geometry per item; past this a bucket covers more
        /// than a pixel, which only happens when a single item spans thousands of on-screen pixels
        /// and is invisible at that width anyway.</summary>
        private const int MaxWaveformBuckets = 8192;

        /// <summary>A click mark on the cursor row: a rounded bar this tall, centered on the
        /// body's midline (shorter on a body that cannot fit it, see RenderCursorActivity).</summary>
        private const double ClickMarkHeight = 10;

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
        private readonly Dictionary<Guid, CursorActivityCache> _cursorActivity = new Dictionary<Guid, CursorActivityCache>();
        private readonly Dictionary<Guid, KeyBlipCache> _keyBlips = new Dictionary<Guid, KeyBlipCache>();

        private EditorSession _session;
        private ITimelinePreviewProvider _previewProvider = NullTimelinePreviewProvider.Instance;
        private IReadOnlyList<TimelineRow> _rows = Array.Empty<TimelineRow>();
        private long _positionTicks;
        private long? _hoverTicks;

        private DragMode _dragMode;
        private EditGesture _gesture;
        private Guid _dragItemId;
        private long _grabOffsetTicks;   // pointer ticks minus the dragged start/edge at press time
        private IPointer _dragPointer;
        private long? _snapGuideTicks;
        private Guid _hoverItemId;

        /// <summary>The clickable edge-jump chevrons drawn this frame — rebuilt on every render,
        /// so a press/hover reads exactly what is on screen.</summary>
        private readonly List<EdgeJump> _edgeJumps = new List<EdgeJump>();

        /// <summary>Where this render put an AI badge, in surface coordinates — the surface is
        /// code-drawn, so a glyph can only carry a tooltip by hit-testing the rect the draw
        /// recorded (the same trick <see cref="_edgeJumps"/> plays for the chevrons).</summary>
        private readonly List<Rect> _aiBadges = new List<Rect>();

        /// <summary>Whether the tip is currently shown for an AI badge. The surface's Tip is set
        /// only while the pointer is on one — left standing it would pop up over any part of the
        /// timeline the pointer rested on.</summary>
        private bool _aiTipShown;
        private (Guid ItemId, bool Left)? _hoverJump;

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
        /// carry a second copy of the selection/lock rules the Delete key follows.</summary>
        public Func<bool> DeleteSelection { get; set; }

        /// <summary>Runs the parent control's <c>RippleDeleteSelection</c> — the cross-track cut
        /// the context menu offers beside the plain delete.</summary>
        public Func<bool> RippleDeleteSelection { get; set; }

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
                ClearPreviewCaches();
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
                ClearPreviewCaches();
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

        /// <summary>Where the ruler's hover ghost is, or null when the pointer is not over the
        /// ruler; the parent pushes it so the ghost's line runs down through the rows.</summary>
        public long? HoverTicks
        {
            get => _hoverTicks;
            set
            {
                if (_hoverTicks == value)
                    return;

                _hoverTicks = value;
                InvalidateVisual();
            }
        }

        /// <summary>Whether move/trim drags snap to other items' edges, the playhead and the
        /// origin. The parent's corner toggle sets it; Alt still bypasses snapping per-drag when
        /// it is on.</summary>
        public bool SnapEnabled { get; set; } = true;

        /// <summary>Rebuilds the row layout from the live project — the parent calls this on
        /// Structural changes (and session swaps). Also prunes caches keyed by item ids that no
        /// longer exist.</summary>
        public void RebuildRows()
        {
            var project = _session?.Project;
            _rows = project == null ? Array.Empty<TimelineRow>() : TimelineRowLayout.Build(project);

            if (project == null)
            {
                ClearPreviewCaches();
            }
            else if (_waveforms.Count > 0 || _cursorActivity.Count > 0 || _keyBlips.Count > 0)
            {
                var live = project.Items.Select(i => i.Id).ToHashSet();
                Prune(_waveforms, live);
                Prune(_cursorActivity, live);
                Prune(_keyBlips, live);
            }

            InvalidateMeasure();
            InvalidateVisual();
        }

        private void ClearPreviewCaches()
        {
            _waveforms.Clear();
            _cursorActivity.Clear();
            _keyBlips.Clear();
        }

        private static void Prune<T>(Dictionary<Guid, T> cache, HashSet<Guid> live)
        {
            if (cache.Count == 0)
                return;
            foreach (var stale in cache.Keys.Where(id => !live.Contains(id)).ToList())
                cache.Remove(stale);
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

            if (OperatingSystem.IsMacOS() && properties.IsLeftButtonPressed &&
                e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                // Ctrl+click IS the secondary click on macOS — the gesture a trackpad user reaches
                // for, and the only one a one-button mouse has. It arrives as an ordinary LEFT
                // press with the Control flag (AppKit does not synthesise a right button, and
                // Avalonia's macOS backend maps only a real rightMouseDown), so without this branch
                // it fell straight into the scrub/select path below and the row menu was
                // unreachable from a Mac trackpad.
                //
                // The menu has to be asked for by hand as well: Avalonia raises ContextRequested
                // only for a real right button (Control.OnPointerReleased), and raising it here is
                // what routes through ContextMenu's own handler — Opening still builds the entries
                // and still cancels the popup when the press landed on nothing. Doing it from the
                // press rather than the release is the AppKit convention, where a contextual menu
                // appears the moment the button goes down.
                PrepareContextMenu(e.GetPosition(this));
                RaiseEvent(new ContextRequestedEventArgs(e));
                e.Handled = true;
                return;
            }

            if (!properties.IsLeftButtonPressed || _session == null || _dragMode != DragMode.None)
                return;

            Focus();

            var pos = e.GetPosition(this);

            // the edge-jump chevrons sit over item bodies, so they claim the press first.
            if (HitJump(pos) is { } jump)
            {
                var jumpItem = FindItem(jump.ItemId);
                if (jumpItem != null)
                    _viewport.EnsureVisible(jump.Left ? jumpItem.TimelineStartTicks : jumpItem.TimelineEndTicks);
                return;
            }

            var hit = HitTestAt(pos);

            switch (hit.Kind)
            {
                case TimelineHitKind.Empty:
                case TimelineHitKind.Ruler: // unreachable (the ruler is a sibling), kept for the shared hit enum
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
                    // (TimelineOps.Move is group-scoped), which cannot desync anything. Cursor and
                    // keyboard items are hard-synced to the recording whatever their group looks
                    // like, so they are named here rather than left to the group test.
                    var track = FindTrack(item.TrackId);
                    if (_session.IsRippleGroup(item.Id) || IsInputOverlayRow(item.TrackId) ||
                        track is not { Locked: false })
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

            // a tip standing over the clip the drag is about to move would follow nothing.
            ShowAiTip(false);

            _gesture = _session.BeginGesture(label, this);
            _dragMode = mode;
            _dragItemId = itemId;
            _grabOffsetTicks = grabOffsetTicks;
            _dragPointer = e.Pointer;
            e.Pointer.Capture(this);

            if (mode == DragMode.MoveItem)
                Cursor = DragCursors.Grabbing;
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
            var pos = e.GetPosition(this);
            var ticks = _viewport.XToTicksClamped(pos.X);
            FinishDrag(commit: true); // before Capture(null): it re-enters OnPointerCaptureLost
            e.Pointer.Capture(null);
            UpdateHover(pos); // restore the grab/resize cursor for whatever is under the release

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

            ShowAiTip(false);

            if (_hoverItemId != Guid.Empty || _hoverJump != null)
            {
                _hoverItemId = Guid.Empty;
                _hoverJump = null;
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
            var mode = _dragMode;
            _dragMode = DragMode.None;
            _dragPointer = null;
            SetSnapGuide(null);

            // back from the grabbing fist; the release handler's hover pass re-applies the open
            // grab if the pointer is still over the item.
            if (mode == DragMode.MoveItem)
                Cursor = null;

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

            if (SnapEnabled && !modifiers.HasFlag(KeyModifiers.Alt))
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

            if (SnapEnabled && !modifiers.HasFlag(KeyModifiers.Alt))
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
            var jump = HitJump(pos);
            var jumpKey = jump is { } j ? ((Guid, bool)?)(j.ItemId, j.Left) : null;
            if (jumpKey != _hoverJump)
            {
                _hoverJump = jumpKey;
                InvalidateVisual();
            }

            ShowAiTip(_aiBadges.Any(r => r.Contains(pos)));

            var hit = HitTestAt(pos);
            var hover = hit.Kind is TimelineHitKind.ItemBody or TimelineHitKind.ItemStart or TimelineHitKind.ItemEnd
                ? hit.ItemId
                : Guid.Empty;
            if (hover != _hoverItemId)
            {
                _hoverItemId = hover;
                InvalidateVisual();
            }

            // a chevron owns the cursor while under it — the press goes to the jump, not the item.
            if (jumpKey != null)
            {
                Cursor = _cursorHand ??= new Cursor(StandardCursorType.Hand);
                return;
            }

            switch (hit.Kind)
            {
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
                                  _session?.IsRippleGroup(item.Id) != true &&
                                  !IsInputOverlayRow(item.TrackId);
                    // Arrow (not the grab hand) on a recording-synced or locked body: no move
                    // affordance IS the cue. Import groups move as one, so their bodies keep it.
                    // Grab (not the pointing Hand — that one belongs to the edge-jump chevrons)
                    // says "this drags".
                    Cursor = movable ? DragCursors.Grab : null;
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
            // pointer picked one clip out; cutting its neighbors too would be an edit nobody
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

            // the cross-track cut: this clip's span removed from its whole recording and the gap
            // closed on every row. Named for what it does to the rest of the timeline, because
            // that is exactly what plain Delete no longer touches.
            menu.Items.Add(NewMenuItem("Ripple Delete", !track.Locked,
                () => RippleDeleteSelection?.Invoke()));

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
            // item has no move affordance. Never offered on a cursor/keyboard row: those read the
            // recording's input capture at the recording's own times, so their sync is not a
            // toggle and the session refuses to take it off (EditorSession.UnlinkTrack).
            if (!IsInputOverlayRow(trackId) &&
                _session.Project.Items.Any(i => i.TrackId == trackId && i.LinkGroupId != null))
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

        private TimelineHit HitTestAt(Point pos) =>
            TimelineHitTester.HitTest(pos.X, pos.Y, 0, ComputeItemRects());

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

        /// <summary>Whether the row is one of the recording's input-capture overlays — read off
        /// the laid-out rows (the layout already classified them by content), which is the same
        /// answer the header panel and the session's refusals give.</summary>
        private bool IsInputOverlayRow(Guid trackId)
        {
            foreach (var row in _rows)
            {
                if (row.TrackId == trackId)
                    return TimelineRowLayout.IsInputOverlay(row.Kind);
            }

            return false;
        }

        private Item FindItem(Guid id) => _session?.Project.Items.FirstOrDefault(i => i.Id == id);

        private Track FindTrack(Guid trackId) => _session?.Project.Tracks.FirstOrDefault(t => t.Id == trackId);

        private Track TrackOfItem(Guid itemId)
        {
            var item = FindItem(itemId);
            return item == null ? null : FindTrack(item.TrackId);
        }

        /// <summary>Whether the item is drawn or mixed through a model: the track's speech
        /// enhancer (audio, where the toggle is track-wide) or an effect that consumes the person
        /// matte. The plain blur is a pixel filter with nothing behind it and does not count.</summary>
        private static bool HasAiFeature(Track track, Item item) =>
            (track.Kind == TrackKind.Audio && track.Denoise) ||
            VideoEffect.NeedsMatte(item.Effect?.Kind ?? VideoEffectKind.None);

        // --------------------------------------------------------------------------- rendering

        public override void Render(DrawingContext context)
        {
            var palette = TimelinePalette.ForVariant(ActualThemeVariant);
            context.FillRectangle(palette.SurfaceBackground, new Rect(Bounds.Size));

            _edgeJumps.Clear();
            _aiBadges.Clear();
            var project = _session?.Project;
            if (project == null)
                return;

            // a screen row and the overlays glued above it read as one combined track: one band
            // of background, one separator under the whole block (the header panel draws them the
            // same way)
            var band = 0;
            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                var (_, unitEnd) = TimelineReorder.UnitRange(_rows, i);
                context.FillRectangle(band % 2 == 0 ? palette.RowBackground : palette.RowBackgroundAlt,
                    new Rect(0, row.Top, Bounds.Width, row.Height));

                // the gutter between two blocks: bare surface, where the drag cannot go
                if (i > 0 && _rows[i - 1].Bottom < row.Top)
                    palette.DrawBlockGap(context,
                        new Rect(0, _rows[i - 1].Bottom, Bounds.Width, row.Top - _rows[i - 1].Bottom));

                if (i != unitEnd)
                    continue;

                // the rule sits between two rows of one block: none under the block's last row
                // (the gutter, or the end of the rows, already separates it — a rule there and
                // none above the block's first row read as an unbalanced frame)
                var lastInBlock = i + 1 == _rows.Count || _rows[i + 1].Top > row.Bottom;
                if (!lastInBlock)
                    context.DrawLine(palette.RowSeparatorPen,
                        new Point(0, row.Bottom - 0.5), new Point(Bounds.Width, row.Bottom - 0.5));
                band++;
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
                    RenderItem(context, palette, project, track, row, item, body, selection.Contains(item.Id),
                        evenRow: i % 2 == 0);
                }
            }

            if (_snapGuideTicks is long guide)
            {
                var gx = _viewport.TickToX(guide);
                context.DrawLine(palette.SnapGuidePen, new Point(gx, 0), new Point(gx, Bounds.Height));
            }

            // playhead line — the ruler above owns the head block. The ruler's hover ghost drops
            // through the rows the same way, so the frame a click would land on is readable against
            // the clips and not just against the ruler.
            var duration = _viewport.DurationTicks;
            if (duration > 0)
            {
                if (_hoverTicks is long hover)
                    DrawFullHeightLine(context, palette.HoverPlayheadPen, _viewport.TickToX(hover));

                DrawFullHeightLine(context, palette.PlayheadPen,
                    _viewport.TickToX(Math.Clamp(_positionTicks, 0, duration)));
            }
        }

        private void DrawFullHeightLine(DrawingContext context, IPen pen, double x)
        {
            if (x < -1 || x > Bounds.Width + 1)
                return;

            // snapped to whole device pixels: a hairline drawn at a fractional x is split across two
            // columns and washes out (see TimelineViewMath.SnapToPixel).
            x = TimelineViewMath.SnapToPixel(x, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0, pen.Thickness);
            context.DrawLine(pen, new Point(x, 0), new Point(x, Bounds.Height));
        }

        private void RenderItem(DrawingContext context, TimelinePalette palette, Project project,
            Track track, TimelineRow row, Item item, Rect body, bool selected, bool evenRow)
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
                case CursorContent cursor:
                    RenderCursorActivity(context, palette, project, item, cursor, body);
                    break;
                case KeyboardContent keyboard:
                    RenderKeyBlips(context, palette, project, item, keyboard, body);
                    break;
            }

            // Media content sits UNDER the transition ramps (the ramp shows what the picture/audio
            // fades over), and the edge fades sit over both; a card's glyph label goes ON TOP of
            // it all — an entry ramp covers the card's left edge, which is exactly where the label
            // lives (and a pinned label sits right over the fade), and an unreadable label defeats
            // its purpose.
            RenderTransitions(context, palette, item, body);
            RenderEdgeFades(context, palette, item, body, evenRow);

            Geometry glyph = null;
            string label = null;
            switch (item.Content)
            {
                case TextContent text:
                    (glyph, label) = (TimelineIcons.Find("IconToolText"), text.Text);
                    break;
                case ImageContent image:
                    (glyph, label) = (TimelineIcons.Find("IconImage"),
                        System.IO.Path.GetFileName(image.Path));
                    break;
                case SpeedContent speed:
                    (glyph, label) = (TimelineIcons.SpeedometerGeometry,
                        speed.Factor.ToString("0.##", CultureInfo.InvariantCulture) + "×");
                    break;
                case ZoomContent zoom:
                    (glyph, label) = (TimelineIcons.MagnifierGeometry,
                        Math.Round(zoom.Zoom * 100).ToString("0", CultureInfo.InvariantCulture) + "%");
                    break;
                // the input overlays name themselves and nothing else: their content is the
                // recording's captured input, which has no one number to put on the card (the
                // style and the timings live in the properties panel) — the activity preview
                // under the label is what says what the capture holds.
                case CursorContent:
                    (glyph, label) = (TimelineIcons.CursorArrowGeometry, "Cursor");
                    break;
                case KeyboardContent:
                    (glyph, label) = (TimelineIcons.KeyboardGeometry, "Keys");
                    break;
            }

            // the AI badge leads the card label — it takes the slot a text card's glyph would
            // have, and pushes the kind glyph and its text along when the card has both (an image
            // with the background removed is the case that carries the pair).
            var ai = HasAiFeature(track, item) && body.Width > AiChipSize * 2;
            if (ai || glyph != null || label != null)
                RenderGlyphLabel(context, palette, body, ai, glyph, label);

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
                RenderTrimHandles(context, palette, body, selected);
        }

        /// <summary>
        /// The two edge handles: a full-height slab at each end of the body with a pair of grip
        /// lines in it, drawn inside the body's rounded clip so the outer corners follow the item's
        /// radius while the inner side stays square. Selected wins over hover, so an item that is
        /// both keeps the accent handles the selection border implies.
        /// </summary>
        private static void RenderTrimHandles(DrawingContext context, TimelinePalette palette, Rect body,
            bool selected)
        {
            var fill = selected ? palette.TrimHandleActiveFill : palette.TrimHandleHoverFill;
            var line = selected ? palette.TrimHandleActiveLine : palette.TrimHandleHoverLine;

            var lineHeight = Math.Max(4, Math.Round(body.Height * 0.4));
            var lineY = Math.Round(body.Y + (body.Height - lineHeight) / 2);

            using (context.PushClip(new RoundedRect(body, ItemCornerRadius)))
            {
                foreach (var handleX in new[] { body.X, body.Right - TrimHandleWidth })
                {
                    context.FillRectangle(fill, new Rect(handleX, body.Y, TrimHandleWidth, body.Height));

                    // 9px handle, split 3 / 1 / 1 / 1 / 3: two 1px lines with a 1px gap, centered.
                    var x0 = Math.Round(handleX);
                    context.FillRectangle(line, new Rect(x0 + 3, lineY, 1, lineHeight));
                    context.FillRectangle(line, new Rect(x0 + 5, lineY, 1, lineHeight));
                }
            }
        }

        /// <summary>How far along [0..1] the edge treatment is for an item end scrolled
        /// <paramref name="overshootPx"/> past the viewport: it eases in over one fade-width of
        /// scrolling instead of popping the moment a pixel is cut off.</summary>
        private static double EdgeCutAmount(double overshootPx) =>
            Math.Clamp(overshootPx / EdgeFadeWidth, 0, 1);

        /// <summary>
        /// Dissolves whichever end of the item the viewport cuts off into the row background — the
        /// cue <see cref="Controls.FadeEdgeScrollViewer"/> gives scrolled lists: a hard clip line
        /// reads as "the clip ends here", the fade as "it keeps going". A chevron drawn inside each
        /// fade jumps the view to the cut-off end when clicked (hit rects are collected per frame,
        /// so hover/press always read what is on screen). Both fade and chevron ease in with
        /// <see cref="EdgeCutAmount"/> as the edge scrolls out.
        /// </summary>
        private void RenderEdgeFades(DrawingContext context, TimelinePalette palette, Item item,
            Rect body, bool evenRow)
        {
            var cutLeft = EdgeCutAmount(-body.X);
            var cutRight = EdgeCutAmount(body.Right - Bounds.Width);
            if (cutLeft <= 0 && cutRight <= 0)
                return;

            using (context.PushClip(new RoundedRect(body, ItemCornerRadius)))
            {
                if (cutLeft > 0)
                {
                    using (context.PushOpacity(cutLeft))
                    {
                        context.FillRectangle(palette.ItemEdgeFade(evenRow, leftEdge: true),
                            new Rect(0, body.Y, EdgeFadeWidth, body.Height));
                        RenderEdgeJump(context, palette, item, body, left: true, amount: cutLeft);
                    }
                }

                if (cutRight > 0)
                {
                    using (context.PushOpacity(cutRight))
                    {
                        context.FillRectangle(palette.ItemEdgeFade(evenRow, leftEdge: false),
                            new Rect(Bounds.Width - EdgeFadeWidth, body.Y, EdgeFadeWidth, body.Height));
                        RenderEdgeJump(context, palette, item, body, left: false, amount: cutRight);
                    }
                }
            }
        }

        private void RenderEdgeJump(DrawingContext context, TimelinePalette palette, Item item,
            Rect body, bool left, double amount)
        {
            var x = left ? 4 : Bounds.Width - JumpGlyphSize - 4;
            var hovered = _hoverJump == (item.Id, left);

            // resting at 0.6 opacity so hovering snaps it to full — a clearer "you are on the
            // button" cue than the brush change alone.
            using (context.PushOpacity(hovered ? 1.0 : 0.6))
                DrawGlyph(context, left ? TimelineIcons.JumpLeftGeometry : TimelineIcons.JumpRightGeometry,
                    hovered ? palette.GripHoverBrush : palette.GripBrush,
                    new Point(x, body.Center.Y - JumpGlyphSize / 2), JumpGlyphSize);

            // the hit rect is the full fade column over this row — the glyph alone would be a
            // 10px target. Only once the chevron is mostly faded in, though: a half-invisible
            // button must not steal the item's own press yet.
            if (amount >= 0.5)
                _edgeJumps.Add(new EdgeJump(item.Id, left,
                    new Rect(left ? 0 : Bounds.Width - EdgeFadeWidth, body.Y, EdgeFadeWidth, body.Height)));
        }

        /// <summary>Opens/closes the AI badge's tip. Driven by hand rather than by a standing
        /// <c>ToolTip.Tip</c>: the badges are drawn regions of one control, so the tooltip service
        /// has nothing of its own to hover — and a Tip left on the surface would show up wherever
        /// the pointer rested on it.</summary>
        private void ShowAiTip(bool show)
        {
            if (show == _aiTipShown)
                return;

            _aiTipShown = show;
            if (show)
            {
                ToolTip.SetTip(this, AiFeaturesTip);
                ToolTip.SetIsOpen(this, true);
            }
            else
            {
                ToolTip.SetIsOpen(this, false);
                ToolTip.SetTip(this, null);
            }
        }

        private EdgeJump? HitJump(Point pos)
        {
            foreach (var jump in _edgeJumps)
            {
                if (jump.Rect.Contains(pos))
                    return jump;
            }

            return null;
        }

        private readonly struct EdgeJump
        {
            public EdgeJump(Guid itemId, bool left, Rect rect)
            {
                ItemId = itemId;
                Left = left;
                Rect = rect;
            }

            public Guid ItemId { get; }

            /// <summary>True when this chevron sits at the left viewport edge (jumps to the item's
            /// start); false for the right edge (jumps to its end).</summary>
            public bool Left { get; }

            public Rect Rect { get; }
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

            // a re-timed item (speed ≠ 1) covers DurationTicks * speed of SOURCE, and one screen
            // pixel spans tpp * speed source ticks — all the strip math below runs in source time,
            // so it uses the scaled tick-per-pixel throughout.
            var speed = TimelineOps.SpeedOf(media);
            var tpp = _viewport.TicksPerPixel * speed;
            var sourceSpan = speed == 1.0 ? item.DurationTicks : (long)Math.Round(item.DurationTicks * speed);
            var naturalSlotPx = Math.Max(8, body.Height * aspect);
            var strip = _previewProvider.GetThumbnails(new ThumbnailRequest(media.SourceId, media.StreamIndex,
                media.SourceInTicks, sourceSpan, (long)(naturalSlotPx * tpp), (int)Math.Round(body.Height)));

            var interval = Math.Max(1, strip.IntervalTicks);
            var slotWidth = interval / tpp;
            if (slotWidth <= 0.5)
                return;

            var thumbs = strip.Thumbnails;
            if (thumbs.Count == 0)
                return; // missing thumbnails leave the body fill visible

            // The provider's grid never gets finer than its base interval, so a deep zoom makes
            // each slot far wider than a frame — one thumb smeared across it. Halving keeps the
            // draw grid a power-of-two subdivision anchored at source 0 (tile edges stay put
            // across zoom levels); the tiles repeat the nearest cached thumb at roughly its
            // natural aspect instead, purely a render decision — nothing new is decoded.
            var drawInterval = interval;
            while (slotWidth > naturalSlotPx * 1.5 && drawInterval > 1)
            {
                drawInterval /= 2;
                slotWidth /= 2;
            }

            // the default bitmap filtering is plain bilinear, which shimmers on the downscales and
            // smears on the stretches a slot inevitably applies; cubic/mipmapped sampling costs
            // nothing measurable at filmstrip sizes.
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            using (context.PushClip(new RoundedRect(body, ItemCornerRadius)))
            {
                // slots sit on the interval grid anchored at source time 0 — the same anchoring
                // the provider quantizes to, so a zoom change reuses decoded thumbnails.
                var visStartX = Math.Max(body.X, 0);
                var visEndX = Math.Min(body.Right, Bounds.Width);
                var firstSlot = Math.Max(0, (media.SourceInTicks + (long)((visStartX - body.X) * tpp)) / drawInterval);

                for (var n = firstSlot; ; n++)
                {
                    var slotSource = n * drawInterval;
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
                // the peaks are read in SOURCE time: a re-timed item's timeline bucket covers
                // perBucket * speed of source, so bucket i lines up under the same x either way.
                var speed = TimelineOps.SpeedOf(media);
                var srcDuration = speed == 1.0 ? item.DurationTicks : (long)Math.Round(item.DurationTicks * speed);
                var srcPerBucket = speed == 1.0 ? perBucket : Math.Max(1L, (long)Math.Round(perBucket * speed));
                var peaks = _previewProvider.GetAudioPeaks(new AudioPeaksRequest(media.SourceId,
                    media.StreamIndex, media.SourceInTicks, srcDuration, srcPerBucket));
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

        /// <summary>
        /// The cursor row's preview: the pointer's speed as a mirrored envelope (the waveform's
        /// shape, so the two read as one language — "activity") with every press over it as a
        /// short bar, stretched to cover a drag. Time runs through the hard-synced screen item,
        /// exactly as the composer places the cursor; with no screen item covering the span the
        /// composer draws no cursor, and neither does this.
        /// </summary>
        private void RenderCursorActivity(DrawingContext context, TimelinePalette palette, Project project,
            Item item, CursorContent cursor, Rect body)
        {
            if (!OverlayTiming.TryResolve(project, item, cursor.SourceId, out var sourceIn, out var speed))
                return;

            var tpp = _viewport.TicksPerPixel;
            var perBucket = Math.Max((long)Math.Round(tpp), Math.Max(1, item.DurationTicks / MaxWaveformBuckets));
            var bucketCount = (int)Math.Min((item.DurationTicks + perBucket - 1) / perBucket, Int32.MaxValue / 2);
            if (bucketCount <= 0)
                return;

            // cached per item like the waveform: scrolling only moves the translate, a zoom or a
            // trim rebuilds, and a still-loading capture rebuilds on each (throttled) repaint.
            if (!_cursorActivity.TryGetValue(item.Id, out var cache) ||
                !cache.Matches(tpp, sourceIn, speed, item.DurationTicks, bucketCount) ||
                !cache.Complete)
            {
                var srcDuration = speed == 1.0 ? item.DurationTicks : (long)Math.Round(item.DurationTicks * speed);
                var srcPerBucket = speed == 1.0 ? perBucket : Math.Max(1L, (long)Math.Round(perBucket * speed));
                var activity = _previewProvider.GetCursorActivity(new CursorActivityRequest(cursor.SourceId,
                    sourceIn, srcDuration, srcPerBucket));
                cache = CursorActivityCache.Build(activity, tpp, sourceIn, speed, item.DurationTicks,
                    bucketCount, perBucket, body.Height);
                _cursorActivity[item.Id] = cache;
            }

            var markHeight = Math.Min(ClickMarkHeight, Math.Max(2, body.Height - 6));
            var markY = Math.Round(body.Height / 2 - markHeight / 2);
            var radius = Math.Min(2, markHeight / 2);

            using (context.PushClip(new RoundedRect(body, ItemCornerRadius)))
            using (context.PushTransform(Matrix.CreateTranslation(body.X, body.Y)))
            {
                if (cache.Motion != null)
                    context.DrawGeometry(palette.CursorMotionBrush, null, cache.Motion);

                // only the marks on screen: zoomed far in, an item can be wider than any screen
                // and the marks are nearly all off it
                var visLeft = -body.X - 2;
                var visRight = Bounds.Width - body.X + 2;
                foreach (var mark in cache.Clicks)
                {
                    if (mark.X + mark.Width < visLeft || mark.X > visRight)
                        continue;
                    context.DrawRectangle(palette.CursorClickBrush, null,
                        new Rect(mark.X, markY, mark.Width, markHeight), radius, radius);
                }
            }
        }

        /// <summary>
        /// The keys row's preview: one blip per keystroke run (the row the overlay will show),
        /// spanning its first key to its last, with runs that crowd together at the current zoom
        /// folded into one blip that grows with the count. Time runs through the hard-synced
        /// screen item; with none the keys fall back to item-relative time, the composer's own
        /// degrade, so the blips still line up with what plays.
        /// </summary>
        private void RenderKeyBlips(DrawingContext context, TimelinePalette palette, Project project,
            Item item, KeyboardContent keyboard, Rect body)
        {
            OverlayTiming.TryResolve(project, item, keyboard.SourceId, out var sourceIn, out var speed);

            var tpp = _viewport.TicksPerPixel;
            if (!(tpp > 0))
                return;

            if (!_keyBlips.TryGetValue(item.Id, out var cache) ||
                !cache.Matches(tpp, sourceIn, speed, item.DurationTicks, keyboard.PauseBreakMs, keyboard.Filter) ||
                !cache.Complete)
            {
                var srcDuration = speed == 1.0 ? item.DurationTicks : (long)Math.Round(item.DurationTicks * speed);
                var runs = _previewProvider.GetKeyRuns(new KeyRunsRequest(keyboard.SourceId, sourceIn, srcDuration,
                    keyboard.PauseBreakMs, keyboard.Filter));
                cache = KeyBlipCache.Build(runs, tpp, sourceIn, speed, item.DurationTicks,
                    keyboard.PauseBreakMs, keyboard.Filter);
                _keyBlips[item.Id] = cache;
            }

            if (cache.Blips.Count == 0)
                return;

            var mid = body.Height / 2;
            using (context.PushClip(new RoundedRect(body, ItemCornerRadius)))
            using (context.PushTransform(Matrix.CreateTranslation(body.X, body.Y)))
            {
                var visLeft = -body.X - 2;
                var visRight = Bounds.Width - body.X + 2;
                foreach (var blip in cache.Blips)
                {
                    if (blip.X + blip.Width < visLeft || blip.X > visRight)
                        continue;

                    var height = InputPreviewMath.KeyBlipHeight(blip.Count, body.Height);
                    var radius = Math.Min(blip.Width, height) / 2;
                    context.DrawRectangle(palette.KeyBlipBrush, null,
                        new Rect(blip.X, Math.Round(mid - height / 2), blip.Width, height), radius, radius);
                }
            }
        }

        private static readonly char[] NewlineChars = { '\r', '\n' };

        private void RenderGlyphLabel(DrawingContext context, TimelinePalette palette, Rect body,
            bool ai, Geometry glyph, string label)
        {
            // inset past the trim handles (they draw inside the body on hover/selection) so the
            // glyph and text never sit under a handle — and never shift when one appears. When the
            // item's start scrolls out of view the label glides to a pin past the edge fade
            // (still-solid body), at the same rate the fade eases in — no jump the frame the edge
            // crosses the viewport.
            var cutLeft = EdgeCutAmount(-body.X);
            var naturalX = body.X + TrimHandleWidth + 3;
            var startX = naturalX + (EdgeFadeWidth + 3 - naturalX) * cutLeft;

            // the star rides a pale chip: blue-on-blue would be invisible over a recording row,
            // whose fill IS the accent. Drawn OUTSIDE the body clip the rest of the label lives
            // in — on the short (26px) card rows the chip clears the body by less than the
            // shadow's reach, and a shadow cut off square at the card edge reads as a seam. Hence
            // the explicit fits-inside test: a pinned label on a mostly-scrolled-past card can sit
            // past the body's right edge, and nothing else would keep the chip in the card.
            if (ai && startX + AiChipSize <= body.Right)
            {
                var chip = new Rect(startX, body.Center.Y - AiChipSize / 2, AiChipSize, AiChipSize);
                context.DrawRectangle(palette.AiBadgeChipFill, null,
                    new RoundedRect(chip, 4), palette.AiBadgeShadow);
                DrawGlyph(context, TimelineIcons.AiSparkleGeometry, palette.AiBadgeBrush,
                    new Point(chip.X + (AiChipSize - AiStarSize) / 2, chip.Y + (AiChipSize - AiStarSize) / 2),
                    AiStarSize);

                // inflated: a 15px square is a small thing to land a pointer on, and the tip is
                // the only place the badge says what it means.
                _aiBadges.Add(chip.Inflate(3).Intersect(body));
                startX += AiChipSize + 5;
            }

            using (context.PushClip(new RoundedRect(body, ItemCornerRadius)))
            {
                var x = startX;

                if (glyph != null)
                {
                    DrawGlyph(context, glyph, palette.ItemLabelBrush,
                        new Point(x, body.Center.Y - GlyphSize / 2), GlyphSize);
                    x += GlyphSize + 5;
                }

                if (String.IsNullOrEmpty(label))
                    return;

                // multi-line text (a Text card) collapses to one line — MaxLineCount alone would
                // cut it at the first newline however much room the item has.
                if (label.IndexOfAny(NewlineChars) >= 0)
                    label = String.Join(" ", label.Split(NewlineChars, StringSplitOptions.RemoveEmptyEntries));

                // capped at the viewport too, so text on an item running past the right edge
                // ellipsizes where it can still be read — clear of the fade and its chevron —
                // easing there at the same rate that fade appears.
                var cutRight = EdgeCutAmount(body.Right - Bounds.Width);
                var naturalLimit = body.Right - TrimHandleWidth - 3;
                var rightLimit = naturalLimit + (Bounds.Width - EdgeFadeWidth - 3 - naturalLimit) * cutRight;
                var maxWidth = rightLimit - x;
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
            // a keystroke overlay spends its Entry/Exit on each key row (see FrameComposer), not
            // on the item as a whole — a ramp on its card would promise a fade that never happens.
            if (item.Content is KeyboardContent)
                return;

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

                // the hidden-row hatch inside the triangle too — the wash alone was too faint to
                // read as "this stretch is attenuated" against a busy filmstrip.
                var span = isEntry
                    ? new Rect(body.X, body.Y, w, body.Height)
                    : new Rect(body.Right - w, body.Y, w, body.Height);
                using (context.PushGeometryClip(geo))
                    DrawHatchLines(context, palette.HatchPen, span);

                context.DrawLine(palette.TransitionEdgePen, top, bottom);
            }
        }

        private static void RenderHatch(DrawingContext context, Pen pen, Rect rect)
        {
            using (context.PushClip(rect))
                DrawHatchLines(context, pen, rect);
        }

        private static void DrawHatchLines(DrawingContext context, Pen pen, Rect rect)
        {
            const double spacing = 6;
            for (var x = rect.X - rect.Height; x < rect.Right; x += spacing)
                context.DrawLine(pen, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Y));
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

        /// <summary>One cursor item's cached preview, in item-local coordinates: the speed
        /// envelope as geometry and the click marks as pixel spans.</summary>
        private sealed class CursorActivityCache
        {
            public double Tpp;
            public long SourceIn;
            public double Speed;
            public long Duration;
            public int BucketCount;
            public bool Complete;
            public StreamGeometry Motion;
            public List<InputMark> Clicks;

            public bool Matches(double tpp, long sourceIn, double speed, long duration, int bucketCount) =>
                Tpp == tpp && SourceIn == sourceIn && Speed == speed && Duration == duration && BucketCount == bucketCount;

            public static CursorActivityCache Build(CursorActivity activity, double tpp, long sourceIn, double speed,
                long duration, int bucketCount, long perBucket, double height)
            {
                var mid = height / 2;
                var half = Math.Max(1, mid - 1);
                var bucketPx = perBucket / tpp;
                var motion = activity.Motion;

                // speed is [0,1]; the ±0.5px floor keeps a still pointer visible as a hairline,
                // like silence on a waveform
                var geometry = new StreamGeometry();
                using (var gc = geometry.Open())
                {
                    gc.BeginFigure(new Point(0, mid - 0.5), true);
                    for (var i = 0; i < bucketCount; i++)
                    {
                        var v = i < motion.Count ? motion[i] : 0;
                        gc.LineTo(new Point(i * bucketPx, mid - Math.Max(0.5, v * half)));
                    }

                    for (var i = bucketCount - 1; i >= 0; i--)
                    {
                        var v = i < motion.Count ? motion[i] : 0;
                        gc.LineTo(new Point(i * bucketPx, mid + Math.Max(0.5, v * half)));
                    }

                    gc.EndFigure(true);
                }

                return new CursorActivityCache
                {
                    Tpp = tpp,
                    SourceIn = sourceIn,
                    Speed = speed,
                    Duration = duration,
                    BucketCount = bucketCount,
                    Complete = activity.IsComplete,
                    Motion = geometry,
                    Clicks = InputPreviewMath.ClickMarks(activity.Clicks, sourceIn, speed, tpp),
                };
            }
        }

        /// <summary>One keys item's cached blips, in item-local pixels.</summary>
        private sealed class KeyBlipCache
        {
            public double Tpp;
            public long SourceIn;
            public double Speed;
            public long Duration;
            public int PauseBreakMs;
            public KeystrokeFilter Filter;
            public bool Complete;
            public List<InputMark> Blips;

            public bool Matches(double tpp, long sourceIn, double speed, long duration, int pauseBreakMs,
                KeystrokeFilter filter) =>
                Tpp == tpp && SourceIn == sourceIn && Speed == speed && Duration == duration &&
                PauseBreakMs == pauseBreakMs && Filter == filter;

            public static KeyBlipCache Build(KeyRuns runs, double tpp, long sourceIn, double speed, long duration,
                int pauseBreakMs, KeystrokeFilter filter) => new KeyBlipCache
            {
                Tpp = tpp,
                SourceIn = sourceIn,
                Speed = speed,
                Duration = duration,
                PauseBreakMs = pauseBreakMs,
                Filter = filter,
                Complete = runs.IsComplete,
                Blips = InputPreviewMath.KeyBlips(runs.Runs, sourceIn, speed, tpp),
            };
        }
    }
}
