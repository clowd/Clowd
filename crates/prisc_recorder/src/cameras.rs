//! `--list-cameras` mode (RECORDER CORE R3): minimal libobs init, enumerate
//! the camera devices the platform capture source offers, print exactly ONE
//! JSON line on stdout and exit.
//!
//! Success: `{"type":"cameras","cameras":[{"id":"...","name":"..."}]}`, exit 0.
//! Failure: `{"type":"error","message":"..."}`, exit 1.
//!
//! The `id` is the source's property list item value — the escaped
//! `<name>:<path>` form win-dshow expects back in `video_device_id` on
//! Windows, an `AVCaptureDevice.uniqueID` on macOS — and is passed to
//! `--webcam` verbatim.

use std::ffi::CString;

use obs::audio::AudioInfo;
use obs::context::ObsContext;
use obs::video::VideoInfo;

use crate::platform;
use crate::status;

/// Never returns; prints the single protocol line and exits.
pub fn run() -> ! {
    match list_cameras() {
        Ok(cameras) => {
            let items: Vec<serde_json::Value> = cameras
                .into_iter()
                .map(|c| serde_json::json!({ "id": c.value, "name": c.name }))
                .collect();
            status::emit_json(serde_json::json!({
                "type": "cameras",
                "cameras": items,
            }));
            platform::exit_process(0)
        }
        Err(msg) => {
            status::emit_json(serde_json::json!({
                "type": "error",
                "message": msg,
            }));
            platform::exit_process(1)
        }
    }
}

fn list_cameras() -> Result<Vec<obs::properties::ListItem>, String> {
    platform::init_process();
    let context =
        ObsContext::new("en-US").map_err(|e| format!("Failed to initialize OBS: {e}"))?;

    // Same init order as Recorder::new — data path before reset_video
    // (graphics init resolves default.effect through the data path), modules
    // after graphics/audio are up.
    let exe_dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|p| p.to_path_buf()))
        .ok_or_else(|| "Could not determine the executable directory".to_string())?;
    let paths = platform::default_obs_paths(&exe_dir);
    if let Some(ref libobs_data) = paths.libobs_data {
        context.add_data_path(libobs_data);
    }

    // Minimal graphics: module load and property enumeration expect an
    // initialized core; 32x32 keeps it cheap (no capture happens).
    context
        .reset_video(&VideoInfo {
            graphics_module: platform::GRAPHICS_MODULE,
            base_width: 32,
            base_height: 32,
            output_width: 32,
            output_height: 32,
            fps_num: 30,
            fps_den: 1,
        })
        .map_err(|e| format!("Failed to reset OBS video: {e}"))?;
    context
        .reset_audio(&AudioInfo {
            samples_per_sec: 44100,
        })
        .map_err(|e| format!("Failed to reset OBS audio: {e}"))?;

    context.add_module_path(&paths.module_bin, &paths.module_data);
    context.load_all_modules();

    // Registration check (same pattern as Recorder::new: get_display_name is
    // null exactly when the id is unregistered).
    let source_id = platform::WEBCAM_SOURCE_ID;
    let source_c = CString::new(source_id).unwrap();
    if unsafe { obs_sys::obs_source_get_display_name(source_c.as_ptr()) }.is_null() {
        return Err(format!(
            "Camera source '{source_id}' is not registered — the camera capture plugin failed \
             to load.\n  module bin:  {}\n  module data: {}",
            paths.module_bin, paths.module_data
        ));
    }

    // win-dshow fills its device list straight from the source *type*'s
    // properties; mac-avcapture only fills it from a modified callback on a
    // real instance, so an empty type-level list falls through to the
    // instance probe rather than reporting "no cameras".
    let key = platform::WEBCAM_DEVICE_KEY;
    let from_type = obs::properties::source_list_property(source_id, key);
    if let Some(items) = from_type {
        if !items.is_empty() {
            return Ok(items);
        }
    }
    obs::properties::source_instance_list_property(source_id, key)
        .ok_or_else(|| "Failed to enumerate camera devices".to_string())
}
