//! The share-region rectangle rules (WP-PURE; README section 4.10).

use crate::error::Error;
use crate::ffi::types::PriscRect;
use crate::panic;

/// Which part of the region a drag moves.
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

/// The region clamped to the minimum size the helper accepts.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_share_region_normalize(rect: *const PriscRect, out: *mut PriscRect) -> i32 {
    panic::guard("prisc_share_region_normalize", || {
        let _ = (rect, out);
        Err(Error::unsupported("prisc_share_region_normalize is not implemented yet"))
    })
}

/// The region after dragging `handle` (a `PriscShareRegionHandle`) by (`dx`, `dy`) from `anchor`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
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
        let _ = (anchor, handle, dx, dy, out);
        Err(Error::unsupported("prisc_share_region_apply_drag is not implemented yet"))
    })
}
