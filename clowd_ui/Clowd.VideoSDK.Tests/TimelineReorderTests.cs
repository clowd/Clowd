using System;
using System.Collections.Generic;
using System.Linq;
using Clowd.UI.VideoEditor.Timeline;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // TimelineReorder is the geometry of dragging a track header by its grip. Pure math, no
    // Avalonia runtime — Clowd.Ui exposes its internals to this project via InternalsVisibleTo.
    public class TimelineReorderTests
    {
        /// <summary>Rows as the header panel sees them: video rows top to bottom (frontmost layer
        /// first), then the audio block, stacked from y = 0 with each kind's real height.</summary>
        private static IReadOnlyList<TimelineRow> Rows(params TimelineRowKind[] kinds)
        {
            var rows = new List<TimelineRow>();
            double top = 0;
            foreach (var kind in kinds)
            {
                var height = TimelineRowLayout.HeightOf(kind);
                rows.Add(new TimelineRow(Guid.NewGuid(), kind, top, height));
                top += height;
            }

            return rows;
        }

        /// <summary>Three picture rows over two audio rows — 56 + 26 (the text card's short row) +
        /// 56, then 2 x 36 — so the block boundaries land at y = 0, 56, 82, 138, 174, 210.</summary>
        private static IReadOnlyList<TimelineRow> Mixed() => Rows(
            TimelineRowKind.Video, TimelineRowKind.Text, TimelineRowKind.Video,
            TimelineRowKind.Audio, TimelineRowKind.Audio);

        [Fact]
        public void GroupRange_keeps_a_row_inside_its_own_block()
        {
            var rows = Mixed();

            // text and image rows are video tracks — they share the picture block, and the row
            // heights differing does not split it
            Assert.Equal((0, 2), TimelineReorder.GroupRange(rows, 0));
            Assert.Equal((0, 2), TimelineReorder.GroupRange(rows, 1));
            Assert.Equal((0, 2), TimelineReorder.GroupRange(rows, 2));

            Assert.Equal((3, 4), TimelineReorder.GroupRange(rows, 3));
            Assert.Equal((3, 4), TimelineReorder.GroupRange(rows, 4));
        }

        [Fact]
        public void DropIndexAt_asks_for_the_boundary_the_pointer_passed()
        {
            var rows = Mixed(); // picture rows at [0,56) [56,82) [82,138)

            Assert.Equal(0, TimelineReorder.DropIndexAt(rows, 0, 0));    // top of the first row
            Assert.Equal(0, TimelineReorder.DropIndexAt(rows, 0, 27));   // still above its midpoint
            Assert.Equal(1, TimelineReorder.DropIndexAt(rows, 0, 29));   // past it: below row 0
            Assert.Equal(2, TimelineReorder.DropIndexAt(rows, 0, 100));  // past the short text row
            Assert.Equal(3, TimelineReorder.DropIndexAt(rows, 0, 137));  // one past the last picture row
        }

        [Fact]
        public void DropIndexAt_clamps_to_the_block_however_far_the_pointer_goes()
        {
            var rows = Mixed();

            // a video row dragged down over the audio block (or off the panel) stops at the
            // picture block's last slot; the audio rows are not somewhere it can land
            Assert.Equal(3, TimelineReorder.DropIndexAt(rows, 1, 5_000));
            Assert.Equal(0, TimelineReorder.DropIndexAt(rows, 1, -5_000));

            // …and an audio row dragged up over the video rows stops at the audio block's first
            Assert.Equal(3, TimelineReorder.DropIndexAt(rows, 4, -5_000));
            Assert.Equal(5, TimelineReorder.DropIndexAt(rows, 4, 5_000));
        }

        [Fact]
        public void IndicatorY_sits_on_the_boundary_the_row_would_land_at()
        {
            var rows = Mixed();

            Assert.Equal(0, TimelineReorder.IndicatorY(rows, 0, 0));
            Assert.Equal(56, TimelineReorder.IndicatorY(rows, 0, 1));
            Assert.Equal(138, TimelineReorder.IndicatorY(rows, 0, 3));   // bottom of the picture block
            Assert.Equal(138, TimelineReorder.IndicatorY(rows, 3, 3));   // …which is the audio block's top
            Assert.Equal(210, TimelineReorder.IndicatorY(rows, 3, 5));   // bottom of the last audio row
        }

        /// <summary>The timeline draws video rows frontmost first, so dragging one <i>up</i> the
        /// panel raises it through the composite stack: display index 0 is the highest layer
        /// index.</summary>
        [Fact]
        public void TargetLayerIndex_flips_display_order_for_video_rows()
        {
            var rows = Mixed();

            Assert.Equal(2, TimelineReorder.TargetLayerIndex(rows, 2, 0));  // bottom row to the top = frontmost
            Assert.Equal(1, TimelineReorder.TargetLayerIndex(rows, 2, 1));
            Assert.Equal(0, TimelineReorder.TargetLayerIndex(rows, 0, 3));  // top row to the bottom = backmost
        }

        /// <summary>Audio rows are listed in the model's own order, so their drop needs no flip.</summary>
        [Fact]
        public void TargetLayerIndex_leaves_audio_rows_in_display_order()
        {
            var rows = Mixed();

            Assert.Equal(1, TimelineReorder.TargetLayerIndex(rows, 3, 5)); // first audio row to last
            Assert.Equal(0, TimelineReorder.TargetLayerIndex(rows, 4, 3)); // last audio row to first
        }

        [Fact]
        public void TargetLayerIndex_is_null_when_the_row_lands_where_it_started()
        {
            var rows = Mixed();

            // the two boundaries either side of a row are both "stay put" — the row is lifted out
            // before it goes back, so the slot below it is its own slot
            Assert.Null(TimelineReorder.TargetLayerIndex(rows, 1, 1));
            Assert.Null(TimelineReorder.TargetLayerIndex(rows, 1, 2));
            Assert.Null(TimelineReorder.TargetLayerIndex(rows, 4, 4));
            Assert.Null(TimelineReorder.TargetLayerIndex(rows, 4, 5));
        }

        /// <summary>A block of one has nowhere to drop — which is what the panel asks before it
        /// gives a row a grip at all.</summary>
        [Fact]
        public void A_lone_row_of_its_kind_has_no_move()
        {
            var rows = Rows(TimelineRowKind.Video, TimelineRowKind.Audio);

            Assert.Equal((0, 0), TimelineReorder.GroupRange(rows, 0));
            Assert.Equal((1, 1), TimelineReorder.GroupRange(rows, 1));
            Assert.Null(TimelineReorder.TargetLayerIndex(rows, 0, TimelineReorder.DropIndexAt(rows, 0, 500)));
            Assert.Null(TimelineReorder.TargetLayerIndex(rows, 1, TimelineReorder.DropIndexAt(rows, 1, -500)));
        }

        /// <summary>Every drop of every row, against a from-scratch reordering of the display list:
        /// lift the row out, put it back at the boundary, and the model index the panel sends must
        /// name the same row order.</summary>
        [Fact]
        public void Every_drop_agrees_with_lifting_the_row_out_and_putting_it_back()
        {
            var rows = Mixed();

            for (var from = 0; from < rows.Count; from++)
            {
                var (start, end) = TimelineReorder.GroupRange(rows, from);
                var audio = rows[from].Kind == TimelineRowKind.Audio;

                for (var drop = start; drop <= end + 1; drop++)
                {
                    // what the drag means, spelled out: the block's rows with the dragged one moved
                    var display = Enumerable.Range(start, end - start + 1).ToList();
                    display.Remove(from);
                    display.Insert(drop > from ? drop - 1 - start : drop - start, from);

                    var layerIndex = TimelineReorder.TargetLayerIndex(rows, from, drop);
                    if (layerIndex == null)
                    {
                        Assert.Equal(from, display[from - start]); // …because nothing moved
                        continue;
                    }

                    // the model lists the block back to front for video, in display order for audio
                    var model = audio ? display : Enumerable.Reverse(display).ToList();
                    Assert.Equal(layerIndex.Value, model.IndexOf(from));
                }
            }
        }
    }
}
