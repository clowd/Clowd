//! macOS app integration (WP-MACAPP; README section 4.5).

use crate::error::Error;
use crate::panic;

/// `NSApplicationActivationPolicy`.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscActivationPolicy {
    Regular = 0,
    Accessory = 1,
    Prohibited = 2,
}

/// macOS: sets the activation policy (a `PriscActivationPolicy`) and, for Regular when asked,
/// activates the app, in one hop.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_STATE`, `PRISC_E_UNSUPPORTED` off macOS.
///
/// Thread: must be the macOS main thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_app_set_activation_policy_macos(policy: i32, activate_if_regular: u8) -> i32 {
    panic::guard("prisc_app_set_activation_policy_macos", || {
        let _ = (policy, activate_if_regular);
        Err(Error::unsupported("prisc_app_set_activation_policy_macos is not implemented yet"))
    })
}
