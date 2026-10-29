//! macOS app integration: the activation policy, which is how Prisc's dock icon appears and
//! disappears (README section 4.5).
//!
//! Prisc is tray resident: on macOS the dock icon should exist only while a real window is open.
//! Avalonia reads `ShowInDock` once at startup, so the policy is flipped at runtime instead,
//! Regular while a visible taskbar window exists and Accessory otherwise (C# `MacDockIcon`).

#[cfg(target_os = "macos")]
use crate::platform::macos::app;

use crate::error::Result;
use crate::panic;

/// `NSApplicationActivationPolicy`.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscActivationPolicy {
    /// A normal app with a dock icon and a menu bar.
    Regular = 0,
    /// No dock icon and no menu bar; windows are still allowed (the tray-resident state).
    Accessory = 1,
    /// The app cannot be activated at all.
    Prohibited = 2,
}

crate::convert::ffi_enum!(PriscActivationPolicy: i32 { Regular, Accessory, Prohibited });

/// macOS: sets `-[NSApplication setActivationPolicy:]` to `policy` (a `PriscActivationPolicy`)
/// and, when `activate_if_regular` is non-zero and the policy is Regular, follows it with
/// `activateIgnoringOtherApps:YES` in the same main-thread hop: an Accessory to Regular flip on its
/// own leaves the app behind whichever app was frontmost, and the window that caused it opens
/// behind that app. Owns nothing.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for an unknown policy (checked first, on every OS and
/// thread); `PRISC_E_STATE` off the main thread; `PRISC_E_OS` (code 0) when AppKit refused the
/// policy; `PRISC_E_UNSUPPORTED` off macOS.
///
/// Thread: must be the macOS main thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_app_set_activation_policy_macos(policy: i32, activate_if_regular: u8) -> i32 {
    panic::guard("prisc_app_set_activation_policy_macos", || {
        set_activation_policy(policy, activate_if_regular)
    })
}

fn set_activation_policy(policy: i32, activate_if_regular: u8) -> Result {
    let policy = PriscActivationPolicy::from_raw(policy)?;
    #[cfg(target_os = "macos")]
    {
        app::set_activation_policy(policy, crate::util::flag(activate_if_regular))
    }
    #[cfg(not(target_os = "macos"))]
    {
        let _ = (policy, activate_if_regular);
        Err(crate::error::Error::unsupported("the activation policy exists only on macOS"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status::PriscInteropStatus as S;

    #[test]
    fn an_unknown_policy_is_invalid_arg_on_every_thread_and_os() {
        for policy in [-1, 3, 99] {
            assert_eq!(
                unsafe { prisc_app_set_activation_policy_macos(policy, 1) },
                S::InvalidArg as i32,
                "{policy}"
            );
        }
    }

    /// libtest never runs on the process main thread, so this is the reachable half on macOS.
    #[test]
    fn a_known_policy_off_the_main_thread_never_touches_appkit() {
        let expected = if cfg!(target_os = "macos") { S::State } else { S::Unsupported };
        for policy in [
            PriscActivationPolicy::Regular,
            PriscActivationPolicy::Accessory,
            PriscActivationPolicy::Prohibited,
        ] {
            assert_eq!(unsafe { prisc_app_set_activation_policy_macos(policy as i32, 0) }, expected as i32);
        }
    }
}
