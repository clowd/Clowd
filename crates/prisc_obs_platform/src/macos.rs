//! macOS platform implementation (DESIGN §2.2). Ports the pre-refactor
//! CoreGraphics logic behind the new platform signatures. Monitor bounds are
//! CG points (§1.1 capture space).

use std::env;
use std::ffi::CStr;
use std::path::Path;
use std::ptr::NonNull;

use obs::data::ObsData;
use objc2_core_foundation::{CFRetained, CFUUID};
use objc2_core_graphics::{
    CGDisplayBounds, CGDisplayCopyDisplayMode, CGDisplayMode, CGError, CGGetActiveDisplayList,
    CGMainDisplayID,
};

use super::{MonitorInfo, ObsPaths};

/// `platform` field of the input-capture header (wire contract).
pub const PLATFORM_NAME: &str = "macos";

pub const GRAPHICS_MODULE: &CStr = c"libobs-metal.dylib";
pub const DISPLAY_CAPTURE_ID: &str = "screen_capture";

// objc2-core-graphics 0.3 does not generate a binding for this one (it is
// absent from the translated CGDirectDisplay.h), so the extern stays
// hand-rolled. Create rule: the caller wraps the result in CFRetained, whose
// drop performs the release.
extern "C-unwind" {
    fn CGDisplayCreateUUIDFromDisplayID(display: u32) -> Option<NonNull<CFUUID>>;
}

/// No-op on macOS.
pub fn init_process() {}

pub fn enumerate_monitors() -> Vec<MonitorInfo> {
    let mut monitors = Vec::new();
    let mut display_ids = [0u32; 32];
    let mut count: u32 = 0;

    let ret = unsafe { CGGetActiveDisplayList(32, display_ids.as_mut_ptr(), &mut count) };
    if ret != CGError::Success {
        return monitors;
    }

    let main_display = CGMainDisplayID();

    for &display_id in display_ids.iter().take(count as usize) {
        let bounds = CGDisplayBounds(display_id);

        // Retina backing scale = current mode pixel width / point width; SCK
        // captures at the same mode's pixel resolution. CFRetained releases
        // the copied mode on drop (Create rule).
        let scale = match CGDisplayCopyDisplayMode(display_id) {
            None => 1.0,
            Some(mode) => {
                let px = CGDisplayMode::pixel_width(Some(&mode)) as f64;
                let pt = CGDisplayMode::width(Some(&mode)) as f64;
                if px > 0.0 && pt > 0.0 {
                    px / pt
                } else {
                    1.0
                }
            }
        };

        // Both the UUID and its string form follow the Create rule; CFRetained
        // drops release them. CFString's Display impl replaces the manual
        // CFStringGetCString buffer dance.
        let uuid_ref = unsafe { CGDisplayCreateUUIDFromDisplayID(display_id) }
            .map(|p| unsafe { CFRetained::from_raw(p) });
        let uuid = if let Some(uuid_ref) = uuid_ref {
            CFUUID::new_string(None, Some(&uuid_ref))
                .map(|s| s.to_string())
                .unwrap_or_default()
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
            scale,
            is_primary: display_id == main_display,
        });
    }

    monitors
}

pub fn find_monitor(id: &str) -> Option<MonitorInfo> {
    super::match_monitor(id, &enumerate_monitors())
}

/// The input-capture header's per-monitor `scale`. On macOS coordinates are
/// points, so the Retina backing scale already stored on the monitor is the
/// right density factor.
pub fn monitor_display_scale(m: &MonitorInfo) -> f64 {
    m.scale
}

pub fn display_capture_settings(m: &MonitorInfo, show_cursor: bool) -> ObsData {
    let settings = ObsData::new();
    settings.set_int("type", 0);
    settings.set_string("display_uuid", &m.id);
    settings.set_bool("show_cursor", show_cursor);
    settings
}

/// Partial `obs_source_update` payload toggling cursor capture on an existing
/// display source (mac-capture applies it live).
pub fn cursor_update_settings(show_cursor: bool) -> ObsData {
    let settings = ObsData::new();
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
