using System;
using System.Collections.Generic;
using Clowd.UI.VideoEditor.Timeline;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    // TimelineHitTester decides what a pointer press means on the timeline surface. Pure geometry,
    // no Avalonia runtime — Clowd.Ui exposes its internals to this project via InternalsVisibleTo.
    public class TimelineHitTesterTests
    {
        private const double Tol = TimelineHitTester.HitTolerance;
        private const double RulerHeight = 20;

        // Row 0 (video): y 20..76. Row 1 (audio): y 76..112.
        private const double Row0Y = 40;
        private const double Row1Y = 90;

        private static readonly Guid ItemA = Guid.NewGuid();
        private static readonly Guid ItemB = Guid.NewGuid();
        private static readonly Guid Narrow = Guid.NewGuid();

        /// <summary>Two wide items on row 0 with 8 px between their facing edges, one item on
        /// row 1.</summary>
        private static IReadOnlyList<TimelineItemRect> Rects() => new[]
        {
            new TimelineItemRect(ItemA, 0, 100, 50, 20, 56),   // x 100..150
            new TimelineItemRect(ItemB, 0, 158, 142, 20, 56),  // x 158..300
            new TimelineItemRect(Narrow, 1, 400, 10, 76, 36),  // x 400..410, too narrow for grips
        };

        [Fact]
        public void The_playhead_is_not_a_target_wherever_it_is_parked()
        {
            // It is moved from the ruler alone, so a press on the rows always means what is drawn
            // there: parked on item A's start edge it still trims, and over a body it still selects.
            Assert.Equal(TimelineHitKind.ItemStart,
                TimelineHitTester.HitTest(100, Row0Y, RulerHeight, Rects()).Kind);
            Assert.Equal(TimelineHitKind.ItemBody,
                TimelineHitTester.HitTest(200, Row0Y, RulerHeight, Rects()).Kind);
        }

        [Fact]
        public void Ruler_wins_above_the_rows()
        {
            var hit = TimelineHitTester.HitTest(200, 5, RulerHeight, Rects());
            Assert.Equal(TimelineHitKind.Ruler, hit.Kind);
            Assert.Equal(-1, hit.RowIndex);
        }

        [Fact]
        public void Item_edges_beat_the_body()
        {
            var start = TimelineHitTester.HitTest(102, Row0Y, RulerHeight, Rects());
            Assert.Equal(TimelineHitKind.ItemStart, start.Kind);
            Assert.Equal(ItemA, start.ItemId);
            Assert.Equal(0, start.RowIndex);

            var end = TimelineHitTester.HitTest(148, Row0Y, RulerHeight, Rects());
            Assert.Equal(TimelineHitKind.ItemEnd, end.Kind);
            Assert.Equal(ItemA, end.ItemId);
        }

        [Fact]
        public void Nearest_edge_wins_between_adjacent_items()
        {
            // x=155: 5 px from A's end, 3 px from B's start.
            var hit = TimelineHitTester.HitTest(155, Row0Y, RulerHeight, Rects());
            Assert.Equal(TimelineHitKind.ItemStart, hit.Kind);
            Assert.Equal(ItemB, hit.ItemId);

            // x=152: 2 px from A's end, 6 px from B's start.
            var other = TimelineHitTester.HitTest(152, Row0Y, RulerHeight, Rects());
            Assert.Equal(TimelineHitKind.ItemEnd, other.Kind);
            Assert.Equal(ItemA, other.ItemId);
        }

        [Fact]
        public void At_a_seam_the_pointer_side_picks_the_edge()
        {
            // A and B are back to back (a split): both edges sit at x=150, so distance alone can
            // never separate them. The pointer's side of the seam decides — left of it trims A's
            // end, at/right of it trims B's start (the half-open [X, Right) convention the body
            // scan uses). Without the tie-break B's in-point would be ungrabbable at any zoom.
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var rects = new[]
            {
                new TimelineItemRect(a, 0, 100, 50, 20, 56),   // x 100..150
                new TimelineItemRect(b, 0, 150, 150, 20, 56),  // x 150..300
            };

            var left = TimelineHitTester.HitTest(148, Row0Y, RulerHeight, rects);
            Assert.Equal(TimelineHitKind.ItemEnd, left.Kind);
            Assert.Equal(a, left.ItemId);

            var right = TimelineHitTester.HitTest(152, Row0Y, RulerHeight, rects);
            Assert.Equal(TimelineHitKind.ItemStart, right.Kind);
            Assert.Equal(b, right.ItemId);

            var exact = TimelineHitTester.HitTest(150, Row0Y, RulerHeight, rects);
            Assert.Equal(TimelineHitKind.ItemStart, exact.Kind);
            Assert.Equal(b, exact.ItemId);
        }

        [Fact]
        public void Body_hits_report_the_item_and_its_row()
        {
            var hit = TimelineHitTester.HitTest(250, Row0Y, RulerHeight, Rects());
            Assert.Equal(TimelineHitKind.ItemBody, hit.Kind);
            Assert.Equal(ItemB, hit.ItemId);
            Assert.Equal(0, hit.RowIndex);
        }

        [Fact]
        public void Narrow_items_have_no_edge_grips()
        {
            // 10 px wide: pressing on what would be its start edge selects the body instead, so the
            // item stays selectable and movable until the user zooms in.
            var hit = TimelineHitTester.HitTest(401, Row1Y, RulerHeight, Rects());
            Assert.Equal(TimelineHitKind.ItemBody, hit.Kind);
            Assert.Equal(Narrow, hit.ItemId);
            Assert.Equal(1, hit.RowIndex);

            // and the suppressed edge does not reach outside the item either
            Assert.Equal(TimelineHitKind.Empty,
                TimelineHitTester.HitTest(397, Row1Y, RulerHeight, Rects()).Kind);
        }

        [Fact]
        public void Edges_only_match_inside_their_own_row()
        {
            // item A's start edge x, but on the audio row below it
            var hit = TimelineHitTester.HitTest(100, Row1Y, RulerHeight, Rects());
            Assert.Equal(TimelineHitKind.Empty, hit.Kind);
        }

        [Fact]
        public void Empty_space_is_empty()
        {
            var gap = TimelineHitTester.HitTest(154, Row0Y, RulerHeight, Rects(), 1);
            Assert.Equal(TimelineHitKind.Empty, gap.Kind);
            Assert.Equal(Guid.Empty, gap.ItemId);
            Assert.Equal(-1, gap.RowIndex);

            Assert.Equal(TimelineHitKind.Empty,
                TimelineHitTester.HitTest(900, Row0Y, RulerHeight, Rects()).Kind);
            Assert.Equal(TimelineHitKind.Empty,
                TimelineHitTester.HitTest(200, 500, RulerHeight, Rects()).Kind);
            Assert.Equal(TimelineHitKind.Empty,
                TimelineHitTester.HitTest(200, Row0Y, RulerHeight, null).Kind);
        }

        [Fact]
        public void Item_bodies_own_their_start_edge_and_not_their_end()
        {
            // with the grips out of the way, the body span is half-open [X, Right) — the same
            // convention the model uses for an item's timeline span.
            Assert.Equal(TimelineHitKind.ItemBody,
                TimelineHitTester.HitTest(400, Row1Y, RulerHeight, Rects(), 0).Kind);
            Assert.Equal(TimelineHitKind.Empty,
                TimelineHitTester.HitTest(410, Row1Y, RulerHeight, Rects(), 0).Kind);
        }
    }
}
