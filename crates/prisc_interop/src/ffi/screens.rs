//! Screens (README section 4.2), a thin layer over `prisc_rust_core::desktop::screens`, the list
//! the capture overlay and the share-region prompt read too. Coordinates are the `DesktopScreen`
//! contract: physical virtual-desktop pixels on Windows, CoreGraphics global points with a
//! top-left origin on macOS. macOS answers from CoreGraphics only (thread-safe), so it knows no
//! work area. Elsewhere the list is empty and the lookups answer `PRISC_S_NONE`.

use prisc_rust_core::desktop::screens::{self, Fallback, OsError, Screen};
use prisc_rust_core::geometry::{RectExt, ScreenPoint, ScreenRect};

use crate::error::{Error, Result};
use crate::ffi::status::{PRISC_OK, PRISC_S_NONE};
use crate::ffi::types::PriscRect;
use crate::{panic, util};

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
/// count (`capacity` 0 with a null `out` is a sizing probe, README B7). An empty desktop is
/// `PRISC_OK` with a count of 0.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null `out_count`, or a null `out` with a
/// non-zero `capacity`; `PRISC_E_OS` when the OS enumeration failed (`EnumDisplayMonitors` /
/// `CGGetActiveDisplayList`), code in `prisc_last_error_code`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_screens_list(out: *mut PriscScreen, capacity: u32, out_count: *mut u32) -> i32 {
    panic::guard("prisc_screens_list", || list(out, capacity, out_count))
}

unsafe fn list(out: *mut PriscScreen, capacity: u32, out_count: *mut u32) -> Result {
    let out_count = util::out(out_count, "out_count")?;
    let out = util::slice_mut(out, capacity as usize, "out")?;
    let screens = screens::list().map_err(os_error)?;
    for (slot, screen) in out.iter_mut().zip(&screens) {
        *slot = to_ffi(screen);
    }
    // the total, not what fitted: that is what lets a caller size and call again
    *out_count = u32::try_from(screens.len()).unwrap_or(u32::MAX);
    Ok(PRISC_OK)
}

/// The screen containing the point, or the `fallback` (a `PriscScreenFallback`) when none does.
/// Windows asks `MonitorFromPoint` (`MONITOR_DEFAULTTONULL` / `NEAREST` / `PRIMARY`); macOS picks
/// from the CoreGraphics list the same way. `out` is written only on `PRISC_OK`.
///
/// Status: `PRISC_OK`; `PRISC_S_NONE` when the point is on no screen and the fallback is None (or
/// the desktop has no screen at all); `PRISC_E_INVALID_ARG` for a null `out` or an unknown
/// `fallback`; `PRISC_E_OS`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_screens_at_point(x: i32, y: i32, fallback: i32, out: *mut PriscScreen) -> i32 {
    panic::guard("prisc_screens_at_point", || at_point(x, y, fallback, out))
}

unsafe fn at_point(x: i32, y: i32, fallback: i32, out: *mut PriscScreen) -> Result {
    let out = util::out(out, "out")?;
    let fallback = PriscScreenFallback::from_raw(fallback)?.into();
    write_found(out, screens::at_point(ScreenPoint::new(x, y), fallback))
}

/// The screen with the largest overlap with `rect`, or the `fallback` when none overlaps. The rect
/// is `x, y, x + width, y + height` exactly as `MonitorFromRect` takes it; an empty rect is looked
/// up by its top-left point. `out` is written only on `PRISC_OK`.
///
/// Status: `PRISC_OK`; `PRISC_S_NONE`; `PRISC_E_INVALID_ARG` for a null `rect` or `out`, or an
/// unknown `fallback`; `PRISC_E_OS`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_screens_at_rect(rect: *const PriscRect, fallback: i32, out: *mut PriscScreen) -> i32 {
    panic::guard("prisc_screens_at_rect", || at_rect(rect, fallback, out))
}

unsafe fn at_rect(rect: *const PriscRect, fallback: i32, out: *mut PriscScreen) -> Result {
    let rect = util::input(rect, "rect")?;
    let out = util::out(out, "out")?;
    let fallback = PriscScreenFallback::from_raw(fallback)?.into();
    write_found(out, screens::at_rect(to_rect(rect), fallback))
}

/// The primary screen (the one at the desktop origin). `out` is written only on `PRISC_OK`.
///
/// Status: `PRISC_OK`; `PRISC_S_NONE` on a desktop without one (a headless session, or a platform
/// without a screen list); `PRISC_E_INVALID_ARG` for a null `out`; `PRISC_E_OS`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_screens_primary(out: *mut PriscScreen) -> i32 {
    panic::guard("prisc_screens_primary", || primary(out))
}

unsafe fn primary(out: *mut PriscScreen) -> Result {
    let out = util::out(out, "out")?;
    write_found(out, screens::primary())
}

impl From<PriscScreenFallback> for Fallback {
    fn from(value: PriscScreenFallback) -> Self {
        match value {
            PriscScreenFallback::None => Fallback::None,
            PriscScreenFallback::Nearest => Fallback::Nearest,
            PriscScreenFallback::Primary => Fallback::Primary,
        }
    }
}

fn write_found(out: &mut PriscScreen, found: std::result::Result<Option<Screen>, OsError>) -> Result {
    match found.map_err(os_error)? {
        Some(screen) => {
            *out = to_ffi(&screen);
            Ok(PRISC_OK)
        }
        None => Ok(PRISC_S_NONE),
    }
}

pub(crate) fn os_error(e: OsError) -> Error {
    Error::os(e.code, e.message)
}

pub(crate) fn to_rect(r: &PriscRect) -> ScreenRect {
    ScreenRect::from_xy_size(r.x, r.y, r.width, r.height)
}

fn from_rect(r: &ScreenRect) -> PriscRect {
    PriscRect {
        x: r.origin.x,
        y: r.origin.y,
        width: r.size.width,
        height: r.size.height,
    }
}

fn to_ffi(screen: &Screen) -> PriscScreen {
    let bounds = from_rect(&screen.bounds);
    PriscScreen {
        bounds,
        work_area: screen
            .work_area
            .as_ref()
            .map_or(bounds, from_rect),
        scale: screen.scale,
        refresh_hz: screen.refresh_hz.unwrap_or(0.0),
        display_id: screen.id,
        is_primary: util::to_flag(screen.is_primary),
        work_area_known: util::to_flag(screen.work_area.is_some()),
        _reserved: [0; 6],
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status::PriscInteropStatus;
    use std::mem::{offset_of, size_of};
    use std::ptr;

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

    #[test]
    fn an_unknown_work_area_reads_as_the_bounds() {
        let screen = Screen {
            id: 9,
            bounds: ScreenRect::from_xy_size(-10, 20, 300, 200),
            work_area: None,
            scale: 2.0,
            refresh_hz: None,
            is_primary: true,
            pixel_width: 600,
            pixel_height: 400,
            device_name: String::new(),
        };
        let ffi = to_ffi(&screen);
        assert_eq!(
            ffi.bounds,
            PriscRect {
                x: -10,
                y: 20,
                width: 300,
                height: 200
            }
        );
        assert_eq!(ffi.work_area, ffi.bounds);
        assert_eq!((ffi.work_area_known, ffi.is_primary, ffi.display_id), (0, 1, 9));
        assert_eq!((ffi.scale, ffi.refresh_hz), (2.0, 0.0));
    }

    #[test]
    fn null_arguments_and_unknown_fallbacks_are_invalid_arg() {
        let invalid = PriscInteropStatus::InvalidArg as i32;
        let mut count = 0u32;
        let mut screen = PriscScreen::default();
        let rect = PriscRect::default();
        assert_eq!(unsafe { prisc_screens_list(ptr::null_mut(), 0, ptr::null_mut()) }, invalid);
        assert_eq!(unsafe { prisc_screens_list(ptr::null_mut(), 4, &mut count) }, invalid);
        assert_eq!(unsafe { prisc_screens_at_point(0, 0, 0, ptr::null_mut()) }, invalid);
        assert_eq!(unsafe { prisc_screens_at_point(0, 0, 3, &mut screen) }, invalid);
        assert_eq!(unsafe { prisc_screens_at_point(0, 0, -1, &mut screen) }, invalid);
        assert_eq!(unsafe { prisc_screens_at_rect(ptr::null(), 0, &mut screen) }, invalid);
        assert_eq!(unsafe { prisc_screens_at_rect(&rect, 0, ptr::null_mut()) }, invalid);
        assert_eq!(unsafe { prisc_screens_at_rect(&rect, 7, &mut screen) }, invalid);
        assert_eq!(unsafe { prisc_screens_primary(ptr::null_mut()) }, invalid);
        // nothing was written on any of those paths
        assert_eq!(screen, PriscScreen::default());
    }
}
