//! Physical key state and Secure Event Input (WP-KEYS; README section 4.9).

use crate::error::Error;
use crate::panic;

/// Bit i of `out_mask` = whether `codes[i]` (a Windows VK, mouse-button VKs included, or a macOS
/// CGKeyCode) is physically down; at most 64 codes.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_keyboard_keys_down(codes: *const u32, count: usize, out_mask: *mut u64) -> i32 {
    panic::guard("prisc_keyboard_keys_down", || {
        let _ = (codes, count, out_mask);
        Err(Error::unsupported("prisc_keyboard_keys_down is not implemented yet"))
    })
}

/// Whether macOS Secure Event Input is on; 0 off macOS.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_keyboard_secure_input_enabled(out_enabled: *mut u8) -> i32 {
    panic::guard("prisc_keyboard_secure_input_enabled", || {
        let _ = out_enabled;
        Err(Error::unsupported("prisc_keyboard_secure_input_enabled is not implemented yet"))
    })
}
