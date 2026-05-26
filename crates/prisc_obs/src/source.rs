use std::ffi::CString;
use std::ptr;

use crate::data::ObsData;
use crate::error::ObsError;

pub struct ObsSource {
    pub(crate) ptr: *mut obs_sys::obs_source_t,
}

impl ObsSource {
    pub fn create(id: &str, name: &str, settings: Option<&ObsData>) -> Result<Self, ObsError> {
        let id_c = CString::new(id).unwrap();
        let name_c = CString::new(name).unwrap();
        let settings_ptr = settings.map_or(ptr::null_mut(), |s| s.ptr);
        let ptr = unsafe {
            obs_sys::obs_source_create(
                id_c.as_ptr(),
                name_c.as_ptr(),
                settings_ptr,
                ptr::null_mut(),
            )
        };
        if ptr.is_null() {
            return Err(ObsError::NullPointer("obs_source_create"));
        }
        Ok(Self { ptr })
    }

    pub fn set_muted(&self, muted: bool) {
        unsafe { obs_sys::obs_source_set_muted(self.ptr, muted) };
    }

    pub fn as_ptr(&self) -> *mut obs_sys::obs_source_t {
        self.ptr
    }
}

impl Drop for ObsSource {
    fn drop(&mut self) {
        unsafe { obs_sys::obs_source_release(self.ptr) };
    }
}
