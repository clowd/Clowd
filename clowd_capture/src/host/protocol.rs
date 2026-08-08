//! Wire types for the persistent-host stdio protocol.
//!
//! One JSON object per line ("NDJSON"). The shell writes [`HostCommand`]s
//! to our stdin; we write [`HostEvent`]s to stdout. In persistent mode
//! stdout carries *only* protocol lines — all logging goes to stderr and
//! the `--log-dir` file — so the parent can treat any `{...}` line as an
//! event and everything else as chatter. Large payloads (screenshots,
//! session JSON) never ride this channel: they stay on disk in the
//! per-capture `session_dir`, exactly as in one-shot mode.

use std::path::PathBuf;

use serde::{Deserialize, Serialize};

use crate::app::CycleAction;
use crate::settings::{parse_hex_color, CaptureMode, CapturerSettings, TipsMode};

/// Parent → child commands.
#[derive(Debug, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum HostCommand {
    /// Start a capture cycle. Ignored (with a warning) while a cycle is
    /// already active.
    Show(ShowParams),
    /// End the active cycle as if the user pressed Escape. No-op when idle.
    Cancel,
    /// Liveness probe; answered with [`HostEvent::Pong`].
    Ping,
    /// Cancel any active cycle and exit cleanly (exit code 0).
    Shutdown,
}

/// Per-capture settings carried by [`HostCommand::Show`]. Fields mirror
/// `CliArgs` one-to-one and every default matches the corresponding CLI
/// default (`CapturerSettings::default()`), so the shell can send
/// `{"type":"show"}` and get the same overlay a bare one-shot launch
/// would produce. Note the polarity: `peek`/`cursor` are positive here
/// where the CLI has `--no-peek`/`--no-cursor`.
#[derive(Debug, Deserialize)]
pub struct ShowParams {
    #[serde(default)]
    pub session_dir: Option<PathBuf>,
    /// Hex `#RRGGBB` / `#RRGGBBAA` string on the wire (same format as
    /// `--accent-color`), parsed with the CLI's `parse_hex_color`.
    #[serde(default = "default_accent_color", deserialize_with = "deserialize_accent_color")]
    pub accent_color: [f32; 4],
    #[serde(default)]
    pub tips_mode: TipsMode,
    #[serde(default = "default_true")]
    pub peek: bool,
    #[serde(default = "default_peek_threshold")]
    pub peek_threshold: f32,
    #[serde(default = "default_true")]
    pub cursor: bool,
    #[serde(default)]
    pub capture_mode: CaptureMode,
    #[serde(default)]
    pub video: bool,
}

impl ShowParams {
    /// Mirror of `CliArgs::into_settings` for settings that arrive with a
    /// `show` command instead of on the command line.
    pub fn into_settings(self) -> CapturerSettings {
        CapturerSettings {
            accent_color: self.accent_color,
            tips_mode_at_startup: self.tips_mode,
            obscured_window_peek_enabled: self.peek,
            obscured_window_detection_threshold: self.peek_threshold,
            cursor_visible_at_startup: self.cursor,
            session_dir: self.session_dir,
            capture_mode: self.capture_mode,
            video_mode: self.video,
        }
    }
}

fn default_true() -> bool {
    true
}

fn default_peek_threshold() -> f32 {
    0.80
}

fn default_accent_color() -> [f32; 4] {
    parse_hex_color("#2F7CAE").expect("default accent colour parses")
}

fn deserialize_accent_color<'de, D>(deserializer: D) -> Result<[f32; 4], D::Error>
where
    D: serde::Deserializer<'de>,
{
    let s = String::deserialize(deserializer)?;
    parse_hex_color(&s).map_err(serde::de::Error::custom)
}

/// Child → parent events.
#[derive(Debug, Serialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum HostEvent {
    /// Warm-up is complete: every render worker is parked (device,
    /// pipelines and surface ready) and a `show` will be fast.
    Ready {
        warmup_ms: u64,
        monitors: usize,
    },
    /// The overlay windows are on screen; `elapsed_ms` measured from the
    /// `show` command.
    Shown {
        elapsed_ms: u64,
    },
    /// The capture cycle ended. `action` is the snake_case `CycleAction`
    /// (`edit|upload|select_color|video|scroll|copy|save|ocr_copy|
    /// ocr_search|ocr_upload|cancelled`); any session payload is already on
    /// disk when this is emitted. The `ocr_*` actions end the cycle from
    /// OCR mode — note that leaving the mode (BACK) is not one of them: it
    /// never ends the cycle, so exactly one `finished` per accepted `show`
    /// still holds however many times the mode is entered and left.
    Finished {
        action: CycleAction,
    },
    Pong,
    /// The monitor topology changed (or a GPU device was lost) under the
    /// warm state; we exit right after emitting this — with
    /// `EXIT_DISPLAY_CHANGED` or `EXIT_GPU_LOST` — so the parent respawns
    /// us without treating it as a crash. Informational: the parent keys
    /// its respawn policy off the exit code, not this event.
    DisplayChanged,
    /// Something went unrecoverably wrong with the active cycle (e.g. the
    /// desktop screenshot never arrived). The cycle is cancelled.
    FatalError {
        message: String,
    },
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn show_defaults_mirror_settings_default() {
        let cmd: HostCommand = serde_json::from_str(r#"{"type":"show"}"#).unwrap();
        let HostCommand::Show(params) = cmd else {
            panic!("expected show");
        };
        let from_show = params.into_settings();
        let default = CapturerSettings::default();
        assert_eq!(from_show.accent_color, default.accent_color);
        assert_eq!(from_show.tips_mode_at_startup, default.tips_mode_at_startup);
        assert_eq!(from_show.obscured_window_peek_enabled, default.obscured_window_peek_enabled);
        assert_eq!(
            from_show.obscured_window_detection_threshold,
            default.obscured_window_detection_threshold
        );
        assert_eq!(from_show.cursor_visible_at_startup, default.cursor_visible_at_startup);
        assert_eq!(from_show.session_dir, default.session_dir);
        assert_eq!(from_show.capture_mode, default.capture_mode);
        assert_eq!(from_show.video_mode, default.video_mode);
    }

    #[test]
    fn show_parses_full_command() {
        let json = r##"{
            "type": "show",
            "session_dir": "C:/tmp/session",
            "accent_color": "#FF0000",
            "tips_mode": "off",
            "peek": false,
            "peek_threshold": 0.5,
            "cursor": false,
            "capture_mode": "window",
            "video": true
        }"##;
        let cmd: HostCommand = serde_json::from_str(json).unwrap();
        let HostCommand::Show(params) = cmd else {
            panic!("expected show");
        };
        assert_eq!(params.accent_color, [1.0, 0.0, 0.0, 1.0]);
        assert_eq!(params.tips_mode, TipsMode::Off);
        assert!(!params.peek);
        assert_eq!(params.peek_threshold, 0.5);
        assert!(!params.cursor);
        assert_eq!(params.capture_mode, CaptureMode::Window);
        assert!(params.video);
        assert_eq!(params.session_dir.as_deref(), Some(std::path::Path::new("C:/tmp/session")));
    }

    #[test]
    fn simple_commands_parse() {
        assert!(matches!(serde_json::from_str(r#"{"type":"cancel"}"#), Ok(HostCommand::Cancel)));
        assert!(matches!(serde_json::from_str(r#"{"type":"ping"}"#), Ok(HostCommand::Ping)));
        assert!(matches!(serde_json::from_str(r#"{"type":"shutdown"}"#), Ok(HostCommand::Shutdown)));
    }

    #[test]
    fn events_serialize_snake_case() {
        assert_eq!(
            serde_json::to_string(&HostEvent::Ready {
                warmup_ms: 850,
                monitors: 2,
            })
            .unwrap(),
            r#"{"type":"ready","warmup_ms":850,"monitors":2}"#
        );
        assert_eq!(
            serde_json::to_string(&HostEvent::Finished {
                action: CycleAction::SelectColor,
            })
            .unwrap(),
            r#"{"type":"finished","action":"select_color"}"#
        );
        // Pins the snake_case wire form of the OCR actions — the shell's
        // dispatcher and CAPTURE_PROTOCOL.md both spell it `ocr_search`.
        assert_eq!(
            serde_json::to_string(&HostEvent::Finished {
                action: CycleAction::OcrSearch,
            })
            .unwrap(),
            r#"{"type":"finished","action":"ocr_search"}"#
        );
        assert_eq!(serde_json::to_string(&HostEvent::Pong).unwrap(), r#"{"type":"pong"}"#);
    }
}
