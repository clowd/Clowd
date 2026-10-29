using System;
using Prisc.Interop;
using Prisc.PlatformUtil;

namespace Prisc.UI
{
    /// <summary>
    /// The pure geometry of a share-region resize drag, in CAPTURE space (physical px in
    /// virtual-desktop coordinates on Windows, CG points on macOS; x/y may be negative).
    /// <para>No Avalonia, no Window, no Dispatcher: this is where a resize can silently produce a
    /// rectangle nobody notices is wrong, so it must be testable without a screen. Public rather
    /// than internal-plus-InternalsVisibleTo, per the repo's standing preference.</para>
    /// </summary>
    public static partial class ShareRegionGeometry
    {
        // MinRegion, the helper's own floor, is generated (ShareRegionGeometry.g.cs) from
        // prisc_rust_core::contract::share_region.

        // Handle indices, the video editor gizmo's numbering (TransformGizmoControl): 0-3 corners
        // in z-order TL,TR,BL,BR; 4-7 edge midpoints L,T,R,B. 8 is the body (move), which the
        // gizmo spends on its rotate handle; a shared region cannot rotate, so there is none.
        public const int HandleTopLeft = (int)ShareRegionHandle.TopLeft;
        public const int HandleTopRight = (int)ShareRegionHandle.TopRight;
        public const int HandleBottomLeft = (int)ShareRegionHandle.BottomLeft;
        public const int HandleBottomRight = (int)ShareRegionHandle.BottomRight;
        public const int HandleLeft = (int)ShareRegionHandle.Left;
        public const int HandleTop = (int)ShareRegionHandle.Top;
        public const int HandleRight = (int)ShareRegionHandle.Right;
        public const int HandleBottom = (int)ShareRegionHandle.Bottom;
        public const int HandleBody = (int)ShareRegionHandle.Body;

        /// <summary>
        /// The helper's own <c>normalize</c> (<c>prisc_rust_core::share_region</c>, the same code):
        /// each side floored at <see cref="MinRegion"/> and THEN rounded DOWN to even, in that order,
        /// with X/Y untouched (the helper never clamps the origin and never clips to the desktop).
        /// Idempotent, so echoing an acked region back through it is safe. Applying it during the
        /// drag is what makes the <c>region_changed</c> ack equal the request in the ordinary case,
        /// so the frame does not visibly snap after the move commits.
        /// </summary>
        public static ScreenRect Clamp(ScreenRect rect)
        {
            if (rect == null)
                throw new ArgumentNullException(nameof(rect));

            var (x, y, w, h) = ShareRegion.Normalize(rect.X, rect.Y, rect.Width, rect.Height);
            return new ScreenRect(x, y, w, h);
        }

        /// <summary>
        /// Applies a drag delta to the press-time rectangle. ALWAYS computed from the press-time
        /// anchor, never accumulated frame to frame: that is what makes the gesture immune to a
        /// dropped pointer-move event and to the window being repositioned under the pointer on
        /// every step.
        /// <para><paramref name="handle"/> = <see cref="HandleBody"/> translates without resizing.
        /// Any other handle moves only the edges it owns.</para>
        /// <para>Crossing an edge swaps the two edge VALUES <b>and</b> which edge is moving; the
        /// result is always normalized (<see cref="Clamp"/>'s rule), and the normalization pushes
        /// back the MOVING edge so the anchored one stays pinned. The rule is
        /// <c>prisc_rust_core::share_region::apply_drag</c>, one native call.</para>
        /// </summary>
        public static ScreenRect ApplyDrag(ScreenRect anchor, int handle, int dx, int dy)
        {
            if (anchor == null)
                throw new ArgumentNullException(nameof(anchor));
            if (handle < HandleTopLeft || handle > HandleBody)
                throw new ArgumentOutOfRangeException(nameof(handle), handle, "Not a share-region handle.");

            var (x, y, w, h) = ShareRegion.ApplyDrag(anchor.X, anchor.Y, anchor.Width, anchor.Height, (ShareRegionHandle)handle, dx, dy);
            return new ScreenRect(x, y, w, h);
        }
    }
}
