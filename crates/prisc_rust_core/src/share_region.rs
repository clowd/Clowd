//! The share-region rectangle rules the region helper (prisc_share_region) and Prisc.Ui's resize
//! drag apply identically, in capture space (physical pixels in virtual-desktop coordinates on
//! Windows, CoreGraphics points on macOS; x and y may be negative).
//!
//! [`normalize`] is the one place a requested region becomes one the helper can mirror, and
//! applying it during the drag is what makes the helper's `region_changed` ack equal the request
//! in the ordinary case, so the frame does not visibly snap after the move commits.

use crate::geometry::{RectExt, ScreenRect};

pub use crate::contract::share_region::MIN_REGION;

/// Which part of the region a drag moves: the video editor gizmo's numbering. 0 to 3 are the
/// corners (top left, top right, bottom left, bottom right), 4 to 7 the edge midpoints (left, top,
/// right, bottom), and the body moves the whole region.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Handle {
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Left,
    Top,
    Right,
    Bottom,
    Body,
}

/// Each side floored at [`MIN_REGION`] and then rounded down to even, in that order, with the
/// origin untouched (the helper never clamps the origin and never clips to the desktop).
///
/// - The floor keeps a degenerate region out of the swapchain and the scene maths.
/// - Even, because the helper's canvas is forced even and the mirror window is sized from the
///   region: an odd 801 px region would present an 800 px swapchain into an 801 px window, a
///   resampled, slightly soft picture, while the ack told Prisc to draw its border at 801. Down
///   rather than up, so a region just inside a display is never pushed a pixel past its edge.
///
/// [`MIN_REGION`] is even, so the rounding can never take a size back below the floor, and the
/// function is idempotent: echoing an acked region back through it does not move it again. A zero
/// or negative size lands on the floor.
pub fn normalize(region: ScreenRect) -> ScreenRect {
    ScreenRect::new(
        region.origin,
        euclid::size2(even_floor(region.size.width), even_floor(region.size.height)),
    )
}

fn even_floor(side: i32) -> i32 {
    side.max(MIN_REGION) & !1
}

/// The region after dragging `handle` by (`dx`, `dy`) from `anchor`, the region as it was when the
/// drag began. Always computed from that anchor, never accumulated frame to frame, which is what
/// makes the gesture immune to a dropped pointer-move event and to the window moving under the
/// pointer on every step.
///
/// [`Handle::Body`] translates without resizing. Any other handle moves only the edges it owns.
/// Crossing an edge swaps the two edge values *and* which of them is moving: after a swap the
/// moving and anchored edges have exchanged roles, so pinning by the original handle would make
/// the region jump. The result is always [`normalize`]d, and the floor and the even rounding push
/// back the *moving* edge, so the anchored one stays pinned.
pub fn apply_drag(anchor: ScreenRect, handle: Handle, dx: i32, dy: i32) -> ScreenRect {
    if handle == Handle::Body {
        return anchor.translate(euclid::vec2(dx, dy));
    }

    let (mut l, mut t) = (anchor.origin.x, anchor.origin.y);
    let (mut r, mut b) = (l.wrapping_add(anchor.size.width), t.wrapping_add(anchor.size.height));

    use Handle::*;
    let mut moving_left = matches!(handle, TopLeft | BottomLeft | Left);
    let moving_right = matches!(handle, TopRight | BottomRight | Right);
    let mut moving_top = matches!(handle, TopLeft | TopRight | Top);
    let moving_bottom = matches!(handle, BottomLeft | BottomRight | Bottom);

    if moving_left {
        l = l.wrapping_add(dx);
    }
    if moving_right {
        r = r.wrapping_add(dx);
    }
    if moving_top {
        t = t.wrapping_add(dy);
    }
    if moving_bottom {
        b = b.wrapping_add(dy);
    }

    // after a swap the edge now on the left is the one that was moving on the right
    if l > r {
        (l, r) = (r, l);
        moving_left = moving_right;
    }
    if t > b {
        (t, b) = (b, t);
        moving_top = moving_bottom;
    }

    let w = even_floor(r.wrapping_sub(l));
    let h = even_floor(b.wrapping_sub(t));
    let x = if moving_left { r.wrapping_sub(w) } else { l };
    let y = if moving_top { b.wrapping_sub(h) } else { t };
    ScreenRect::from_xy_size(x, y, w, h)
}

#[cfg(test)]
mod tests {
    use super::*;
    use Handle::*;

    fn rect(x: i32, y: i32, w: i32, h: i32) -> ScreenRect {
        ScreenRect::from_xy_size(x, y, w, h)
    }

    fn ltrb(r: ScreenRect) -> (i32, i32, i32, i32) {
        (r.min_x(), r.min_y(), r.max_x(), r.max_y())
    }

    const ALL: [Handle; 9] = [TopLeft, TopRight, BottomLeft, BottomRight, Left, Top, Right, Bottom, Body];

    /// Every rect that leaves `normalize` is one the mirror can present: at least the floor on both
    /// axes, even, idempotent, and never moved (ported from prisc_share_region's mirror.rs).
    #[test]
    fn normalize_is_a_fixed_point_of_the_canvas_math() {
        for w in [1, 2, 63, 64, 65, 800, 801, 1919, 3840] {
            for h in [1, 2, 63, 64, 65, 600, 601, 1081, 2160] {
                let n = normalize(rect(10, -20, w, h));
                assert!(n.size.width >= MIN_REGION && n.size.height >= MIN_REGION, "{w}x{h} -> {n:?}");
                assert_eq!((n.size.width % 2, n.size.height % 2), (0, 0), "{w}x{h} -> {n:?}");
                assert_eq!(normalize(n), n);
                assert_eq!((n.origin.x, n.origin.y), (10, -20));
            }
        }
    }

    #[test]
    fn normalize_applies_the_floor_before_the_even_rounding() {
        assert_eq!(normalize(rect(0, 0, 1, 1)).size.width, MIN_REGION);
        assert_eq!(
            normalize(rect(0, 0, MIN_REGION + 1, 1))
                .size
                .width,
            MIN_REGION
        );
        assert_eq!(normalize(rect(0, 0, 2, 2)).size.height, MIN_REGION);
        assert_eq!(normalize(rect(0, 0, 801, 601)), rect(0, 0, 800, 600));
    }

    #[test]
    fn normalize_takes_zero_and_negative_sizes_to_the_floor() {
        assert_eq!(normalize(rect(5, 6, 0, -40)), rect(5, 6, 64, 64));
        assert_eq!(normalize(rect(-3, -4, -1, 0)), rect(-3, -4, 64, 64));
    }

    #[test]
    fn a_body_drag_translates_without_resizing() {
        assert_eq!(apply_drag(rect(100, 100, 400, 300), Body, -150, 35), rect(-50, 135, 400, 300));
        // not normalised: the body moves the region the helper already acked
        assert_eq!(apply_drag(rect(0, 0, 63, 63), Body, 1, 1), rect(1, 1, 63, 63));
    }

    #[test]
    fn an_edge_drag_pins_the_opposite_edge() {
        let a = rect(100, 100, 400, 300);
        assert_eq!(ltrb(apply_drag(a, Left, 50, 999)), (150, 100, 500, 400));
        assert_eq!(ltrb(apply_drag(a, Top, 999, -40)), (100, 60, 500, 400));
        assert_eq!(ltrb(apply_drag(a, Right, 30, 999)), (100, 100, 530, 400));
        assert_eq!(ltrb(apply_drag(a, Bottom, 999, 20)), (100, 100, 500, 420));
    }

    #[test]
    fn a_corner_drag_moves_exactly_two_edges() {
        let a = rect(100, 100, 400, 300);
        assert_eq!(ltrb(apply_drag(a, TopLeft, 10, 20)), (110, 120, 500, 400));
        assert_eq!(ltrb(apply_drag(a, TopRight, 10, 20)), (100, 120, 510, 400));
        assert_eq!(ltrb(apply_drag(a, BottomLeft, 10, 20)), (110, 100, 500, 420));
        assert_eq!(ltrb(apply_drag(a, BottomRight, 50, -20)), (100, 100, 550, 380));
    }

    #[test]
    fn shrinking_past_the_floor_pins_the_anchored_edge() {
        let a = rect(100, 100, 400, 300);
        // the right edge stays at 500; the moving left edge is pushed back to 500 - 64
        assert_eq!(ltrb(apply_drag(a, Left, 380, 0)), (436, 100, 500, 400));
        assert_eq!(ltrb(apply_drag(a, Top, 0, 290)), (100, 336, 500, 400));
        assert_eq!(ltrb(apply_drag(a, Right, -390, 0)), (100, 100, 164, 400));
    }

    #[test]
    fn an_odd_result_rounds_down_on_the_moving_edge() {
        let a = rect(100, 100, 400, 300);
        assert_eq!(ltrb(apply_drag(a, Left, 1, 0)), (102, 100, 500, 400));
        assert_eq!(ltrb(apply_drag(a, Right, 1, 0)), (100, 100, 500, 400));
        assert_eq!(ltrb(apply_drag(a, Bottom, 0, 3)), (100, 100, 500, 402));
    }

    #[test]
    fn crossing_the_opposite_edge_flips_which_edge_is_anchored() {
        let a = rect(100, 100, 400, 300);
        // the right edge dragged 500 px left lands at 0: the old left edge (100) is now the right
        assert_eq!(ltrb(apply_drag(a, Right, -500, 0)), (0, 100, 100, 400));
        // the left edge dragged past the right: the old right edge (500) is now the left, pinned
        assert_eq!(ltrb(apply_drag(a, Left, 500, 0)), (500, 100, 600, 400));
        assert_eq!(ltrb(apply_drag(a, TopLeft, 500, 400)), (500, 400, 600, 500));
    }

    /// Whatever the handle and delta, the result is normalised: no negative extent, never below
    /// the floor, even on both axes (the C# `ShareRegionGeometryTests` sweep).
    #[test]
    fn every_drag_is_normalised() {
        let a = rect(-300, 50, 400, 300);
        for handle in ALL.into_iter().filter(|h| *h != Body) {
            for dx in (-900..=900).step_by(37) {
                for dy in (-700..=700).step_by(41) {
                    let r = apply_drag(a, handle, dx, dy);
                    assert!(
                        r.size.width >= MIN_REGION && r.size.height >= MIN_REGION,
                        "{handle:?} {dx},{dy}: {r:?}"
                    );
                    assert_eq!((r.size.width % 2, r.size.height % 2), (0, 0), "{handle:?} {dx},{dy}");
                    assert_eq!(normalize(r), r);
                }
            }
        }
    }

    /// The cases the C# `ShareRegionGeometryTests` pinned rule by rule, ported as they were:
    /// (anchor, handle, dx, dy) to the expected rect.
    #[test]
    fn the_csharp_geometry_vectors_hold() {
        let a = rect(100, 100, 200, 200);
        let cases = [
            (a, Body, 37, 13, rect(137, 113, 200, 200)),
            (a, Body, -5000, -160, rect(-4900, -60, 200, 200)),
            (a, Left, 10, 0, rect(110, 100, 190, 200)),
            (a, Top, 0, 10, rect(100, 110, 200, 190)),
            (a, Right, 50, 0, rect(100, 100, 250, 200)),
            (a, TopLeft, 20, 30, rect(120, 130, 180, 170)),
            (a, Left, 10, -75, rect(110, 100, 190, 200)),
            (rect(0, 0, 200, 200), Left, 180, 0, rect(136, 0, 64, 200)),
            (rect(0, 0, 200, 200), Top, 0, 180, rect(0, 136, 200, 64)),
            (a, Right, 1, 0, a),
            (a, Left, -1, 0, a),
            (a, Left, 1, 0, rect(102, 100, 198, 200)),
            (rect(100, 0, 200, 200), Right, -1000, 0, rect(-700, 0, 800, 200)),
            (rect(100, 0, 200, 200), Left, 1000, 0, rect(300, 0, 800, 200)),
            (rect(0, 100, 200, 200), Top, 0, 1000, rect(0, 300, 200, 800)),
            (rect(0, 0, 200, 200), TopLeft, 1000, 1000, rect(200, 200, 800, 800)),
        ];
        for (anchor, handle, dx, dy, expected) in cases {
            assert_eq!(apply_drag(anchor, handle, dx, dy), expected, "{anchor:?} {handle:?} {dx},{dy}");
        }
        for (input, expected) in [
            (rect(0, 0, 10, 10), rect(0, 0, 64, 64)),
            (rect(0, 0, 801, 201), rect(0, 0, 800, 200)),
            (rect(0, 0, 63, 65), rect(0, 0, 64, 64)),
            (rect(-1920, -1080, 10, 10), rect(-1920, -1080, 64, 64)),
            (rect(-5000, -5000, 3000, 3000), rect(-5000, -5000, 3000, 3000)),
        ] {
            assert_eq!(normalize(input), expected);
        }
    }

    /// The same press-time anchor and total delta give the same rect, however the drag got there.
    #[test]
    fn a_drag_is_a_function_of_the_anchor_only() {
        let a = rect(100, 100, 400, 300);
        for handle in ALL {
            let direct = apply_drag(a, handle, 120, -80);
            let _ = apply_drag(a, handle, 7, 9);
            assert_eq!(apply_drag(a, handle, 120, -80), direct);
        }
    }
}
