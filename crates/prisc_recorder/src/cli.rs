use std::path::PathBuf;

use clap::Parser;

use crate::region;
use crate::settings::Settings;
use crate::tracks::MAX_AUDIO_TRACKS;

/// Maximum total audio sources (speakers + microphones).
pub const MAX_AUDIO_SOURCES: usize = 8;

#[derive(Parser, Debug)]
#[command(name = "obs-express", about = "Minimal screen recorder backed by OBS")]
pub struct Cli {
    /// Recording file path; must end .mp4 and its parent directory must exist.
    /// Required in recording mode (i.e. unless --list-cameras).
    #[arg(long, required_unless_present = "list_cameras")]
    pub output: Option<PathBuf>,

    /// List available webcam (DirectShow) devices as one JSON line on stdout
    /// and exit. Mutually exclusive with all recording flags.
    #[arg(long, conflicts_with_all = [
        "output", "region", "monitor", "fps", "crf", "max_width", "max_height",
        "hw_accel", "low_cpu", "no_cursor", "tracker", "tracker_color", "pause",
        "speaker", "microphone", "speaker_volume_compensation", "settings",
        "webcam", "multi_track", "input_capture", "window_capture",
        "capture_method",
    ])]
    pub list_cameras: bool,

    /// Record every stream to its own track using OBS's hybrid mp4 output:
    /// video track 0 = screen, video track 1 = webcam (--webcam), and one
    /// audio track per --speaker / --microphone device (speakers first) —
    /// at most 6 audio tracks. Off by default: without it the recording uses
    /// the single-track ffmpeg muxer, mixing all audio into one track, and
    /// --webcam is unavailable.
    #[arg(long)]
    pub multi_track: bool,

    /// Record a webcam as a second video track (track 0 = screen, track 1 =
    /// webcam). Value is a device id exactly as printed by --list-cameras.
    /// Requires --multi-track. The hidden value "test" substitutes a solid
    /// color source (for machines without a camera).
    #[arg(long)]
    pub webcam: Option<String>,

    /// Record cursor position/shape, mouse buttons and keys to a JSONL
    /// sidecar at this path (DESIGN §1 wire format) alongside the video.
    /// Session-fixed, like --output; the parent directory must exist.
    #[arg(long)]
    pub input_capture: Option<PathBuf>,

    /// Record the live geometry of every on-screen window intersecting the
    /// capture region to a JSONL sidecar at this path, in coordinates relative
    /// to that region. Session-fixed, like --input-capture; the parent
    /// directory must exist.
    #[arg(long)]
    pub window_capture: Option<PathBuf>,

    /// Capture region "X,Y,W,H" in the platform capture coordinate space
    /// (Windows: physical px, virtual desktop; macOS: CG points).
    /// X,Y may be negative (virtual desktop), hence allow_hyphen_values.
    #[arg(long, allow_hyphen_values = true)]
    pub region: Option<String>,

    /// Record a whole monitor (id, alternate id, or 0-based index).
    /// Mutually exclusive with --region.
    #[arg(long, conflicts_with = "region")]
    pub monitor: Option<String>,

    #[arg(long, default_value = "30")]
    pub fps: u32,

    /// Quality (x264 CRF / hardware CQP), 0-51.
    #[arg(long, default_value = "24", value_parser = clap::value_parser!(u16).range(0..=51))]
    pub crf: u16,

    /// Aspect-preserving downscale cap; 0 = off.
    #[arg(long, default_value = "0")]
    pub max_width: u32,

    /// Aspect-preserving downscale cap; 0 = off.
    #[arg(long, default_value = "0")]
    pub max_height: u32,

    /// Prefer a hardware H.264 encoder; falls back to x264.
    #[arg(long)]
    pub hw_accel: bool,

    /// x264 preset ultrafast instead of veryfast. No effect with a hardware
    /// encoder.
    #[arg(long)]
    pub low_cpu: bool,

    #[arg(long)]
    pub no_cursor: bool,

    /// Which OS API backs display capture: `auto`, `dxgi` or `wgc`.
    /// Windows only — ignored on macOS. Session-fixed (the capture sources are
    /// built once), so unlike the tuning knobs it is not re-readable via
    /// `--settings` / stdin `configure`.
    ///
    /// Use `dxgi` on Windows 10 to lose the yellow capture border Windows
    /// draws around a WGC-captured display: suppressing it needs
    /// `GraphicsCaptureSession::IsBorderRequired`, which only exists on
    /// Windows 11.
    #[arg(long, value_name = "METHOD", default_value = "wgc")]
    pub capture_method: crate::platform::CaptureMethod,

    /// Render an expanding, fading highlight at the pointer on every mouse
    /// click (recording only — the real screen is untouched).
    #[arg(long)]
    pub tracker: bool,

    /// Click-highlight color as "R,G,B", each component 0-255.
    #[arg(long, default_value = "255,0,0")]
    pub tracker_color: String,

    /// Initialized-wait mode: build the pipeline, emit `initialized`, and do
    /// not start the output until stdin `start` arrives.
    #[arg(long)]
    pub pause: bool,

    /// Audio output-capture device id ("default" or a platform device id); repeatable.
    /// On macOS 13+ system audio is captured via ScreenCaptureKit and the value
    /// only toggles capture on — per-device selection is not possible and
    /// repeating the flag is rejected.
    #[arg(long)]
    pub speaker: Vec<String>,

    /// Audio input-capture device id; repeatable.
    #[arg(long)]
    pub microphone: Vec<String>,

    /// Windows: boost speaker capture to undo the system master volume when
    /// the audio device applies it in software (on such devices the loopback
    /// stream Windows hands to recorders is already attenuated by the volume
    /// slider, so recordings sound quieter than the played content). Devices
    /// with hardware volume are unaffected, as is macOS. Tracks volume changes
    /// while recording (~100 ms).
    #[arg(long)]
    pub speaker_volume_compensation: bool,

    /// JSON settings file replacing the individual tuning flags; re-readable at
    /// runtime via the stdin `configure` command.
    #[arg(long, conflicts_with_all = [
        "fps", "crf", "max_width", "max_height", "hw_accel", "low_cpu",
        "no_cursor", "tracker", "tracker_color", "speaker", "microphone",
        "speaker_volume_compensation",
    ])]
    pub settings: Option<PathBuf>,
}

impl Cli {
    /// §1.1 validations that clap cannot express, plus resolution of the
    /// effective tunable settings (`--settings` file, or the individual
    /// flags). Violations → exit 2.
    pub fn validate(&self) -> Result<Settings, String> {
        // --list-cameras is handled before validate (it has no settings); in
        // recording mode --output is mandatory.
        let output = match self.output {
            Some(ref p) => p,
            None => return Err("--output is required".to_string()),
        };
        let output_str = output.to_string_lossy();
        if !output_str.to_ascii_lowercase().ends_with(".mp4") {
            return Err(format!("--output must end with .mp4: '{output_str}'"));
        }
        match output.parent() {
            // A bare file name has an empty parent — that is the CWD, which exists.
            Some(p) if !p.as_os_str().is_empty() && !p.is_dir() => {
                return Err(format!(
                    "--output parent directory does not exist: '{}'",
                    p.display()
                ));
            }
            None => {
                return Err(format!("--output is not a file path: '{output_str}'"));
            }
            _ => {}
        }

        // Same parent-dir rule as --output for both JSONL sidecars: fail fast
        // (exit 2) rather than discovering an unwritable sidecar path
        // mid-pipeline.
        for (flag, path) in [
            ("--input-capture", self.input_capture.as_ref()),
            ("--window-capture", self.window_capture.as_ref()),
        ] {
            let Some(path) = path else { continue };
            match path.parent() {
                Some(p) if !p.as_os_str().is_empty() && !p.is_dir() => {
                    return Err(format!(
                        "{flag} parent directory does not exist: '{}'",
                        p.display()
                    ));
                }
                None => {
                    return Err(format!("{flag} is not a file path: '{}'", path.display()));
                }
                _ => {}
            }
        }

        // Two sidecars writing the same file would interleave two different
        // wire formats into one stream.
        if let (Some(a), Some(b)) = (self.input_capture.as_ref(), self.window_capture.as_ref()) {
            if a == b {
                return Err(
                    "--input-capture and --window-capture must be different files".to_string(),
                );
            }
        }

        // clap's conflicts_with already rejects --region + --monitor, but keep
        // the check for programmatic construction.
        if self.region.is_some() && self.monitor.is_some() {
            return Err("--region and --monitor are mutually exclusive".to_string());
        }

        // The default single-track muxer cannot carry the webcam's second
        // video track.
        if self.webcam.is_some() && !self.multi_track {
            return Err(
                "--webcam requires --multi-track (the single-track muxer carries one video \
                 track only)"
                    .to_string(),
            );
        }
        if let Some(ref w) = self.webcam {
            if w.is_empty() {
                return Err("--webcam device id must not be empty".to_string());
            }
        }

        if let Some(ref r) = self.region {
            region::parse_region(r).map_err(|e| e.to_string())?;
        }

        // Both paths run the same value validation (fps/crf/tracker
        // color/audio caps), so a bad --settings file fails startup exactly
        // like bad flags.
        let settings = match self.settings {
            Some(ref path) => Settings::load(path)?,
            None => {
                let settings = Settings::from_cli(self);
                settings.validate()?;
                settings
            }
        };

        // Same restriction for a webcam requested via the settings file (the
        // earlier check only sees the --webcam flag).
        if !self.multi_track && !settings.webcam_device.is_empty() {
            return Err(
                "a webcam (--webcam or the settings `webcam_device` key) requires --multi-track"
                    .to_string(),
            );
        }

        // Multi-track gives every audio device its own track, and libobs
        // outputs carry at most MAX_AUDIO_TRACKS of them (single-track
        // recordings mix all MAX_AUDIO_SOURCES down into one track instead).
        let audio_sources = settings.speakers.len() + settings.microphones.len();
        if self.multi_track && audio_sources > MAX_AUDIO_TRACKS {
            return Err(format!(
                "--multi-track records one audio track per device and supports at most \
                 {MAX_AUDIO_TRACKS} (got {audio_sources})"
            ));
        }

        Ok(settings)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parse(args: &[&str]) -> Result<Cli, clap::Error> {
        Cli::try_parse_from(std::iter::once("obs-express").chain(args.iter().copied()))
    }

    #[test]
    fn output_must_be_mp4() {
        let cli = parse(&["--output", "video.mkv"]).unwrap();
        assert!(cli.validate().is_err());
        let cli = parse(&["--output", "video.mp4"]).unwrap();
        assert!(cli.validate().is_ok());
        // Case-insensitive suffix.
        let cli = parse(&["--output", "VIDEO.MP4"]).unwrap();
        assert!(cli.validate().is_ok());
    }

    #[test]
    fn output_parent_must_exist() {
        let cli = parse(&["--output", "Z:/definitely/not/a/real/dir/video.mp4"]).unwrap();
        assert!(cli.validate().is_err());
    }

    #[test]
    fn input_capture_parent_must_exist() {
        let cli = parse(&[
            "--output",
            "a.mp4",
            "--input-capture",
            "Z:/definitely/not/a/real/dir/input.jsonl",
        ])
        .unwrap();
        let err = cli.validate().unwrap_err();
        assert!(err.contains("--input-capture"), "{err}");

        // A bare file name (CWD parent) and an existing dir are both fine.
        let cli = parse(&["--output", "a.mp4", "--input-capture", "input.jsonl"]).unwrap();
        assert!(cli.validate().is_ok());
        let dir = std::env::temp_dir().join("ic.jsonl");
        let path = dir.to_string_lossy().into_owned();
        let cli = parse(&["--output", "a.mp4", "--input-capture", &path]).unwrap();
        assert!(cli.validate().is_ok());
    }

    #[test]
    fn input_capture_coexists_with_settings_and_multi_track() {
        // Session-fixed arg: NOT in the --settings conflicts list.
        assert!(parse(&[
            "--output",
            "a.mp4",
            "--settings",
            "s.json",
            "--multi-track",
            "--input-capture",
            "input.jsonl",
        ])
        .is_ok());
        // ...but is a recording flag, so --list-cameras rejects it.
        assert!(parse(&["--list-cameras", "--input-capture", "input.jsonl"]).is_err());
        // The jsonl sidecar itself does not require --multi-track.
        let cli = parse(&["--output", "a.mp4", "--input-capture", "input.jsonl"]).unwrap();
        assert!(cli.validate().is_ok());
    }

    #[test]
    fn capture_method_parses_and_defaults_to_wgc() {
        use crate::platform::CaptureMethod;

        let cli = parse(&["--output", "a.mp4"]).unwrap();
        assert_eq!(cli.capture_method, CaptureMethod::Wgc);

        let cli = parse(&["--output", "a.mp4", "--capture-method", "dxgi"]).unwrap();
        assert_eq!(cli.capture_method, CaptureMethod::Dxgi);
        assert!(cli.validate().is_ok());

        assert_eq!(
            parse(&["--output", "a.mp4", "--capture-method", "auto"])
                .unwrap()
                .capture_method,
            CaptureMethod::Auto
        );
        assert!(parse(&["--output", "a.mp4", "--capture-method", "ddapi"]).is_err());

        // Session-fixed arg: NOT in the --settings conflicts list, but a
        // recording flag, so --list-cameras rejects it.
        assert!(parse(&[
            "--output",
            "a.mp4",
            "--settings",
            "s.json",
            "--capture-method",
            "dxgi",
        ])
        .is_ok());
        assert!(parse(&["--list-cameras", "--capture-method", "dxgi"]).is_err());
    }

    #[test]
    fn window_capture_mirrors_input_capture() {
        // Same parent-dir rule...
        let cli = parse(&[
            "--output",
            "a.mp4",
            "--window-capture",
            "Z:/definitely/not/a/real/dir/windows.jsonl",
        ])
        .unwrap();
        let err = cli.validate().unwrap_err();
        assert!(err.contains("--window-capture"), "{err}");

        // ...same session-fixed placement: coexists with --settings and
        // --multi-track, rejected by --list-cameras.
        assert!(parse(&[
            "--output",
            "a.mp4",
            "--settings",
            "s.json",
            "--multi-track",
            "--window-capture",
            "windows.jsonl",
        ])
        .is_ok());
        assert!(parse(&["--list-cameras", "--window-capture", "windows.jsonl"]).is_err());

        // Both sidecars together is the expected pairing.
        let cli = parse(&[
            "--output",
            "a.mp4",
            "--input-capture",
            "input.jsonl",
            "--window-capture",
            "windows.jsonl",
        ])
        .unwrap();
        assert!(cli.validate().is_ok());
        assert_eq!(
            cli.window_capture.as_deref(),
            Some(std::path::Path::new("windows.jsonl"))
        );
    }

    #[test]
    fn the_two_sidecars_must_not_share_a_file() {
        let cli = parse(&[
            "--output",
            "a.mp4",
            "--input-capture",
            "both.jsonl",
            "--window-capture",
            "both.jsonl",
        ])
        .unwrap();
        let err = cli.validate().unwrap_err();
        assert!(err.contains("different files"), "{err}");
    }

    #[test]
    fn region_and_monitor_are_exclusive() {
        // clap-level conflict.
        assert!(parse(&[
            "--output",
            "a.mp4",
            "--region",
            "0,0,640,480",
            "--monitor",
            "0"
        ])
        .is_err());
    }

    #[test]
    fn region_string_is_validated() {
        let cli = parse(&["--output", "a.mp4", "--region", "0,0,640"]).unwrap();
        assert!(cli.validate().is_err());
        let cli = parse(&["--output", "a.mp4", "--region", "0,0,1,480"]).unwrap();
        assert!(cli.validate().is_err()); // W < 2
        let cli = parse(&["--output", "a.mp4", "--region", "-100,-100,640,480"]).unwrap();
        assert!(cli.validate().is_ok());
    }

    #[test]
    fn audio_sources_capped_at_eight() {
        let mut args = vec!["--output".to_string(), "a.mp4".to_string()];
        for i in 0..5 {
            args.push("--speaker".to_string());
            args.push(format!("spk{i}"));
        }
        for i in 0..4 {
            args.push("--microphone".to_string());
            args.push(format!("mic{i}"));
        }
        let argv: Vec<&str> = args.iter().map(String::as_str).collect();
        let cli = parse(&argv).unwrap();
        assert!(cli.validate().is_err()); // 9 total

        let argv2: Vec<&str> = argv[..argv.len() - 2].to_vec();
        let cli = parse(&argv2).unwrap();
        assert!(cli.validate().is_ok()); // 8 total
    }

    #[test]
    fn crf_range_enforced_by_clap() {
        assert!(parse(&["--output", "a.mp4", "--crf", "52"]).is_err());
        assert!(parse(&["--output", "a.mp4", "--crf", "51"]).is_ok());
    }

    #[test]
    fn defaults() {
        let cli = parse(&["--output", "a.mp4"]).unwrap();
        assert_eq!(cli.fps, 30);
        assert_eq!(cli.crf, 24);
        assert_eq!(cli.max_width, 0);
        assert_eq!(cli.max_height, 0);
        assert!(!cli.pause);
        assert!(!cli.hw_accel);
        assert!(!cli.tracker);
        assert_eq!(cli.tracker_color, "255,0,0");
    }

    #[test]
    fn settings_conflicts_with_the_replaced_flags() {
        for flag in [
            &["--fps", "60"][..],
            &["--crf", "20"],
            &["--max-width", "1920"],
            &["--hw-accel"],
            &["--low-cpu"],
            &["--no-cursor"],
            &["--tracker"],
            &["--tracker-color", "0,0,255"],
            &["--speaker", "default"],
            &["--microphone", "default"],
            &["--speaker-volume-compensation"],
        ] {
            let mut args = vec!["--output", "a.mp4", "--settings", "s.json"];
            args.extend_from_slice(flag);
            assert!(parse(&args).is_err(), "expected conflict for {flag:?}");
        }
        // Session-fixed args are untouched and coexist with --settings.
        assert!(parse(&[
            "--output",
            "a.mp4",
            "--settings",
            "s.json",
            "--region",
            "0,0,640,480",
            "--pause",
        ])
        .is_ok());
    }

    #[test]
    fn settings_file_resolves_the_effective_config() {
        let dir = std::env::temp_dir();
        let path = dir.join(format!(
            "obs-express-cli-settings-{}.json",
            std::process::id()
        ));
        std::fs::write(&path, r#"{"fps": 60, "tracker": true}"#).unwrap();

        let path_str = path.to_string_lossy().into_owned();
        let cli = parse(&["--output", "a.mp4", "--settings", &path_str]).unwrap();
        let settings = cli.validate().unwrap();
        assert_eq!(settings.fps, 60);
        assert!(settings.tracker);
        assert_eq!(settings.crf, 24); // missing field = default

        std::fs::write(&path, r#"{"fps": 0}"#).unwrap();
        assert!(cli.validate().is_err());
        std::fs::remove_file(&path).unwrap();
        assert!(cli.validate().is_err()); // missing file

        // Without --settings the flags themselves are the effective config.
        let cli = parse(&["--output", "a.mp4", "--no-cursor", "--speaker", "spk"]).unwrap();
        let settings = cli.validate().unwrap();
        assert!(!settings.cursor);
        assert_eq!(settings.speakers, vec!["spk".to_string()]);
    }

    #[test]
    fn output_is_required_unless_list_cameras() {
        assert!(parse(&[]).is_err());
        assert!(parse(&["--region", "0,0,640,480"]).is_err());
        let cli = parse(&["--list-cameras"]).unwrap();
        assert!(cli.list_cameras);
        assert!(cli.output.is_none());
        // Programmatic misuse (validate without an output outside
        // --list-cameras mode) is still rejected.
        assert!(cli.validate().is_err());
    }

    #[test]
    fn list_cameras_conflicts_with_recording_flags() {
        assert!(parse(&["--list-cameras", "--output", "a.mp4"]).is_err());
        assert!(parse(&["--list-cameras", "--region", "0,0,640,480"]).is_err());
        assert!(parse(&["--list-cameras", "--webcam", "dev"]).is_err());
        assert!(parse(&["--list-cameras", "--multi-track"]).is_err());
        assert!(parse(&["--list-cameras", "--pause"]).is_err());
        assert!(parse(&["--list-cameras", "--settings", "s.json"]).is_err());
    }

    #[test]
    fn webcam_requires_multi_track() {
        let cli = parse(&[
            "--output",
            "a.mp4",
            "--multi-track",
            "--webcam",
            "Cam:\\\\?\\usb#vid",
        ])
        .unwrap();
        assert_eq!(cli.webcam.as_deref(), Some("Cam:\\\\?\\usb#vid"));
        assert!(cli.validate().is_ok());

        // Single-track (the default) carries one video track only.
        let cli = parse(&["--output", "a.mp4", "--webcam", "dev"]).unwrap();
        let err = cli.validate().unwrap_err();
        assert!(err.contains("--multi-track"), "{err}");

        // --multi-track alone (no webcam) is fine.
        let cli = parse(&["--output", "a.mp4", "--multi-track"]).unwrap();
        assert!(cli.validate().is_ok());
        assert!(cli.multi_track);
        // ...and is off by default.
        assert!(!parse(&["--output", "a.mp4"]).unwrap().multi_track);

        let cli = parse(&["--output", "a.mp4", "--multi-track", "--webcam", ""]).unwrap();
        assert!(cli.validate().is_err());
    }

    #[test]
    fn multi_track_caps_audio_devices_at_the_libobs_track_limit() {
        let mut args = vec![
            "--output".to_string(),
            "a.mp4".to_string(),
            "--multi-track".to_string(),
        ];
        for i in 0..4 {
            args.push("--speaker".to_string());
            args.push(format!("spk{i}"));
        }
        for i in 0..2 {
            args.push("--microphone".to_string());
            args.push(format!("mic{i}"));
        }
        let argv: Vec<&str> = args.iter().map(String::as_str).collect();
        let cli = parse(&argv).unwrap();
        assert!(cli.validate().is_ok()); // 6 total = MAX_AUDIO_TRACKS

        let mut args7 = args.clone();
        args7.push("--microphone".to_string());
        args7.push("mic2".to_string());
        let argv7: Vec<&str> = args7.iter().map(String::as_str).collect();
        let cli = parse(&argv7).unwrap();
        let err = cli.validate().unwrap_err();
        assert!(err.contains("at most 6"), "{err}");

        // Single-track mixes them all down, so 7 devices stay legal there.
        let argv_single: Vec<&str> = argv7
            .iter()
            .copied()
            .filter(|a| *a != "--multi-track")
            .collect();
        assert!(parse(&argv_single).unwrap().validate().is_ok());
    }

    #[test]
    fn settings_webcam_device_flows_through_and_requires_multi_track() {
        let dir = std::env::temp_dir();
        let path = dir.join(format!(
            "obs-express-cli-webcam-{}.json",
            std::process::id()
        ));
        std::fs::write(&path, r#"{"webcam_device": "test"}"#).unwrap();
        let path_str = path.to_string_lossy().into_owned();

        let cli = parse(&[
            "--output",
            "a.mp4",
            "--multi-track",
            "--settings",
            &path_str,
        ])
        .unwrap();
        assert_eq!(cli.validate().unwrap().webcam_device, "test");

        // The settings-file webcam hits the same multi-track requirement as
        // the --webcam flag.
        let cli = parse(&["--output", "a.mp4", "--settings", &path_str]).unwrap();
        assert!(cli.validate().is_err());
        std::fs::remove_file(&path).unwrap();

        // Without --settings, the --webcam flag lands in the effective config.
        let cli = parse(&["--output", "a.mp4", "--multi-track", "--webcam", "test"]).unwrap();
        assert_eq!(cli.validate().unwrap().webcam_device, "test");
    }

    #[test]
    fn tracker_color_is_validated() {
        let cli = parse(&["--output", "a.mp4", "--tracker-color", "0,0"]).unwrap();
        assert!(cli.validate().is_err());
        let cli = parse(&["--output", "a.mp4", "--tracker-color", "300,0,0"]).unwrap();
        assert!(cli.validate().is_err());
        let cli = parse(&[
            "--output",
            "a.mp4",
            "--tracker",
            "--tracker-color",
            "0,128,255",
        ])
        .unwrap();
        assert!(cli.validate().is_ok());
        assert!(cli.tracker);
    }
}
