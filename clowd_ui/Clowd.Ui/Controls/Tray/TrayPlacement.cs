using System;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using Clowd.PlatformUtil;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// The room a tray window needs around its tray, in logical px, one value per side: a band of
    /// window that is not the tray itself (today the shadow's reserve) and so must be counted by
    /// every fits-here test, but which may legitimately hang over the region because nothing opaque
    /// is drawn in it.
    /// </summary>
    public readonly record struct TrayInsets(int Left, int Top, int Right, int Bottom)
    {
        /// <summary>
        /// Logical px → capture space, rounded UP per side: a reserve that is a fraction of a pixel
        /// short is a fringe of shadow inside the region, which is the one thing the window extents
        /// exist to prevent.
        /// </summary>
        public static TrayInsets FromLogical(Thickness t, double scaling) => new TrayInsets(
            (int)Math.Ceiling(t.Left * scaling),
            (int)Math.Ceiling(t.Top * scaling),
            (int)Math.Ceiling(t.Right * scaling),
            (int)Math.Ceiling(t.Bottom * scaling));
    }

    /// <summary>The WINDOW top-left in capture px, plus the axis the cascade picked for the tray.</summary>
    public readonly record struct TrayPlacementResult(int X, int Y, Orientation Orientation);

    /// <summary>
    /// Where a floating tray goes relative to the rectangle it belongs to. Pure integer maths in
    /// capture space (physical px on Windows; CG points, which are logical units, on macOS) so it can
    /// be tested without a render platform: nothing here touches a control, a screen enumeration or a
    /// dispatcher — the window chassis measures and converts, this decides.
    /// </summary>
    public static class TrayPlacement
    {
        /// <summary>
        /// The centred-below → [above] → right → left → inside cascade the previous toolbar window's
        /// <c>PositionNearRegion</c> used, ported verbatim with the band of window around the tray on
        /// all four sides expressed as <paramref name="reserve"/>: every fits-here extent below is a
        /// WINDOW extent (tray + reserve). The gap, though, is measured from the PAINTED tray: the
        /// window is pulled back toward the region by its near-side reserve (capped at the gap, so a
        /// reserved pixel still never lands inside the region), and the shadow falls into the gap
        /// instead of pushing the strip a reserve further away than the old toolbar sat.
        /// <para>
        /// All math in physical px on the monitor containing the region's center; the caller skips it
        /// once the user has dragged or rotated the strip. The short/long-edge formulation is
        /// orientation-independent, so a single pass both picks the orientation and computes the final
        /// position.
        /// </para>
        /// <para>
        /// Unlike the WPF original this measures and clamps against the monitor's WORKING area
        /// (<paramref name="workArea"/>) rather than its full bounds, so the strip is never dealt a slot
        /// underneath the macOS dock / menu bar or the Windows taskbar, which are painted over it
        /// (issue #72). The selection itself still comes from the full bounds
        /// (<paramref name="screenBounds"/>) — the capture region legitimately covers the reserved
        /// strips, and clipping it would shift the centering.
        /// </para>
        /// <para>
        /// The cascade is below → [<paramref name="preferAbove"/>: above] → right → left → inside, and
        /// the last rung is never withheld.
        /// </para>
        /// </summary>
        public static TrayPlacementResult Near(ScreenRect region, ScreenRect screenBounds, ScreenRect workArea,
            int trayLong, int trayShort, TrayInsets reserve, int minDistance, int maxDistance, bool preferAbove)
        {
            var selection = region.Intersect(screenBounds);
            if (selection.IsEmpty())
                selection = region;

            var bottomSpace = Math.Max(workArea.Bottom - selection.Bottom, 0) - minDistance;
            var rightSpace = Math.Max(workArea.Right - selection.Right, 0) - minDistance;
            var leftSpace = Math.Max(selection.Left - workArea.Left, 0) - minDistance;
            var topSpace = Math.Max(selection.Top - workArea.Top, 0) - minDistance;

            // the shadow sits in a band outside the tray on all four sides — so the window is deeper
            // than the tray on both axes, and every fits-here test below has to ask for the room the
            // WINDOW needs, not the tray's. Read off the reserve the caller measured rather than a
            // constant: it is only as deep as the shadow actually in use (and zero when the platform
            // refused transparency and the shadow was dropped).
            var winShortH = trayShort + reserve.Top + reserve.Bottom;    // horizontal rungs: the window's height
            var winLongH = trayLong + reserve.Left + reserve.Right;      // horizontal rungs: the window's width
            var winShortV = trayShort + reserve.Left + reserve.Right;    // vertical rungs: the window's width
            var winLongV = trayLong + reserve.Top + reserve.Bottom;      // vertical rungs: the window's height

            // the near-side reserve overlaps the gap so the TRAY keeps maxDistance from the region;
            // never more than the gap itself, or the window (and its click-eating fringe) would enter it
            var pullBelow = Math.Min(reserve.Top, maxDistance);
            var pullAbove = Math.Min(reserve.Bottom, maxDistance);
            var pullRight = Math.Min(reserve.Left, maxDistance);
            var pullLeft = Math.Min(reserve.Right, maxDistance);

            Orientation orientation;
            int indLeft, indTop;

            if (bottomSpace >= winShortH)
            {
                // below the selection: the shadow hangs further below, away from what is captured.
                orientation = Orientation.Horizontal;
                indLeft = selection.Left + selection.Width / 2 - winLongH / 2;
                indTop = Math.Min(workArea.Bottom, selection.Bottom + maxDistance - pullBelow + winShortH) - winShortH;
            }
            else if (preferAbove && topSpace >= winShortH)
            {
                // above the selection, for the callers that ask for it. It is preferred over the two
                // vertical rungs because a horizontal strip is the shape the user grabbed the handle on
                // and the one whose labels read at a glance — and a region that is watched live is
                // photographed continuously, so anything the cascade parks inside it is not a stray
                // frame or two at the end of a capture but Clowd's own toolbar broadcast into someone
                // else's meeting for as long as it lasts. A caller whose region is watched live
                // therefore gets this fourth outside-the-region rung before it considers the inside one.
                orientation = Orientation.Horizontal;
                indLeft = selection.Left + selection.Width / 2 - winLongH / 2;
                indTop = Math.Max(selection.Top - maxDistance - winShortH + pullAbove, workArea.Top);
            }
            else if (rightSpace >= winShortV)
            {
                // to the right of the selection: the shadow goes further right, for the same reason.
                orientation = Orientation.Vertical;
                indLeft = Math.Min(workArea.Right, selection.Right + maxDistance - pullRight + winShortV) - winShortV;
                indTop = selection.Bottom - winLongV;
            }
            else if (leftSpace >= winShortV)
            {
                orientation = Orientation.Vertical;
                indLeft = Math.Max(selection.Left - maxDistance - winShortV + pullLeft, workArea.Left);
                indTop = selection.Bottom - winLongV;
            }
            else // inside capture rect
            {
                // Last resort, and for a region that is watched live a genuinely last one: reaching
                // here means the region has no room on ANY of its four sides, i.e. it covers
                // essentially the whole monitor. There is then nowhere on that monitor the strip would
                // not be in the picture, so refusing to place it would not keep it out of the meeting
                // — it would only cost the user the button that ends the meeting's view of their
                // screen. Showing it always wins; the strip is never withheld.

                orientation = Orientation.Horizontal;
                indLeft = selection.Left + selection.Width / 2 - winLongH / 2;
                // keep the gap measured from the placeable bottom edge, so a full-height
                // selection lands above the dock/taskbar rather than flush against it.
                indTop = Math.Min(selection.Bottom, workArea.Bottom) - winShortH - maxDistance * 2;
            }

            // window, not tray: indLeft/indTop are where the whole thing goes, reserve included.
            var horizontalSize = orientation == Orientation.Horizontal ? winLongH : winShortV;
            var verticalSize = orientation == Orientation.Horizontal ? winShortH : winLongV;

            if (indLeft < workArea.Left)
                indLeft = workArea.Left;
            else if (indLeft + horizontalSize > workArea.Right)
                indLeft = workArea.Right - horizontalSize;

            // the vertical clamp the WPF original never had: the two branches that anchor to the
            // selection's bottom edge (vertical placement, and the fallback inside the capture
            // rect) can otherwise run the strip off the bottom of the placeable area — which is
            // exactly where a full-height selection puts it on a machine with a bottom dock.
            if (indTop < workArea.Top)
                indTop = workArea.Top;
            else if (indTop + verticalSize > workArea.Bottom)
                indTop = workArea.Bottom - verticalSize;

            return new TrayPlacementResult(indLeft, indTop, orientation);
        }

        /// <summary>
        /// The cascade the previous fixed-size status strip's <c>TryComputePlacement</c> used, ported
        /// verbatim: the <see cref="Near"/> order (below → right → left) plus an "above" rung, minus
        /// its last resort of sitting inside the region. A candidate is accepted only if it fits
        /// entirely inside <paramref name="area"/> and misses the region entirely; the second test is
        /// the invariant, the first only keeps the strip reachable. <c>null</c> = refuse, and the caller
        /// must then not show the window at all — being invisible is always better than being in the
        /// picture.
        /// <para>
        /// <paramref name="width"/>/<paramref name="height"/> are WINDOW extents (tray + shadow
        /// reserve). As in <see cref="Near"/>, <paramref name="gap"/> is measured from the painted tray:
        /// each candidate is pulled back toward the region by its near-side <paramref name="reserve"/>,
        /// capped at the gap, and the intersection test below still rejects anything that would put a
        /// reserved pixel in the region. One deliberate deviation from the old code:
        /// <paramref name="area"/> is the monitor's working area, where the original used the full
        /// bounds and could hand back a slot underneath the taskbar.
        /// </para>
        /// </summary>
        public static ScreenRect Outside(ScreenRect region, ScreenRect area, int width, int height, int gap, TrayInsets reserve = default)
        {
            // the part of the region actually on this monitor, so a selection spanning two
            // displays is placed against the edge the strip can reach.
            var selection = region.Intersect(area);
            if (selection.IsEmpty())
                selection = region;

            var centerX = Clamp(selection.Left + selection.Width / 2 - width / 2, area.Left, area.Right - width);
            var centerY = Clamp(selection.Top + selection.Height / 2 - height / 2, area.Top, area.Bottom - height);

            var pull = Math.Max(gap, 0);
            var candidates = new[]
            {
                new ScreenRect(centerX, selection.Bottom + gap - Math.Min(reserve.Top, pull), width, height),       // below
                new ScreenRect(selection.Right + gap - Math.Min(reserve.Left, pull), centerY, width, height),       // right
                new ScreenRect(selection.Left - gap - width + Math.Min(reserve.Right, pull), centerY, width, height), // left
                new ScreenRect(centerX, selection.Top - gap - height + Math.Min(reserve.Bottom, pull), width, height), // above
            };

            foreach (var candidate in candidates)
            {
                if (candidate.Left < area.Left || candidate.Top < area.Top ||
                    candidate.Right > area.Right || candidate.Bottom > area.Bottom)
                    continue;

                // the load-bearing test: whatever the arithmetic above produced, it does not
                // overlap the rectangle that is about to be photographed. Touching ScreenRects do
                // not intersect, so a candidate flush against the region's edge is accepted — which
                // is what a zero gap is supposed to mean.
                if (candidate.IntersectsWith(region))
                    continue;

                return candidate;
            }

            return null;
        }

        /// <summary>Math.Clamp with a monitor narrower than the strip tolerated: the caller's
        /// bounds check rejects that candidate anyway, but Math.Clamp would throw first.</summary>
        public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, Math.Max(min, max));

        /// <summary>
        /// The room a <see cref="BoxShadows"/> needs around the tray, logical px: left/right are
        /// blur + spread, the top is that less the vertical offset and the bottom that plus it, each
        /// floored at 0. The first shadow in the collection is the one measured — these strips draw one.
        /// <para>
        /// This is what makes the shadow safe on a capture surface: the band is part of the window, so
        /// counting it here is what keeps the placement cascade from tucking a transparent, click-eating
        /// fringe inside the recorded region.
        /// </para>
        /// </summary>
        public static Thickness ShadowReserve(BoxShadows shadow)
        {
            if (shadow.Count == 0)
                return default;

            var s = shadow[0];
            var side = s.Blur + s.Spread;
            return new Thickness(
                Math.Max(side, 0),
                Math.Max(side - s.OffsetY, 0),
                Math.Max(side, 0),
                Math.Max(side + s.OffsetY, 0));
        }
    }
}
