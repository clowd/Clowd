use std::ffi::CStr;

pub fn enum_encoder_types() -> Vec<String> {
    let mut types = Vec::new();
    let mut idx: usize = 0;
    loop {
        let mut id_ptr: *const std::ffi::c_char = std::ptr::null();
        let has_more = unsafe { obs_sys::obs_enum_encoder_types(idx, &mut id_ptr) };
        if !has_more || id_ptr.is_null() {
            break;
        }
        let id = unsafe { CStr::from_ptr(id_ptr) }
            .to_string_lossy()
            .into_owned();
        types.push(id);
        idx += 1;
    }
    types
}
