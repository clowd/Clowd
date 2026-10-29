//! Screens (WP-DESKTOP; README section 4.2). Coordinates are the `DesktopScreen` contract:
//! physical virtual-desktop pixels on Windows, CoreGraphics global points with a top-left origin
//! on macOS. macOS answers from CoreGraphics only (thread-safe), so it knows no work area.

use crate::error::Error;
use crate::ffi::types::PriscRect;
use crate::panic;

/// One screen (64 bytes).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct PriscScreen {
    pub bounds: PriscRect,
    /// The work area (bounds minus taskbar / Dock); equal to `bounds` when `work_area_known` is 0.
    pub work_area: PriscRect,
    /// Effective DPI / 96 (Windows); mode pixel width / point width (macOS).
    pub scale: f64,
    /// 0 = unknown (an OS answer of 0 or 1, NaN or infinity).
    pub refresh_hz: f64,
    /// HMONITOR (Windows) or CGDirectDisplayID (macOS).
    pub display_id: u64,
    pub is_primary: u8,
    /// 1 on Windows (rcWork); 0 on macOS (CoreGraphics has no work area).
    pub work_area_known: u8,
    pub _reserved: [u8; 6],
}

/// What `prisc_screens_at_point` / `prisc_screens_at_rect` answer when nothing matches.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscScreenFallback {
    /// `PRISC_S_NONE`.
    None = 0,
    /// The nearest screen.
    Nearest = 1,
    /// The primary screen.
    Primary = 2,
}

crate::convert::ffi_enum!(PriscScreenFallback: i32 { None, Nearest, Primary });

/// Every screen, written into `out` up to `capacity` entries; `out_count` always receives the full
/// count (`capacity` 0 with a null `out` is a sizing probe, README B7).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_OS`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_screens_list(out: *mut PriscScreen, capacity: u32, out_count: *mut u32) -> i32 {
    panic::guard("prisc_screens_list", || {
        let _ = (out, capacity, out_count);
        Err(Error::unsupported("prisc_screens_list is not implemented yet"))
    })
}

/// The screen containing the point, or the `fallback` (a `PriscScreenFallback`) when none does.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_S_NONE` (off-screen with fallback None), `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_screens_at_point(x: i32, y: i32, fallback: i32, out: *mut PriscScreen) -> i32 {
    panic::guard("prisc_screens_at_point", || {
        let _ = (x, y, fallback, out);
        Err(Error::unsupported("prisc_screens_at_point is not implemented yet"))
    })
}

/// The screen with the largest overlap with `rect`, or the `fallback` when none overlaps.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_S_NONE`, `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_screens_at_rect(rect: *const PriscRect, fallback: i32, out: *mut PriscScreen) -> i32 {
    panic::guard("prisc_screens_at_rect", || {
        let _ = (rect, fallback, out);
        Err(Error::unsupported("prisc_screens_at_rect is not implemented yet"))
    })
}

/// The primary screen.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_OS`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_screens_primary(out: *mut PriscScreen) -> i32 {
    panic::guard("prisc_screens_primary", || {
        let _ = out;
        Err(Error::unsupported("prisc_screens_primary is not implemented yet"))
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::{offset_of, size_of};

    #[test]
    fn layouts_are_pinned() {
        assert_eq!(size_of::<PriscScreen>(), 64);
        assert_eq!(offset_of!(PriscScreen, bounds), 0);
        assert_eq!(offset_of!(PriscScreen, work_area), 16);
        assert_eq!(offset_of!(PriscScreen, scale), 32);
        assert_eq!(offset_of!(PriscScreen, refresh_hz), 40);
        assert_eq!(offset_of!(PriscScreen, display_id), 48);
        assert_eq!(offset_of!(PriscScreen, is_primary), 56);
        assert_eq!(offset_of!(PriscScreen, work_area_known), 57);
        assert_eq!(offset_of!(PriscScreen, _reserved), 58);
    }
}
