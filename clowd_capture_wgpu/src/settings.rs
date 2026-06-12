//! User-configurable capturer settings.
//!
//! Every knob is exposed as a clap CLI flag (`CliArgs`, see `--help`).
//! `main.rs` parses the command line, converts it into a
//! `CapturerSettings` wrapped in an `Arc`, and hands it to `App` / the
//! render threads. New knobs only need a field on both structs and a
//! line in `into_settings`.

use std::path::PathBuf;

use clap::Parser;

#[derive(Debug, Default, Clone, Copy, PartialEq, Eq, clap::ValueEnum)]
pub enum TipsMode {
    #[default]
    Hints,
    Tips,
    Off,
}

impl TipsMode {
    pub fn next(self) -> Self {
        match self {
            TipsMode::Hints => TipsMode::Tips,
            TipsMode::Tips => TipsMode::Off,
            TipsMode::Off => TipsMode::Hints,
        }
    }

    pub fn show_hints(self) -> bool {
        matches!(self, TipsMode::Hints)
    }

    pub fn show_tips_panel(self) -> bool {
        matches!(self, TipsMode::Tips)
    }
}

/// Settings that influence how the capturer renders. Cheap to clone
/// via `Arc` — we never mutate it after construction.
#[derive(Debug, Clone)]
pub struct CapturerSettings {
    /// RGBA (each channel in [0, 1]) accent colour used for crosshair
    /// arms, selection borders, and UI highlights. Written into the
    /// per-window uniform buffer once, at render-thread startup.
    pub accent_color: [f32; 4],
    /// Which tips/hints mode is active when the capturer first opens.
    /// The user cycles through modes with the `T` key.
    pub tips_mode_at_startup: TipsMode,
    /// When enabled, obstructed windows are captured via PrintWindow
    /// and a peek-through composite is shown when hovering them.
    pub obscured_window_peek_enabled: bool,
    /// Maximum fraction of a window's area that can be obstructed by
    /// higher-Z windows before it is dropped from hit-test results.
    /// 0.80 = windows up to 80% covered are still selectable. Range 0.0–1.0.
    pub obscured_window_detection_threshold: f32,
    /// Whether the captured OS cursor is rendered in the preview and
    /// included when saving/copying. The cursor image is always captured
    /// regardless; this only controls visibility. Toggled at runtime
    /// with the `M` key.
    pub cursor_visible_at_startup: bool,
    /// Directory to write the session payload into, supplied by the
    /// shell via `--session-dir <path>`. When set, the EDIT action
    /// writes desktop/cursor/preview PNGs plus `session.json` here and
    /// exits (see CAPTURE_PROTOCOL.md). `None` = standalone mode.
    pub session_dir: Option<PathBuf>,
}

impl Default for CapturerSettings {
    fn default() -> Self {
        Self {
            // #3B97D2 — the legacy "clowd blue" accent.
            accent_color: [0x3B as f32 / 255.0, 0x97 as f32 / 255.0, 0xD2 as f32 / 255.0, 1.0],
            tips_mode_at_startup: TipsMode::default(),
            obscured_window_peek_enabled: true,
            obscured_window_detection_threshold: 0.80,
            cursor_visible_at_startup: true,
            session_dir: None,
        }
    }
}

/// Command-line interface. One flag per `CapturerSettings` knob; flag
/// defaults mirror `CapturerSettings::default()` exactly.
#[derive(Debug, Parser)]
#[command(version, about = "Clowd screen capturer")]
pub struct CliArgs {
    /// Directory to write the session payload into. When set, the EDIT
    /// action writes desktop/cursor/preview PNGs plus session.json here
    /// and exits (see CAPTURE_PROTOCOL.md). Omit for standalone mode.
    #[arg(long, value_name = "PATH")]
    pub session_dir: Option<PathBuf>,

    /// Accent colour for the crosshair, selection borders, and UI
    /// highlights, as hex `#RRGGBB` or `#RRGGBBAA` (leading `#` optional).
    #[arg(long, value_name = "HEX", default_value = "#3B97D2", value_parser = parse_hex_color)]
    pub accent_color: [f32; 4],

    /// Tips/hints overlay mode at startup (cycled at runtime with T).
    #[arg(long, value_enum, default_value_t = TipsMode::Hints)]
    pub tips_mode: TipsMode,

    /// Disable obstructed-window peek-through capture.
    #[arg(long)]
    pub no_peek: bool,

    /// Maximum fraction of a window's area that may be obstructed before
    /// it is dropped from hit-test results (0.0 - 1.0).
    #[arg(long, value_name = "FRACTION", default_value_t = 0.80, value_parser = parse_fraction)]
    pub peek_threshold: f32,

    /// Start with the captured cursor hidden (toggled at runtime with M).
    #[arg(long)]
    pub no_cursor: bool,
}

impl CliArgs {
    pub fn into_settings(self) -> CapturerSettings {
        CapturerSettings {
            accent_color: self.accent_color,
            tips_mode_at_startup: self.tips_mode,
            obscured_window_peek_enabled: !self.no_peek,
            obscured_window_detection_threshold: self.peek_threshold,
            cursor_visible_at_startup: !self.no_cursor,
            session_dir: self.session_dir,
        }
    }
}

fn parse_hex_color(s: &str) -> Result<[f32; 4], String> {
    let hex = s.trim_start_matches('#');
    if !matches!(hex.len(), 6 | 8) || !hex.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err(format!("'{s}' is not a #RRGGBB or #RRGGBBAA colour"));
    }
    let channel = |i: usize| u8::from_str_radix(&hex[i..i + 2], 16).unwrap() as f32 / 255.0;
    let alpha = if hex.len() == 8 { channel(6) } else { 1.0 };
    Ok([channel(0), channel(2), channel(4), alpha])
}

fn parse_fraction(s: &str) -> Result<f32, String> {
    let v: f32 = s
        .parse()
        .map_err(|_| format!("'{s}' is not a number"))?;
    if (0.0..=1.0).contains(&v) {
        Ok(v)
    } else {
        Err(format!("{v} is outside the range 0.0 - 1.0"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cli_defaults_mirror_settings_default() {
        let cli = CliArgs::parse_from(["clowd_capture_wgpu"]);
        let from_cli = cli.into_settings();
        let default = CapturerSettings::default();
        assert_eq!(from_cli.accent_color, default.accent_color);
        assert_eq!(from_cli.tips_mode_at_startup, default.tips_mode_at_startup);
        assert_eq!(from_cli.obscured_window_peek_enabled, default.obscured_window_peek_enabled);
        assert_eq!(
            from_cli.obscured_window_detection_threshold,
            default.obscured_window_detection_threshold
        );
        assert_eq!(from_cli.cursor_visible_at_startup, default.cursor_visible_at_startup);
        assert_eq!(from_cli.session_dir, default.session_dir);
    }

    #[test]
    fn hex_color_parses_rgb_and_rgba() {
        assert_eq!(parse_hex_color("#FF0000").unwrap(), [1.0, 0.0, 0.0, 1.0]);
        assert_eq!(parse_hex_color("00FF00").unwrap(), [0.0, 1.0, 0.0, 1.0]);
        assert_eq!(parse_hex_color("#00000080").unwrap()[3], 128.0 / 255.0);
        assert!(parse_hex_color("#F00").is_err());
        assert!(parse_hex_color("not-a-color").is_err());
    }

    #[test]
    fn fraction_rejects_out_of_range() {
        assert_eq!(parse_fraction("0.5").unwrap(), 0.5);
        assert!(parse_fraction("1.5").is_err());
        assert!(parse_fraction("-0.1").is_err());
    }
}
