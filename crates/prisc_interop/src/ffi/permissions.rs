//! Privacy permissions (WP-MACAPP; README section 4.5).

use crate::error::Error;
use crate::panic;

/// A permission `prisc_permission_state` / `prisc_permission_request` know.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscPermissionKind {
    ScreenRecording = 0,
    Accessibility = 1,
    Microphone = 2,
    Camera = 3,
}

crate::convert::ffi_enum!(PriscPermissionKind: i32 {
    ScreenRecording, Accessibility, Microphone, Camera
});

/// What the OS says about a permission.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscPermissionState {
    Granted = 0,
    Denied = 1,
    NotDetermined = 2,
    /// The OS has no such permission (every kind off macOS).
    NotApplicable = 3,
}

crate::convert::ffi_enum!(PriscPermissionState: i32 {
    Granted, Denied, NotDetermined, NotApplicable
});

/// The current state of a permission (a `PriscPermissionKind`) as a `PriscPermissionState`. Never
/// prompts.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_permission_state(kind: i32, out_state: *mut i32) -> i32 {
    panic::guard("prisc_permission_state", || {
        let _ = (kind, out_state);
        Err(Error::unsupported("prisc_permission_state is not implemented yet"))
    })
}

/// Makes sure a permission is granted, prompting only when it is not (an idempotent "ensure"), and
/// writes the state read afterwards. Microphone and Camera are `PRISC_E_INVALID_ARG`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_permission_request(kind: i32, out_state: *mut i32) -> i32 {
    panic::guard("prisc_permission_request", || {
        let _ = (kind, out_state);
        Err(Error::unsupported("prisc_permission_request is not implemented yet"))
    })
}
