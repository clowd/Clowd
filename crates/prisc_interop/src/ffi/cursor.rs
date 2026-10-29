//! The cursor position (WP-WINDOW; README section 4.3).

use crate::error::Result;
use crate::ffi::types::PriscPoint;
use crate::{panic, util};

/// The cursor position right now, in the space `prisc_screens_*` reports screens in: physical
/// virtual-desktop pixels on Windows (`GetCursorPos`), CoreGraphics global points with a top-left
/// origin on macOS (the location of a fresh `CGEvent`, floored). Needs no window and no primary
/// screen. `out_point` is written only on `PRISC_OK`.
///
/// Status: `PRISC_OK`; `PRISC_S_NONE` when the OS will not say (`GetCursorPos` failed, as on a
/// secure desktop, or the window server made no event); `PRISC_E_INVALID_ARG` for a null
/// `out_point`; `PRISC_E_UNSUPPORTED` on a platform without a cursor backend.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_cursor_position(out_point: *mut PriscPoint) -> i32 {
    panic::guard("prisc_cursor_position", || cursor_position(out_point))
}

unsafe fn cursor_position(out_point: *mut PriscPoint) -> Result {
    let out_point = util::out(out_point, "out_point")?;
    #[cfg(windows)]
    let point = prisc_rust_core::desktop::cursor_position().map(|p| (p.x, p.y));
    #[cfg(target_os = "macos")]
    let point = prisc_rust_core::desktop::cursor_location().map(|p| (p.x.floor() as i32, p.y.floor() as i32));
    #[cfg(any(windows, target_os = "macos"))]
    {
        match point {
            Some((x, y)) => {
                *out_point = PriscPoint {
                    x,
                    y,
                };
                Ok(crate::ffi::status::PRISC_OK)
            }
            None => Ok(crate::ffi::status::PRISC_S_NONE),
        }
    }
    #[cfg(not(any(windows, target_os = "macos")))]
    {
        let _ = out_point;
        Err(crate::error::Error::unsupported("there is no cursor backend on this platform"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status::{PriscInteropStatus, PRISC_OK, PRISC_S_NONE};

    #[test]
    fn a_null_out_is_invalid_arg() {
        assert_eq!(
            unsafe { prisc_cursor_position(std::ptr::null_mut()) },
            PriscInteropStatus::InvalidArg as i32
        );
    }

    #[test]
    fn the_position_is_written_only_on_ok() {
        let mut point = PriscPoint {
            x: i32::MIN,
            y: i32::MIN,
        };
        let status = unsafe { prisc_cursor_position(&mut point) };
        if !cfg!(any(windows, target_os = "macos")) {
            assert_eq!(status, PriscInteropStatus::Unsupported as i32);
            return;
        }
        if status == PRISC_S_NONE {
            assert_eq!(
                point,
                PriscPoint {
                    x: i32::MIN,
                    y: i32::MIN
                }
            );
            return;
        }
        assert_eq!(status, PRISC_OK);
        assert_ne!(
            point,
            PriscPoint {
                x: i32::MIN,
                y: i32::MIN
            }
        );
    }
}
