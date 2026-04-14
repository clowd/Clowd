use winit::window::CursorIcon;

use crate::geometry::{RectExt, ScreenPointF, ScreenRect};

/// Pre-DPI radius (in virtual-desktop pixels) of the resize-handle hit
/// boxes used once a selection is captured. Matches
/// `UNSCALED_DRAG_HANDLE_SIZE` at DxScreenCapture.cpp:23. The C++ scales
/// it by `dpizoom = monitor.dpi / BASE_DPI`; we do the same per-monitor
/// using `monitor_dpi`.
pub const UNSCALED_DRAG_HANDLE_SIZE: f32 = 10.0;

/// Result of hit-testing the cursor against a captured selection rect.
/// Drives both the cursor-icon swap (one-to-one with `IDC_*` cursors in
/// `FrameSetCursor` at DxScreenCapture.cpp:1732-1798) and the resize/move
/// drag mode that mouse-down promotes it to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Hittest {
    /// Cursor is outside the selection (and not on any handle). Default
    /// arrow cursor; mouse-down does nothing in this state.
    Outside,
    /// Cursor is inside the selection's interior. Move cursor;
    /// mouse-down enters MoveDrag.
    Inside,
    Top,
    Right,
    Bottom,
    Left,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

impl Hittest {
    /// Map this hittest to the cursor icon that should be displayed
    /// while hovering. Mirrors `FrameSetCursor`'s switch at
    /// DxScreenCapture.cpp:1736.
    pub fn cursor(self) -> CursorIcon {
        match self {
            Hittest::Outside => CursorIcon::Default,
            Hittest::Inside => CursorIcon::Move,
            Hittest::Top | Hittest::Bottom => CursorIcon::NsResize,
            Hittest::Left | Hittest::Right => CursorIcon::EwResize,
            Hittest::TopLeft | Hittest::BottomRight => CursorIcon::NwseResize,
            Hittest::TopRight | Hittest::BottomLeft => CursorIcon::NeswResize,
        }
    }
}

/// What kind of drag is currently in progress. Set on mouse-down,
/// consumed by every CursorMoved until mouse-up. The pre-capture
/// "drawing a new selection" path doesn't use this enum (it's still
/// driven by `mouse_down` + `dragging`); these variants only fire
/// once `captured` is true.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DragMode {
    /// Translating the whole rect. Each frame the rect is shifted by
    /// `(virtual_cursor - mouse_down_pt)` from the snapshotted anchor
    /// selection, then soft-clamped to keep it on-screen *without*
    /// changing its size.
    Move,
    /// Resizing via one of the eight handles. The handle's edges are
    /// dragged to the cursor each frame; the un-touched edges keep
    /// their value from the snapshotted anchor selection. Hard-clamp
    /// each moved edge to vd bounds, then normalise via min/max so
    /// the rect stays well-formed even when the user drags past the
    /// opposite edge (matches the C++ `Xy12Rect` normalisation).
    Resize(Hittest),
}

/// Hit-test the cursor (in virtual-desktop pixels) against a captured
/// selection rect. The handle radius is `UNSCALED_DRAG_HANDLE_SIZE *
/// dpi_scale` rounded down, mirroring DxScreenCapture.cpp:1676. Corners
/// are tested before edges so they win at the corner squares (same
/// loop order as DxScreenCapture.cpp:1695-1721).
pub fn hit_test(cursor: ScreenPointF, sel: ScreenRect, dpi_scale: f32) -> Hittest {
    let r = (UNSCALED_DRAG_HANDLE_SIZE * dpi_scale).floor() as i32;
    let cx = cursor.x.floor() as i32;
    let cy = cursor.y.floor() as i32;

    // Corner: a `(2r+1) × (2r+1)` square centred on the corner pixel,
    // matching `PtToWidenedRect` at rectex.h:119-127.
    let in_corner = |x: i32, y: i32| -> bool {
        cx >= x - r && cx <= x + r && cy >= y - r && cy <= y + r
    };
    // Edge: a rect widened by `r` on every side around the line
    // segment. Same shape as the corners on the perpendicular axis,
    // but stretched along the edge — matching `LineToWidenedRect` at
    // rectex.h:129-137.
    let in_edge = |x1: i32, y1: i32, x2: i32, y2: i32| -> bool {
        let lx = x1.min(x2) - r;
        let rx = x1.max(x2) + r;
        let ty = y1.min(y2) - r;
        let by = y1.max(y2) + r;
        cx >= lx && cx <= rx && cy >= ty && cy <= by
    };

    let l = sel.left();
    let t = sel.top();
    let r_edge = sel.right();
    let b = sel.bottom();

    // Order matches the C++ handles[] array at DxScreenCapture.cpp:1695:
    // four corners first, then four edges, with first-hit-wins.
    if in_corner(l, t) {
        return Hittest::TopLeft;
    }
    if in_corner(r_edge, t) {
        return Hittest::TopRight;
    }
    if in_corner(r_edge, b) {
        return Hittest::BottomRight;
    }
    if in_corner(l, b) {
        return Hittest::BottomLeft;
    }
    if in_edge(l, t, r_edge, t) {
        return Hittest::Top;
    }
    if in_edge(r_edge, t, r_edge, b) {
        return Hittest::Right;
    }
    if in_edge(r_edge, b, l, b) {
        return Hittest::Bottom;
    }
    if in_edge(l, t, l, b) {
        return Hittest::Left;
    }

    // Fall through: inside the rect (move) or fully outside.
    if cx >= l && cx < r_edge && cy >= t && cy < b {
        Hittest::Inside
    } else {
        Hittest::Outside
    }
}

/// Translate the anchor rect by `(delta_x, delta_y)` and return its
/// intersection with the virtual desktop bounds, or `None` if the
/// translated rect is fully outside the vd. This is the "move" path's
/// core: the underlying logical position is free-form (whatever the
/// cursor points at), and what gets drawn + eventually saved is the
/// *cropped* rect so the selection can't "run off" a display — it
/// visually shrinks against the boundary and reappears when the
/// cursor comes back, all without mutating the anchor.
pub fn move_and_crop(
    anchor: ScreenRect,
    delta_x: i32,
    delta_y: i32,
    vd: ScreenRect,
) -> Option<ScreenRect> {
    let moved = ScreenRect::from_xy_size(
        anchor.left() + delta_x,
        anchor.top() + delta_y,
        anchor.width(),
        anchor.height(),
    );
    intersect_rects(moved, vd)
}

/// Intersection of two rects, or `None` if they don't overlap. Used by
/// the move-and-crop path to clip the translated rect to vd bounds
/// each frame.
pub fn intersect_rects(a: ScreenRect, b: ScreenRect) -> Option<ScreenRect> {
    let l = a.left().max(b.left());
    let t = a.top().max(b.top());
    let r = a.right().min(b.right());
    let bot = a.bottom().min(b.bottom());
    if r > l && bot > t {
        Some(ScreenRect::from_exact(l, t, r, bot))
    } else {
        None
    }
}

/// Apply a `Resize` drag to an anchor selection rect via the cursor's
/// current virtual-desktop position. Each handle pulls a different
/// subset of the four edges:
///   * corners drag two edges (the corner-opposite stays fixed)
///   * edges drag one edge (the other three stay fixed)
///   * inside / outside don't enter resize mode
///
/// Each moved edge is hard-clamped to the vd bounds before the rect is
/// re-normalised, so dragging the right edge past the left flips the
/// rect (matches the C++ `Xy12Rect` normalisation at rectex.h:111-117
/// and DxScreenCapture.cpp:1419-1458).
pub fn resize_with_clamp(
    anchor: ScreenRect,
    handle: Hittest,
    cursor_x: i32,
    cursor_y: i32,
    vd: ScreenRect,
) -> ScreenRect {
    let mut left = anchor.left();
    let mut top = anchor.top();
    let mut right = anchor.right();
    let mut bottom = anchor.bottom();
    // Each handle pins the opposite corner and drags the named one(s).
    match handle {
        Hittest::Top => {
            top = cursor_y;
        }
        Hittest::Bottom => {
            bottom = cursor_y;
        }
        Hittest::Left => {
            left = cursor_x;
        }
        Hittest::Right => {
            right = cursor_x;
        }
        Hittest::TopLeft => {
            left = cursor_x;
            top = cursor_y;
        }
        Hittest::TopRight => {
            right = cursor_x;
            top = cursor_y;
        }
        Hittest::BottomLeft => {
            left = cursor_x;
            bottom = cursor_y;
        }
        Hittest::BottomRight => {
            right = cursor_x;
            bottom = cursor_y;
        }
        // The Inside/Outside hittests aren't valid resize handles —
        // mouse-down dispatch routes them to Move / no-op respectively,
        // so we should never see them here. Defensive no-op.
        Hittest::Inside | Hittest::Outside => {}
    }
    // Clamp every edge — even ones we didn't move — so an anchor that
    // was already partly off-screen can't escape further. The C++
    // `ClipRectBy` only runs at finalisation, but here we want the
    // soft-clamp behaviour live on every frame.
    left = left.clamp(vd.left(), vd.right());
    right = right.clamp(vd.left(), vd.right());
    top = top.clamp(vd.top(), vd.bottom());
    bottom = bottom.clamp(vd.top(), vd.bottom());
    // Normalise into a min/max rect (mirrors `Xy12Rect`).
    let nl = left.min(right);
    let nr = left.max(right);
    let nt = top.min(bottom);
    let nb = top.max(bottom);
    ScreenRect::from_exact(nl, nt, nr, nb)
}

/// Look up the DPI scale of whichever monitor currently contains `p`,
/// returning `1.0` if the point falls in the multi-monitor dead zone.
/// Used at mouse-down to seed the drag-distance threshold so that
/// dragging across a monitor boundary doesn't change the threshold mid-
/// gesture (matches the C++ behaviour at DxScreenCapture.cpp:1497, which
/// reads `dpizoom` once from the monitor under `mouseDownPt`).
pub fn dpi_at_point(p: ScreenPointF, monitors: &[ScreenRect], dpis: &[f32]) -> f32 {
    for (i, m) in monitors.iter().enumerate() {
        let mx = m.min_x() as f32;
        let my = m.min_y() as f32;
        let mw = m.width() as f32;
        let mh = m.height() as f32;
        if p.x >= mx && p.x < mx + mw && p.y >= my && p.y < my + mh {
            return dpis.get(i).copied().unwrap_or(1.0);
        }
    }
    1.0
}

/// Clamp the point to whichever monitor currently contains it, or to the
/// nearest monitor if it's in the multi-monitor dead zone. A tiny epsilon
/// is subtracted from the max edge so the point never lands on the
/// exclusive right/bottom of a rect (matches Screens.cpp:147-153 which
/// subtracts 0.001 from the right/bottom bounds).
pub fn clamp_to_nearest_monitor(p: &mut ScreenPointF, monitors: &[ScreenRect]) {
    if monitors.is_empty() {
        return;
    }
    // First try: already inside a monitor? Leave it alone.
    for m in monitors {
        let mx = m.min_x() as f32;
        let my = m.min_y() as f32;
        let mw = m.width() as f32;
        let mh = m.height() as f32;
        if p.x >= mx && p.x < mx + mw && p.y >= my && p.y < my + mh {
            return;
        }
    }
    // Not inside any monitor — pick the one whose centre is closest and
    // clamp into its bounds. Distance-to-centre is good enough as a
    // "nearest monitor" heuristic for a cursor that's just crossed a
    // boundary.
    let (best_ix, _) = monitors
        .iter()
        .enumerate()
        .map(|(i, m)| {
            let cx = m.min_x() as f32 + m.width() as f32 * 0.5;
            let cy = m.min_y() as f32 + m.height() as f32 * 0.5;
            let dx = p.x - cx;
            let dy = p.y - cy;
            (i, dx * dx + dy * dy)
        })
        .fold((0usize, f32::INFINITY), |(bi, bd), (i, d)| {
            if d < bd {
                (i, d)
            } else {
                (bi, bd)
            }
        });
    let m = &monitors[best_ix];
    let min_x = m.min_x() as f32;
    let min_y = m.min_y() as f32;
    let max_x = (m.min_x() + m.width()) as f32 - 0.001;
    let max_y = (m.min_y() + m.height()) as f32 - 0.001;
    p.x = p.x.clamp(min_x, max_x);
    p.y = p.y.clamp(min_y, max_y);
}
