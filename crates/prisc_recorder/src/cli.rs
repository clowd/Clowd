use std::path::PathBuf;

use clap::Parser;

use crate::region;
use crate::settings::Settings;

/// Maximum total audio sources (speakers + microphones).
pub const MAX_AUDIO_SOURCES: usize = 8;

#[derive(Parser, Debug)]
#[command(name = "obs-express", about = "Minimal screen recorder backed by OBS")]
pub struct Cli {
    /// Recording file path; must end .mp4 and its parent directory must exist.
    #[arg(long)]
    pub output: PathBuf,

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

    /// JSON settings file replacing the individual tuning flags; re-readable at
    /// runtime via the stdin `configure` command.
    #[arg(long, conflicts_with_all = [
        "fps", "crf", "max_width", "max_height", "hw_accel", "low_cpu",
        "no_cursor", "tracker", "tracker_color", "speaker", "microphone",
    ])]
    pub settings: Option<PathBuf>,
}

impl Cli {
    /// §1.1 validations that clap cannot express, plus resolution of the
    /// effective tunable settings (`--settings` file, or the individual
    /// flags). Violations → exit 2.
    pub fn validate(&self) -> Result<Settings, String> {
        let output_str = self.output.to_string_lossy();
        if !output_str.to_ascii_lowercase().ends_with(".mp4") {
            return Err(format!("--output must end with .mp4: '{output_str}'"));
        }
        match self.output.parent() {
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

        // clap's conflicts_with already rejects --region + --monitor, but keep
        // the check for programmatic construction.
        if self.region.is_some() && self.monitor.is_some() {
            return Err("--region and --monitor are mutually exclusive".to_string());
        }

        if let Some(ref r) = self.region {
            region::parse_region(r).map_err(|e| e.to_string())?;
        }

        // Both paths run the same value validation (fps/crf/tracker
        // color/audio caps), so a bad --settings file fails startup exactly
        // like bad flags.
        match self.settings {
            Some(ref path) => Settings::load(path),
            None => {
                let settings = Settings::from_cli(self);
                settings.validate()?;
                Ok(settings)
            }
        }
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
