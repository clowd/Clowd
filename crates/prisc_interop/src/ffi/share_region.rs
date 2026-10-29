//! The share-region rectangle rules (README section 4.10) over `prisc_rust_core::share_region`, the
//! one implementation the region helper (prisc_share_region) applies to every region it mirrors.
//! Rects are in capture space: physical pixels in virtual-desktop coordinates on Windows,
//! CoreGraphics points on macOS; x and y may be negative.

use crate::ffi::status::PRISC_OK;
use crate::ffi::types::PriscRect;
use crate::{panic, util};
use prisc_rust_core::geometry::{RectExt, ScreenRect};
use prisc_rust_core::share_region::{self, Handle};

/// Which part of the region a drag moves: the corners, the edge midpoints, or the body (a move).
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscShareRegionHandle {
    TopLeft = 0,
    TopRight = 1,
    BottomLeft = 2,
    BottomRight = 3,
    Left = 4,
    Top = 5,
    Right = 6,
    Bottom = 7,
    Body = 8,
}

crate::convert::ffi_enum!(PriscShareRegionHandle: i32 {
    TopLeft, TopRight, BottomLeft, BottomRight, Left, Top, Right, Bottom, Body
});

impl From<PriscShareRegionHandle> for Handle {
    fn from(handle: PriscShareRegionHandle) -> Self {
        match handle {
            PriscShareRegionHandle::TopLeft => Handle::TopLeft,
            PriscShareRegionHandle::TopRight => Handle::TopRight,
            PriscShareRegionHandle::BottomLeft => Handle::BottomLeft,
            PriscShareRegionHandle::BottomRight => Handle::BottomRight,
            PriscShareRegionHandle::Left => Handle::Left,
            PriscShareRegionHandle::Top => Handle::Top,
            PriscShareRegionHandle::Right => Handle::Right,
            PriscShareRegionHandle::Bottom => Handle::Bottom,
            PriscShareRegionHandle::Body => Handle::Body,
        }
    }
}

/// The region the helper would mirror for `rect`: each side floored at the minimum region size
/// (`ShareRegionGeometry.MinRegion`) and then rounded down to even, the origin untouched.
/// Idempotent.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null `rect` or `out`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_share_region_normalize(rect: *const PriscRect, out: *mut PriscRect) -> i32 {
    panic::guard("prisc_share_region_normalize", || {
        let rect = util::input(rect, "rect")?;
        let out = util::out(out, "out")?;
        *out = to_ffi(share_region::normalize(from_ffi(rect)));
        Ok(PRISC_OK)
    })
}

/// The region after dragging `handle` (a `PriscShareRegionHandle`) by (`dx`, `dy`) from `anchor`,
/// the region as the drag began. The body moves the region unchanged in size; any other handle
/// moves only its own edges, swaps an edge that crosses its opposite (and which one is moving), and
/// normalizes the result by pushing back the moving edge, so the anchored edge stays pinned.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null `anchor` or `out`, or an unknown `handle`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_share_region_apply_drag(
    anchor: *const PriscRect,
    handle: i32,
    dx: i32,
    dy: i32,
    out: *mut PriscRect,
) -> i32 {
    panic::guard("prisc_share_region_apply_drag", || {
        let anchor = util::input(anchor, "anchor")?;
        let handle = PriscShareRegionHandle::from_raw(handle)?;
        let out = util::out(out, "out")?;
        *out = to_ffi(share_region::apply_drag(from_ffi(anchor), handle.into(), dx, dy));
        Ok(PRISC_OK)
    })
}

fn from_ffi(r: &PriscRect) -> ScreenRect {
    ScreenRect::from_xy_size(r.x, r.y, r.width, r.height)
}

fn to_ffi(r: ScreenRect) -> PriscRect {
    PriscRect {
        x: r.origin.x,
        y: r.origin.y,
        width: r.size.width,
        height: r.size.height,
    }
}
