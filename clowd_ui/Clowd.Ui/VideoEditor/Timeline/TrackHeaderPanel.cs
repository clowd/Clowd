using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Clowd.Drawing;
using Clowd.UI.Controls;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>
    /// The native column to the left of the drawing surface: one header per row (heights from
    /// <see cref="TimelineRowLayout"/>, so the two columns stay pixel-aligned) with the drag grip
    /// and the track's kind icon on the left, and on the right — reading left to right — the link
    /// badge (on rows whose items are link-grouped), a duplicate button, the enable button (eye →
    /// <c>Track.Hidden</c> for picture rows, speaker → <c>Track.Muted</c> for audio rows) and a
    /// delete button. Rebuilt wholesale on Structural project changes — under ten rows, so there
    /// is nothing to diff.
    ///
    /// <para>A screen row and the cursor/keyboard rows glued above it are built as one combined
    /// track: one bordered block, no separator between its rows, and one grip spanning all of
    /// them — the grip is the screen row's, because that is the row the session moves (its
    /// overlays travel with it, see <see cref="TimelineReorder.UnitRange"/>). The kind icon is
    /// the block's too — one video icon centered down the block, next to the grip; the overlay
    /// rows do not repeat theirs, their items on the surface already carry it. Each row inside
    /// keeps its own buttons.</para>
    ///
    /// <para>The badge is a label, not a button: unlinking is a rare, consequential edit, so it
    /// lives in the inspector ("Unlink from recording") where it can carry an explanation, rather
    /// than being one stray click away in every row.</para>
    ///
    /// <para>The rows live in a <see cref="StackPanel"/> inside this panel rather than being its own
    /// children, so the drop indicator of a reorder drag can be a sibling laid <i>over</i> them —
    /// drawing it in <c>Render</c> would put it under the row borders it has to sit on.</para>
    /// </summary>
    internal sealed class TrackHeaderPanel : Panel, IRowReorderDragHost
    {
        private const string SyncedTip =
            "Synced — moves and splits with the other recording tracks. Unsync from the properties panel.";

        /// <summary>The cursor/keyboard rows' badge tip: their sync is not a toggle. The overlay
        /// reads the recording's input capture at the recording's own times, so an unsynced one
        /// would draw the wrong moment — the session refuses to unlink it (see
        /// <c>EditorSession.UnlinkTrack</c>) and the properties panel offers nothing to click.</summary>
        private const string PinnedSyncTip =
            "Always synced — this overlay follows the recording it was captured with.";

        /// <summary>Breathing room either side of the grip: it is the first thing in the row, so
        /// without it the dots crowd both the panel edge and the kind icon.</summary>
        private const double GripMarginX = 5;

        private readonly StackPanel _stack = new StackPanel();

        /// <summary>The line showing where the dragged row would land. A sibling of the rows (not a
        /// <c>Render</c> call) so it paints over their borders; never hit-testable, so it cannot
        /// steal the moves of the drag that is showing it.</summary>
        private readonly Border _indicator = new Border
        {
            Height = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            IsVisible = false,
        };

        private EditorSession _session;

        /// <summary>The link badge of every built row, so <see cref="RefreshLinkBadges"/> can
        /// re-read link state after a Mapping change (unlink/relink raise no rebuild, and come from
        /// the inspector rather than from this panel).</summary>
        private readonly List<(Guid TrackId, Control Badge)> _linkBadges =
            new List<(Guid, Control)>();

        /// <summary>The laid-out rows of the current build, and the combined-track visual each
        /// belongs to, in display order — index i of both is row i (the rows of one unit share one
        /// visual, so lifting any of them lifts the whole block). The reorder drag works in this
        /// space (top to bottom, which for video rows is the reverse of the model's layer order).</summary>
        private IReadOnlyList<TimelineRow> _rows = Array.Empty<TimelineRow>();
        private readonly List<Border> _rowUnits = new List<Border>();
        private readonly RowReorderDrag _drag;

        public TrackHeaderPanel()
        {
            Children.Add(_stack);
            Children.Add(_indicator);
            _drag = new RowReorderDrag(this, this, _indicator, this);
        }

        public void SetSession(EditorSession session)
        {
            _session = session;
            Rebuild();
        }

        /// <summary>Rebuilds every row from the live project. The parent control calls this on
        /// Structural <c>ProjectChanged</c> (undo/redo replaces the Project instance, so nothing
        /// from a previous build may be kept) and on theme changes.</summary>
        public void Rebuild()
        {
            // a rebuild from elsewhere (an undo, the inspector) destroys the visuals the drag is
            // holding on to — end it before, not after, its indexes go stale.
            _drag.Cancel();

            _stack.Children.Clear();
            _rowUnits.Clear();
            _linkBadges.Clear();
            _rows = Array.Empty<TimelineRow>();

            var palette = TimelinePalette.ForVariant(ActualThemeVariant);
            Background = palette.RulerBackground;
            _indicator.Background = palette.DropIndicatorBrush;

            var project = _session?.Project;
            if (project == null)
                return;

            _rows = TimelineRowLayout.Build(project);

            // top down, one combined track at a time: a unit is entered at its first row (an
            // overlay, or the screen row itself when it has none) and skipped past as a whole
            for (var i = 0; i < _rows.Count;)
            {
                var (unitStart, unitEnd) = TimelineReorder.UnitRange(_rows, i);

                // the gutter between two blocks (TimelineRowLayout.BlockGap): the same bare strip
                // the surface leaves, so the two columns break in one line
                if (i > 0 && _rows[i - 1].Bottom < _rows[i].Top)
                {
                    _stack.Children.Add(new Border
                    {
                        Height = _rows[i].Top - _rows[i - 1].Bottom,
                        BorderThickness = new Thickness(0, 0, 1, 0),
                        BorderBrush = palette.RowSeparatorPen.Brush,
                        Child = new BlockGapStrip(palette),
                    });
                }

                var unit = BuildUnit(palette, project, unitStart, unitEnd);
                for (var k = unitStart; k <= unitEnd; k++)
                    _rowUnits.Add(unit);
                _stack.Children.Add(unit);
                i = unitEnd + 1;
            }
        }

        /// <summary>Re-reads every row's link state from the live project — the parent calls this
        /// on Mapping changes, because unlink/relink are Mapping (no rebuild follows) and are
        /// issued from the inspector, not from here.</summary>
        public void RefreshLinkBadges()
        {
            var project = _session?.Project;
            if (project == null)
                return;

            foreach (var (trackId, badge) in _linkBadges)
                badge.IsVisible = project.Items.Any(i => i.TrackId == trackId && i.LinkGroupId != null);
        }

        /// <summary>One combined-track block: the rows <paramref name="unitStart"/> to
        /// <paramref name="unitEnd"/> (a lone row, or a screen row under its glued overlays)
        /// stacked inside one border, with the unit's single grip in a column of its own down the
        /// left so it spans every row. The block's height is the rows' total, so the column stays
        /// pixel-aligned with the surface; the bottom border is drawn inside the last row, exactly
        /// as the surface draws its separator inside the row.</summary>
        private Border BuildUnit(TimelinePalette palette, Project project, int unitStart, int unitEnd)
        {
            // the rule under the block, as the surface draws it: between two units of one block
            // only — none under the last unit of a block (the gutter or the end of the rows
            // follows), so the block is not framed below and open above
            var lastInBlock = unitEnd + 1 == _rows.Count || _rows[unitEnd + 1].Top > _rows[unitEnd].Bottom;
            var borderBottom = lastInBlock ? 0 : 1;

            // the grip is the screen row's (the last row of the unit — what the session moves);
            // its cell is reserved on every unit, and the dots (and the drag) are only there when
            // the row has somewhere to go — a locked row is one the context menu's Move Up/Down
            // refuse too, a lone row of its kind cannot reorder against anything, and the speed
            // row is pinned by meaning (see TimelineReorder.CanDrag).
            var screen = _rows[unitEnd];
            var screenTrack = project.Tracks.FirstOrDefault(t => t.Id == screen.TrackId);
            var draggable = screenTrack is { Locked: false } && TimelineReorder.CanDrag(_rows, unitEnd);
            // a grip spanning a taller block gets more dots, not bigger ones — six more per
            // overlay row (the dot pitch against the row heights): four over a lone row, ten over
            // a screen row and one overlay, sixteen over both. The ordinary four would float in
            // the middle of a block three rows tall.
            var dotRows = RowReorderDrag.DefaultGripDotRows + 6 * (unitEnd - unitStart);
            var grip = _drag.BuildGrip(unitEnd, draggable, palette.GripBrush, palette.GripHoverBrush,
                new Thickness(GripMarginX, 2, GripMarginX, 2), dotRows);

            var cells = new StackPanel();
            double height = 0;
            for (var i = unitStart; i <= unitEnd; i++)
            {
                var row = _rows[i];
                var track = project.Tracks.FirstOrDefault(t => t.Id == row.TrackId);
                var cell = new Border
                {
                    // the last row gives up the pixel the border takes, as every row did alone
                    Height = row.Height - (i == unitEnd ? borderBottom : 0),
                    Child = track == null ? null : BuildRowContent(palette, project, track, row),
                };
                cells.Children.Add(cell);
                height += row.Height;
            }

            // ------- kind icon: the screen row's (the block's only left-side label), centered
            // down the whole block like the grip
            var icon = TimelineIcons.NewIcon(KindIconGeometry(screen.Kind), 13, palette.LabelBrush);
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(icon, 1);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
            Grid.SetColumn(cells, 2);
            grid.Children.Add(grip);
            grid.Children.Add(icon);
            grid.Children.Add(cells);

            return new Border
            {
                Height = height,
                Padding = new Thickness(2, 0, 2, 0),
                BorderThickness = new Thickness(0, 0, 1, borderBottom),
                BorderBrush = palette.RowSeparatorPen.Brush,
                Child = grid,
            };
        }

        /// <summary>One row's content, grip and kind icon excluded (they are the block's): the
        /// button cluster on the right.</summary>
        private DockPanel BuildRowContent(TimelinePalette palette, Project project, Track track, TimelineRow row)
        {
            var trackId = track.Id;
            var isAudio = row.Kind == TimelineRowKind.Audio;
            var isInputOverlay = TimelineRowLayout.IsInputOverlay(row.Kind);
            var buttonSize = Math.Min(20, row.Height - 4);

            // no fill child: the row is the button cluster on the right and empty space (the
            // track name labels said nothing the block's kind icon does not).
            var dock = new DockPanel { LastChildFill = false };

            // ------- the right-side cluster, docked right so the FIRST added is the RIGHTMOST.
            // Reading order on screen is link badge → duplicate → enable (eye/speaker) → delete.
            // Delete/duplicate/enable all raise Structural changes, which rebuild this whole
            // panel — nothing here updates its own icon.
            // the same brush the corner buttons above these rows use (RulerLabelBrush), so the
            // two clusters carry one weight — plain white read brighter than the ruler row.
            var buttonBrush = palette.RulerLabelBrush;
            var delete = RowIconButton.Build(TimelineIcons.Find("IconDelete"), buttonBrush,
                track.Locked ? "Row is locked" : "Delete this row and everything on it", buttonSize);
            delete.IsEnabled = !track.Locked;
            delete.Click += (_, _) => _session?.DeleteTrack(trackId, this);
            DockPanel.SetDock(delete, Dock.Right);
            dock.Children.Add(delete);

            // effect rows keep the eye — Hidden switches the effect off — but "hide" would be the
            // wrong word for something that was never in the picture.
            var isEffect = row.Kind is TimelineRowKind.Speed or TimelineRowKind.Zoom;
            var enabled = isAudio ? !track.Muted : !track.Hidden;
            var enable = RowIconButton.Build(
                TimelineIcons.Find(isAudio
                    ? (enabled ? "IconSpeakerEnabled" : "IconSpeakerDisabled")
                    : (enabled ? "IconEye" : "IconEyeOff")),
                buttonBrush,
                isAudio
                    ? (enabled ? "Mute this row" : "Include this audio in the mix")
                    : isEffect
                        ? (enabled ? "Disable this effect" : "Enable this effect")
                        : (enabled ? "Hide this row" : "Show this track in the picture"),
                buttonSize,
                // white when on, faded back when off: at the label's weight the "on" state read as
                // dirt rather than as a lit control.
                enabled ? 1.0 : 0.4);
            enable.Click += (_, _) =>
            {
                if (_session == null)
                    return;

                if (isAudio)
                    _session.SetTrackMuted(trackId, enabled, this);
                else
                    _session.SetTrackHidden(trackId, enabled, this);
            };
            DockPanel.SetDock(enable, Dock.Right);
            dock.Children.Add(enable);

            // no duplicate on the speed row: playback speed is a single global timeline, and the
            // session refuses a second one anyway (see EditorSession.DuplicateTrack). Nor on a
            // cursor/keyboard row: the copy could not be pinned or hard-synced, and the session
            // refuses it for the same reason.
            if (row.Kind != TimelineRowKind.Speed && !isInputOverlay)
            {
                var duplicate = RowIconButton.Build(TimelineIcons.Find("IconCopy"), buttonBrush,
                    "Duplicate this row and its clips", buttonSize);
                duplicate.Click += (_, _) => _session?.DuplicateTrack(trackId, this);
                DockPanel.SetDock(duplicate, Dock.Right);
                dock.Children.Add(duplicate);
            }

            // ------- link badge: a label saying the row's items move with the recording, not a
            // control. Built on every row and collapsed when unlinked, so RefreshLinkBadges can
            // flip it either way without a rebuild (unlink/relink are Mapping changes).
            var badge = new Border
            {
                Width = buttonSize,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = project.Items.Any(i => i.TrackId == trackId && i.LinkGroupId != null),
                Child = TimelineIcons.NewIcon(TimelineIcons.LinkGeometry, buttonSize * 0.6, palette.LinkBadgeBrush),
                Background = Brushes.Transparent, // a null background is not hit-testable — no tooltip
            };
            ToolTip.SetTip(badge, isInputOverlay ? PinnedSyncTip : SyncedTip);
            DockPanel.SetDock(badge, Dock.Right);
            dock.Children.Add(badge);
            _linkBadges.Add((trackId, badge));

            return dock;
        }

        // --------------------------------------------------- reorder drag (RowReorderDrag host)

        int IRowReorderDragHost.RowCount => _rows.Count;

        (double Top, double Height) IRowReorderDragHost.RowExtent(int row) =>
            (_rows[row].Top, _rows[row].Height);

        (int Start, int End) IRowReorderDragHost.SlotGroup(int row) =>
            TimelineReorder.GroupRange(_rows, row);

        /// <summary>Keeps the indicator off the boundaries between a screen row and the
        /// cursor/keyboard rows pinned above it — the layout would put a row dropped there
        /// somewhere else entirely, and an indicator that lies is worse than one that will not
        /// follow the pointer. (The dragged unit's own run included: a pointer over it asks for
        /// the unit's top edge, which is the drag coming home.)</summary>
        int IRowReorderDragHost.CoerceSlot(int row, int slot) =>
            TimelineReorder.CoerceDropIndex(_rows, row, slot);

        /// <summary>A press while another control's drag owns the session would land its drop as
        /// a mid-gesture Preview and be rolled back with it.</summary>
        bool IRowReorderDragHost.CanBeginDrag => _session is { IsGestureActive: false };

        /// <summary>Dims the whole combined track the row belongs to — the screen row's overlays
        /// travel with it, so they lift with it.</summary>
        void IRowReorderDragHost.SetRowLifted(int row, bool lifted)
        {
            if (row < _rowUnits.Count)
                _rowUnits[row].Opacity = lifted ? 0.45 : 1;
        }

        void IRowReorderDragHost.Drop(int fromRow, int dropSlot)
        {
            // Structural — the mutation rebuilds this whole panel; null is a drag that came home.
            var layerIndex = TimelineReorder.TargetLayerIndex(_rows, fromRow, dropSlot);
            if (layerIndex != null)
                _session?.MoveTrackToIndex(_rows[fromRow].TrackId, layerIndex.Value, this);
        }

        private static Geometry KindIconGeometry(TimelineRowKind kind) => kind switch
        {
            TimelineRowKind.Audio => TimelineIcons.Find("IconMusicNote"),
            TimelineRowKind.Text => TimelineIcons.Find("IconToolText"),
            TimelineRowKind.Image => TimelineIcons.Find("IconImage"),
            TimelineRowKind.Speed => TimelineIcons.SpeedometerGeometry,
            TimelineRowKind.Zoom => TimelineIcons.MagnifierGeometry,
            TimelineRowKind.Cursor => TimelineIcons.CursorArrowGeometry,
            TimelineRowKind.Keyboard => TimelineIcons.KeyboardGeometry,
            _ => TimelineIcons.Find("IconVideoClip"),
        };
    }

    /// <summary>The header column's share of a block gutter: the same ground and criss-cross the
    /// surface paints in its strip (<see cref="TimelinePalette.DrawBlockGap"/>), so the gutter
    /// runs across both columns as one.</summary>
    internal sealed class BlockGapStrip : Control
    {
        private readonly TimelinePalette _palette;

        public BlockGapStrip(TimelinePalette palette)
        {
            _palette = palette;
        }

        public override void Render(DrawingContext context) =>
            _palette.DrawBlockGap(context, new Rect(Bounds.Size));
    }

    /// <summary>Icon geometry access for the timeline's code-drawn/code-built visuals: resource
    /// lookup into <c>Assets/VectorIcons.axaml</c>, plus the glyphs that file does not carry
    /// (a zoom-to-fit glyph and a chain link for the sync toggle; the header rows' drag grip
    /// lives with <see cref="RowReorderDrag"/>).</summary>
    internal static class TimelineIcons
    {
        /// <summary>"Fit the whole project": two arrows spreading between end stops (24x24 box).
        /// VectorIcons has no zoom glyph.</summary>
        public static readonly Geometry ZoomToFitGeometry = StreamGeometry.Parse(
            "M2,5 L4,5 L4,19 L2,19 Z M20,5 L22,5 L22,19 L20,19 Z M11,7 L11,17 L5.5,12 Z " +
            "M13,7 L13,17 L18.5,12 Z");

        /// <summary>"Back to the default zoom": an evenly notched ruler (24x24 box), deliberately
        /// unlike the fit glyph's arrows — one is "show everything", the other "one second is this
        /// wide again".</summary>
        public static readonly Geometry ResetZoomGeometry = StreamGeometry.Parse(
            "M2,16 L22,16 L22,18 L2,18 Z M6,8 L8,8 L8,16 L6,16 Z M11,5 L13,5 L13,16 L11,16 Z " +
            "M16,8 L18,8 L18,16 L16,16 Z");

        /// <summary>"Split every track at the playhead": a cut line between two half-blocks (16x16
        /// box) — the same glyph the window toolbar carried before the button moved here.</summary>
        public static readonly Geometry SplitGeometry = StreamGeometry.Parse(
            "M7,1 L9,1 L9,15 L7,15 Z M1,4 L5,4 L5,12 L1,12 Z M11,4 L15,4 L15,12 L11,12 Z");

        /// <summary>"Snap while dragging": two clips butting together with spark marks (24x24 box;
        /// Icons8 "Clip Snapping", Material Filled #DyQBg200VjCa) — the toggle in the timeline's
        /// corner cell. VectorIcons has no snap glyph.</summary>
        public static readonly Geometry SnapGeometry = StreamGeometry.Parse(
            "M 11 2 L 11 5 L 13 5 L 13 2 L 11 2 z M 6.6992188 3.2851562 L 5.3007812 4.7148438 " +
            "L 7.3007812 6.671875 L 8.6992188 5.2441406 L 6.6992188 3.2851562 z " +
            "M 17.300781 3.2851562 L 15.300781 5.2441406 L 16.699219 6.671875 " +
            "L 18.699219 4.7148438 L 17.300781 3.2851562 z M 3 8 C 1.895 8 1 8.895 1 10 L 1 14 " +
            "C 1 15.105 1.895 16 3 16 L 9 16 C 10.105 16 11 15.105 11 14 L 11 10 " +
            "C 11 8.895 10.105 8 9 8 L 3 8 z M 15 8 C 13.895 8 13 8.895 13 10 L 13 14 " +
            "C 13 15.105 13.895 16 15 16 L 21 16 C 22.105 16 23 15.105 23 14 L 23 10 " +
            "C 23 8.895 22.105 8 21 8 L 15 8 z M 7.3007812 17.328125 L 5.3007812 19.285156 " +
            "L 6.6992188 20.714844 L 8.6992188 18.755859 L 7.3007812 17.328125 z " +
            "M 16.699219 17.328125 L 15.300781 18.755859 L 17.300781 20.714844 " +
            "L 18.699219 19.285156 L 16.699219 17.328125 z M 11 19 L 11 22 L 13 22 L 13 19 L 11 19 z");

        /// <summary>A speedometer for the speed effect row and its items (24x24 box; Icons8
        /// "Speed", Material Outlined #93731) — a gauge with the needle at speed.</summary>
        public static readonly Geometry SpeedometerGeometry = StreamGeometry.Parse(
            "M 12 3 C 5.9365932 3 1 7.9365932 1 14 C 1 16.000192 1.536449 17.883667 " +
            "2.4726562 19.501953 A 1.0001 1.0001 0 0 0 3.3378906 20.001953 L 20.662109 20 " +
            "A 1.0001 1.0001 0 0 0 21.527344 19.5 C 22.463453 17.881884 23 16.000192 23 14 " +
            "C 23 7.9365932 18.063407 3 12 3 z M 12 5 C 16.982593 5 21 9.0174068 21 14 " +
            "C 21 15.450887 20.612539 16.789626 20.005859 18 L 18.974609 18 A 1 1 0 0 0 " +
            "19 17.78125 A 1 1 0 0 0 18 16.78125 A 1 1 0 0 0 17 17.78125 A 1 1 0 0 0 " +
            "17.025391 18 L 6.8632812 18.001953 A 1 1 0 0 0 7 17.5 A 1 1 0 0 0 6 16.5 " +
            "A 1 1 0 0 0 5 17.5 A 1 1 0 0 0 5.1367188 18.001953 L 3.9960938 18.001953 " +
            "C 3.3888684 16.790962 3 15.451647 3 14 C 3 9.0174068 7.0174068 5 12 5 z " +
            "M 12 6 A 1 1 0 0 0 11 7 A 1 1 0 0 0 12 8 A 1 1 0 0 0 13 7 A 1 1 0 0 0 12 6 z " +
            "M 8.5 7 A 1 1 0 0 0 7.5 8 A 1 1 0 0 0 8.5 9 A 1 1 0 0 0 9.5 8 A 1 1 0 0 0 " +
            "8.5 7 z M 15.4375 7 A 1 1 0 0 0 14.4375 8 A 1 1 0 0 0 15.4375 9 A 1 1 0 0 0 " +
            "16.4375 8 A 1 1 0 0 0 15.4375 7 z M 18.041016 9.3964844 L 13.005859 12.273438 " +
            "A 2 2 0 0 0 12 12 A 2 2 0 0 0 10 14 A 2 2 0 0 0 12 16 A 2 2 0 0 0 14 14.009766 " +
            "L 19.033203 11.132812 L 18.041016 9.3964844 z M 6 9.5 A 1 1 0 0 0 5 10.5 " +
            "A 1 1 0 0 0 6 11.5 A 1 1 0 0 0 7 10.5 A 1 1 0 0 0 6 9.5 z M 5 13 A 1 1 0 0 0 " +
            "4 14 A 1 1 0 0 0 5 15 A 1 1 0 0 0 6 14 A 1 1 0 0 0 5 13 z M 19 13 A 1 1 0 0 0 " +
            "18 14 A 1 1 0 0 0 19 15 A 1 1 0 0 0 20 14 A 1 1 0 0 0 19 13 z");

        /// <summary>A magnifier with a plus for the zoom effect rows and their items (24x24 box) —
        /// the same glyph the sidebar's add-zoom button carries.</summary>
        public static readonly Geometry MagnifierGeometry = StreamGeometry.Parse(
            "M 9 2 C 5.1458514 2 2 5.1458514 2 9 C 2 12.854149 5.1458514 16 9 16 " +
            "C 10.747998 16 12.345009 15.348024 13.574219 14.28125 L 14 14.707031 L 14 16 " +
            "L 19.585938 21.585938 C 20.137937 22.137937 21.033938 22.137938 21.585938 " +
            "21.585938 C 22.137938 21.033938 22.137938 20.137938 21.585938 19.585938 " +
            "L 16 14 L 14.707031 14 L 14.28125 13.574219 C 15.348024 12.345009 16 " +
            "10.747998 16 9 C 16 5.1458514 12.854149 2 9 2 z M 9 4 C 11.773268 4 14 " +
            "6.2267316 14 9 C 14 11.773268 11.773268 14 9 14 C 6.2267316 14 4 11.773268 " +
            "4 9 C 4 6.2267316 6.2267316 4 9 4 z M 8.984375 5.9863281 A 1.0001 1.0001 0 0 0 " +
            "8 7 L 8 8 L 7 8 A 1.0001 1.0001 0 1 0 7 10 L 8 10 L 8 11 A 1.0001 1.0001 0 1 0 " +
            "10 11 L 10 10 L 11 10 A 1.0001 1.0001 0 1 0 11 8 L 10 8 L 10 7 A 1.0001 1.0001 " +
            "0 0 0 8.984375 5.9863281 z");

        /// <summary>The classic pointer for the cursor overlay row and its items (24x24 box):
        /// tip top-left, tail bottom-right. Hand-authored — VectorIcons has no pointer glyph, and
        /// the row's job is exactly "this is where the mouse was".</summary>
        public static readonly Geometry CursorArrowGeometry = StreamGeometry.Parse(
            "M5,2 L5,19.4 L9.4,15.4 L12.1,21.5 L14.6,20.4 L11.9,14.4 L17.6,14.4 Z");

        /// <summary>A keyboard for the keystroke overlay row and its items (24x24 box): a slab
        /// with two rows of keys and a space bar punched out of it (even-odd fill, so the keys are
        /// holes rather than a second shape to keep aligned).</summary>
        public static readonly Geometry KeyboardGeometry = StreamGeometry.Parse(
            "M2,5 L22,5 L22,19 L2,19 Z " +
            "M5,7.5 L7,7.5 L7,9.5 L5,9.5 Z M8,7.5 L10,7.5 L10,9.5 L8,9.5 Z " +
            "M11,7.5 L13,7.5 L13,9.5 L11,9.5 Z M14,7.5 L16,7.5 L16,9.5 L14,9.5 Z " +
            "M17,7.5 L19,7.5 L19,9.5 L17,9.5 Z " +
            "M5,11 L7,11 L7,13 L5,13 Z M8,11 L10,11 L10,13 L8,13 Z " +
            "M11,11 L13,11 L13,13 L11,13 Z M14,11 L16,11 L16,13 L14,13 Z " +
            "M17,11 L19,11 L19,13 L17,13 Z " +
            "M8,14.5 L16,14.5 L16,16.5 L8,16.5 Z");

        /// <summary>The four-point star cluster that marks an AI-backed feature (50x50 box;
        /// Icons8 "Sparkles", Material Filled #dQ0TcR10zyYB) — a big star with a small one off its
        /// shoulder. Filled rather than outlined: at badge size a hollow star reads as noise.
        /// VectorIcons has no sparkle glyph.</summary>
        public static readonly Geometry AiSparkleGeometry = StreamGeometry.Parse(
            "M22.462 11.035l2.88 7.097c1.204 2.968 3.558 5.322 6.526 6.526l7.097 2.88" +
            "c1.312.533 1.312 2.391 0 2.923l-7.097 2.88c-2.968 1.204-5.322 3.558-6.526 6.526" +
            "l-2.88 7.097c-.533 1.312-2.391 1.312-2.923 0l-2.88-7.097" +
            "c-1.204-2.968-3.558-5.322-6.526-6.526l-7.097-2.88c-1.312-.533-1.312-2.391 0-2.923" +
            "l7.097-2.88c2.968-1.204 5.322-3.558 6.526-6.526l2.88-7.097" +
            "C20.071 9.723 21.929 9.723 22.462 11.035z" +
            "M39.945 2.701l.842 2.428c.664 1.915 2.169 3.42 4.084 4.084l2.428.842" +
            "c.896.311.896 1.578 0 1.889l-2.428.842c-1.915.664-3.42 2.169-4.084 4.084l-.842 2.428" +
            "c-.311.896-1.578.896-1.889 0l-.842-2.428c-.664-1.915-2.169-3.42-4.084-4.084l-2.428-.842" +
            "c-.896-.311-.896-1.578 0-1.889l2.428-.842c1.915-.664 3.42-2.169 4.084-4.084l.842-2.428" +
            "C38.366 1.805 39.634 1.805 39.945 2.701z");

        /// <summary>A simple chain-link glyph (24x24 box); VectorIcons has no link icon.</summary>
        public static readonly Geometry LinkGeometry = StreamGeometry.Parse(
            "M3.9,12C3.9,10.29 5.29,8.9 7,8.9H11V7H7A5,5 0 0,0 2,12A5,5 0 0,0 7,17H11V15.1H7C5.29," +
            "15.1 3.9,13.71 3.9,12M8,13H16V11H8V13M17,7H13V8.9H17C18.71,8.9 20.1,10.29 20.1,12C20.1," +
            "13.71 18.71,15.1 17,15.1H13V17H17A5,5 0 0,0 22,12A5,5 0 0,0 17,7Z");

        /// <summary>Fast-forward chevrons without the end bar (16x16 box): the "this clip keeps
        /// going past the viewport edge" jump affordance on the timeline surface.</summary>
        public static readonly Geometry JumpRightGeometry = StreamGeometry.Parse(
            "M2,3 L8,8 L2,13 Z M8,3 L14,8 L8,13 Z");

        /// <summary>The same, mirrored for the left edge.</summary>
        public static readonly Geometry JumpLeftGeometry = StreamGeometry.Parse(
            "M14,3 L8,8 L14,13 Z M8,3 L2,8 L8,13 Z");

        /// <summary>A <see cref="GlyphIcon"/> drawing <paramref name="key"/>'s glyph centered in a
        /// <paramref name="box"/> square — see that class for why a <see cref="Path"/> with
        /// <c>Stretch.Uniform</c> would hang wide glyphs (the old 26 x 14 camera icon) above the
        /// row's text.</summary>
        public static Control NewIcon(string key, double box, IBrush brush) =>
            NewIcon(Find(key), box, brush);

        /// <summary>The same for a geometry this class carries itself (the chain link), which has
        /// no resource key to look up.</summary>
        public static Control NewIcon(Geometry geometry, double box, IBrush brush) =>
            new GlyphIcon(geometry, brush) { Width = box, Height = box };

        /// <summary>The named StreamGeometry from the application resources, or null (a Path with
        /// null Data simply draws nothing — the headers stay usable even if an icon goes missing).</summary>
        public static Geometry Find(string key)
        {
            var app = Application.Current;
            if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is Geometry geometry)
                return geometry;

            return null;
        }
    }
}
