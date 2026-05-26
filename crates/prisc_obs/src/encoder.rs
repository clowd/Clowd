use std::ffi::CString;
use std::ptr;

use crate::data::ObsData;
use crate::error::ObsError;

pub struct ObsEncoder {
    pub(crate) ptr: *mut obs_sys::obs_encoder_t,
}

impl ObsEncoder {
    pub fn create_video(id: &str, name: &str, settings: Option<&ObsData>) -> Result<Self, ObsError> {
        let id_c = CString::new(id).unwrap();
        let name_c = CString::new(name).unwrap();
        let settings_ptr = settings.map_or(ptr::null_mut(), |s| s.ptr);
        let ptr = unsafe {
            obs_sys::obs_video_encoder_create(
                id_c.as_ptr(),
                name_c.as_ptr(),
                settings_ptr,
                ptr::null_mut(),
            )
        };
        if ptr.is_null() {
            return Err(ObsError::NullPointer("obs_video_encoder_create"));
        }
        Ok(Self { ptr })
    }

    pub fn create_audio(
        id: &str,
        name: &str,
        mixer_idx: usize,
        settings: Option<&ObsData>,
    ) -> Result<Self, ObsError> {
        let id_c = CString::new(id).unwrap();
        let name_c = CString::new(name).unwrap();
        let settings_ptr = settings.map_or(ptr::null_mut(), |s| s.ptr);
        let ptr = unsafe {
            obs_sys::obs_audio_encoder_create(
                id_c.as_ptr(),
                name_c.as_ptr(),
                settings_ptr,
                mixer_idx,
                ptr::null_mut(),
            )
        };
        if ptr.is_null() {
            return Err(ObsError::NullPointer("obs_audio_encoder_create"));
        }
        Ok(Self { ptr })
    }

    pub fn set_video(&self, video: *mut obs_sys::video_t) {
        unsafe { obs_sys::obs_encoder_set_video(self.ptr, video) };
    }

    pub fn set_audio(&self, audio: *mut obs_sys::audio_t) {
        unsafe { obs_sys::obs_encoder_set_audio(self.ptr, audio) };
    }

    pub fn update(&self, settings: &ObsData) {
        unsafe { obs_sys::obs_encoder_update(self.ptr, settings.ptr) };
    }

    pub fn as_ptr(&self) -> *mut obs_sys::obs_encoder_t {
        self.ptr
    }
}

impl Drop for ObsEncoder {
    fn drop(&mut self) {
        unsafe { obs_sys::obs_encoder_release(self.ptr) };
    }
}
