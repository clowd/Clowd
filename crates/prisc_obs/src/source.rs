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

    /// Attaches `filter` to this source. libobs takes its own reference, so the
    /// caller keeps ownership of the `ObsSource` it passes in.
    pub fn add_filter(&self, filter: &ObsSource) {
        unsafe { obs_sys::obs_source_filter_add(self.ptr, filter.ptr) };
    }

    /// Merges `settings` into the source's settings. For video sources libobs
    /// defers the actual `update` call to the graphics thread.
    pub fn update(&self, settings: &ObsData) {
        unsafe { obs_sys::obs_source_update(self.ptr, settings.ptr) };
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
