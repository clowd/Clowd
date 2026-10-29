//! System preferences (WP-DESKTOP; README section 4.2).

use crate::error::Error;
use crate::panic;

/// Whether the user wants UI animations: `SPI_GETCLIENTAREAANIMATION` on Windows (a failed query
/// reads as enabled), `!accessibilityDisplayShouldReduceMotion` on macOS.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_system_animations_enabled(out_enabled: *mut u8) -> i32 {
    panic::guard("prisc_system_animations_enabled", || {
        let _ = out_enabled;
        Err(Error::unsupported("prisc_system_animations_enabled is not implemented yet"))
    })
}
