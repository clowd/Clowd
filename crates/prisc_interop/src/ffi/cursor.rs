//! The cursor position (WP-WINDOW; README section 4.3).

use crate::error::Error;
use crate::ffi::types::PriscPoint;
use crate::panic;

/// The cursor position in the screen coordinate space (`GetCursorPos` / the CGEvent location,
/// top-left origin, floored). Needs no window and no primary screen.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_S_NONE` (the OS will not say), `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_cursor_position(out_point: *mut PriscPoint) -> i32 {
    panic::guard("prisc_cursor_position", || {
        let _ = out_point;
        Err(Error::unsupported("prisc_cursor_position is not implemented yet"))
    })
}
