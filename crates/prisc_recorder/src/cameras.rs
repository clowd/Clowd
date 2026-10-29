//! `--list-cameras` mode (RECORDER CORE R3): minimal libobs init, enumerate
//! the camera devices the platform capture source offers, answer with one
//! terminal line after `hello` and exit.
//!
//! Success: `cameras` (crates/prisc_capture/HELPER_PROTOCOL.md §4.1), exit 0.
//! Failure: a fatal `error{code:"init_failed"}`, exit 1.
//!
//! The `id` is the source's property list item value — the escaped
//! `<name>:<path>` form win-dshow expects back in `video_device_id` on
//! Windows, an `AVCaptureDevice.uniqueID` on macOS — and is passed to
//! `--webcam` verbatim.

use prisc_obs::audio::AudioInfo;
use prisc_obs::context::ObsContext;
use prisc_obs::video::VideoInfo;

use prisc_ipc::catalog::recorder::{Camera, Event};
use prisc_ipc::catalog::ErrorCode;

use crate::platform;
use crate::status;

/// Never returns; emits the single terminal line and exits.
pub fn run() -> ! {
    match list_cameras() {
        Ok(cameras) => {
            status::out().emit(&Event::Cameras {
                cameras: cameras
                    .into_iter()
                    .map(|c| Camera { id: c.value, name: c.name })
                    .collect(),
            });
            prisc_ipc::lifetime::exit(prisc_ipc::exit::OK)
        }
        Err(msg) => status::out().fatal(ErrorCode::InitFailed, msg),
    }
}

fn list_cameras() -> Result<Vec<prisc_obs::properties::ListItem>, String> {
    platform::init_process()?;
    let context = ObsContext::new("en-US").map_err(|e| format!("Failed to initialize OBS: {e}"))?;

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
            // Enumeration only — nothing is captured, so the default adapter
            // is as good as any.
            adapter: 0,
        })
        .map_err(|e| format!("Failed to reset OBS video: {e}"))?;
    context
        .reset_audio(&AudioInfo {
            samples_per_sec: crate::encoder_config::AUDIO_SAMPLE_RATE,
        })
        .map_err(|e| format!("Failed to reset OBS audio: {e}"))?;

    context.add_module_path(&paths.module_bin, &paths.module_data);
    context.load_all_modules();

    // Registration check (same as Recorder::new).
    let source_id = platform::WEBCAM_SOURCE_ID;
    if !prisc_obs::source::is_registered(source_id) {
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
    //
    // Linux skips the type-level query entirely: linux-v4l2's
    // `v4l2_properties` dereferences its instance data unconditionally, so
    // `obs_get_source_properties("v4l2_input")` (no instance) segfaults. The
    // instance probe below lists the same /dev/video* devices.
    let key = platform::WEBCAM_DEVICE_KEY;
    #[cfg(not(target_os = "linux"))]
    {
        let from_type = prisc_obs::properties::source_list_property(source_id, key);
        if let Some(items) = from_type {
            if !items.is_empty() {
                return Ok(items);
            }
        }
    }
    prisc_obs::properties::source_instance_list_property(source_id, key)
        .ok_or_else(|| "Failed to enumerate camera devices".to_string())
}
