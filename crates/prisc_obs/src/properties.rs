use std::ffi::{CStr, CString};

/// One entry of a string-list property (`name` = display name, `value` = the
/// string handed back to the source's settings).
pub struct ListItem {
    pub name: String,
    pub value: String,
}

/// Enumerates a string-list property from a source *type*'s properties
/// (`obs_get_source_properties`, no instance needed). Returns `None` when the
/// source id is unregistered or the property does not exist; an empty vec
/// means the property exists but lists nothing (e.g. no devices).
pub fn source_list_property(source_id: &str, property: &str) -> Option<Vec<ListItem>> {
    let id_c = CString::new(source_id).ok()?;
    let prop_c = CString::new(property).ok()?;
    unsafe {
        let props = obs_sys::obs_get_source_properties(id_c.as_ptr());
        if props.is_null() {
            return None;
        }
        let prop = obs_sys::obs_properties_get(props, prop_c.as_ptr());
        let result = if prop.is_null() {
            None
        } else {
            let count = obs_sys::obs_property_list_item_count(prop);
            let mut items = Vec::new();
            for i in 0..count {
                let name = cstr_to_string(obs_sys::obs_property_list_item_name(prop, i));
                let value = cstr_to_string(obs_sys::obs_property_list_item_string(prop, i));
                // Skip separators / placeholder entries with no value.
                if value.is_empty() {
                    continue;
                }
                items.push(ListItem { name, value });
            }
            Some(items)
        };
        obs_sys::obs_properties_destroy(props);
        result
    }
}

unsafe fn cstr_to_string(ptr: *const std::ffi::c_char) -> String {
    if ptr.is_null() {
        return String::new();
    }
    CStr::from_ptr(ptr).to_string_lossy().into_owned()
}

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
