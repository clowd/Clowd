//! Privacy permissions (README section 4.5).
//!
//! Screen Recording and Accessibility answer with `prisc_platform::permissions`, the one rule the
//! hotkey hook, the scrolling driver, the capturer and the UI share (Accessibility is
//! `AXIsProcessTrusted && CGPreflightPostEventAccess`). Microphone and Camera read AVFoundation's
//! stored authorization. Off macOS every kind is `NotApplicable`.

#[cfg(target_os = "macos")]
use crate::platform::macos::permissions as av;
use prisc_platform::permissions::{self as platform, Permission, State};

use crate::error::{Error, Result};
use crate::ffi::status::PRISC_OK;
use crate::{panic, util};

/// A permission `prisc_permission_state` / `prisc_permission_request` know.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscPermissionKind {
    /// Capturing the screen and other apps' windows.
    ScreenRecording = 0,
    /// Event taps and posted events (the hotkey hook, the scrolling driver).
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

/// The current state of a permission (a `PriscPermissionKind`), written to `out_state` as a
/// `PriscPermissionState`. Never prompts and never caches. Screen Recording and Accessibility are
/// Granted or Denied on macOS; Microphone and Camera may also be NotDetermined (Restricted reads as
/// Denied). Every kind is NotApplicable off macOS.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for an unknown kind or a null `out_state`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_permission_state(kind: i32, out_state: *mut i32) -> i32 {
    panic::guard("prisc_permission_state", || state(kind, out_state))
}

/// Makes sure a permission is granted, prompting only when it is not (an idempotent "ensure"),
/// and writes the state read afterwards with the same rule as `prisc_permission_state`. Already
/// Granted (or NotApplicable) returns at once without prompting. Otherwise Screen Recording calls
/// `CGRequestScreenCaptureAccess`; Accessibility calls `AXIsProcessTrustedWithOptions` with the
/// prompt option when AX is not trusted, or `CGRequestPostEventAccess` when only post-event access
/// is missing. macOS offers each prompt once per app, and a fresh Screen Recording grant reaches a
/// running process only after a restart, so Denied afterwards means "not usable yet", not "refused".
/// Microphone and Camera are not requested through this export (no caller asks for them).
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for an unknown kind, Microphone or Camera, or a null
/// `out_state`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_permission_request(kind: i32, out_state: *mut i32) -> i32 {
    panic::guard("prisc_permission_request", || request(kind, out_state))
}

unsafe fn state(kind: i32, out_state: *mut i32) -> Result {
    let kind = PriscPermissionKind::from_raw(kind)?;
    let out_state = util::out(out_state, "out_state")?;
    let state = match shared(kind) {
        Some(permission) => from_platform(platform::state(permission)),
        None => av_state(kind),
    };
    *out_state = state as i32;
    Ok(PRISC_OK)
}

unsafe fn request(kind: i32, out_state: *mut i32) -> Result {
    let kind = PriscPermissionKind::from_raw(kind)?;
    let out_state = util::out(out_state, "out_state")?;
    let Some(permission) = shared(kind) else {
        return Err(Error::invalid_arg(format!(
            "{kind:?} cannot be requested through prisc_permission_request"
        )));
    };
    *out_state = from_platform(platform::request(permission)) as i32;
    Ok(PRISC_OK)
}

/// The kinds prisc_platform answers for every crate.
fn shared(kind: PriscPermissionKind) -> Option<Permission> {
    match kind {
        PriscPermissionKind::ScreenRecording => Some(Permission::ScreenRecording),
        PriscPermissionKind::Accessibility => Some(Permission::Accessibility),
        PriscPermissionKind::Microphone | PriscPermissionKind::Camera => None,
    }
}

fn from_platform(state: State) -> PriscPermissionState {
    match state {
        State::Granted => PriscPermissionState::Granted,
        State::Denied => PriscPermissionState::Denied,
        State::NotApplicable => PriscPermissionState::NotApplicable,
    }
}

/// Microphone or Camera: AVFoundation's stored authorization on macOS, NotApplicable elsewhere.
fn av_state(kind: PriscPermissionKind) -> PriscPermissionState {
    #[cfg(target_os = "macos")]
    {
        av::state(kind == PriscPermissionKind::Microphone)
    }
    #[cfg(not(target_os = "macos"))]
    {
        let _ = kind;
        PriscPermissionState::NotApplicable
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status::PriscInteropStatus as S;

    const UNWRITTEN: i32 = -77;

    #[test]
    fn unknown_kinds_and_null_outs_are_invalid_arg_and_write_nothing() {
        for export in [prisc_permission_state, prisc_permission_request] {
            let mut state = UNWRITTEN;
            assert_eq!(unsafe { export(4, &mut state) }, S::InvalidArg as i32);
            assert_eq!(unsafe { export(-1, &mut state) }, S::InvalidArg as i32);
            assert_eq!(state, UNWRITTEN);
            assert_eq!(unsafe { export(0, std::ptr::null_mut()) }, S::InvalidArg as i32);
        }
    }

    #[test]
    fn microphone_and_camera_are_not_requested() {
        for kind in [PriscPermissionKind::Microphone, PriscPermissionKind::Camera] {
            let mut state = UNWRITTEN;
            assert_eq!(unsafe { prisc_permission_request(kind as i32, &mut state) }, S::InvalidArg as i32);
            assert_eq!(state, UNWRITTEN);
        }
    }

    #[test]
    fn every_kind_has_a_state_and_none_is_not_applicable_on_macos() {
        for kind in [
            PriscPermissionKind::ScreenRecording,
            PriscPermissionKind::Accessibility,
            PriscPermissionKind::Microphone,
            PriscPermissionKind::Camera,
        ] {
            let mut state = UNWRITTEN;
            assert_eq!(unsafe { prisc_permission_state(kind as i32, &mut state) }, PRISC_OK);
            let state = PriscPermissionState::from_raw(state).expect("a known state");
            if cfg!(target_os = "macos") {
                assert_ne!(state, PriscPermissionState::NotApplicable, "{kind:?}");
            } else {
                assert_eq!(state, PriscPermissionState::NotApplicable, "{kind:?}");
            }
        }
    }
}
