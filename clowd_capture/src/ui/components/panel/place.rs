//! Where the tray goes: choose the side beside the selection, ask for the
//! strip's size in that orientation, and keep the whole box on the
//! monitor (tail wins when it cannot fit).
//!
//! Pure and in integer physical pixels, the same units the selection and
//! the monitor bounds are in. `show` turns the result into the
//! monitor-local point position egui's `Area` wants.

use clowd_rust_core::geometry::{RectExt, ScreenRect};

use super::theme::tokens;

/// Which way the strip runs.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Axis {
    Row,
    Column,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Side {
    /// A row beneath the anchor, centred on it.
    Below,
    /// A column to the right, bottom-anchored.
    Right,
    /// A column to the left, bottom-anchored.
    Left,
    /// A row pulled up inside the anchor: the unconditional fallback.
    Inside,
}

impl Side {
    pub fn axis(self) -> Axis {
        match self {
            Side::Below | Side::Inside => Axis::Row,
            Side::Right | Side::Left => Axis::Column,
        }
    }
}

/// The LONGEST box the strip can become in one orientation: `len` along
/// it, `thick` across.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Footprint {
    pub len: i32,
    pub thick: i32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Fit {
    pub row: Footprint,
    pub col: Footprint,
}

/// `min_distance` is clearance kept from the bounds edge in the fit test;
/// `max_distance` the gap between the anchor and the box.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Near {
    pub min_distance: i32,
    pub max_distance: i32,
}

impl Near {
    /// The two logical distances at one monitor's scale. Both are
    /// distances, so both round up, exactly as the retired kit's
    /// `Scale::space` did.
    pub fn at_dpi(dpi: f32) -> Self {
        Self {
            min_distance: (tokens::NEAR_MIN * dpi).ceil() as i32,
            max_distance: (tokens::NEAR_MAX * dpi).ceil() as i32,
        }
    }
}

/// Choose the side for `anchor` (already clipped to `bounds`), ask
/// `size_for` ONCE for that axis's `(width, height)` in px, put the box at
/// its origin and clamp both axes onto `bounds`.
///
/// `lock` pins the strip to one axis: the sides running the other way are
/// struck out of the cascade before the fit tests, so a set whose contents
/// only make sense along one axis (the scroll-picker's wrapped
/// instruction) can never be stood on its end.
pub fn place(
    anchor: ScreenRect,
    bounds: ScreenRect,
    fit: Fit,
    near: Near,
    lock: Option<Axis>,
    size_for: impl FnOnce(Axis) -> (i32, i32),
) -> (Side, ScreenRect) {
    let side = choose_side(anchor, bounds, fit, near, lock);
    let (w, h) = size_for(side.axis());
    let (x, y) = origin(side, anchor, bounds, (w, h), near);
    // Keep the WHOLE box on the bounds, both axes (a shadow may clip).
    // Only when nothing fits can the box still be longer than the bounds;
    // then the tail stays on the bounds and the head overflows.
    let rect = ScreenRect::from_xy_size(
        clamp_span(x, w, bounds.left(), bounds.right()),
        clamp_span(y, h, bounds.top(), bounds.bottom()),
        w,
        h,
    );
    (side, rect)
}

const CASCADE: [Side; 4] = [Side::Below, Side::Right, Side::Left, Side::Inside];

/// A candidate fits when the box's THICKNESS in that orientation fits the
/// free space on that side AND the longest strip fits the bounds ALONG the
/// strip. Neither test depends on what the strip currently holds, so a
/// content change can never flip the orientation. When no candidate passes
/// both tests the thickness alone decides.
fn choose_side(anchor: ScreenRect, bounds: ScreenRect, fit: Fit, near: Near, lock: Option<Axis>) -> Side {
    // `min_distance` is subtracted so the box never hugs the bounds edge;
    // negative means "no space".
    let bottom = (bounds.bottom() - anchor.bottom()).max(0) - near.min_distance;
    let right = (bounds.right() - anchor.right()).max(0) - near.min_distance;
    let left = (anchor.left() - bounds.left()).max(0) - near.min_distance;
    let row_fits = fit.row.len <= bounds.width();
    let col_fits = fit.col.len <= bounds.height();
    let across = |side: Side| match side {
        Side::Below => bottom >= fit.row.thick,
        Side::Right => right >= fit.col.thick,
        Side::Left => left >= fit.col.thick,
        Side::Inside => true,
    };
    let along = |side: Side| match side.axis() {
        Axis::Row => row_fits,
        Axis::Column => col_fits,
    };
    let allowed = |s: Side| lock.is_none_or(|axis| s.axis() == axis);
    CASCADE
        .into_iter()
        .filter(|&s| allowed(s))
        .find(|&s| across(s) && along(s))
        .or_else(|| {
            CASCADE
                .into_iter()
                .filter(|&s| allowed(s))
                .find(|&s| across(s))
        })
        // `Inside` is a row, so a row lock always has this fallback; a
        // column lock falls back to its own last candidate.
        .unwrap_or(Side::Inside)
}

/// Top-left of a `size` box on `side`, before the bounds clamp. Integer
/// division throughout.
fn origin(side: Side, anchor: ScreenRect, bounds: ScreenRect, size: (i32, i32), near: Near) -> (i32, i32) {
    let (w, h) = size;
    let centred = anchor.left() + anchor.width() / 2 - w / 2;
    match side {
        Side::Below => (
            centred,
            bounds
                .bottom()
                .min(anchor.bottom() + near.max_distance + h)
                - h,
        ),
        Side::Right => (
            bounds
                .right()
                .min(anchor.right() + near.max_distance + w)
                - w,
            anchor.bottom() - h,
        ),
        // No bounds pull-back here: the clamp in `place` does it, which is
        // what keeps a negative-origin bounds a pure translation.
        Side::Left => (anchor.left() - near.max_distance - w, anchor.bottom() - h),
        Side::Inside => (centred, anchor.bottom() - h - 2 * near.max_distance),
    }
}

/// `pos` moved so `[pos, pos + len)` lies in `[lo, hi)`; the high edge wins
/// when `len` exceeds the span.
fn clamp_span(pos: i32, len: i32, lo: i32, hi: i32) -> i32 {
    pos.max(lo).min(hi - len)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::Cell;

    const FIT: Fit = Fit {
        row: Footprint {
            len: 600,
            thick: 56,
        },
        col: Footprint {
            len: 500,
            thick: 92,
        },
    };

    fn near() -> Near {
        Near::at_dpi(1.0)
    }

    fn rect(x: i32, y: i32, w: i32, h: i32) -> ScreenRect {
        ScreenRect::from_xy_size(x, y, w, h)
    }

    /// The `FIT` box for `axis`, as `size_for` returns it.
    fn full(axis: Axis) -> (i32, i32) {
        match axis {
            Axis::Row => (FIT.row.len, FIT.row.thick),
            Axis::Column => (FIT.col.thick, FIT.col.len),
        }
    }

    fn placed(anchor: ScreenRect, bounds: ScreenRect) -> (ScreenRect, Side) {
        let (side, rect) = place(anchor, bounds, FIT, near(), None, full);
        (rect, side)
    }

    const HD: ScreenRect = ScreenRect {
        origin: euclid::Point2D::new(0, 0),
        size: euclid::Size2D::new(1920, 1080),
    };

    #[test]
    fn below_when_it_fits() {
        assert_eq!(placed(rect(500, 300, 100, 100), HD), (rect(250, 415, 600, 56), Side::Below));
    }

    #[test]
    fn right_when_below_is_short() {
        assert_eq!(placed(rect(500, 1000, 100, 60), HD), (rect(615, 560, 92, 500), Side::Right));
    }

    #[test]
    fn left_when_right_is_short() {
        assert_eq!(placed(rect(1800, 1000, 100, 60), HD), (rect(1693, 560, 92, 500), Side::Left));
    }

    #[test]
    fn inside_when_nothing_fits_across() {
        assert_eq!(placed(HD, HD), (rect(660, 994, 600, 56), Side::Inside));
    }

    #[test]
    fn along_axis_fit_uses_the_longest_box() {
        // Room beneath, but the row is longer than the bounds are wide.
        let narrow = rect(0, 0, 500, 1080);
        assert_eq!(placed(rect(100, 100, 100, 100), narrow).1, Side::Right);
        // Room to the right and left, but the column is taller than the
        // bounds; a row fits, so Inside wins over Right.
        let short = rect(0, 0, 1920, 400);
        assert_eq!(placed(rect(100, 300, 300, 60), short).1, Side::Inside);
    }

    #[test]
    fn thickness_only_fallback_picks_the_first_side_with_room() {
        // Neither orientation fits along; no room beneath; room to the right.
        let small = rect(0, 0, 500, 400);
        assert_eq!(placed(rect(100, 50, 100, 330), small).1, Side::Right);
        // And with the right side gone too, the left.
        assert_eq!(placed(rect(400, 50, 100, 330), small).1, Side::Left);
    }

    #[test]
    fn thresholds_are_exact() {
        let short_by_one = 1080 - FIT.row.thick - near().min_distance + 1;
        assert_eq!(placed(rect(500, 100, 100, short_by_one - 100), HD).1, Side::Right);
        assert_eq!(placed(rect(500, 100, 100, short_by_one - 101), HD).1, Side::Below);
    }

    #[test]
    fn min_distance_is_subtracted_before_the_compare() {
        let anchor = rect(500, 100, 100, 1080 - FIT.row.thick - 101);
        let loose = Near {
            min_distance: 0,
            ..near()
        };
        assert_eq!(place(anchor, HD, FIT, loose, None, full).0, Side::Below);
        assert_eq!(place(anchor, HD, FIT, near(), None, full).0, Side::Right);
    }

    #[test]
    fn below_and_right_pull_back_to_the_bounds_edge() {
        let (r, side) = placed(rect(500, 1020, 100, 2), HD);
        assert_eq!((side, r.bottom()), (Side::Below, 1080), "not 1022 + 15 + 56");
        let (r, side) = placed(rect(1800, 1000, 20, 60), HD);
        assert_eq!((side, r.right()), (Side::Right, 1920), "not 1820 + 15 + 92");
    }

    #[test]
    fn clamp_keeps_the_high_edge_when_the_box_is_longer_than_the_bounds() {
        let small = rect(0, 0, 500, 400);
        let (r, side) = placed(rect(100, 50, 100, 330), small);
        assert_eq!((side, r.top(), r.bottom()), (Side::Right, -100, 400), "the head overflows the top");
        let (r, side) = placed(rect(10, 50, 480, 330), small);
        assert_eq!(
            (side, r.left(), r.right()),
            (Side::Inside, -100, 500),
            "the head overflows the left"
        );
    }

    #[test]
    fn bottom_anchored_column_is_pushed_down_to_the_top() {
        let narrow = rect(0, 0, 500, 1080);
        let (r, side) = placed(rect(100, 50, 100, 100), narrow);
        assert_eq!((side, r.top()), (Side::Right, 0), "150 - 500 clamps to the top edge");
    }

    #[test]
    fn negative_origin_bounds_are_a_pure_translation() {
        let shifted = rect(-1920, 0, 1920, 1080);
        for anchor in [rect(500, 300, 100, 100), rect(1800, 1000, 100, 60)] {
            let (here, side) = placed(anchor, HD);
            let (there, side2) = placed(anchor.translate(euclid::vec2(-1920, 0)), shifted);
            assert_eq!(side, side2);
            assert_eq!(there, here.translate(euclid::vec2(-1920, 0)), "{side:?}");
        }
    }

    #[test]
    fn centre_uses_integer_division() {
        for (aw, w) in [(100, 600), (101, 600), (100, 601), (101, 601)] {
            let anchor = rect(500, 300, aw, 100);
            let fit = Fit {
                row: Footprint {
                    len: w,
                    thick: 56,
                },
                ..FIT
            };
            let (side, r) = place(anchor, HD, fit, near(), None, |_| (w, 56));
            assert_eq!(side, Side::Below);
            let mid = r.left() + w / 2;
            assert!((mid - (500 + aw / 2)).abs() <= 1, "anchor {aw} box {w}: mid {mid}");
        }
    }

    #[test]
    fn size_for_is_called_once_with_the_chosen_axis() {
        let calls = Cell::new(0);
        let axis = Cell::new(None);
        let (side, _) = place(rect(500, 1000, 100, 60), HD, FIT, near(), None, |a| {
            calls.set(calls.get() + 1);
            axis.set(Some(a));
            full(a)
        });
        assert_eq!(calls.get(), 1);
        assert_eq!(axis.get(), Some(side.axis()));
        assert_eq!(side.axis(), Axis::Column);
    }

    /// Each strip is centred with its OWN width, which is what lets a set
    /// swap re-centre under the cursor (`PanelSwapGuard`'s premise).
    #[test]
    fn each_strip_is_centred_with_its_own_width() {
        let anchor = rect(500, 300, 100, 100);
        let mids = [400, 520].map(|w| {
            let (_, r) = place(anchor, HD, FIT, near(), None, |_| (w, 56));
            r.left() + w / 2
        });
        assert!((mids[0] - mids[1]).abs() <= 1, "{mids:?}");
    }

    #[test]
    fn near_scales_by_ceil_at_each_dpi() {
        for (dpi, min, max) in [(1.0, 2, 15), (1.25, 3, 19), (1.5, 3, 23), (2.0, 4, 30)] {
            let near = Near::at_dpi(dpi);
            assert_eq!((near.min_distance, near.max_distance), (min, max), "at {dpi}");
        }
    }
}
