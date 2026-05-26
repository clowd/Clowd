use std::ffi::CString;

use crate::error::ObsError;
use crate::source::ObsSource;

pub struct ObsScene {
    pub(crate) ptr: *mut obs_sys::obs_scene_t,
}

pub struct ObsSceneItem {
    pub(crate) ptr: *mut obs_sys::obs_sceneitem_t,
}

impl ObsScene {
    pub fn create(name: &str) -> Result<Self, ObsError> {
        let name_c = CString::new(name).unwrap();
        let ptr = unsafe { obs_sys::obs_scene_create(name_c.as_ptr()) };
        if ptr.is_null() {
            return Err(ObsError::NullPointer("obs_scene_create"));
        }
        Ok(Self { ptr })
    }

    pub fn get_source(&self) -> *mut obs_sys::obs_source_t {
        unsafe { obs_sys::obs_scene_get_source(self.ptr as *const _) }
    }

    pub fn add(&self, source: &ObsSource) -> ObsSceneItem {
        let ptr = unsafe { obs_sys::obs_scene_add(self.ptr, source.ptr) };
        unsafe { obs_sys::obs_sceneitem_addref(ptr) };
        ObsSceneItem { ptr }
    }
}

impl Drop for ObsScene {
    fn drop(&mut self) {
        unsafe { obs_sys::obs_scene_release(self.ptr) };
    }
}

impl ObsSceneItem {
    pub fn set_visible(&self, visible: bool) {
        unsafe { obs_sys::obs_sceneitem_set_visible(self.ptr, visible) };
    }
}

impl Clone for ObsSceneItem {
    fn clone(&self) -> Self {
        unsafe { obs_sys::obs_sceneitem_addref(self.ptr) };
        Self { ptr: self.ptr }
    }
}

impl Drop for ObsSceneItem {
    fn drop(&mut self) {
        unsafe { obs_sys::obs_sceneitem_release(self.ptr) };
    }
}
