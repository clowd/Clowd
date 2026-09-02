using System;
using Clowd.PlatformUtil;

namespace Clowd.UI
{
    /// <summary>
    /// The pure geometry of a share-region resize drag, in CAPTURE space (physical px in
    /// virtual-desktop coordinates on Windows, CG points on macOS; x/y may be negative).
    /// <para>No Avalonia, no Window, no Dispatcher — for the same reason
    /// <see cref="ShareRegionProtocol"/> has none: this is where a resize can silently produce a
    /// rectangle nobody notices is wrong, so it must be testable without a screen. Public rather
    /// than internal-plus-InternalsVisibleTo, per the repo's standing preference.</para>
    /// </summary>
    public static class ShareRegionGeometry
    {
        /// <summary>The helper's own floor (clowd-share-region <c>mirror.rs</c> MIN_REGION).</summary>
        public const int MinSide = 64;

        // Handle indices, the video editor gizmo's numbering (TransformGizmoControl): 0-3 corners
        // in z-order TL,TR,BL,BR; 4-7 edge midpoints L,T,R,B. 8 is the body (move), which the
        // gizmo spends on its rotate handle — a shared region cannot rotate, so there is none.
        public const int HandleTopLeft = 0;
        public const int HandleTopRight = 1;
        public const int HandleBottomLeft = 2;
        public const int HandleBottomRight = 3;
        public const int HandleLeft = 4;
        public const int HandleTop = 5;
        public const int HandleRight = 6;
        public const int HandleBottom = 7;
        public const int HandleBody = 8;

        /// <summary>
        /// The client-side twin of the helper's <c>normalize_region</c>: each side floored at
        /// <see cref="MinSide"/> and THEN rounded DOWN to even, in that order, with X/Y untouched
        /// (the helper never clamps the origin and never clips to the desktop). Idempotent, so
        /// echoing an acked region back through it is safe. Applying it during the drag is what
        /// makes the <c>region_changed</c> ack equal the request in the ordinary case, so the
        /// frame does not visibly snap after the move commits.
        /// </summary>
        public static ScreenRect Clamp(ScreenRect rect)
        {
            if (rect == null)
                throw new ArgumentNullException(nameof(rect));

            var w = Math.Max(rect.Width, MinSide) & ~1;
            var h = Math.Max(rect.Height, MinSide) & ~1;
            return new ScreenRect(rect.X, rect.Y, w, h);
        }

        /// <summary>
        /// Applies a drag delta to the press-time rectangle. ALWAYS computed from the press-time
        /// anchor, never accumulated frame to frame — that is what makes the gesture immune to a
        /// dropped pointer-move event and to the window being repositioned under the pointer on
        /// every step.
        /// <para><paramref name="handle"/> = <see cref="HandleBody"/> translates without resizing.
        /// Any other value moves only the edges that handle owns.</para>
        /// <para>Crossing an edge is handled by swapping the two edge VALUES <b>and</b> the
        /// which-edge-is-moving flags together. Doing only the first is the bug: after a swap the
        /// moving and anchored edges have exchanged roles, so pinning by the original handle id
        /// makes the rect jump. Note also that <see cref="ScreenRect.FromLTRB"/> performs no
        /// normalization and <see cref="ScreenRect.IsEmpty"/> is false for a negative-extent rect,
        /// so an unnormalized result passes every downstream guard and breaks every consumer.</para>
        /// <para>The result is always <see cref="Clamp"/>ed, and the clamp pushes back the MOVING
        /// edge so the anchored one stays pinned.</para>
        /// </summary>
        public static ScreenRect ApplyDrag(ScreenRect anchor, int handle, int dx, int dy)
        {
            if (anchor == null)
                throw new ArgumentNullException(nameof(anchor));

            if (handle == HandleBody)
                return anchor.Translate(dx, dy);

            int l = anchor.Left, t = anchor.Top, r = anchor.Right, b = anchor.Bottom;

            var movingLeft = handle is HandleTopLeft or HandleBottomLeft or HandleLeft;
            var movingRight = handle is HandleTopRight or HandleBottomRight or HandleRight;
            var movingTop = handle is HandleTopLeft or HandleTopRight or HandleTop;
            var movingBottom = handle is HandleBottomLeft or HandleBottomRight or HandleBottom;

            if (movingLeft) l += dx;
            if (movingRight) r += dx;
            if (movingTop) t += dy;
            if (movingBottom) b += dy;

            if (l > r)
            {
                (l, r) = (r, l);
                (movingLeft, movingRight) = (movingRight, movingLeft);
            }

            if (t > b)
            {
                (t, b) = (b, t);
                (movingTop, movingBottom) = (movingBottom, movingTop);
            }

            var w = Math.Max(r - l, MinSide) & ~1;
            var h = Math.Max(b - t, MinSide) & ~1;

            var x = movingLeft ? r - w : l;
            var y = movingTop ? b - h : t;

            return new ScreenRect(x, y, w, h);
        }
    }
}
