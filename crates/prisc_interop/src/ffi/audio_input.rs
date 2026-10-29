//! The microphone input (WP-AUDIO; README section 4.6). Windows answers `PRISC_E_UNSUPPORTED` this
//! phase (NAudio serves it).

use std::ffi::c_void;

use crate::error::Error;
use crate::ffi::types::PriscString;
use crate::panic;

/// The microphone input handle.
pub struct PriscAudioInput {
    _private: [u8; 0],
}

/// What a `PriscAudioInputEvent` carries.
#[repr(u32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscAudioInputEventKind {
    Data = 0,
    Stopped = 1,
}

/// Why the input stopped by itself.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscAudioInputStop {
    DeviceLost = 1,
    RequeueFailed = 2,
}

/// One input event (24 bytes), borrowed for the callback.
#[repr(C)]
#[derive(Debug)]
pub struct PriscAudioInputEvent {
    /// A `PriscAudioInputEventKind`.
    pub kind: u32,
    pub data_len: u32,
    /// Float32 interleaved samples (Data only).
    pub data: *const u8,
    /// A `PriscAudioInputStop` (Stopped only).
    pub stop: i32,
    pub os_status: i32,
}

/// What `prisc_audio_input_open` reports (32 bytes). `name` is mode 3, valid until the handle's
/// `_destroy`.
#[repr(C)]
#[derive(Debug)]
pub struct PriscAudioInputInfo {
    pub channels: u32,
    pub sample_rate_hz: u32,
    pub used_default: u8,
    pub _reserved: [u8; 7],
    pub name: PriscString,
}

/// The input callback: return 1 to continue, 0 to end (no Stopped follows a callee-requested
/// end). Runs on the OS audio thread; must not call any export (README C3).
pub type PriscAudioInputCallback = Option<unsafe extern "C" fn(user: *mut c_void, event: *const PriscAudioInputEvent) -> u8>;

/// Opens the input device with the UTF-8 `device_uid` (falling back to the default input) for
/// float32 capture; nothing is left open on failure.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_PERMISSION`, `PRISC_E_CAPABILITY` (no default input),
/// `PRISC_E_OS`, `PRISC_E_UNSUPPORTED`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_audio_input_open(
    device_uid: *const u8,
    uid_len: usize,
    sample_rate_hz: u32,
    buffer_ms: u32,
    callback: PriscAudioInputCallback,
    user: *mut c_void,
    out_input: *mut *mut PriscAudioInput,
    out_info: *mut PriscAudioInputInfo,
) -> i32 {
    panic::guard("prisc_audio_input_open", || {
        let _ = (device_uid, uid_len, sample_rate_hz, buffer_ms, callback, user, out_input, out_info);
        Err(Error::unsupported("prisc_audio_input_open is not implemented yet"))
    })
}

/// Starts delivery and the device-alive listener (`out_watching` = whether the listener is on).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_OS`.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_audio_input_start(input: *mut PriscAudioInput, out_watching: *mut u8) -> i32 {
    panic::guard("prisc_audio_input_start", || {
        let _ = (input, out_watching);
        Err(Error::unsupported("prisc_audio_input_start is not implemented yet"))
    })
}

/// Ends delivery once (`out_first` = 1 for the call that ended it); never invokes the callback.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_audio_input_request_stop(input: *mut PriscAudioInput, out_first: *mut u8) -> i32 {
    panic::guard("prisc_audio_input_request_stop", || {
        let _ = (input, out_first);
        Err(Error::unsupported("prisc_audio_input_request_stop is not implemented yet"))
    })
}

/// Removes the listener, stops and disposes the queue and waits out an in-flight callback; never
/// call it from inside the callback. Null is `PRISC_OK`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_audio_input_destroy(input: *mut PriscAudioInput) -> i32 {
    panic::guard("prisc_audio_input_destroy", || {
        let _ = input;
        Err(Error::unsupported("prisc_audio_input_destroy is not implemented yet"))
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::{offset_of, size_of};

    #[test]
    fn layouts_are_pinned() {
        assert_eq!(size_of::<PriscAudioInputEvent>(), 24);
        assert_eq!(offset_of!(PriscAudioInputEvent, kind), 0);
        assert_eq!(offset_of!(PriscAudioInputEvent, data_len), 4);
        assert_eq!(offset_of!(PriscAudioInputEvent, data), 8);
        assert_eq!(offset_of!(PriscAudioInputEvent, stop), 16);
        assert_eq!(offset_of!(PriscAudioInputEvent, os_status), 20);
        assert_eq!(size_of::<PriscAudioInputInfo>(), 32);
        assert_eq!(offset_of!(PriscAudioInputInfo, channels), 0);
        assert_eq!(offset_of!(PriscAudioInputInfo, sample_rate_hz), 4);
        assert_eq!(offset_of!(PriscAudioInputInfo, used_default), 8);
        assert_eq!(offset_of!(PriscAudioInputInfo, _reserved), 9);
        assert_eq!(offset_of!(PriscAudioInputInfo, name), 16);
    }
}
