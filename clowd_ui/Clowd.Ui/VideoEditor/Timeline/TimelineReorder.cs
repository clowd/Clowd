using System;
using System.Collections.Generic;
using Clowd.UI.Controls;

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>
    /// The geometry behind dragging a track header by its grip: which rows a dragged row may land
    /// among, which boundary a pointer y is asking for, where to draw the line saying so, and what
    /// the drop means to the model. Pure — no Avalonia types — so the panel that draws the drag and
    /// the tests that assert it run the same code.
    ///
    /// <para>Everything here works in <b>display</b> space: the top-to-bottom row order
    /// <see cref="TimelineRowLayout.Build"/> lays out, which for video rows is the reverse of the
    /// model's ascending <c>(Order, Id)</c> layer order — except where the input-overlay rows
    /// were lifted to sit glued above their screen row, which is why the drop translation reads
    /// <see cref="TimelineRow.LayerIndex"/> instead of flipping positions. Drop positions are
    /// <i>insertion slots</i>, so they run from the first row of a block to one past its
    /// last.</para>
    /// </summary>
    internal static class TimelineReorder
    {
        /// <summary>The rows the row at <paramref name="rowIndex"/> may be reordered among: its
        /// block — the pinned speed row, the video block (video, zoom and the input-overlay rows
        /// sitting in it), or the audio block — which <see cref="TimelineRowLayout.Build"/> lays
        /// out contiguously in that order. A row cannot cross between blocks — its kind is a
        /// property of the track, not of where it sits — and the speed row is a block of one,
        /// which is what denies it a grip. So is each cursor/keyboard row: it is pinned to its
        /// screen row and the session refuses to reorder it at all. Inclusive at both ends.</summary>
        public static (int Start, int End) GroupRange(IReadOnlyList<TimelineRow> rows, int rowIndex)
        {
            RequireRow(rows, rowIndex);

            // pinned rows are blocks of one — they never move, so there is nothing to range over.
            if (TimelineRowLayout.IsInputOverlay(rows[rowIndex].Kind))
                return (rowIndex, rowIndex);

            var block = BlockOf(rows, rowIndex);

            var start = rowIndex;
            while (start > 0 && BlockOf(rows, start - 1) == block)
                start--;

            var end = rowIndex;
            while (end + 1 < rows.Count && BlockOf(rows, end + 1) == block)
                end++;

            return (start, end);
        }

        /// <summary>The insertion slot a drop at <paramref name="y"/> is asking for: the boundary
        /// nearest the pointer, clamped to the dragged row's own block and then to one the model
        /// will honor (see <see cref="LegalSlot"/>). A pointer above the block (or off the top of
        /// the panel) gives its first slot, one below gives its last.</summary>
        public static int DropIndexAt(IReadOnlyList<TimelineRow> rows, int rowIndex, double y)
        {
            var (start, end) = GroupRange(rows, rowIndex);
            return LegalSlot(rows, rowIndex, start,
                RowReorderMath.DropSlot(start, end, y, i => (rows[i].Top, rows[i].Height)));
        }

        /// <summary>
        /// The nearest slot at or above <paramref name="slot"/> that a drop may actually land on.
        /// Cursor/keyboard rows are drawn glued to the top of their screen row, so no row may come
        /// to rest between them and it: a boundary directly below an overlay row is pushed up past
        /// the whole overlay run. And the screen row itself may not rise above its own overlays —
        /// <c>EditorSession</c> refuses that move outright — so when the dragged row is the one
        /// under the run, its own top edge is as high as the indicator may go (a no-op drop).
        /// </summary>
        private static int LegalSlot(IReadOnlyList<TimelineRow> rows, int rowIndex, int start, int slot)
        {
            var floor = rowIndex > 0 && TimelineRowLayout.IsInputOverlay(rows[rowIndex - 1].Kind)
                ? rowIndex
                : start;

            slot = Math.Max(slot, floor);
            while (slot > floor && TimelineRowLayout.IsInputOverlay(rows[slot - 1].Kind))
                slot--;

            return slot;
        }

        /// <summary>The slot a drop at <paramref name="dropIndex"/> really lands on — what the
        /// drag controller asks after its own hit test so the indicator line never promises a
        /// landing the model would refuse. Idempotent; see <see cref="LegalSlot"/>.</summary>
        public static int CoerceDropIndex(IReadOnlyList<TimelineRow> rows, int rowIndex, int dropIndex)
        {
            var (start, end) = GroupRange(rows, rowIndex);
            return LegalSlot(rows, rowIndex, start, Math.Clamp(dropIndex, start, end + 1));
        }

        /// <summary>Where the drop indicator sits for <paramref name="dropIndex"/> — the top edge
        /// of the row that would be pushed down, or the bottom edge of the block when the drop is
        /// past its last row.</summary>
        public static double IndicatorY(IReadOnlyList<TimelineRow> rows, int rowIndex, int dropIndex)
        {
            var (start, end) = GroupRange(rows, rowIndex);
            return RowReorderMath.IndicatorY(start, end, dropIndex, i => (rows[i].Top, rows[i].Height));
        }

        /// <summary>
        /// The drop as the index <see cref="Clowd.VideoSDK.Editing.EditorSession.MoveTrackToIndex"/>
        /// counts in, or null when the row would land back where it started (a click, or a drag
        /// that came home).
        ///
        /// <para>Two conversions happen here. The row is lifted out before it is put back, so a
        /// drop below its own slot lands one place higher than the boundary the indicator sat on;
        /// and video-block rows are drawn highest layer first, so a drop must be translated back
        /// into the model's ascending space — audio rows are listed in model order and need only
        /// the lift correction. The speed row is always null — it is pinned, and so is every
        /// cursor/keyboard row.</para>
        ///
        /// <para>The video translation counts in <see cref="TimelineRow.LayerIndex"/> rather than
        /// flipping display positions, because the display is <i>not</i> always the exact reverse
        /// of the model: the input-overlay rows are drawn glued above their screen row wherever
        /// their real <c>Order</c> sits (see <see cref="TimelineRowLayout.Build"/>), and a flip
        /// through a displaced overlay row would land the drop a layer away from the boundary the
        /// indicator promised. The dragged row instead comes to rest directly beneath the display
        /// row above the slot — never an overlay, <see cref="LegalSlot"/> pushes every slot out
        /// of a glued run — by taking that row's layer index once the dragged row is lifted out
        /// from under it. Non-overlay rows are displayed in strict descending layer order, so
        /// resting directly beneath that row in the model is exactly the promised display slot.</para>
        /// </summary>
        public static int? TargetLayerIndex(IReadOnlyList<TimelineRow> rows, int rowIndex, int dropIndex)
        {
            var (start, end) = GroupRange(rows, rowIndex);
            var insert = LegalSlot(rows, rowIndex, start, Math.Clamp(dropIndex, start, end + 1));

            var target = RowReorderMath.TargetRow(rowIndex, insert);
            if (target == rowIndex)
                return null;

            var block = BlockOf(rows, rowIndex);
            // the pinned rows: the speed row and the input overlays. Their block of one already
            // collapses every drop into the no-op above; this says so where it is read.
            if (block == RowBlock.Speed || TimelineRowLayout.IsInputOverlay(rows[rowIndex].Kind))
                return null;

            if (block == RowBlock.Audio)
                return target - start;

            // the block's top slot has no row above it: the frontmost layer.
            if (insert == start)
                return end - start;

            var above = rows[insert - 1]; // never the dragged row itself: target != rowIndex
            return above.LayerIndex > rows[rowIndex].LayerIndex
                ? above.LayerIndex - 1 // lifting the dragged row out shifts the rows above it down
                : above.LayerIndex;    // inserting at its index pushes it up, leaving us beneath it
        }

        private enum RowBlock
        {
            Speed,
            VideoZoom,
            Audio,
        }

        /// <summary>Which block a row belongs to. Cursor/keyboard rows count as video-block rows
        /// even though they cannot move: they sit inside that block, they take up a layer index in
        /// the session's space, and a block that ended at them would strand the rows either side
        /// of an overlay in ranges of their own.</summary>
        private static RowBlock BlockOf(IReadOnlyList<TimelineRow> rows, int rowIndex) =>
            rows[rowIndex].Kind switch
            {
                TimelineRowKind.Speed => RowBlock.Speed,
                TimelineRowKind.Audio => RowBlock.Audio,
                _ => RowBlock.VideoZoom,
            };

        private static void RequireRow(IReadOnlyList<TimelineRow> rows, int rowIndex)
        {
            ArgumentNullException.ThrowIfNull(rows);

            if (rowIndex < 0 || rowIndex >= rows.Count)
                throw new ArgumentOutOfRangeException(nameof(rowIndex));
        }
    }
}
