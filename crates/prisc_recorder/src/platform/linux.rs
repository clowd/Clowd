//! Linux platform implementation (DESIGN §2.2) — the recorder-specific
//! remainder: audio/webcam helpers, plus stubs for the pointer-sampling
//! entry points. The monitor / paths / display-capture layer lives in the
//! shared `obs-platform` crate, which also owns the X11-vs-Wayland session
//! decision.
//!
//! What is out of scope on Linux, and how it stays out:
//!
//! - The input-capture and window-capture sidecars and the click tracker all
//!   need global pointer/button/keyboard state or a window list. X11 could
//!   provide some of it; Wayland deliberately provides none of it to a
//!   non-focused client. They are rejected up front (`Cli::validate` for the
//!   sidecar flags, `Settings::validate` for the tracker, which also arrives
//!   through `--settings` and stdin `configure`), so the functions below that
//!   only those features call are unreachable and say so loudly if that
//!   invariant is ever broken, rather than feeding the editor made-up data.
//! - `--speaker-volume-compensation` is a Windows software-volume workaround
//!   that has no Linux counterpart here: the gain is always unity, so the flag
//!   is accepted and has no effect.

use obs::data::ObsData;

use crate::cursor_sprite::SpriteEvent;

use super::{CursorState, MouseInfo, WindowInfo};

/// Microphone capture (plugins/linux-pulseaudio). Works unchanged on PipeWire
/// desktops through pipewire-pulse. Its device key is `device_id` — the same
/// key the recorder sets for every platform — and `"default"` resolves to the
/// server's default source when capture starts.
pub const AUDIO_INPUT_CAPTURE_ID: &str = "pulse_input_capture";
/// Speaker capture: records a sink's monitor source.
const AUDIO_OUTPUT_CAPTURE_ID: &str = "pulse_output_capture";
/// Webcam capture source (`--webcam` / `--list-cameras`): Video4Linux2.
pub const WEBCAM_SOURCE_ID: &str = "v4l2_input";
/// The `WEBCAM_SOURCE_ID` settings key (and property) holding the device — a
/// `/dev/videoN` path, exactly as `--list-cameras` prints it.
pub const WEBCAM_DEVICE_KEY: &str = "device_id";

/// Unreachable on Linux: only the click tracker samples the pointer, and the
/// tracker is rejected by `Settings::validate` here.
pub fn get_mouse_info() -> MouseInfo {
    unreachable!("the click tracker is rejected on Linux (Settings::validate)")
}

/// Unreachable on Linux: only the input-capture sidecar samples the cursor,
/// and `--input-capture` is rejected by `Cli::validate` here.
pub fn get_cursor_state() -> CursorState {
    unreachable!("--input-capture is rejected on Linux (Cli::validate)")
}

/// Unreachable on Linux, for the same reason as [`get_cursor_state`].
pub fn take_cursor_sprite(_state: &CursorState) -> SpriteEvent {
    unreachable!("--input-capture is rejected on Linux (Cli::validate)")
}

/// Unreachable on Linux: only the window-capture sidecar lists windows, and
/// `--window-capture` is rejected by `Cli::validate` here.
pub fn enumerate_windows() -> Vec<WindowInfo> {
    unreachable!("--window-capture is rejected on Linux (Cli::validate)")
}

/// Source id + settings for a speaker (output) capture source.
/// `pulse_output_capture` records a *monitor source*: `"default"` resolves to
/// `<default sink>.monitor` when capture starts, and every other value is a
/// monitor source name — which is what the plugin's own device list offers as
/// values, so ids a client discovered through OBS pass straight through.
pub fn audio_output_capture(device_id: &str) -> (&'static str, ObsData) {
    let settings = ObsData::new();
    settings.set_string("device_id", device_id);
    (AUDIO_OUTPUT_CAPTURE_ID, settings)
}

/// Settings for a `WEBCAM_SOURCE_ID` instance capturing `device_id` (a
/// `/dev/videoN` path). Everything else stays at the plugin defaults, which
/// keep the device's current input, pixel format, resolution and frame rate
/// (all -1 = "leave as configured"); the recorder downscales the mix itself.
/// linux-v4l2 carries no audio, so there is nothing to disable.
pub fn webcam_settings(device_id: &str) -> ObsData {
    let settings = ObsData::new();
    settings.set_string(WEBCAM_DEVICE_KEY, device_id);
    settings
}

/// Always unity. The compensation undoes Windows endpoints that apply the
/// master volume in software inside the loopback stream; whether a Linux sink
/// monitor carries the sink volume depends on the server and the sink's
/// volume mode, and reading that is out of scope, so no correction is
/// attempted (the flag is accepted and is a no-op, as on macOS).
pub fn speaker_compensation_gain(_device_id: &str) -> f32 {
    1.0
}
