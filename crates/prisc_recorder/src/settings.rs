//! Effective tunable configuration (CONTRACT §4): CLI-flag defaults overlaid
//! by the optional `--settings` JSON file, re-read at runtime on the stdin
//! `configure` command. A missing field always means the DEFAULT below — never
//! "keep current" — so startup and reconfigure resolve identically.

use std::path::Path;

use serde::Deserialize;

use crate::cli::{Cli, MAX_AUDIO_SOURCES};
use crate::tracker;

fn default_fps() -> u32 {
    30
}

fn default_crf() -> u16 {
    24
}

fn default_cursor() -> bool {
    true
}

fn default_tracker_color() -> String {
    "255,0,0".to_string()
}

/// The tunable config. Field names match the JSON schema exactly (no serde
/// renames); defaults are byte-for-byte the CLI defaults in `cli.rs`. Unknown
/// JSON fields are ignored (serde default).
#[derive(Debug, Clone, PartialEq, Deserialize)]
pub struct Settings {
    #[serde(default = "default_fps")]
    pub fps: u32,
    #[serde(default = "default_crf")]
    pub crf: u16,
    #[serde(default)]
    pub max_width: u32,
    #[serde(default)]
    pub max_height: u32,
    #[serde(default)]
    pub hw_accel: bool,
    #[serde(default)]
    pub low_cpu: bool,
    /// Positive polarity ("capture the cursor") — the inverse of `--no-cursor`.
    #[serde(default = "default_cursor")]
    pub cursor: bool,
    #[serde(default)]
    pub tracker: bool,
    #[serde(default = "default_tracker_color")]
    pub tracker_color: String,
    #[serde(default)]
    pub speakers: Vec<String>,
    #[serde(default)]
    pub microphones: Vec<String>,
    /// Windows: boost speaker capture to undo the system master volume when
    /// the endpoint applies it in software (where it lands inside the loopback
    /// stream, making recordings quieter than the played content). No-op on
    /// devices with hardware volume and on macOS.
    #[serde(default)]
    pub speaker_volume_compensation: bool,
    /// Webcam (DirectShow) device id recorded as video track 1, exactly as
    /// printed by `--list-cameras`; empty = no webcam. The `--webcam` CLI flag
    /// takes precedence and pins the device for the process lifetime. A
    /// pipeline element rather than a live tunable: applied at build time and
    /// on pre-start `configure`; ignored (and reported in `ignored_keys`) once
    /// recording has started.
    #[serde(default)]
    pub webcam_device: String,
}

impl Settings {
    /// Effective config when spawned without `--settings`: the individual CLI
    /// flag values (clap already applied the same defaults).
    pub fn from_cli(cli: &Cli) -> Settings {
        Settings {
            fps: cli.fps,
            crf: cli.crf,
            max_width: cli.max_width,
            max_height: cli.max_height,
            hw_accel: cli.hw_accel,
            low_cpu: cli.low_cpu,
            cursor: !cli.no_cursor,
            tracker: cli.tracker,
            tracker_color: cli.tracker_color.clone(),
            speakers: cli.speaker.clone(),
            microphones: cli.microphone.clone(),
            speaker_volume_compensation: cli.speaker_volume_compensation,
            webcam_device: cli.webcam.clone().unwrap_or_default(),
        }
    }

    /// Reads + parses + validates a settings file. Used both at startup
    /// (`--settings`) and on every stdin `configure`.
    pub fn load(path: &Path) -> Result<Settings, String> {
        let text = std::fs::read_to_string(path)
            .map_err(|e| format!("cannot read settings file '{}': {e}", path.display()))?;
        // Tolerate a UTF-8 BOM even though our writer never emits one.
        let text = text.strip_prefix('\u{feff}').unwrap_or(&text);
        let settings: Settings = serde_json::from_str(text)
            .map_err(|e| format!("invalid settings file '{}': {e}", path.display()))?;
        settings.validate()?;
        Ok(settings)
    }

    /// The value constraints clap enforced on the flags these fields replace.
    /// (The macOS single-`speakers` limit needs loaded modules and is checked
    /// where the sources are built.)
    pub fn validate(&self) -> Result<(), String> {
        if self.fps == 0 {
            return Err("fps must be at least 1".to_string());
        }
        if self.crf > 51 {
            return Err(format!("crf must be 0-51, got {}", self.crf));
        }
        // Validated even with the tracker off, so a bad color is never
        // silently accepted.
        tracker::parse_color(&self.tracker_color)?;
        if self.speakers.len() + self.microphones.len() > MAX_AUDIO_SOURCES {
            return Err(format!(
                "Too many audio sources: at most {MAX_AUDIO_SOURCES} total speakers/microphones \
                 are supported"
            ));
        }
        // Webcam capture is DirectShow-based and exists only on Windows: fail
        // with a clear message instead of at dshow source creation.
        #[cfg(not(windows))]
        if !self.webcam_device.is_empty() {
            return Err(
                "webcam capture (`webcam_device` / --webcam) is only supported on Windows"
                    .to_string(),
            );
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parse(json: &str) -> Settings {
        serde_json::from_str(json).unwrap()
    }

    #[test]
    fn empty_object_yields_cli_defaults() {
        let s = parse("{}");
        assert_eq!(s.fps, 30);
        assert_eq!(s.crf, 24);
        assert_eq!(s.max_width, 0);
        assert_eq!(s.max_height, 0);
        assert!(!s.hw_accel);
        assert!(!s.low_cpu);
        assert!(s.cursor);
        assert!(!s.tracker);
        assert_eq!(s.tracker_color, "255,0,0");
        assert!(s.speakers.is_empty());
        assert!(s.microphones.is_empty());
        assert!(!s.speaker_volume_compensation);
        assert_eq!(s.webcam_device, "");
        assert!(s.validate().is_ok());
    }

    #[test]
    fn missing_field_means_default_not_keep_current() {
        // Only fps present: everything else must resolve to its default.
        let s = parse(r#"{"fps": 60}"#);
        assert_eq!(s.fps, 60);
        assert_eq!(s.crf, 24);
        assert!(s.cursor);
    }

    #[test]
    fn full_file_parses() {
        let s = parse(
            r#"{
                "fps": 30, "crf": 23, "max_width": 1920, "max_height": 1080,
                "hw_accel": true, "low_cpu": false, "cursor": false,
                "tracker": true, "tracker_color": "0,128,255",
                "speakers": ["default"], "microphones": ["mic-id"],
                "speaker_volume_compensation": true,
                "webcam_device": "Live Streamer CAM 313:\\\\?\\usb#vid"
            }"#,
        );
        assert_eq!(s.crf, 23);
        assert_eq!(s.max_width, 1920);
        assert!(s.hw_accel);
        assert!(!s.cursor);
        assert!(s.tracker);
        assert_eq!(s.speakers, vec!["default".to_string()]);
        assert_eq!(s.microphones, vec!["mic-id".to_string()]);
        assert!(s.speaker_volume_compensation);
        assert_eq!(s.webcam_device, "Live Streamer CAM 313:\\\\?\\usb#vid");
        // A non-empty webcam_device is valid on Windows only (DirectShow).
        #[cfg(windows)]
        assert!(s.validate().is_ok());
        #[cfg(not(windows))]
        assert!(s.validate().is_err());
    }

    #[cfg(not(windows))]
    #[test]
    fn webcam_device_is_rejected_off_windows() {
        let err = parse(r#"{"webcam_device": "test"}"#).validate().unwrap_err();
        assert!(err.contains("only supported on Windows"), "{err}");
        // Empty (= disabled) stays valid everywhere.
        assert!(parse(r#"{"webcam_device": ""}"#).validate().is_ok());
    }

    #[test]
    fn unknown_fields_are_ignored() {
        let s = parse(r#"{"fps": 25, "future_knob": {"nested": true}}"#);
        assert_eq!(s.fps, 25);
    }

    #[test]
    fn validation_rejects_bad_values() {
        assert!(parse(r#"{"fps": 0}"#).validate().is_err());
        assert!(parse(r#"{"crf": 52}"#).validate().is_err());
        assert!(parse(r#"{"tracker_color": "300,0,0"}"#).validate().is_err());
        let nine: Vec<String> = (0..9).map(|i| format!("d{i}")).collect();
        let mut s = parse("{}");
        s.speakers = nine[..5].to_vec();
        s.microphones = nine[5..].to_vec();
        assert!(s.validate().is_err());
        s.microphones.pop();
        assert!(s.validate().is_ok());
    }

    #[test]
    fn wrong_types_fail_to_parse() {
        assert!(serde_json::from_str::<Settings>(r#"{"fps": "30"}"#).is_err());
        assert!(serde_json::from_str::<Settings>(r#"{"crf": -1}"#).is_err());
        assert!(serde_json::from_str::<Settings>(r#"{"speakers": "default"}"#).is_err());
        assert!(serde_json::from_str::<Settings>("null").is_err());
    }

    #[test]
    fn load_strips_bom_and_reports_errors() {
        let dir = std::env::temp_dir();
        let path = dir.join(format!(
            "obs-express-settings-test-{}.json",
            std::process::id()
        ));

        std::fs::write(&path, "\u{feff}{\"fps\": 48}").unwrap();
        assert_eq!(Settings::load(&path).unwrap().fps, 48);

        std::fs::write(&path, "not json").unwrap();
        assert!(Settings::load(&path).is_err());

        std::fs::remove_file(&path).unwrap();
        assert!(Settings::load(&path).is_err()); // missing file
    }
}
