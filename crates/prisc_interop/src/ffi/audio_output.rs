//! The audio output device (WP-AUDIO; README section 4.6): the default output, pulling
//! interleaved float32 from a render callback. Windows answers `PRISC_E_UNSUPPORTED` this phase
//! (NAudio's WASAPI output serves it, R15).

use std::ffi::c_void;

use crate::error::Error;
use crate::ffi::status::PRISC_OK;
use crate::panic;
use crate::util;

#[cfg(target_os = "macos")]
use crate::platform::macos::audio_output as os;

/// The audio output handle.
pub struct PriscAudioOutput {
    _private: [u8; 0],
}

/// What `prisc_audio_output_create` reports about the opened output (8 bytes).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PriscAudioOutputInfo {
    /// The device's IO buffer plus its own latency plus its safety offset, in frames at
    /// `sample_rate_hz`; 0 when the device publishes none of the three (keep the requested figure).
    pub latency_frames: u32,
    /// The rate `latency_frames` counts in: the device side of the output unit (the requested rate
    /// when the device format cannot be read).
    pub sample_rate_hz: u32,
}

/// The render callback: fill all `frames * channels` interleaved float32 samples (pad silence on
/// an underrun; the device plays whatever is in the buffer). Runs on the OS audio thread; must not
/// block, allocate or take a lock the control path holds, and must not call any export (README C3).
pub type PriscAudioRenderCallback = Option<unsafe extern "C" fn(user: *mut c_void, samples: *mut f32, frames: u32, channels: u32)>;

/// The most channels `prisc_audio_output_create` accepts (it bounds the stream-format arithmetic).
pub const PRISC_AUDIO_OUTPUT_MAX_CHANNELS: u32 = 4096;

/// Opens the default output (float32 interleaved at `sample_rate_hz`, `channels` in 1..=4096) and
/// installs `render` (with `user`, opaque, handed back unchanged), initialised but not started:
/// the unit is instantiated, formatted, callback-installed and initialised, and `out_info` carries
/// its measured latency. Nothing is left open on failure. Binds a device (tens of milliseconds on a
/// cold HAL), so it belongs on a play path, not a paint path.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` (zero rate, channels out of range, null callback or
/// out pointer); `PRISC_E_CAPABILITY` (no default output unit); `PRISC_E_OS` (an AudioToolbox call
/// failed, OSStatus in the last-error code); `PRISC_E_UNSUPPORTED` on Windows and off macOS.
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
        let out_output = util::out(out_output, "out_output")?;
        let out_info = util::out(out_info, "out_info")?;
        if sample_rate_hz == 0 {
            return Err(Error::invalid_arg("sample_rate_hz was zero"));
        }
        if channels == 0 || channels > PRISC_AUDIO_OUTPUT_MAX_CHANNELS {
            return Err(Error::invalid_arg(format!(
                "channels {channels} is not in 1..={PRISC_AUDIO_OUTPUT_MAX_CHANNELS}"
            )));
        }
        let render = render.ok_or_else(|| Error::null("render"))?;
        #[cfg(target_os = "macos")]
        {
            let (output, info) = os::Output::create(sample_rate_hz, channels, render, user)?;
            *out_info = info;
            *out_output = Box::into_raw(output).cast();
            Ok(PRISC_OK)
        }
        #[cfg(not(target_os = "macos"))]
        {
            let _ = (render, user, out_output, out_info);
            Err(Error::unsupported(
                "prisc_audio_output_create is implemented on macOS only (NAudio serves Windows)",
            ))
        }
    })
}

/// Starts pulling (idempotent: `PRISC_OK` when already running). After it returns the callback may
/// already have run on the audio thread. A failed start leaves the handle valid and stopped, so a
/// later start retries.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` (null handle); `PRISC_E_OS`; `PRISC_E_PANIC` (a
/// render callback trampoline panicked since the last call); `PRISC_E_UNSUPPORTED` off macOS.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_audio_output_start(output: *mut PriscAudioOutput) -> i32 {
    panic::guard("prisc_audio_output_start", || {
        #[cfg(target_os = "macos")]
        {
            os::Output::live(output)?.start()?;
            Ok(PRISC_OK)
        }
        #[cfg(not(target_os = "macos"))]
        {
            let _ = output;
            Err(Error::unsupported("prisc_audio_output_start is implemented on macOS only"))
        }
    })
}

/// Stops pulling (idempotent). After it returns no further callback begins, but one already
/// running may still be in flight, so stopping never frees what the callback touches; only
/// `prisc_audio_output_destroy` does. The unit stays initialised, so a later start resumes without
/// binding a device again.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` (null handle); `PRISC_E_OS`; `PRISC_E_PANIC`;
/// `PRISC_E_UNSUPPORTED` off macOS.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_audio_output_stop(output: *mut PriscAudioOutput) -> i32 {
    panic::guard("prisc_audio_output_stop", || {
        #[cfg(target_os = "macos")]
        {
            os::Output::live(output)?.stop()?;
            Ok(PRISC_OK)
        }
        #[cfg(not(target_os = "macos"))]
        {
            let _ = output;
            Err(Error::unsupported("prisc_audio_output_stop is implemented on macOS only"))
        }
    })
}

/// Stops, uninitialises and disposes the unit, then frees the handle; null is `PRISC_OK`. The order
/// is the whole contract with an in-flight callback: uninitialising tears the IO proc down and waits
/// out a callback already running, so when this returns the callback will never run again and the
/// caller may free whatever `user` points at. Teardown always runs; a teardown OSStatus and a
/// callback panic not yet reported are logged, not returned.
///
/// Status: `PRISC_OK`; `PRISC_E_UNSUPPORTED` off macOS (for a non-null handle).
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_audio_output_destroy(output: *mut PriscAudioOutput) -> i32 {
    panic::guard("prisc_audio_output_destroy", || {
        if output.is_null() {
            return Ok(PRISC_OK);
        }
        #[cfg(target_os = "macos")]
        {
            os::Output::destroy(Box::from_raw(output.cast::<os::Output>()));
            Ok(PRISC_OK)
        }
        #[cfg(not(target_os = "macos"))]
        {
            Err(Error::unsupported("prisc_audio_output_destroy is implemented on macOS only"))
        }
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
