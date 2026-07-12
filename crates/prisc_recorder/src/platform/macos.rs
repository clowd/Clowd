//! macOS platform implementation (DESIGN §2.2). Compile-guarded and untested
//! on this machine; ports the pre-refactor CoreGraphics logic behind the new
//! platform signatures. Monitor bounds are CG points (§1.1 capture space).

use std::env;
use std::ffi::CStr;
use std::path::Path;

use obs::data::ObsData;

use super::{MonitorInfo, ObsPaths};

pub const GRAPHICS_MODULE: &CStr = c"libobs-metal.dylib";
pub const DISPLAY_CAPTURE_ID: &str = "screen_capture";
pub const AUDIO_OUTPUT_CAPTURE_ID: &str = "coreaudio_output_capture";
pub const AUDIO_INPUT_CAPTURE_ID: &str = "coreaudio_input_capture";

extern "C" {
    fn CGGetActiveDisplayList(
        max_displays: u32,
        active_displays: *mut u32,
        display_count: *mut u32,
    ) -> i32;
    fn CGDisplayBounds(display: u32) -> CGRect;
    fn CGMainDisplayID() -> u32;
    fn CGDisplayCreateUUIDFromDisplayID(display: u32) -> *const std::ffi::c_void;
    fn CFUUIDCreateString(
        allocator: *const std::ffi::c_void,
        uuid: *const std::ffi::c_void,
    ) -> *const std::ffi::c_void;
    fn CFRelease(cf: *const std::ffi::c_void);
}

#[repr(C)]
#[derive(Debug, Copy, Clone)]
struct CGPoint {
    x: f64,
    y: f64,
}

#[repr(C)]
#[derive(Debug, Copy, Clone)]
struct CGSize {
    width: f64,
    height: f64,
}

#[repr(C)]
#[derive(Debug, Copy, Clone)]
struct CGRect {
    origin: CGPoint,
    size: CGSize,
}

fn cfstring_to_string(cfstr: *const std::ffi::c_void) -> String {
    if cfstr.is_null() {
        return String::new();
    }
    extern "C" {
        fn CFStringGetLength(the_string: *const std::ffi::c_void) -> isize;
        fn CFStringGetCString(
            the_string: *const std::ffi::c_void,
            buffer: *mut u8,
            buffer_size: isize,
            encoding: u32,
        ) -> bool;
    }
    unsafe {
        let len = CFStringGetLength(cfstr);
        let mut buf = vec![0u8; (len as usize + 1) * 4];
        let ok = CFStringGetCString(cfstr, buf.as_mut_ptr(), buf.len() as isize, 0x08000100); // kCFStringEncodingUTF8
        if ok {
            let s = CStr::from_ptr(buf.as_ptr() as *const _);
            s.to_string_lossy().into_owned()
        } else {
            String::new()
        }
    }
}

/// No-op on macOS.
pub fn init_process() {}

pub fn enumerate_monitors() -> Vec<MonitorInfo> {
    let mut monitors = Vec::new();
    let mut display_ids = [0u32; 32];
    let mut count: u32 = 0;

    let ret = unsafe { CGGetActiveDisplayList(32, display_ids.as_mut_ptr(), &mut count) };
    if ret != 0 {
        return monitors;
    }

    let main_display = unsafe { CGMainDisplayID() };

    for &display_id in display_ids.iter().take(count as usize) {
        let bounds = unsafe { CGDisplayBounds(display_id) };

        let uuid_ref = unsafe { CGDisplayCreateUUIDFromDisplayID(display_id) };
        let uuid = if !uuid_ref.is_null() {
            let cfstr = unsafe { CFUUIDCreateString(std::ptr::null(), uuid_ref) };
            let s = cfstring_to_string(cfstr);
            if !cfstr.is_null() {
                unsafe { CFRelease(cfstr) };
            }
            unsafe { CFRelease(uuid_ref) };
            s
        } else {
            format!("{display_id}")
        };

        monitors.push(MonitorInfo {
            id: uuid,
            alt_id: Some(display_id.to_string()),
            x: bounds.origin.x as i32,
            y: bounds.origin.y as i32,
            width: bounds.size.width as u32,
            height: bounds.size.height as u32,
            is_primary: display_id == main_display,
        });
    }

    monitors
}

pub fn find_monitor(id: &str) -> Option<MonitorInfo> {
    super::match_monitor(id, &enumerate_monitors())
}

pub fn display_capture_settings(m: &MonitorInfo, show_cursor: bool) -> ObsData {
    let settings = ObsData::new();
    settings.set_int("type", 0);
    settings.set_string("display_uuid", &m.id);
    settings.set_bool("show_cursor", show_cursor);
    settings
}

pub fn default_obs_paths(exe_dir: &Path) -> ObsPaths {
    // Base plugin dir. A bundled `obs-plugins` dir next to the executable (the
    // relocatable release layout) wins; otherwise honour the OBS_PLUGIN_PATH
    // override, then the absolute path baked in by build.rs (dev builds run
    // against the OBS build tree in place — §2.4).
    let base = env::var("OBS_PLUGIN_PATH").unwrap_or_else(|_| {
        let bundled = exe_dir.join("obs-plugins");
        if bundled.is_dir() {
            bundled.to_string_lossy().into_owned()
        } else {
            env!("OBS_PLUGIN_DIR").to_string()
        }
    });
    let module_bin = format!("{base}/%module%/RelWithDebInfo/%module%.plugin/Contents/MacOS");
    let module_data = match env::var("OBS_PLUGIN_DATA_PATH") {
        Ok(v) => format!("{v}/%module%"),
        Err(_) => format!("{base}/%module%/RelWithDebInfo/%module%.plugin/Contents/Resources"),
    };
    // libobs core data is framework-embedded on macOS; only an explicit
    // override registers an extra data path.
    let libobs_data = env::var("OBS_DATA_PATH").ok().map(std::path::PathBuf::from);
    ObsPaths {
        module_bin,
        module_data,
        libobs_data,
    }
}

pub fn exit_process(code: i32) -> ! {
    unsafe { libc::_exit(code) }
}
