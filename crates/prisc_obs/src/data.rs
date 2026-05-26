use std::ffi::CString;

pub struct ObsData {
    pub(crate) ptr: *mut obs_sys::obs_data_t,
}

impl ObsData {
    pub fn new() -> Self {
        Self {
            ptr: unsafe { obs_sys::obs_data_create() },
        }
    }

    pub fn set_string(&self, name: &str, val: &str) {
        let name_c = CString::new(name).unwrap();
        let val_c = CString::new(val).unwrap();
        unsafe { obs_sys::obs_data_set_string(self.ptr, name_c.as_ptr(), val_c.as_ptr()) };
    }

    pub fn set_int(&self, name: &str, val: i64) {
        let name_c = CString::new(name).unwrap();
        unsafe { obs_sys::obs_data_set_int(self.ptr, name_c.as_ptr(), val as _) };
    }

    pub fn set_bool(&self, name: &str, val: bool) {
        let name_c = CString::new(name).unwrap();
        unsafe { obs_sys::obs_data_set_bool(self.ptr, name_c.as_ptr(), val) };
    }

    pub fn set_double(&self, name: &str, val: f64) {
        let name_c = CString::new(name).unwrap();
        unsafe { obs_sys::obs_data_set_double(self.ptr, name_c.as_ptr(), val) };
    }

    pub fn as_ptr(&self) -> *mut obs_sys::obs_data_t {
        self.ptr
    }
}

impl Default for ObsData {
    fn default() -> Self {
        Self::new()
    }
}

impl Clone for ObsData {
    fn clone(&self) -> Self {
        unsafe { obs_sys::obs_data_addref(self.ptr) };
        Self { ptr: self.ptr }
    }
}

impl Drop for ObsData {
    fn drop(&mut self) {
        unsafe { obs_sys::obs_data_release(self.ptr) };
    }
}
