//! The idle time (WP-DESKTOP; README section 4.2).

use crate::error::Error;
use crate::panic;

/// Seconds since the last user input: `GetLastInputInfo` against `GetTickCount` (wrap-safe) on
/// Windows, `CGEventSourceSecondsSinceLastEventType` on macOS.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_OS`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_idle_seconds(out_seconds: *mut f64) -> i32 {
    panic::guard("prisc_idle_seconds", || {
        let _ = out_seconds;
        Err(Error::unsupported("prisc_idle_seconds is not implemented yet"))
    })
}
