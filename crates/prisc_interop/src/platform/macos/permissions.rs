//! macOS: microphone and camera authorization (`prisc_permission_*`). Screen Recording and
//! Accessibility are `prisc_platform::permissions`, the rule every crate shares.

use objc2_av_foundation::{AVAuthorizationStatus, AVCaptureDevice, AVMediaType, AVMediaTypeAudio, AVMediaTypeVideo};

use crate::ffi::permissions::PriscPermissionState;

/// What AVFoundation has stored about this process's access to the microphone (`audio`) or the
/// camera. Never prompts; any thread. The one reader of that status: the permission exports and
/// the microphone open (`audio_input.rs`) both call it.
pub(crate) fn authorization(audio: bool) -> AVAuthorizationStatus {
    // SAFETY: AVMediaTypeAudio / AVMediaTypeVideo are AVFoundation constants, the only two media
    // types authorizationStatusForMediaType: accepts.
    let media: Option<&AVMediaType> = unsafe {
        if audio {
            AVMediaTypeAudio
        } else {
            AVMediaTypeVideo
        }
    };
    match media {
        Some(media) => unsafe { AVCaptureDevice::authorizationStatusForMediaType(media) },
        None => AVAuthorizationStatus::NotDetermined,
    }
}

/// [`authorization`] as the ABI state: Authorized is Granted, Denied and Restricted (a policy the
/// user cannot change) are Denied.
pub(crate) fn state(audio: bool) -> PriscPermissionState {
    match authorization(audio) {
        AVAuthorizationStatus::Authorized => PriscPermissionState::Granted,
        AVAuthorizationStatus::NotDetermined => PriscPermissionState::NotDetermined,
        _ => PriscPermissionState::Denied,
    }
}
