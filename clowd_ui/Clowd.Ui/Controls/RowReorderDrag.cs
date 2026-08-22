using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Clowd.Drawing;

namespace Clowd.UI.Controls
{
    /// <summary>
    /// The vertical geometry shared by every drag-to-reorder list: which insertion slot a pointer
    /// y is asking for, where the drop indicator sits for it, and which row the drop means once
    /// the dragged row is lifted out. Pure — rows come in as a <c>(Top, Height)</c> accessor — so
    /// the panels that draw the drag and the tests that assert it run the same code.
    /// </summary>
    internal static class RowReorderMath
    {
        /// <summary>The insertion slot a pointer at <paramref name="y"/> is asking for: the
        /// boundary nearest the pointer, clamped to the dragged row's own block
        /// (<paramref name="start"/>..<paramref name="end"/>, inclusive rows — so slots run to one
        /// past <paramref name="end"/>). A pointer above the block gives its first slot, one below
        /// gives its last.</summary>
        public static int DropSlot(int start, int end, double y, Func<int, (double Top, double Height)> rows)
        {
            var slot = start;
            while (slot <= end && y > rows(slot).Top + rows(slot).Height / 2)
                slot++;

            return slot;
        }

        /// <summary>Where the drop indicator sits for <paramref name="slot"/> — the top edge of
        /// the row that would be pushed down, or the bottom edge of the block when the drop is
        /// past its last row.</summary>
        public static double IndicatorY(int start, int end, int slot, Func<int, (double Top, double Height)> rows)
        {
            slot = Math.Clamp(slot, start, end + 1);
            if (slot <= end)
                return rows(slot).Top;

            var (top, height) = rows(end);
            return top + height;
        }

        /// <summary>The display row the drop lands on: the row is lifted out before it is put
        /// back, so a drop below its own slot lands one place higher than the boundary the
        /// indicator sat on. Equal to <paramref name="fromRow"/> when nothing moved.</summary>
        public static int TargetRow(int fromRow, int dropSlot) =>
            dropSlot > fromRow ? dropSlot - 1 : dropSlot;
    }

    /// <summary>What a panel of reorderable rows tells <see cref="RowReorderDrag"/> about itself.
    /// Everything is asked at drag time, so a host only has to keep its own row list current.</summary>
    internal interface IRowReorderDragHost
    {
        int RowCount { get; }

        /// <summary>A row's vertical extent, in the coordinate space the controller was given.</summary>
        (double Top, double Height) RowExtent(int row);

        /// <summary>The rows the given row may be reordered among (inclusive at both ends) — the
        /// whole list, unless the host partitions rows into blocks a drag cannot cross.</summary>
        (int Start, int End) SlotGroup(int row);

        /// <summary>Bends the slot the pointer asked for to one the host's model will actually
        /// honor — for boundaries a block cannot express, e.g. a pair of rows glued together that
        /// nothing may land between. Called on every move, so it must be idempotent and cheap; the
        /// default accepts whatever the pointer picked.</summary>
        int CoerceSlot(int row, int slot) => slot;

        /// <summary>False refuses the press outright — e.g. while another control's gesture owns
        /// the model and a mid-gesture mutation would be rolled back with it.</summary>
        bool CanBeginDrag { get; }

        /// <summary>Shows/clears the "lifted out" look on the dragged row (the panels dim it).</summary>
        void SetRowLifted(int row, bool lifted);

        /// <summary>Commits the drop. <paramref name="dropSlot"/> is the insertion slot in display
        /// space, within the row's own <see cref="SlotGroup"/> — map it with
        /// <see cref="RowReorderMath.TargetRow"/> (or richer model math) and mutate; a drop that
        /// came home is the host's no-op to detect. Only called for a real drag, never a click.</summary>
        void Drop(int fromRow, int dropSlot);
    }

    /// <summary>
    /// Drag-to-reorder for a panel of vertically stacked rows, shared by the video editor's track
    /// headers and the image editor's layers panel: builds the grip cell rows grab by (dot glyph,
    /// grab/grabbing cursors, hover brighten), then runs the whole gesture — press, threshold,
    /// lift, drop-indicator line, Esc/capture-loss cancel — and hands the finished drop to the
    /// host. The host keeps what is genuinely its own: row geometry, slot constraints, and what a
    /// drop means to its model.
    /// </summary>
    internal sealed class RowReorderDrag
    {
        /// <summary>Width of the grip cell. Hosts reserve it on every row — including the ones
        /// that cannot be dragged — so the content down the column stays on one left edge.</summary>
        public const double GripWidth = 10;

        /// <summary>Grip dots at rest. Not fully opaque — bright dots on every row would
        /// out-shout the row content — but well clear of the "is that an artifact?" range.</summary>
        private const double GripRestOpacity = 0.75;

        /// <summary>How far the pointer has to travel before a press on a grip becomes a drag: a
        /// click that wobbles a pixel must not reorder anything.</summary>
        private const double DragThresholdPx = 4;

        /// <summary>The drag grip: two columns of four dots (a 6x13 box), the arrangement every
        /// reorderable list uses. Drawn at its natural size — a grip that scaled with the row
        /// would read as a different control on short rows.</summary>
        public static readonly Geometry DragGripGeometry = BuildDragGrip();

        private readonly Control _owner;
        private readonly Visual _coordinateSpace;
        private readonly Border _indicator;
        private readonly IRowReorderDragHost _host;

        private const string GripTip = "Drag to reorder";

        private int _fromRow = -1;   // -1 = no press being tracked
        private int _dropSlot;       // insertion slot in display space, in [groupStart, groupEnd + 1]
        private bool _dragging;      // …and past the threshold, so the drop indicator is up
        private double _originY;
        private IPointer _pointer;
        private Control _pressedGrip;

        /// <summary>
        /// <paramref name="owner"/> takes the pointer capture, the grabbing cursor and the move/
        /// release handling — the drag leaves the pressed row at once, so it must be an ancestor
        /// of every row. <paramref name="coordinateSpace"/> is the visual the host's
        /// <see cref="IRowReorderDragHost.RowExtent"/> values and <paramref name="indicator"/>'s
        /// top margin are measured in (often the owner itself). The indicator element stays the
        /// host's: it decides the brush and where in its tree the line paints over the rows.
        /// </summary>
        public RowReorderDrag(Control owner, Visual coordinateSpace, Border indicator, IRowReorderDragHost host)
        {
            _owner = owner;
            _coordinateSpace = coordinateSpace;
            _indicator = indicator;
            _host = host;

            _owner.AddHandler(InputElement.PointerMovedEvent, Owner_PointerMoved, RoutingStrategies.Bubble);
            _owner.AddHandler(InputElement.PointerReleasedEvent, Owner_PointerReleased, RoutingStrategies.Bubble);
            _owner.AddHandler(InputElement.PointerCaptureLostEvent, Owner_PointerCaptureLost, RoutingStrategies.Direct);
        }

        /// <summary>The grip cell: a transparent <see cref="Border"/> (a null background would not
        /// hit-test, leaving only the dots themselves grabbable) around the dot pattern, carrying
        /// the grab cursor and the press that opens a drag. <paramref name="draggable"/> false
        /// builds the same cell empty and inert, purely to keep the column aligned.</summary>
        public Control BuildGrip(int rowIndex, bool draggable, IBrush restBrush, IBrush hoverBrush, Thickness margin)
        {
            var grip = new Border
            {
                Width = GripWidth,
                Margin = margin,
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            if (!draggable)
            {
                grip.IsHitTestVisible = false;
                return grip;
            }

            var dots = new Path
            {
                Data = DragGripGeometry,
                Fill = restBrush,
                Stretch = Stretch.None,
                Opacity = GripRestOpacity,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false, // the Border is the whole target, dots and gaps alike
            };
            grip.Child = dots;

            // The browsers' grab hand, not Hand (the click cursor) and not SizeAll — grab is the
            // one cursor that only ever means "this can be dragged".
            grip.Cursor = DragCursors.Grab;
            ToolTip.SetTip(grip, GripTip);

            // the hover cue is the dots themselves brightening to full-contrast — a background
            // behind them would read as a button, and this is a handle
            grip.PointerEntered += (_, _) =>
            {
                dots.Opacity = 1;
                dots.Fill = hoverBrush;
            };
            grip.PointerExited += (_, _) =>
            {
                dots.Opacity = GripRestOpacity;
                dots.Fill = restBrush;
            };
            grip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(_owner).Properties.IsLeftButtonPressed || _fromRow != -1)
                    return;

                // a second pointer (touch/pen) can land here while the host is not ready to move
                // rows — e.g. another control's drag owns its model
                if (!_host.CanBeginDrag)
                    return;

                _fromRow = rowIndex;
                _dropSlot = rowIndex;
                _dragging = false;
                _originY = e.GetPosition(_coordinateSpace).Y;
                _pointer = e.Pointer;

                // no tooltip while the button is down — clearing the tip (not just closing) keeps
                // the hover timer from re-opening it mid-drag; EndDrag puts it back
                ToolTip.SetIsOpen(grip, false);
                ToolTip.SetTip(grip, null);
                _pressedGrip = grip;
                e.Pointer.Capture(_owner); // the owner, not the grip: the drag leaves the row at once
                _owner.Cursor = DragCursors.Grabbing; // the capture makes the owner's cursor the shown one
                e.Handled = true; // rows may select on press; a grab is not a click
                HookEscape(true);
            };

            return grip;
        }

        /// <summary>Aborts any active drag, restoring the rows as they were. Hosts call this
        /// before a rebuild destroys the row visuals the drag is holding on to.</summary>
        public void Cancel()
        {
            if (_fromRow == -1)
                return;

            var pointer = _pointer;
            EndDrag();                 // clears state first so the capture-lost re-entry is a no-op
            pointer?.Capture(null);
        }

        private void Owner_PointerMoved(object sender, PointerEventArgs e)
        {
            if (_fromRow == -1 || !Equals(e.Pointer.Captured, _owner))
                return;

            var y = e.GetPosition(_coordinateSpace).Y;
            if (!_dragging)
            {
                if (Math.Abs(y - _originY) < DragThresholdPx)
                    return;

                _dragging = true;
                _host.SetRowLifted(_fromRow, true);
            }

            var (start, end) = _host.SlotGroup(_fromRow);
            SetDropSlot(_host.CoerceSlot(_fromRow, RowReorderMath.DropSlot(start, end, y, _host.RowExtent)),
                start, end);
        }

        private void Owner_PointerReleased(object sender, PointerReleasedEventArgs e)
        {
            if (_fromRow == -1 || !Equals(e.Pointer.Captured, _owner))
                return;

            var fromRow = _fromRow;
            var dropSlot = _dropSlot;
            var dragged = _dragging;

            EndDrag();               // before Capture(null): it re-enters Owner_PointerCaptureLost
            e.Pointer.Capture(null);

            // the host's mutation typically rebuilds its whole panel — which is why nothing here
            // may touch the old row visuals afterwards
            if (dragged)
                _host.Drop(fromRow, dropSlot);
        }

        private void Owner_PointerCaptureLost(object sender, PointerCaptureLostEventArgs e)
        {
            if (_fromRow != -1)
                EndDrag(); // capture lost without a release is an abort — the rows stay as they were
        }

        /// <summary>Esc cancels the drag. The owner holds the pointer, not the focus, so the key
        /// has to be caught on the way down the tree rather than waited for here.</summary>
        private void HookEscape(bool on)
        {
            var top = TopLevel.GetTopLevel(_owner);
            if (top == null)
                return;

            if (on)
                top.AddHandler(InputElement.KeyDownEvent, Drag_KeyDown, RoutingStrategies.Tunnel);
            else
                top.RemoveHandler(InputElement.KeyDownEvent, Drag_KeyDown);
        }

        private void Drag_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || _fromRow == -1)
                return;

            Cancel();
            e.Handled = true;
        }

        private void EndDrag()
        {
            if (_dragging && _fromRow < _host.RowCount)
                _host.SetRowLifted(_fromRow, false);

            _dragging = false;
            _fromRow = -1;
            _pointer = null;
            _indicator.IsVisible = false;
            _owner.Cursor = null;
            HookEscape(false);

            if (_pressedGrip != null)
            {
                ToolTip.SetTip(_pressedGrip, GripTip);
                _pressedGrip = null;
            }
        }

        private void SetDropSlot(int slot, int start, int end)
        {
            if (_indicator.IsVisible && _dropSlot == slot)
                return;

            _dropSlot = slot;

            // the line straddles the boundary, except at the two outermost ones — half of it would
            // fall outside the rows there, where it reads as a thinner line meaning something else
            var y = RowReorderMath.IndicatorY(start, end, slot, _host.RowExtent) - _indicator.Height / 2;
            var (lastTop, lastHeight) = _host.RowExtent(_host.RowCount - 1);
            var last = lastTop + lastHeight - _indicator.Height;
            _indicator.Margin = new Thickness(0, Math.Clamp(y, 0, Math.Max(0, last)), 0, 0);
            _indicator.IsVisible = true;
        }

        private static Geometry BuildDragGrip()
        {
            const double diameter = 2.5;
            const double pitch = 3.5;

            var dots = new GeometryGroup();
            for (var column = 0; column < 2; column++)
            {
                for (var row = 0; row < 4; row++)
                    dots.Children.Add(new EllipseGeometry(
                        new Rect(column * pitch, row * pitch, diameter, diameter)));
            }

            return dots;
        }
    }
}
