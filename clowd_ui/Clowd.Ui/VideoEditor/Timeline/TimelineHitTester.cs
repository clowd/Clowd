using System;
using System.Collections.Generic;

namespace Clowd.UI.VideoEditor.Timeline
{
    /// <summary>What part of the timeline a pointer position lands on.</summary>
    internal enum TimelineHitKind
    {
        /// <summary>Bare row background (or below the last row): a press clears the selection and
        /// scrubs.</summary>
        Empty,

        /// <summary>The time ruler strip: a press scrubs.</summary>
        Ruler,

        /// <summary>The middle of an item: select, then move when the item is movable.</summary>
        ItemBody,

        /// <summary>The item's left edge grip: trim the start.</summary>
        ItemStart,

        /// <summary>The item's right edge grip: trim the end.</summary>
        ItemEnd,

        Playhead,
    }

    /// <summary>A hit. <see cref="ItemId"/> is <see cref="Guid.Empty"/> and <see cref="RowIndex"/>
    /// is -1 for everything that is not an item.</summary>
    internal readonly record struct TimelineHit(TimelineHitKind Kind, Guid ItemId, int RowIndex)
    {
        public static readonly TimelineHit Empty = new TimelineHit(TimelineHitKind.Empty, Guid.Empty, -1);
    }

    /// <summary>One visible item's on-screen rectangle, produced by the surface's layout pass and
    /// consumed by the hit tester. <see cref="X"/> is already the (possibly negative) viewport
    /// coordinate — items are clipped for drawing, never for hit-testing, so an edge scrolled just
    /// off screen still refuses the grab naturally.</summary>
    internal readonly record struct TimelineItemRect(Guid ItemId, int RowIndex, double X, double Width,
        double Top, double Height)
    {
        public double Right => X + Width;

        public double Bottom => Top + Height;
    }

    /// <summary>
    /// Pure hit-testing over the timeline's precomputed item rectangles, in strict priority order:
    /// <b>playhead &gt; item edge &gt; item body &gt; ruler &gt; empty</b>. The playhead wins
    /// everywhere because scrubbing must stay reachable no matter what it is parked over; edges beat
    /// bodies because a trim grip is the smaller target. Free of Avalonia types so the priorities
    /// are testable.
    /// </summary>
    internal static class TimelineHitTester
    {
        /// <summary>Pointer slop for the playhead and item edges (see
        /// <see cref="TimelineViewMath.HitTolerance"/>).</summary>
        public const double HitTolerance = TimelineViewMath.HitTolerance;

        /// <summary>An item narrower than this has no room for two independent edge grips — its
        /// whole width would be edge, leaving no way to select or move it — so edges are suppressed
        /// and the item is body-only until the user zooms in.</summary>
        public const double MinEdgeGrabWidth = 14.0;

        /// <summary>
        /// Hit-tests a pointer position given in the surface's coordinate space (x from the left of
        /// the drawing area, y from the top of the ruler; the rows start at
        /// <paramref name="rulerHeight"/>).
        /// </summary>
        /// <param name="playheadX">Current playhead x, or <see cref="Double.NaN"/> when it is not
        /// drawn — NaN comparisons are false, so it simply never matches.</param>
        /// <param name="rects">Rectangles of the visible items, in draw order. Their
        /// <see cref="TimelineItemRect.Top"/> is in the same space as <paramref name="y"/>.</param>
        public static TimelineHit HitTest(double x, double y, double playheadX, double rulerHeight,
            IReadOnlyList<TimelineItemRect> rects, double tolerance = HitTolerance)
        {
            if (Math.Abs(x - playheadX) <= tolerance)
                return new TimelineHit(TimelineHitKind.Playhead, Guid.Empty, -1);

            if (y < rulerHeight)
                return new TimelineHit(TimelineHitKind.Ruler, Guid.Empty, -1);

            if (rects == null || rects.Count == 0)
                return TimelineHit.Empty;

            var bestDistance = Double.PositiveInfinity;
            var bestPreferred = false;
            var bestEdge = TimelineHit.Empty;

            for (var i = 0; i < rects.Count; i++)
            {
                var rect = rects[i];
                if (y < rect.Top || y >= rect.Bottom || rect.Width < MinEdgeGrabWidth)
                    continue;

                // at a cut seam two edges are coincident (the left item's end IS the right item's
                // start), so equal distances are tied by which side of the edge the pointer sits —
                // the half-open [X, Right) convention the body scan uses. Without the tie-break the
                // right half's in-point would be ungrabbable forever (the left item enumerates
                // first and a strict < never lets an equal candidate displace it).
                var toStart = Math.Abs(x - rect.X);
                var startPreferred = x >= rect.X; // at/after the edge belongs to the start grip
                if (toStart <= tolerance && IsBetter(toStart, startPreferred, bestDistance, bestPreferred))
                {
                    bestDistance = toStart;
                    bestPreferred = startPreferred;
                    bestEdge = new TimelineHit(TimelineHitKind.ItemStart, rect.ItemId, rect.RowIndex);
                }

                var toEnd = Math.Abs(x - rect.Right);
                var endPreferred = x < rect.Right; // before the edge belongs to the end grip
                if (toEnd <= tolerance && IsBetter(toEnd, endPreferred, bestDistance, bestPreferred))
                {
                    bestDistance = toEnd;
                    bestPreferred = endPreferred;
                    bestEdge = new TimelineHit(TimelineHitKind.ItemEnd, rect.ItemId, rect.RowIndex);
                }
            }

            if (bestEdge.Kind != TimelineHitKind.Empty)
                return bestEdge;

            // topmost drawn item wins the body, so an overlap (only possible across rows) resolves
            // the same way the surface painted it.
            for (var i = rects.Count - 1; i >= 0; i--)
            {
                var rect = rects[i];
                if (x >= rect.X && x < rect.Right && y >= rect.Top && y < rect.Bottom)
                    return new TimelineHit(TimelineHitKind.ItemBody, rect.ItemId, rect.RowIndex);
            }

            return TimelineHit.Empty;
        }

        /// <summary>Coincident-edge epsilon: rects come from two float paths (<c>X + Width</c> vs
        /// the neighbour's own <c>X</c>) that can differ by an ULP, which must still count as a
        /// tie rather than an arbitrary winner.</summary>
        private const double EdgeTieEpsilon = 1e-6;

        private static bool IsBetter(double distance, bool preferred, double bestDistance, bool bestPreferred) =>
            distance < bestDistance - EdgeTieEpsilon ||
            (distance <= bestDistance + EdgeTieEpsilon && preferred && !bestPreferred);
    }
}
