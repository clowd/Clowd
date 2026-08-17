//! macOS platform implementation (DESIGN §2.2). Compile-guarded and untested
//! on this machine; ports the pre-refactor CoreGraphics logic behind the new
//! platform signatures. Monitor bounds are CG points (§1.1 capture space).

use std::env;
use std::ffi::CStr;
use std::path::Path;

use obs::data::ObsData;

use super::{CursorKind, CursorState, MonitorInfo, MouseInfo, ObsPaths};

/// `platform` field of the input-capture header (wire contract).
pub const PLATFORM_NAME: &str = "macos";

pub const GRAPHICS_MODULE: &CStr = c"libobs-metal.dylib";
pub const DISPLAY_CAPTURE_ID: &str = "screen_capture";
pub const AUDIO_INPUT_CAPTURE_ID: &str = "coreaudio_input_capture";
/// Webcam capture source (`--webcam` / `--list-cameras`): AVFoundation. The
/// async ("macos-avcapture") variant rather than the fast path: it delivers
/// frames without needing the source to be "showing" in a rendered scene.
pub const WEBCAM_SOURCE_ID: &str = "macos-avcapture";
/// The `WEBCAM_SOURCE_ID` settings key (and property) holding the device id
/// (an `AVCaptureDevice.uniqueID`).
pub const WEBCAM_DEVICE_KEY: &str = "device";

extern "C" {
    fn CGGetActiveDisplayList(
        max_displays: u32,
        active_displays: *mut u32,
        display_count: *mut u32,
    ) -> i32;
    fn CGDisplayBounds(display: u32) -> CGRect;
    fn CGMainDisplayID() -> u32;
    fn CGDisplayCreateUUIDFromDisplayID(display: u32) -> *const std::ffi::c_void;
    fn CGDisplayCopyDisplayMode(display: u32) -> *mut std::ffi::c_void;
    fn CGDisplayModeGetPixelWidth(mode: *mut std::ffi::c_void) -> usize;
    fn CGDisplayModeGetWidth(mode: *mut std::ffi::c_void) -> usize;
    fn CGDisplayModeRelease(mode: *mut std::ffi::c_void);
    fn CFUUIDCreateString(
        allocator: *const std::ffi::c_void,
        uuid: *const std::ffi::c_void,
    ) -> *const std::ffi::c_void;
    fn CFRelease(cf: *const std::ffi::c_void);

    /// `CGEventCreate(NULL)` snapshots the current event state; its location is
    /// the cursor position in global display coordinates (points).
    fn CGEventCreate(source: *const std::ffi::c_void) -> *const std::ffi::c_void;
    fn CGEventGetLocation(event: *const std::ffi::c_void) -> CGPoint;
    /// Reads button state without an event tap, so no Accessibility /
    /// Input Monitoring permission is involved.
    fn CGEventSourceButtonState(state_id: i32, button: u32) -> bool;
    /// Whether the cursor is currently drawn. Deprecated since 10.9 but still
    /// the only public answer, and it needs no permission — the alternative is
    /// private CGS SPI.
    fn CGCursorIsVisible() -> bool;
}

/// `kCGEventSourceStateCombinedSessionState` — the session's combined state,
/// which includes synthesized clicks (the closest analogue to Win32's
/// `GetAsyncKeyState`).
const CG_EVENT_SOURCE_STATE_COMBINED_SESSION: i32 = 0;
const CG_MOUSE_BUTTON_LEFT: u32 = 0;
const CG_MOUSE_BUTTON_RIGHT: u32 = 1;

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

        // Retina backing scale = current mode pixel width / point width; SCK
        // captures at the same mode's pixel resolution.
        let scale = unsafe {
            let mode = CGDisplayCopyDisplayMode(display_id);
            if mode.is_null() {
                1.0
            } else {
                let px = CGDisplayModeGetPixelWidth(mode) as f64;
                let pt = CGDisplayModeGetWidth(mode) as f64;
                CGDisplayModeRelease(mode);
                if px > 0.0 && pt > 0.0 {
                    px / pt
                } else {
                    1.0
                }
            }
        };

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
            scale,
            is_primary: display_id == main_display,
        });
    }

    monitors
}

pub fn find_monitor(id: &str) -> Option<MonitorInfo> {
    super::match_monitor(id, &enumerate_monitors())
}

/// Cursor position in global display points (the same space as
/// `CGDisplayBounds`, hence as `MonitorInfo` and `--region`) plus the
/// left/right button state.
///
/// `scale` is 1.0: unlike Windows physical pixels, points are already
/// density-independent, so the highlight needs no DPI compensation here (the
/// region planner separately scales points → canvas pixels).
pub fn get_mouse_info() -> MouseInfo {
    let pressed = unsafe {
        CGEventSourceButtonState(CG_EVENT_SOURCE_STATE_COMBINED_SESSION, CG_MOUSE_BUTTON_LEFT)
            || CGEventSourceButtonState(
                CG_EVENT_SOURCE_STATE_COMBINED_SESSION,
                CG_MOUSE_BUTTON_RIGHT,
            )
    };

    let event = unsafe { CGEventCreate(std::ptr::null()) };
    let (x, y) = if event.is_null() {
        (0.0, 0.0)
    } else {
        let p = unsafe { CGEventGetLocation(event) };
        unsafe { CFRelease(event) };
        (p.x, p.y)
    };

    MouseInfo {
        x,
        y,
        pressed,
        scale: 1.0,
    }
}

/// Position from the same CGEvent snapshot as `get_mouse_info`, plus the
/// visible/hidden distinction.
///
/// Shape classification is not implemented: every visible sample reports
/// `arrow` (the editor's universal fallback kind). Unlike Windows, where
/// `GetCursorInfo` hands back a comparable `HCURSOR`, macOS exposes no public,
/// cheap way to identify the active cursor — `NSCursor.currentSystemCursor`
/// means linking AppKit and hashing a fresh TIFF per sample, far too expensive
/// for a per-frame call on the graphics thread. `Hidden` is still worth
/// reporting on its own: it stops the editor compositing an arrow over content
/// where macOS was drawing nothing.
pub fn get_cursor_state() -> CursorState {
    let event = unsafe { CGEventCreate(std::ptr::null()) };
    let (x, y) = if event.is_null() {
        (0.0, 0.0)
    } else {
        let p = unsafe { CGEventGetLocation(event) };
        unsafe { CFRelease(event) };
        (p.x, p.y)
    };
    let kind = if unsafe { CGCursorIsVisible() } {
        CursorKind::Arrow
    } else {
        CursorKind::Hidden
    };
    // Points are fractional here (Windows' GetCursorInfo is integral), so round
    // rather than truncate — truncation biases toward zero and would skew
    // negative coordinates on displays left of / above the primary.
    CursorState {
        x: x.round() as i32,
        y: y.round() as i32,
        kind,
    }
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

/// Source id + settings for a speaker (output) capture source. Must be called
/// after modules are loaded (the registration probe reads plugin state).
///
/// Prefers ScreenCaptureKit system-audio capture (macOS 13+), which captures
/// all system output — `device_id` is ignored on that path. Falls back to
/// coreaudio_output_capture on macOS 12.
pub fn audio_output_capture(device_id: &str) -> (&'static str, ObsData) {
    // NOT obs_source_create != null: libobs creates a placeholder source for
    // unknown ids; get_display_name returns null exactly when unregistered.
    let sck_registered =
        !unsafe { obs_sys::obs_source_get_display_name(c"sck_audio_capture".as_ptr()) }.is_null();
    if sck_registered {
        let settings = ObsData::new();
        settings.set_int("type", 0);
        ("sck_audio_capture", settings)
    } else {
        let settings = ObsData::new();
        settings.set_string("device_id", device_id);
        ("coreaudio_output_capture", settings)
    }
}

/// Settings for a `WEBCAM_SOURCE_ID` instance capturing `device_id` (an
/// `AVCaptureDevice.uniqueID`, exactly as printed by `--list-cameras`).
pub fn webcam_settings(device_id: &str) -> ObsData {
    let settings = ObsData::new();
    settings.set_string(WEBCAM_DEVICE_KEY, device_id);
    // Keep the plugin's default "High" session preset (it picks the device's
    // best supported format); the recorder downscales the mix itself.
    settings.set_bool("use_preset", true);
    // Many cameras expose a muxed audio stream. `webcam::create` already
    // clears the source's audio-mixer mask and mutes it, but not asking the
    // device for audio at all also avoids the microphone-permission prompt.
    settings.set_bool("enable_audio", false);
    settings
}

/// System-audio capture on macOS (ScreenCaptureKit) taps upstream of the
/// output volume, so the Windows software-master-volume problem does not
/// exist here — compensation is always unity.
pub fn speaker_compensation_gain(_device_id: &str) -> f32 {
    1.0
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
