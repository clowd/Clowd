//! User-configurable capturer settings.
//!
//! Every knob is exposed as a clap CLI flag (`CliArgs`, see `--help`).
//! `main.rs` parses the command line, converts it into a
//! `CapturerSettings` wrapped in an `Arc`, and hands it to `App` / the
//! render threads. New knobs only need a field on both structs and a
//! line in `into_settings`.

use std::path::PathBuf;

use clap::Parser;

use crate::geometry::{RectExt, ScreenPoint, ScreenRect};

#[derive(Debug, Default, Clone, Copy, PartialEq, Eq, clap::ValueEnum, serde::Deserialize)]
#[serde(rename_all = "snake_case")]
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
/// heap blocks small — the right trade for a host that idles in the
/// background; `MaxPerformance` restores wgpu's large-block default.
/// Process-level: read once at device creation, so the persistent host
/// must be relaunched for a change to take effect.
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq, clap::ValueEnum, serde::Deserialize)]
#[serde(rename_all = "snake_case")]
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
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq, clap::ValueEnum, serde::Deserialize)]
#[serde(rename_all = "snake_case")]
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
        }
    }
}

/// Command-line interface. One flag per `CapturerSettings` knob; flag
/// defaults mirror `CapturerSettings::default()` exactly.
///
/// The `--scroll-drive` group at the bottom is the exception: those flags
/// select a different *mode* of the binary (the scrolling-capture driver,
/// CAPTURE_PROTOCOL.md §3) rather than tuning the overlay, so they never
/// reach `CapturerSettings` — `main::run` branches on them before any
/// session, winit or wgpu setup exists.
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
    /// allocator's retained heap blocks small so an idle background host
    /// holds minimal memory; `max-performance` restores wgpu's large-block
    /// allocator. Read once at GPU device creation — applies to one-shot
    /// and persistent mode alike, but a running persistent host must be
    /// relaunched for a change to take effect.
    #[arg(long, value_enum, default_value_t = MemoryHintsMode::LowerMemoryUsage)]
    pub memory_hints: MemoryHintsMode,

    /// Run as a persistent capture host: warm up (workers, hidden windows),
    /// then stay resident reading NDJSON commands on stdin and emitting
    /// events on stdout (see `host::protocol`). The per-capture flags above
    /// are ignored — every capture's settings ride in with its `show`
    /// command.
    #[arg(long)]
    pub persistent: bool,

    /// Directory for the persistent host's log file (`capture-host.log`,
    /// truncated on start; the previous run is kept as `.1`). Only used
    /// with `--persistent` — one-shot mode logs into `--session-dir`.
    #[arg(long, value_name = "PATH")]
    pub log_dir: Option<PathBuf>,

    /// Run as the scrolling-capture driver instead of showing an overlay:
    /// scroll the window under `--point`, capture and stitch the region,
    /// then write a finished session into `--session-dir`. No winit, no
    /// wgpu; stdout carries only NDJSON protocol lines and stdin accepts
    /// `stop`/`cancel` (CAPTURE_PROTOCOL.md §3). Windows only — exits
    /// `EXIT_CAPTURE_FAILED` elsewhere. Requires `--session-dir`,
    /// `--region` and `--point`.
    #[arg(long)]
    pub scroll_drive: bool,

    /// `--scroll-drive` capture region, `X,Y,W,H` in the platform capture
    /// coordinate space (physical virtual-desktop px on Windows) — the same
    /// space and format the overlay's `scroll` action marker uses, passed
    /// through verbatim by the shell. `allow_hyphen_values`: a monitor left
    /// of or above the primary puts the whole region at negative
    /// coordinates, and without it clap reads a separate-token value like
    /// `-1920,0,…` as an unknown flag and refuses the command line.
    #[arg(long, value_name = "X,Y,W,H", value_parser = parse_region, allow_hyphen_values = true)]
    pub region: Option<ScreenRect>,

    /// `--scroll-drive` scroll point, `PX,PY` in the same space as
    /// `--region` (negative coordinates included, hence
    /// `allow_hyphen_values`). The cursor is parked here for the whole run
    /// and every wheel event is aimed at it, so it decides which pane
    /// scrolls.
    #[arg(long, value_name = "PX,PY", value_parser = parse_point, allow_hyphen_values = true)]
    pub point: Option<ScreenPoint>,

    /// `--scroll-drive` target window handle as a decimal integer, as
    /// resolved by the overlay when the user picked the scroll point. `0`
    /// (the default) or a handle that no longer holds up means "work it out
    /// from `--point`" — the driver re-validates it either way.
    #[arg(long, value_name = "N", default_value_t = 0, allow_hyphen_values = true)]
    pub hwnd: i64,
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

/// `--region X,Y,W,H`. Zero-area regions are rejected here rather than
/// deeper in the driver: a BitBlt of a 0-wide rect fails with a Win32 error
/// that says nothing about where the bad rect came from.
fn parse_region(s: &str) -> Result<ScreenRect, String> {
    let n = parse_i32_list::<4>(s)?;
    if n[2] <= 0 || n[3] <= 0 {
        return Err(format!("'{s}' has a non-positive width or height"));
    }
    Ok(ScreenRect::from_xy_size(n[0], n[1], n[2], n[3]))
}

/// `--point PX,PY`. Negative coordinates are legal and common — a monitor
/// left of or above the primary one lives at negative virtual-desktop
/// coordinates.
fn parse_point(s: &str) -> Result<ScreenPoint, String> {
    let n = parse_i32_list::<2>(s)?;
    Ok(ScreenPoint::new(n[0], n[1]))
}

/// Exactly `N` comma-separated decimal integers, no more and no fewer.
fn parse_i32_list<const N: usize>(s: &str) -> Result<[i32; N], String> {
    let mut out = [0i32; N];
    let mut parts = s.split(',');
    for slot in out.iter_mut() {
        let part = parts
            .next()
            .ok_or_else(|| format!("'{s}' needs {N} comma-separated integers"))?
            .trim();
        *slot = part
            .parse()
            .map_err(|_| format!("'{part}' is not an integer"))?;
    }
    if parts.next().is_some() {
        return Err(format!("'{s}' needs exactly {N} comma-separated integers"));
    }
    Ok(out)
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
    fn region_parses_and_rejects_degenerate() {
        assert_eq!(parse_region("10,20,300,400").unwrap(), ScreenRect::from_xy_size(10, 20, 300, 400));
        // Secondary monitor left of the primary: negative origin, positive size.
        assert_eq!(
            parse_region("-1920,0,1920,1080").unwrap(),
            ScreenRect::from_xy_size(-1920, 0, 1920, 1080)
        );
        assert_eq!(parse_region(" 1 , 2 , 3 , 4 ").unwrap(), ScreenRect::from_xy_size(1, 2, 3, 4));
        assert!(parse_region("10,20,0,400").is_err());
        assert!(parse_region("10,20,300,-1").is_err());
        assert!(parse_region("10,20,300").is_err());
        assert!(parse_region("10,20,300,400,500").is_err());
        assert!(parse_region("10,20,300,x").is_err());
    }

    #[test]
    fn point_parses_negative_coordinates() {
        assert_eq!(parse_point("40,50").unwrap(), ScreenPoint::new(40, 50));
        assert_eq!(parse_point("-40,-50").unwrap(), ScreenPoint::new(-40, -50));
        assert!(parse_point("40").is_err());
        assert!(parse_point("40,50,60").is_err());
    }

    #[test]
    fn scroll_drive_flags_parse() {
        let cli = CliArgs::parse_from([
            "clowd_capture_wgpu",
            "--scroll-drive",
            "--session-dir",
            "C:/tmp/session",
            "--region",
            "100,200,800,600",
            "--point",
            "450,500",
            "--hwnd",
            "133756",
        ]);
        assert!(cli.scroll_drive);
        assert_eq!(cli.region, Some(ScreenRect::from_xy_size(100, 200, 800, 600)));
        assert_eq!(cli.point, Some(ScreenPoint::new(450, 500)));
        assert_eq!(cli.hwnd, 133756);
        assert_eq!(cli.session_dir.as_deref(), Some(std::path::Path::new("C:/tmp/session")));
    }

    #[test]
    fn scroll_flags_accept_negative_origins_as_separate_tokens() {
        // A monitor left of or above the primary puts the region and point
        // at negative virtual-desktop coordinates, and the shell passes
        // each flag and its value as separate argv tokens. Without
        // allow_hyphen_values clap reads "-1920,…" as an unknown flag and
        // the driver dies with a usage error before emitting a single
        // protocol line — making scrolling capture unusable on that
        // monitor.
        let cli = CliArgs::try_parse_from([
            "clowd_capture_wgpu",
            "--scroll-drive",
            "--session-dir",
            "C:/tmp/s",
            "--region",
            "-1920,-1080,1920,1080",
            "--point",
            "-960,-500",
            "--hwnd",
            "133756",
        ])
        .expect("separate-token negative coordinates must parse");
        assert_eq!(cli.region, Some(ScreenRect::from_xy_size(-1920, -1080, 1920, 1080)));
        assert_eq!(cli.point, Some(ScreenPoint::new(-960, -500)));
        assert_eq!(cli.hwnd, 133756);

        // The shell is moving to the `--flag=value` single-token spelling;
        // both forms must keep parsing.
        let eq_form = CliArgs::try_parse_from([
            "clowd_capture_wgpu",
            "--scroll-drive",
            "--region=-1920,0,1920,1080",
            "--point=-960,500",
            "--hwnd=133756",
        ])
        .expect("single-token negative coordinates must parse");
        assert_eq!(eq_form.region, Some(ScreenRect::from_xy_size(-1920, 0, 1920, 1080)));
        assert_eq!(eq_form.point, Some(ScreenPoint::new(-960, 500)));
        assert_eq!(eq_form.hwnd, 133756);
    }

    #[test]
    fn scroll_drive_absent_by_default() {
        let cli = CliArgs::parse_from(["clowd_capture_wgpu"]);
        assert!(!cli.scroll_drive);
        assert_eq!(cli.region, None);
        assert_eq!(cli.point, None);
        assert_eq!(cli.hwnd, 0);
    }

    #[test]
    fn fraction_rejects_out_of_range() {
        assert_eq!(parse_fraction("0.5").unwrap(), 0.5);
        assert!(parse_fraction("1.5").is_err());
        assert!(parse_fraction("-0.1").is_err());
    }
}
