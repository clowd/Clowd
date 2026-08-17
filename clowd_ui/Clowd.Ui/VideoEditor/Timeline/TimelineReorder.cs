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
    /// model's ascending <c>(Order, Id)</c> layer order. Drop positions are <i>insertion slots</i>,
    /// so they run from the first row of a block to one past its last.</para>
    /// </summary>
    internal static class TimelineReorder
    {
        /// <summary>The rows the row at <paramref name="rowIndex"/> may be reordered among: its
        /// block — the pinned speed row, the video block (video and zoom rows), or the audio
        /// block — which <see cref="TimelineRowLayout.Build"/> lays out contiguously in that
        /// order. A row cannot cross between blocks — its kind is a property of the track, not of
        /// where it sits — and the speed row is a block of one, which is what denies it a grip.
        /// Inclusive at both ends.</summary>
        public static (int Start, int End) GroupRange(IReadOnlyList<TimelineRow> rows, int rowIndex)
        {
            RequireRow(rows, rowIndex);

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
        /// nearest the pointer, clamped to the dragged row's own block. A pointer above the block
        /// (or off the top of the panel) gives its first slot, one below gives its last.</summary>
        public static int DropIndexAt(IReadOnlyList<TimelineRow> rows, int rowIndex, double y)
        {
            var (start, end) = GroupRange(rows, rowIndex);
            return RowReorderMath.DropSlot(start, end, y, i => (rows[i].Top, rows[i].Height));
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
        /// and video-block rows are drawn highest layer first, so their display order is the
        /// reverse of the model's — audio rows are listed in model order and need no flip. The
        /// video block's flip counts zoom rows too, matching the session's index space (non-audio
        /// tracks minus the speed row); the speed row itself is always null — it is pinned.</para>
        /// </summary>
        public static int? TargetLayerIndex(IReadOnlyList<TimelineRow> rows, int rowIndex, int dropIndex)
        {
            var (start, end) = GroupRange(rows, rowIndex);
            var insert = Math.Clamp(dropIndex, start, end + 1);

            var target = RowReorderMath.TargetRow(rowIndex, insert);
            if (target == rowIndex)
                return null;

            var block = BlockOf(rows, rowIndex);
            if (block == RowBlock.Speed)
                return null;

            var within = target - start;
            return block == RowBlock.Audio ? within : end - start - within;
        }

        private enum RowBlock
        {
            Speed,
            VideoZoom,
            Audio,
        }

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
