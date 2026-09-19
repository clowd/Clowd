//! Anchor an arranged strip beside a region: choose the side, arrange for
//! that side's axis, move the scene to its origin, and keep the whole box on
//! the bounds (tail wins when it cannot fit).

use super::scene::Scene;
use super::tree::Axis;
use clowd_rust_core::geometry::{RectExt, ScreenRect};

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

/// The LONGEST box the strip can become in one orientation: `len` along it,
/// `thick` across.
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

impl Fit {
    pub fn thick(self, axis: Axis) -> i32 {
        match axis {
            Axis::Row => self.row.thick,
            Axis::Column => self.col.thick,
        }
    }
}

/// `min_distance` is clearance kept from the bounds edge in the fit test;
/// `max_distance` the gap between the anchor and the box.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Near {
    pub min_distance: i32,
    pub max_distance: i32,
}

/// Choose the side, arrange for its axis, move the scene to its origin, and
/// make `bounds` its live rect. `arrange` receives the axis and returns the
/// scene at (0, 0); it runs once. `anchor` is not intersected with `bounds`
/// here: the owner clips it first, so this stays a total function.
pub fn place(anchor: ScreenRect, bounds: ScreenRect, fit: Fit, near: Near, arrange: impl FnOnce(Axis) -> Scene) -> (Scene, Side) {
    let side = choose_side(anchor, bounds, fit, near);
    let mut scene = arrange(side.axis());
    let size = scene.bounds().size;
    let (x, y) = origin(side, anchor, bounds, (size.width, size.height), near);
    // Keep the WHOLE box on the bounds, both axes (a shadow may clip). Only
    // when nothing fits can the box still be longer than the bounds; then
    // the tail stays on the bounds and the head overflows.
    let x = clamp_span(x, size.width, bounds.left(), bounds.right());
    let y = clamp_span(y, size.height, bounds.top(), bounds.bottom());
    scene.translate(x, y);
    scene.live = bounds;
    (scene, side)
}

const CASCADE: [Side; 4] = [Side::Below, Side::Right, Side::Left, Side::Inside];

/// A candidate fits when the box's THICKNESS in that orientation fits the
/// free space on that side AND the longest strip fits the bounds ALONG the
/// strip. Neither test depends on what the strip currently holds, so a
/// content change can never flip the orientation. When no candidate passes
/// both tests the thickness alone decides.
fn choose_side(anchor: ScreenRect, bounds: ScreenRect, fit: Fit, near: Near) -> Side {
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
    CASCADE
        .into_iter()
        .find(|&s| across(s) && along(s))
        .or_else(|| CASCADE.into_iter().find(|&s| across(s)))
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
    use crate::ui::kit::scene::{Kind, Placed};
    use crate::ui::kit::tokens::{self, Scale};
    use crate::ui::kit::tree::Look;
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
        let s = Scale::new(1.0);
        Near {
            min_distance: s.space(tokens::NEAR_MIN_DISTANCE),
            max_distance: s.space(tokens::NEAR_MAX_DISTANCE),
        }
    }

    fn rect(x: i32, y: i32, w: i32, h: i32) -> ScreenRect {
        ScreenRect::from_xy_size(x, y, w, h)
    }

    /// A one-node scene the size of `FIT`'s box for `axis`.
    fn full(axis: Axis) -> Scene {
        let (w, h) = match axis {
            Axis::Row => (FIT.row.len, FIT.row.thick),
            Axis::Column => (FIT.col.thick, FIT.col.len),
        };
        let r = rect(0, 0, w, h);
        Scene {
            nodes: vec![Placed {
                id: None,
                rect: r,
                kind: Kind::Box(Look::default()),
            }],
            live: r,
        }
    }

    fn placed(anchor: ScreenRect, bounds: ScreenRect) -> (ScreenRect, Side) {
        let (scene, side) = place(anchor, bounds, FIT, near(), full);
        (scene.bounds(), side)
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
        assert_eq!(place(anchor, HD, FIT, loose, full).1, Side::Below);
        assert_eq!(place(anchor, HD, FIT, near(), full).1, Side::Right);
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
            let (scene, side) = place(anchor, HD, fit, near(), |_| {
                let r = rect(0, 0, w, 56);
                Scene {
                    nodes: vec![Placed {
                        id: None,
                        rect: r,
                        kind: Kind::Box(Look::default()),
                    }],
                    live: r,
                }
            });
            assert_eq!(side, Side::Below);
            let mid = scene.bounds().left() + w / 2;
            assert!((mid - (500 + aw / 2)).abs() <= 1, "anchor {aw} box {w}: mid {mid}");
        }
    }

    #[test]
    fn place_arranges_once_for_the_chosen_axis_and_sets_live() {
        let calls = Cell::new(0);
        let axis = Cell::new(None);
        let (scene, side) = place(rect(500, 1000, 100, 60), HD, FIT, near(), |a| {
            calls.set(calls.get() + 1);
            axis.set(Some(a));
            full(a)
        });
        assert_eq!(calls.get(), 1);
        assert_eq!(axis.get(), Some(side.axis()));
        assert_eq!(side.axis(), Axis::Column);
        assert_eq!(FIT.thick(side.axis()), 92);
        assert_eq!(scene.live, HD, "live widens to the bounds, not the box");
        assert!(scene.contains(620.0, 700.0));
        assert!(!scene.contains(-1.0, 700.0));
    }
}
