use std::ffi::{CStr, CString};
use std::ptr;

use crate::data::ObsData;
use crate::encoder::ObsEncoder;
use crate::error::ObsError;

/// libobs's per-output track limits (`MAX_AUDIO_MIXES` and friends), for
/// callers that size their own track tables against them.
pub use prisc_obs_sys::{MAX_AUDIO_MIXES, MAX_OUTPUT_AUDIO_ENCODERS, MAX_OUTPUT_VIDEO_ENCODERS};

pub struct ObsOutput {
    pub(crate) ptr: *mut prisc_obs_sys::obs_output_t,
}

/// A borrowed, copyable view of an [`ObsOutput`] for threads that only read
/// its pause state: the graphics thread's tick callbacks and the sidecar
/// writers. It holds no reference of its own; see [`ObsOutput::handle`].
#[derive(Debug, Clone, Copy)]
pub struct OutputHandle {
    ptr: *mut prisc_obs_sys::obs_output_t,
}

// SAFETY: the pause queries are thread-safe in libobs (they read the
// output's and encoders' pause state under their own locks), and the handle's
// validity is the caller's promise, made when it was created.
unsafe impl Send for OutputHandle {}
unsafe impl Sync for OutputHandle {}

impl OutputHandle {
    /// Whether the output is paused right now.
    pub fn paused(&self) -> bool {
        unsafe { prisc_obs_sys::obs_output_paused(self.ptr) }
    }

    /// Accumulated pause time (ns), read off the track-0 video encoder: the
    /// outputs used here are OBS_OUTPUT_ENCODED, whose pause path
    /// (`obs_encoded_output_pause`) updates only the per-encoder pause
    /// structs, so `obs_output_get_pause_offset` would always return 0. All of
    /// the output's encoders pause at the same `closest_v_ts`, so track 0
    /// speaks for the recording. 0 when there is no encoder
    /// (`obs_encoder_get_pause_offset(NULL)` is 0).
    ///
    /// Must not race a video encoder swap; the recorder only swaps encoders
    /// before the output starts.
    pub fn pause_offset_ns(&self) -> u64 {
        unsafe {
            let encoder = prisc_obs_sys::obs_output_get_video_encoder(self.ptr);
            prisc_obs_sys::obs_encoder_get_pause_offset(encoder)
        }
    }
}

impl ObsOutput {
    pub fn create(id: &str, name: &str, settings: Option<&ObsData>) -> Result<Self, ObsError> {
        let id_c = CString::new(id).unwrap();
        let name_c = CString::new(name).unwrap();
        let settings_ptr = settings.map_or(ptr::null_mut(), |s| s.ptr);
        let ptr = unsafe {
            prisc_obs_sys::obs_output_create(
                id_c.as_ptr(),
                name_c.as_ptr(),
                settings_ptr,
                ptr::null_mut(),
            )
        };
        if ptr.is_null() {
            return Err(ObsError::NullPointer("obs_output_create"));
        }
        Ok(Self { ptr })
    }

    pub fn set_video_encoder(&self, encoder: &ObsEncoder) {
        unsafe { prisc_obs_sys::obs_output_set_video_encoder(self.ptr, encoder.ptr) };
    }

    /// Assigns (or with `None` clears) the video encoder for track `idx`.
    /// Only outputs with the `OBS_OUTPUT_MULTI_TRACK_VIDEO` capability honor
    /// `idx > 0` — on others libobs silently ignores the call.
    pub fn set_video_encoder2(&self, encoder: Option<&ObsEncoder>, idx: usize) {
        let enc_ptr = encoder.map_or(ptr::null_mut(), |e| e.ptr);
        unsafe { prisc_obs_sys::obs_output_set_video_encoder2(self.ptr, enc_ptr, idx) };
    }

    /// Assigns (or with `None` clears) the audio encoder for track `idx`.
    /// Only outputs with the `OBS_OUTPUT_MULTI_TRACK_AUDIO` capability honor
    /// `idx > 0` — on others libobs silently ignores the call. The encoder's
    /// mixer index (fixed at creation) selects which audio mix feeds the
    /// track; it need not equal `idx`, but keeping them equal is the sane
    /// mapping.
    pub fn set_audio_encoder(&self, encoder: Option<&ObsEncoder>, idx: usize) {
        let enc_ptr = encoder.map_or(ptr::null_mut(), |e| e.ptr);
        unsafe { prisc_obs_sys::obs_output_set_audio_encoder(self.ptr, enc_ptr, idx) };
    }

    pub fn start(&self) -> Result<(), ObsError> {
        let ok = unsafe { prisc_obs_sys::obs_output_start(self.ptr) };
        if !ok {
            let err = self
                .get_last_error()
                .unwrap_or_else(|| "unknown error".to_string());
            return Err(ObsError::OutputStart(err));
        }
        Ok(())
    }

    pub fn stop(&self) {
        unsafe { prisc_obs_sys::obs_output_stop(self.ptr) };
    }

    pub fn active(&self) -> bool {
        unsafe { prisc_obs_sys::obs_output_active(self.ptr as *const _) }
    }

    pub fn pause(&self, pause: bool) -> bool {
        unsafe { prisc_obs_sys::obs_output_pause(self.ptr, pause) }
    }

    pub fn get_total_frames(&self) -> i32 {
        unsafe { prisc_obs_sys::obs_output_get_total_frames(self.ptr as *const _) }
    }

    pub fn get_frames_dropped(&self) -> i32 {
        unsafe { prisc_obs_sys::obs_output_get_frames_dropped(self.ptr as *const _) }
    }

    pub fn get_last_error(&self) -> Option<String> {
        let ptr = unsafe { prisc_obs_sys::obs_output_get_last_error(self.ptr) };
        if ptr.is_null() {
            return None;
        }
        Some(
            unsafe { CStr::from_ptr(ptr) }
                .to_string_lossy()
                .into_owned(),
        )
    }

    pub fn signal_handler(&self) -> *mut prisc_obs_sys::signal_handler_t {
        unsafe { prisc_obs_sys::obs_output_get_signal_handler(self.ptr as *const _) }
    }

    /// A [`OutputHandle`] onto this output.
    ///
    /// # Safety
    /// The handle holds no reference: it must not be used once this
    /// `ObsOutput` has been dropped.
    pub unsafe fn handle(&self) -> OutputHandle {
        OutputHandle { ptr: self.ptr }
    }

    pub fn as_ptr(&self) -> *mut prisc_obs_sys::obs_output_t {
        self.ptr
    }
}

impl Drop for ObsOutput {
    fn drop(&mut self) {
        if self.active() {
            self.stop();
        }
        unsafe { prisc_obs_sys::obs_output_release(self.ptr) };
    }
}
