//! `--list-cameras` mode (RECORDER CORE R3): minimal libobs init, enumerate
//! DirectShow video devices from the `dshow_input` source type's properties,
//! print exactly ONE JSON line on stdout and exit.
//!
//! Success: `{"type":"cameras","cameras":[{"id":"...","name":"..."}]}`, exit 0.
//! Failure: `{"type":"error","message":"..."}`, exit 1.
//!
//! The `id` is the dshow property list item value — the escaped
//! `<name>:<path>` form win-dshow expects back in `video_device_id` — and is
//! passed to `--webcam` verbatim.

use std::ffi::CString;

use obs::audio::AudioInfo;
use obs::context::ObsContext;
use obs::video::VideoInfo;

use crate::platform;
use crate::status;

const DSHOW_SOURCE_ID: &str = "dshow_input";

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
    let dshow_c = CString::new(DSHOW_SOURCE_ID).unwrap();
    if unsafe { obs_sys::obs_source_get_display_name(dshow_c.as_ptr()) }.is_null() {
        return Err(format!(
            "Camera source '{DSHOW_SOURCE_ID}' is not registered — the win-dshow plugin failed \
             to load.\n  module bin:  {}\n  module data: {}",
            paths.module_bin, paths.module_data
        ));
    }

    obs::properties::source_list_property(DSHOW_SOURCE_ID, "video_device_id")
        .ok_or_else(|| "Failed to enumerate camera devices".to_string())
}
