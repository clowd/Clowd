//! User-configurable capturer settings.
//!
//! Every knob is exposed as a clap CLI flag (`CliArgs`, see `--help`).
//! `main.rs` parses the command line, converts it into a
//! `CapturerSettings` wrapped in an `Arc`, and hands it to `App` / the
//! render threads. New knobs only need a field on both structs and a
//! line in `into_settings`.

use std::path::PathBuf;

use clap::Parser;

use crate::ui::components::panel::model::PanelFeatures;

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

/// GPU allocator sizing strategy, mirrored from the shell's capture
/// settings. `LowerMemoryUsage` (default) keeps gpu-allocator's retained
/// heap blocks small — the right trade for a process that exits after one
/// capture; `MaxPerformance` restores wgpu's large-block default.
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq, clap::ValueEnum)]
pub enum MemoryHintsMode {
    #[default]
    LowerMemoryUsage,
    MaxPerformance,
}

/// What the capturer should have selected when it opens. `Region` is the
/// default free-selection crosshair; `Screen` and `Window` pre-select the
/// active monitor / foreground window and show the action panel so the
/// user can confirm or adjust (mirrors pressing `F` / `W` at startup).
/// Chosen by the shell from which capture hotkey fired.
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq, clap::ValueEnum)]
pub enum CaptureMode {
    #[default]
    Region,
    Screen,
    Window,
}

impl CaptureMode {
    /// Whether this mode pre-selects a region at startup (vs. free
    /// crosshair selection).
    pub fn is_preselect(self) -> bool {
        !matches!(self, CaptureMode::Region)
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
    /// shell via `--session-dir <path>`. When set, the EDIT/UPLOAD
    /// actions write desktop/cursor/preview PNGs plus `session.json`
    /// here (UPLOAD also an `action.txt` marker), SELECT-COLOR writes
    /// only `action.txt`, and the capturer exits (see
    /// CAPTURE_PROTOCOL.md). `None` = standalone mode.
    pub session_dir: Option<PathBuf>,
    /// What to have selected when the capturer opens. `Region` starts with
    /// the free-selection crosshair; `Screen`/`Window` pre-select the
    /// active monitor / foreground window (see `CaptureMode`). Set by the
    /// shell per the capture hotkey the user pressed.
    pub capture_mode: CaptureMode,
    /// When true, the shell launched the overlay specifically to pick a
    /// recording region (StartStopRecording hotkey / tray). As soon as a
    /// selection becomes captured the app dispatches `Command::Video`
    /// instead of waiting for a panel click. The VIDEO panel button still
    /// works in normal mode.
    pub video_mode: bool,
    /// Which of the optional panel buttons (UPLOAD / SCROLL / OCR) the
    /// user has left switched on — see [`PanelFeatures`]. Everything on
    /// by default, so a standalone run shows the full strip.
    pub panel_features: PanelFeatures,
}

impl Default for CapturerSettings {
    fn default() -> Self {
        Self {
            // #2F7CAE — the legacy "clowd blue" (#3B97D2) darkened to a 4.5:1 contrast ratio
            // against the white labels drawn on accent-filled buttons (issue #48). The shell
            // always passes `--accent-color` (the OS accent, or the user's pick, put through
            // the same correction — see AccentColors in Clowd.Shared), so this is the
            // standalone default only.
            accent_color: [0x2F as f32 / 255.0, 0x7C as f32 / 255.0, 0xAE as f32 / 255.0, 1.0],
            tips_mode_at_startup: TipsMode::default(),
            obscured_window_peek_enabled: true,
            obscured_window_detection_threshold: 0.80,
            cursor_visible_at_startup: true,
            session_dir: None,
            capture_mode: CaptureMode::default(),
            video_mode: false,
            panel_features: PanelFeatures::ALL,
        }
    }
}

/// Command-line interface. One flag per `CapturerSettings` knob; flag
/// defaults mirror `CapturerSettings::default()` exactly.
///
/// The second half of a scrolling capture has its own binary and its own
/// command line — see `clowd_scroll_driver` and CAPTURE_PROTOCOL.md §2.
/// Nothing about it belongs here: the overlay's part ends when it writes
/// the `scroll` action marker.
#[derive(Debug, Parser)]
#[command(version, about = "Clowd screen capturer")]
pub struct CliArgs {
    /// Directory to write the session payload into. When set, the
    /// EDIT/UPLOAD/SELECT-COLOR actions write their payload here and
    /// exit (see CAPTURE_PROTOCOL.md). Omit for standalone mode.
    #[arg(long, value_name = "PATH")]
    pub session_dir: Option<PathBuf>,

    /// Accent colour for the crosshair, selection borders, and UI
    /// highlights, as hex `#RRGGBB` or `#RRGGBBAA` (leading `#` optional).
    #[arg(long, value_name = "HEX", default_value = "#2F7CAE", value_parser = parse_hex_color)]
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

    /// What to have selected when the capturer opens. `region` (default)
    /// starts the free-selection crosshair; `screen` pre-selects the
    /// active monitor and `window` the foreground window, in both cases
    /// showing the action panel so the user can confirm or adjust.
    #[arg(long, value_enum, default_value_t = CaptureMode::Region)]
    pub capture_mode: CaptureMode,

    /// Open the overlay in video mode: as soon as a region is selected,
    /// write the VIDEO action and exit (the shell then starts recording).
    /// Requires `--session-dir`.
    #[arg(long)]
    pub video: bool,

    /// GPU allocator strategy. `lower-memory-usage` (the default) keeps the
    /// allocator's retained heap blocks small; `max-performance` restores
    /// wgpu's large-block allocator. Read once at GPU device creation.
    #[arg(long, value_enum, default_value_t = MemoryHintsMode::LowerMemoryUsage)]
    pub memory_hints: MemoryHintsMode,

    /// Hide the UPLOAD button (both the capture strip's and the OCR
    /// strip's — a user who turned uploading off did not mean "except
    /// for text").
    #[arg(long)]
    pub no_upload: bool,

    /// Hide the SCROLL (scrolling capture) button.
    #[arg(long)]
    pub no_scroll_capture: bool,

    /// Hide the OCR button, which is the only way into OCR mode.
    #[arg(long)]
    pub no_ocr: bool,

    /// The shell's process id, so the overlay can hand its foreground
    /// rights back with `AllowSetForegroundWindow` as each cycle ends —
    /// the shell needs them to raise whatever it opens next, and cannot
    /// grant what it no longer holds (CAPTURE_PROTOCOL.md §2.5).
    ///
    /// Process-level rather than a `CapturerSettings` knob: it is the same
    /// for every cycle a process serves, and the shell that spawned us is
    /// also the shell that outlives us — the capturer dies with it, so the
    /// two can never disagree about who to hand rights to. Omitted in
    /// standalone runs, where there is no shell and nothing to hand back.
    #[arg(long, value_name = "PID")]
    pub shell_pid: Option<u32>,
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
            capture_mode: self.capture_mode,
            video_mode: self.video,
            panel_features: PanelFeatures {
                upload: !self.no_upload,
                scroll_capture: !self.no_scroll_capture,
                ocr: !self.no_ocr,
            },
        }
    }
}

pub(crate) fn parse_hex_color(s: &str) -> Result<[f32; 4], String> {
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
        assert_eq!(from_cli.capture_mode, default.capture_mode);
        assert_eq!(from_cli.video_mode, default.video_mode);
        assert_eq!(from_cli.panel_features, default.panel_features);
    }

    /// The optional-button flags are opt-OUT: a bare command line shows
    /// the full strip, and each flag removes exactly its own button.
    #[test]
    fn panel_feature_flags_are_opt_out() {
        let bare = CliArgs::parse_from(["clowd_capture_wgpu"]).into_settings();
        assert_eq!(bare.panel_features, PanelFeatures::ALL);

        let none = CliArgs::parse_from(["clowd_capture_wgpu", "--no-upload", "--no-scroll-capture", "--no-ocr"]).into_settings();
        assert_eq!(
            none.panel_features,
            PanelFeatures {
                upload: false,
                scroll_capture: false,
                ocr: false,
            }
        );

        let no_ocr = CliArgs::parse_from(["clowd_capture_wgpu", "--no-ocr"]).into_settings();
        assert!(!no_ocr.panel_features.ocr);
        assert!(no_ocr.panel_features.upload && no_ocr.panel_features.scroll_capture);
    }

    #[test]
    fn shell_pid_parses_and_is_absent_by_default() {
        // Standalone runs have no shell, and nothing to hand foreground
        // rights back to.
        assert_eq!(CliArgs::parse_from(["clowd_capture_wgpu"]).shell_pid, None);
        assert_eq!(
            CliArgs::parse_from(["clowd_capture_wgpu", "--shell-pid", "4321"]).shell_pid,
            Some(4321)
        );
        // Both spawn paths use the two-token form; the `=` form is what a
        // human types, and clap accepts either.
        assert_eq!(
            CliArgs::parse_from(["clowd_capture_wgpu", "--shell-pid=4321"]).shell_pid,
            Some(4321)
        );
    }

    #[test]
    fn capture_mode_defaults_to_region_and_parses_variants() {
        let default = CliArgs::parse_from(["clowd_capture_wgpu"]);
        assert_eq!(default.capture_mode, CaptureMode::Region);
        assert!(!default.capture_mode.is_preselect());

        let window = CliArgs::parse_from(["clowd_capture_wgpu", "--capture-mode", "window"]);
        assert_eq!(window.capture_mode, CaptureMode::Window);
        assert!(window.capture_mode.is_preselect());

        let screen = CliArgs::parse_from(["clowd_capture_wgpu", "--capture-mode", "screen"]);
        assert_eq!(screen.capture_mode, CaptureMode::Screen);
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
