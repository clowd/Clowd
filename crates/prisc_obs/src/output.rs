use std::ffi::{CStr, CString};
use std::ptr;

use crate::data::ObsData;
use crate::encoder::ObsEncoder;
use crate::error::ObsError;

pub struct ObsOutput {
    pub(crate) ptr: *mut obs_sys::obs_output_t,
}

impl ObsOutput {
    pub fn create(id: &str, name: &str, settings: Option<&ObsData>) -> Result<Self, ObsError> {
        let id_c = CString::new(id).unwrap();
        let name_c = CString::new(name).unwrap();
        let settings_ptr = settings.map_or(ptr::null_mut(), |s| s.ptr);
        let ptr = unsafe {
            obs_sys::obs_output_create(
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
        unsafe { obs_sys::obs_output_set_video_encoder(self.ptr, encoder.ptr) };
    }

    pub fn set_audio_encoder(&self, encoder: &ObsEncoder, idx: usize) {
        unsafe { obs_sys::obs_output_set_audio_encoder(self.ptr, encoder.ptr, idx) };
    }

    pub fn start(&self) -> Result<(), ObsError> {
        let ok = unsafe { obs_sys::obs_output_start(self.ptr) };
        if !ok {
            let err = self
                .get_last_error()
                .unwrap_or_else(|| "unknown error".to_string());
            return Err(ObsError::OutputStart(err));
        }
        Ok(())
    }

    pub fn stop(&self) {
        unsafe { obs_sys::obs_output_stop(self.ptr) };
    }

    pub fn active(&self) -> bool {
        unsafe { obs_sys::obs_output_active(self.ptr as *const _) }
    }

    pub fn pause(&self, pause: bool) -> bool {
        unsafe { obs_sys::obs_output_pause(self.ptr, pause) }
    }

    pub fn get_total_frames(&self) -> i32 {
        unsafe { obs_sys::obs_output_get_total_frames(self.ptr as *const _) }
    }

    pub fn get_frames_dropped(&self) -> i32 {
        unsafe { obs_sys::obs_output_get_frames_dropped(self.ptr as *const _) }
    }

    pub fn get_last_error(&self) -> Option<String> {
        let ptr = unsafe { obs_sys::obs_output_get_last_error(self.ptr) };
        if ptr.is_null() {
            return None;
        }
        Some(
            unsafe { CStr::from_ptr(ptr) }
                .to_string_lossy()
                .into_owned(),
        )
    }

    pub fn signal_handler(&self) -> *mut obs_sys::signal_handler_t {
        unsafe { obs_sys::obs_output_get_signal_handler(self.ptr as *const _) }
    }

    pub fn as_ptr(&self) -> *mut obs_sys::obs_output_t {
        self.ptr
    }
}

impl Drop for ObsOutput {
    fn drop(&mut self) {
        if self.active() {
            self.stop();
        }
        unsafe { obs_sys::obs_output_release(self.ptr) };
    }
}
