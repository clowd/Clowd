//! The audio output device (WP-AUDIO; README section 4.6). Windows answers `PRISC_E_UNSUPPORTED`
//! this phase (NAudio serves it).

use std::ffi::c_void;

use crate::error::Error;
use crate::panic;

/// The audio output handle.
pub struct PriscAudioOutput {
    _private: [u8; 0],
}

/// What `prisc_audio_output_create` reports about the opened output (8 bytes).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PriscAudioOutputInfo {
    pub latency_frames: u32,
    pub sample_rate_hz: u32,
}

/// The render callback: fill `frames` frames of `channels` interleaved float32 samples. Runs on
/// the OS audio thread; must not block and must not call any export (README C3).
pub type PriscAudioRenderCallback = Option<unsafe extern "C" fn(user: *mut c_void, samples: *mut f32, frames: u32, channels: u32)>;

/// Opens the default output (float32 interleaved) pulling from `render`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_OS`, `PRISC_E_UNSUPPORTED`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_audio_output_create(
    sample_rate_hz: u32,
    channels: u32,
    render: PriscAudioRenderCallback,
    user: *mut c_void,
    out_output: *mut *mut PriscAudioOutput,
    out_info: *mut PriscAudioOutputInfo,
) -> i32 {
    panic::guard("prisc_audio_output_create", || {
        let _ = (sample_rate_hz, channels, render, user, out_output, out_info);
        Err(Error::unsupported("prisc_audio_output_create is not implemented yet"))
    })
}

/// Starts pulling (idempotent).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_OS`.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_audio_output_start(output: *mut PriscAudioOutput) -> i32 {
    panic::guard("prisc_audio_output_start", || {
        let _ = output;
        Err(Error::unsupported("prisc_audio_output_start is not implemented yet"))
    })
}

/// Stops pulling (idempotent).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_OS`.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_audio_output_stop(output: *mut PriscAudioOutput) -> i32 {
    panic::guard("prisc_audio_output_stop", || {
        let _ = output;
        Err(Error::unsupported("prisc_audio_output_stop is not implemented yet"))
    })
}

/// Stops, uninitialises and frees the output; null is `PRISC_OK`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_audio_output_destroy(output: *mut PriscAudioOutput) -> i32 {
    panic::guard("prisc_audio_output_destroy", || {
        let _ = output;
        Err(Error::unsupported("prisc_audio_output_destroy is not implemented yet"))
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::{offset_of, size_of};

    #[test]
    fn layouts_are_pinned() {
        assert_eq!(size_of::<PriscAudioOutputInfo>(), 8);
        assert_eq!(offset_of!(PriscAudioOutputInfo, latency_frames), 0);
        assert_eq!(offset_of!(PriscAudioOutputInfo, sample_rate_hz), 4);
    }
}
