//! User-configurable capturer settings.
//!
//! Every knob is exposed as a clap CLI flag (`CliArgs`, see `--help`).
//! `main.rs` parses the command line, converts it into a
//! `CapturerSettings` wrapped in an `Arc`, and hands it to `App` / the
//! render threads. New knobs only need a field on both structs and a
//! line in `into_settings`.

use std::path::PathBuf;

use clap::Parser;

use crate::filename_pattern::DEFAULT_FILENAME_PATTERN;
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
    /// RGBA (each channel in [0, 1]) accent color used for crosshair
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
    /// When enabled, a selection made by picking a window (hover + click,
    /// `W`, `--capture-mode window`) takes on that window's OS corner
    /// radius: the dashed border is drawn rounded and the copied / saved /
    /// previewed image has transparent corners instead of a few pixels of
    /// whatever sat behind the window. Dragged selections — and a window
    /// selection once it has been moved or resized — stay square. The
    /// radius comes from the OS where it can be asked (DWM on Windows 11,
    /// the window server's own corner mask on macOS) and from a per-version
    /// table otherwise; see `system::corners`.
    pub rounded_window_corners: bool,
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
    /// Benchmark mode: tear the cycle down as soon as the overlay's first
    /// frame is on screen, having logged the startup breakdown. No payload
    /// is written and no window is left up — the run exists only to produce
    /// that one log record.
    pub bench_startup: bool,
    /// .NET custom date-format string the SAVE dialog's suggested file name is
    /// rendered from, mirrored from the shell's "Filename pattern" setting so
    /// that saving straight from the overlay names the file the same way saving
    /// from the editor does. See `filename_pattern`.
    pub filename_pattern: String,
    /// Folder the SAVE dialog opens in, and the one the suggested name is
    /// uniquified against — the shell's last save path. `None` = let the dialog
    /// open wherever the OS last left it (standalone runs, and a shell that has
    /// no last save path yet).
    pub save_directory: Option<PathBuf>,
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
            rounded_window_corners: true,
            session_dir: None,
            capture_mode: CaptureMode::default(),
            video_mode: false,
            panel_features: PanelFeatures::ALL,
            bench_startup: false,
            filename_pattern: DEFAULT_FILENAME_PATTERN.to_string(),
            save_directory: None,
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
#[derive(Debug, Clone, Parser)]
#[command(version, about = "Clowd screen capturer")]
pub struct CliArgs {
    /// Wait for one of the global screenshot hotkeys (or a stdin capture
    /// request) before initializing the capture stack. Each trigger runs one
    /// normal capture cycle, then the process returns to waiting; it exits
    /// only on stdin EOF or a fatal error (CAPTURE_PROTOCOL.md). This process
    /// owns the hotkeys — via a low-level keyboard hook, not RegisterHotKey —
    /// so a paged-out shell cannot delay the trigger, and the key is
    /// suppressed before OS handlers like Windows 11's PrintScreen-opens-
    /// Snipping-Tool can steal it.
    #[arg(long, requires = "session_root")]
    pub standby: bool,

    /// Parent directory in which standby mode creates a unique capture session.
    #[arg(long, value_name = "PATH", requires = "standby")]
    pub session_root: Option<PathBuf>,

    /// Global hotkey for a region capture, in handy-keys grammar
    /// (for example `Control+Shift+PrintScreen`).
    #[arg(long, value_name = "GESTURE", requires = "standby")]
    pub hk_main: Option<String>,

    /// Global hotkey for an active-window capture.
    #[arg(long, value_name = "GESTURE", requires = "standby")]
    pub hk_window: Option<String>,

    /// Global hotkey for an active-monitor capture.
    #[arg(long, value_name = "GESTURE", requires = "standby")]
    pub hk_monitor: Option<String>,

    /// Directory to write the session payload into. When set, the
    /// EDIT/UPLOAD/SELECT-COLOR actions write their payload here and
    /// exit (see CAPTURE_PROTOCOL.md). Omit for standalone mode.
    #[arg(long, value_name = "PATH")]
    pub session_dir: Option<PathBuf>,

    /// Accent color for the crosshair, selection borders, and UI
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

    /// Keep window selections square: do not round the selection border to
    /// the window's OS corner radius, and do not leave those corner pixels
    /// transparent in the copied / saved image.
    #[arg(long)]
    pub no_rounded_corners: bool,

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
    /// rights back with `AllowSetForegroundWindow` as the cycle ends —
    /// the shell needs them to raise whatever it opens next, and cannot
    /// grant what it no longer holds (CAPTURE_PROTOCOL.md §2.5).
    ///
    /// Process-level rather than a `CapturerSettings` knob: the shell that
    /// spawned us is also the shell that outlives us — the capturer dies
    /// with it, so the two can never disagree about who to hand rights to
    /// (see `SystemInterop::set_shell_pid`). Omitted in standalone runs,
    /// where there is no shell and nothing to hand back.
    #[arg(long, value_name = "PID")]
    pub shell_pid: Option<u32>,

    /// Exit immediately after the overlay's first frame is on screen, after
    /// logging the startup timing breakdown. For benchmarking startup latency.
    #[arg(long)]
    pub bench_startup: bool,

    /// Date format the SAVE dialog's suggested file name is built from — a
    /// .NET custom date-format string, the same one the shell's "Filename
    /// pattern" setting holds and the editor's save dialog uses, so a capture
    /// saved from the overlay and one saved from the editor get the same name.
    /// English month/day names, and the timezone specifiers are not rendered
    /// (see `filename_pattern`).
    #[arg(long, value_name = "FORMAT", default_value = DEFAULT_FILENAME_PATTERN)]
    pub filename_pattern: String,

    /// Folder the SAVE dialog opens in, and the one the suggested name is
    /// checked against for collisions ("name (1)", "name (2)", …). The shell
    /// passes its last save path. Omit to let the dialog open wherever the OS
    /// last left it.
    #[arg(long, value_name = "PATH")]
    pub save_dir: Option<PathBuf>,

    /// Collect per-pass GPU timings for the debug panel. Off by default.
    /// Only implemented by the wgpu backend (macOS, or Windows built with
    /// `--features backend-wgpu`), where it requests
    /// `Features::TIMESTAMP_QUERY` at device creation and builds a query
    /// set plus four buffers per worker before the first frame. The
    /// shipped Windows d3d11 backend has no GPU timestamp support yet, so
    /// there the debug panel's GPU column stays "n/a" regardless of this
    /// flag. Read once, before the render workers start.
    #[arg(long)]
    pub gpu_timing: bool,
}

impl CliArgs {
    pub fn into_settings(self) -> CapturerSettings {
        CapturerSettings {
            accent_color: self.accent_color,
            tips_mode_at_startup: self.tips_mode,
            obscured_window_peek_enabled: !self.no_peek,
            obscured_window_detection_threshold: self.peek_threshold,
            cursor_visible_at_startup: !self.no_cursor,
            rounded_window_corners: !self.no_rounded_corners,
            session_dir: self.session_dir,
            capture_mode: self.capture_mode,
            video_mode: self.video,
            panel_features: PanelFeatures {
                upload: !self.no_upload,
                scroll_capture: !self.no_scroll_capture,
                ocr: !self.no_ocr,
            },
            bench_startup: self.bench_startup,
            filename_pattern: self.filename_pattern,
            save_directory: self.save_dir,
        }
    }
}

fn parse_hex_color(s: &str) -> Result<[f32; 4], String> {
    let hex = s.trim_start_matches('#');
    if !matches!(hex.len(), 6 | 8) || !hex.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err(format!("'{s}' is not a #RRGGBB or #RRGGBBAA color"));
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
        assert_eq!(from_cli.rounded_window_corners, default.rounded_window_corners);
        assert_eq!(from_cli.session_dir, default.session_dir);
        assert_eq!(from_cli.capture_mode, default.capture_mode);
        assert_eq!(from_cli.video_mode, default.video_mode);
        assert_eq!(from_cli.panel_features, default.panel_features);
        assert_eq!(from_cli.bench_startup, default.bench_startup);
        assert_eq!(from_cli.filename_pattern, default.filename_pattern);
        assert_eq!(from_cli.save_directory, default.save_directory);
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

    /// Rounded corners are on by a bare command line and `--no-rounded-corners`
    /// is the only way off — opt-out like the rest of the behaviour flags.
    #[test]
    fn rounded_corners_flag_is_opt_out() {
        assert!(
            CliArgs::parse_from(["clowd_capture_wgpu"])
                .into_settings()
                .rounded_window_corners
        );
        assert!(
            !CliArgs::parse_from(["clowd_capture_wgpu", "--no-rounded-corners"])
                .into_settings()
                .rounded_window_corners
        );
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

    /// The save-dialog naming flags: a bare command line keeps the shell's own
    /// default pattern and lets the dialog pick its folder.
    #[test]
    fn filename_pattern_and_save_dir_parse() {
        let bare = CliArgs::parse_from(["clowd_capture_wgpu"]).into_settings();
        assert_eq!(bare.filename_pattern, DEFAULT_FILENAME_PATTERN);
        assert_eq!(bare.save_directory, None);

        let set = CliArgs::parse_from([
            "clowd_capture_wgpu",
            "--filename-pattern",
            "'clowd' yyyy-MM-dd",
            "--save-dir",
            "/tmp/shots",
        ])
        .into_settings();
        assert_eq!(set.filename_pattern, "'clowd' yyyy-MM-dd");
        assert_eq!(set.save_directory, Some(PathBuf::from("/tmp/shots")));
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
