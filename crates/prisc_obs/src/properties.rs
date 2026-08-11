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
        let result = read_list_items(props, prop_c.as_ptr());
        obs_sys::obs_properties_destroy(props);
        result
    }
}

/// Same as [`source_list_property`] but against a temporary source *instance*.
///
/// Some plugins only fill their device list from a property's modified
/// callback, which `obs_get_source_properties` (no instance, no settings
/// application) never runs — macOS `macos-avcapture` is one: its type-level
/// "device" list comes back empty, while the instance path populated by
/// `obs_properties_apply_settings` lists every camera. Creating the instance
/// with default settings does not open a device (the id defaults to "").
pub fn source_instance_list_property(source_id: &str, property: &str) -> Option<Vec<ListItem>> {
    let id_c = CString::new(source_id).ok()?;
    let name_c = CString::new("__property_probe").ok()?;
    let prop_c = CString::new(property).ok()?;
    unsafe {
        let source = obs_sys::obs_source_create_private(
            id_c.as_ptr(),
            name_c.as_ptr(),
            std::ptr::null_mut(),
        );
        if source.is_null() {
            return None;
        }
        let props = obs_sys::obs_source_properties(source);
        let result = if props.is_null() {
            None
        } else {
            // Runs the properties' modified callbacks, which is what fills the
            // device list on plugins that populate it lazily.
            let settings = obs_sys::obs_source_get_settings(source);
            obs_sys::obs_properties_apply_settings(props, settings);
            let items = read_list_items(props, prop_c.as_ptr());
            obs_sys::obs_data_release(settings);
            obs_sys::obs_properties_destroy(props);
            items
        };
        obs_sys::obs_source_release(source);
        result
    }
}

/// Reads a string-list property's items, skipping separators / placeholders
/// (entries with an empty value). `None` = the property does not exist.
unsafe fn read_list_items(
    props: *mut obs_sys::obs_properties_t,
    property: *const std::ffi::c_char,
) -> Option<Vec<ListItem>> {
    let prop = obs_sys::obs_properties_get(props, property);
    if prop.is_null() {
        return None;
    }
    let count = obs_sys::obs_property_list_item_count(prop);
    let mut items = Vec::new();
    for i in 0..count {
        let name = cstr_to_string(obs_sys::obs_property_list_item_name(prop, i));
        let value = cstr_to_string(obs_sys::obs_property_list_item_string(prop, i));
        if value.is_empty() {
            continue;
        }
        items.push(ListItem { name, value });
    }
    Some(items)
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
