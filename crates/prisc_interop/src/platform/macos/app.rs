//! macOS: the activation policy (`prisc_app_set_activation_policy_macos`).
//!
//! `NSApplication` is touched only on the main thread: the export answers `PRISC_E_STATE`
//! elsewhere instead of trespassing, which the hand-written `objc_msgSend` calls had no way to
//! enforce.

use objc2_app_kit::{NSApplication, NSApplicationActivationPolicy};

use crate::error::{Error, Result};
use crate::ffi::app::PriscActivationPolicy;
use crate::ffi::status::PRISC_OK;
use crate::platform::macos::main_thread;

fn to_appkit(policy: PriscActivationPolicy) -> NSApplicationActivationPolicy {
    match policy {
        PriscActivationPolicy::Regular => NSApplicationActivationPolicy::Regular,
        PriscActivationPolicy::Accessory => NSApplicationActivationPolicy::Accessory,
        PriscActivationPolicy::Prohibited => NSApplicationActivationPolicy::Prohibited,
    }
}

/// Sets the policy and, for Regular with `activate_if_regular`, takes focus in the same hop.
pub(crate) fn set_activation_policy(policy: PriscActivationPolicy, activate_if_regular: bool) -> Result {
    let mtm = main_thread("prisc_app_set_activation_policy_macos")?;
    let app = NSApplication::sharedApplication(mtm);
    let wanted = to_appkit(policy);
    // setActivationPolicy: also answers NO when the policy is already the current one, so only a
    // NO that left another policy in place is a refusal. It reports a boolean only: no OS code.
    if !app.setActivationPolicy(wanted) && app.activationPolicy() != wanted {
        return Err(Error::os(0, format!("setActivationPolicy: refused {policy:?}")));
    }
    if activate_if_regular && policy == PriscActivationPolicy::Regular {
        // The replacement, -[NSApplication activate], is cooperative and does not take focus from
        // the frontmost app, which is exactly what this flip needs, so the deprecated call stays.
        #[allow(deprecated)]
        app.activateIgnoringOtherApps(true);
    }
    Ok(PRISC_OK)
}
