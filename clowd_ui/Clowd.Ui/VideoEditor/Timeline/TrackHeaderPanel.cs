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
    /// <see cref="TimelineRowLayout"/>, so the two columns stay pixel-aligned) with the drag grip,
    /// the track's kind icon and name, the enable button (eye → <c>Track.Hidden</c> for picture
    /// rows, speaker → <c>Track.Muted</c> for audio rows) and, on rows whose items are
    /// link-grouped, a static chain badge. Rebuilt wholesale on Structural project changes — under
    /// ten rows, so there is nothing to diff.
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

        /// <summary>The laid-out rows of the current build, and their visuals, in display order —
        /// index i of both is row i. The reorder drag works in this space (top to bottom, which for
        /// video rows is the reverse of the model's layer order).</summary>
        private IReadOnlyList<TimelineRow> _rows = Array.Empty<TimelineRow>();
        private readonly List<Border> _rowBorders = new List<Border>();
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
            _rowBorders.Clear();
            _linkBadges.Clear();
            _rows = Array.Empty<TimelineRow>();

            var palette = TimelinePalette.ForVariant(ActualThemeVariant);
            Background = palette.RulerBackground;
            _indicator.Background = palette.DropIndicatorBrush;

            var project = _session?.Project;
            if (project == null)
                return;

            _rows = TimelineRowLayout.Build(project);

            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                var track = project.Tracks.FirstOrDefault(t => t.Id == row.TrackId);
                if (track == null)
                    continue;

                var border = BuildRow(palette, project, track, row, i);
                _rowBorders.Add(border);
                _stack.Children.Add(border);
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

        private Border BuildRow(TimelinePalette palette, Project project, Track track, TimelineRow row, int rowIndex)
        {
            var trackId = track.Id;
            var isAudio = row.Kind == TimelineRowKind.Audio;
            var buttonSize = Math.Min(20, row.Height - 4);

            var dock = new DockPanel { LastChildFill = true };

            // ------- drag grip, leftmost. Its cell is reserved on every row; the dots (and the
            // drag) are only there when the row has somewhere to go — a locked row is one the
            // context menu's Move Up/Down refuse too, and a lone row of its kind cannot reorder
            // against anything.
            var group = TimelineReorder.GroupRange(_rows, rowIndex);
            var draggable = !track.Locked && group.End > group.Start;
            var grip = _drag.BuildGrip(rowIndex, draggable, palette.GripBrush, palette.GripHoverBrush,
                new Thickness(GripMarginX, 2, GripMarginX, 2));
            DockPanel.SetDock(grip, Dock.Left);
            dock.Children.Add(grip);

            // ------- enable button (eye / speaker), rightmost — the layers panel's row button, so
            // the two panels' rows read the same. SetTrackHidden/Muted raise a Structural change,
            // which rebuilds this whole panel: the fresh button carries the fresh glyph, so nothing
            // here updates its own icon, and the state is the glyph rather than a checked fill.
            var enabled = isAudio ? !track.Muted : !track.Hidden;
            var enable = RowIconButton.Build(
                TimelineIcons.Find(isAudio
                    ? (enabled ? "IconSpeakerEnabled" : "IconSpeakerDisabled")
                    : (enabled ? "IconEye" : "IconEyeOff")),
                Brushes.White,
                isAudio
                    ? (enabled ? "Mute this row" : "Include this audio in the mix")
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

            // ------- link badge: a label saying the row's items move with the recording, not a
            // control. Built on every row and collapsed when unlinked, so RefreshLinkBadges can
            // flip it either way without a rebuild (unlink/relink are Mapping changes).
            var badge = new Border
            {
                Width = buttonSize,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = project.Items.Any(i => i.TrackId == trackId && i.LinkGroupId != null),
                Child = TimelineIcons.NewIcon(TimelineIcons.LinkGeometry, buttonSize * 0.6, palette.LabelBrush),
                Opacity = 0.7,
                Background = Brushes.Transparent, // a null background is not hit-testable — no tooltip
            };
            ToolTip.SetTip(badge, SyncedTip);
            DockPanel.SetDock(badge, Dock.Right);
            dock.Children.Add(badge);
            _linkBadges.Add((trackId, badge));

            // ------- kind icon + name
            var icon = TimelineIcons.NewIcon(KindIconKey(row.Kind), 13, palette.LabelBrush);
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon);

            var name = new TextBlock
            {
                Text = String.IsNullOrEmpty(track.Name) ? (isAudio ? "Audio" : "Video") : track.Name,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = palette.LabelBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            dock.Children.Add(name);

            return new Border
            {
                Height = row.Height,
                Padding = new Thickness(2, 0, 2, 0),
                BorderThickness = new Thickness(0, 0, 1, 1),
                BorderBrush = palette.RowSeparatorPen.Brush,
                Child = dock,
            };
        }

        // --------------------------------------------------- reorder drag (RowReorderDrag host)

        int IRowReorderDragHost.RowCount => _rows.Count;

        (double Top, double Height) IRowReorderDragHost.RowExtent(int row) =>
            (_rows[row].Top, _rows[row].Height);

        (int Start, int End) IRowReorderDragHost.SlotGroup(int row) =>
            TimelineReorder.GroupRange(_rows, row);

        /// <summary>A press while another control's drag owns the session would land its drop as
        /// a mid-gesture Preview and be rolled back with it.</summary>
        bool IRowReorderDragHost.CanBeginDrag => _session is { IsGestureActive: false };

        void IRowReorderDragHost.SetRowLifted(int row, bool lifted)
        {
            if (row < _rowBorders.Count)
                _rowBorders[row].Opacity = lifted ? 0.45 : 1;
        }

        void IRowReorderDragHost.Drop(int fromRow, int dropSlot)
        {
            // Structural — the mutation rebuilds this whole panel; null is a drag that came home.
            var layerIndex = TimelineReorder.TargetLayerIndex(_rows, fromRow, dropSlot);
            if (layerIndex != null)
                _session?.MoveTrackToIndex(_rows[fromRow].TrackId, layerIndex.Value, this);
        }

        private static string KindIconKey(TimelineRowKind kind) => kind switch
        {
            TimelineRowKind.Audio => "IconMicrophoneEnabled",
            TimelineRowKind.Text => "IconToolText",
            TimelineRowKind.Image => "IconPhoto",
            _ => "IconVideo",
        };
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

        /// <summary>A simple chain-link glyph (24x24 box); VectorIcons has no link icon.</summary>
        public static readonly Geometry LinkGeometry = StreamGeometry.Parse(
            "M3.9,12C3.9,10.29 5.29,8.9 7,8.9H11V7H7A5,5 0 0,0 2,12A5,5 0 0,0 7,17H11V15.1H7C5.29," +
            "15.1 3.9,13.71 3.9,12M8,13H16V11H8V13M17,7H13V8.9H17C18.71,8.9 20.1,10.29 20.1,12C20.1," +
            "13.71 18.71,15.1 17,15.1H13V17H17A5,5 0 0,0 22,12A5,5 0 0,0 17,7Z");

        /// <summary>A <see cref="GlyphIcon"/> drawing <paramref name="key"/>'s glyph centred in a
        /// <paramref name="box"/> square — see that class for why a <see cref="Path"/> with
        /// <c>Stretch.Uniform</c> would hang wide glyphs (IconVideo's 26 x 14 camera) above the
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
