use std::ffi::CString;
use std::ptr;
use std::sync::atomic::{AtomicBool, Ordering};

use crate::audio::AudioInfo;
use crate::error::ObsError;
use crate::video::VideoInfo;

static INITIALIZED: AtomicBool = AtomicBool::new(false);

pub struct ObsContext {
    _not_send_sync: std::marker::PhantomData<*mut ()>,
}

impl ObsContext {
    pub fn new(locale: &str) -> Result<Self, ObsError> {
        if INITIALIZED.swap(true, Ordering::SeqCst) {
            return Err(ObsError::AlreadyInitialized);
        }
        let locale_c = CString::new(locale).unwrap();
        let ok = unsafe { obs_sys::obs_startup(locale_c.as_ptr(), ptr::null(), ptr::null_mut()) };
        if !ok {
            INITIALIZED.store(false, Ordering::SeqCst);
            return Err(ObsError::Startup);
        }
        Ok(ObsContext {
            _not_send_sync: std::marker::PhantomData,
        })
    }

    pub fn reset_video(&self, info: &VideoInfo) -> Result<(), ObsError> {
        let mut raw = info.to_raw();
        let ret = unsafe { obs_sys::obs_reset_video(&mut raw) };
        if ret != 0 {
            return Err(ObsError::VideoReset(ret as i32));
        }
        Ok(())
    }

    pub fn reset_audio(&self, info: &AudioInfo) -> Result<(), ObsError> {
        let raw = info.to_raw();
        let ok = unsafe { obs_sys::obs_reset_audio(&raw) };
        if !ok {
            return Err(ObsError::AudioReset(false));
        }
        Ok(())
    }

    pub fn add_module_path(&self, bin: &str, data: &str) {
        let bin_c = CString::new(bin).unwrap();
        let data_c = CString::new(data).unwrap();
        unsafe { obs_sys::obs_add_module_path(bin_c.as_ptr(), data_c.as_ptr()) };
    }

    pub fn load_all_modules(&self) {
        unsafe {
            obs_sys::obs_load_all_modules();
            obs_sys::obs_post_load_modules();
        }
    }

    pub fn get_video(&self) -> *mut obs_sys::video_t {
        unsafe { obs_sys::obs_get_video() }
    }

    pub fn get_audio(&self) -> *mut obs_sys::audio_t {
        unsafe { obs_sys::obs_get_audio() }
    }
}

impl Drop for ObsContext {
    fn drop(&mut self) {
        unsafe { obs_sys::obs_shutdown() };
        INITIALIZED.store(false, Ordering::SeqCst);
    }
}
