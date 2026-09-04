//! Platform abstraction. Both platform modules expose identical public
//! signatures (DESIGN §2.2); shared, platform-neutral types live here.
//!
//! The monitor/paths/display-capture layer (`MonitorInfo`, `ObsPaths`,
//! `enumerate_monitors`, `default_obs_paths`, ...) moved to the shared
//! `obs-platform` crate (SHARE_REGION_PLAN §4.3) and is re-exported below so
//! every existing `crate::platform::*` path keeps resolving; the platform
//! modules here keep only the recorder-specific remainder (cursor/mouse
//! sampling, audio/webcam helpers).

#[cfg(windows)]
mod windows;
#[cfg(windows)]
pub use self::windows::*;

#[cfg(target_os = "macos")]
mod macos;
#[cfg(target_os = "macos")]
pub use self::macos::*;

pub use obs_platform::{
    cursor_update_settings, default_obs_paths, display_capture_settings, enumerate_monitors,
    exit_process, find_monitor, init_process, monitor_display_scale, region_adapter_index,
    CaptureMethod, MonitorInfo, DISPLAY_CAPTURE_ID, GRAPHICS_MODULE, PLATFORM_NAME,
};
// Part of the compat surface (`crate::platform::ObsPaths`) but referenced
// nowhere inside this bin crate by name — callers only consume it through
// `default_obs_paths`'s return value — and a bin warns on unused re-exports.
#[allow(unused_imports)]
pub use obs_platform::ObsPaths;

/// Live pointer state, sampled once per rendered frame by the click tracker.
#[derive(Debug, Clone, Copy)]
pub struct MouseInfo {
    /// Cursor position in the platform capture coordinate space (§1.1) — the
    /// same space as `MonitorInfo::x/y` and `--region`.
    pub x: f64,
    pub y: f64,
    /// Any mouse button (left or right) currently held down.
    pub pressed: bool,
    /// Density factor for the highlight's on-screen size, so a click looks the
    /// same physical size on every display. Windows: the cursor monitor's
    /// DPI / 96, because capture coords are physical pixels. macOS: 1.0 —
    /// capture coords are points, which are already density-independent.
    pub scale: f64,
}

/// What the system cursor currently looks like, classified against the stock
/// cursor set. The string forms are the input-capture wire contract (`c` in
/// frame rows) — the editor keys its themed cursor assets off them, so the
/// list is append-only.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
// The variant list is the full wire contract; only the Windows classifier
// maps every stock cursor to one, so off Windows several are never built.
#[cfg_attr(not(target_os = "windows"), allow(dead_code))]
pub enum CursorKind {
    Arrow,
    IBeam,
    Wait,
    Cross,
    UpArrow,
    SizeNwse,
    SizeNesw,
    SizeWe,
    SizeNs,
    SizeAll,
    No,
    Hand,
    AppStarting,
    Help,
    Pen,
    Person,
    /// A cursor handle matching none of the cached stock cursors (application
    /// custom cursor).
    Custom,
    /// The cursor is not currently shown at all.
    Hidden,
}

impl CursorKind {
    /// The `c` value in input-capture frame rows (wire contract).
    pub fn as_str(self) -> &'static str {
        match self {
            CursorKind::Arrow => "arrow",
            CursorKind::IBeam => "ibeam",
            CursorKind::Wait => "wait",
            CursorKind::Cross => "cross",
            CursorKind::UpArrow => "uparrow",
            CursorKind::SizeNwse => "sizenwse",
            CursorKind::SizeNesw => "sizenesw",
            CursorKind::SizeWe => "sizewe",
            CursorKind::SizeNs => "sizens",
            CursorKind::SizeAll => "sizeall",
            CursorKind::No => "no",
            CursorKind::Hand => "hand",
            CursorKind::AppStarting => "appstarting",
            CursorKind::Help => "help",
            CursorKind::Pen => "pen",
            CursorKind::Person => "person",
            CursorKind::Custom => "custom",
            CursorKind::Hidden => "hidden",
        }
    }
}

/// One on-screen top-level window, sampled by `--window-capture`.
///
/// Windows are reported topmost-first (the platform z-order), and the bounds
/// are the *visible* frame in the platform capture coordinate space — the same
/// space as `MonitorInfo::x/y`, `CursorState::x/y` and `--region`. On Windows
/// that means the DWM extended frame bounds (physical px) rather than
/// `GetWindowRect`, whose invisible resize border would inflate every window by
/// several pixels; on macOS it is `kCGWindowBounds` (points, top-left origin —
/// the same convention as `CGDisplayBounds`).
#[derive(Debug, Clone)]
pub struct WindowInfo {
    /// Platform window identity, stable while the window lives (Windows:
    /// `HWND`; macOS: `CGWindowID`). Handles ARE recycled after a window is
    /// destroyed, which is why the window-capture identity map keys on
    /// `(id, pid)` rather than this alone.
    pub id: u64,
    /// Owning process id, the second half of that identity key.
    pub pid: u32,
    pub x: i32,
    pub y: i32,
    pub w: u32,
    pub h: u32,
    /// Window title as the user sees it; may be empty (a titleless window, or
    /// — on macOS — no Screen Recording permission).
    pub title: String,
    /// Executable file name (Windows) or application name (macOS); empty when
    /// it cannot be resolved.
    pub app: String,
}

/// One sample of the cursor for input capture: hotspot position in the
/// platform capture coordinate space (same space as `MonitorInfo::x/y` and
/// `--region`) plus the classified cursor shape.
#[derive(Debug, Clone, Copy)]
pub struct CursorState {
    pub x: i32,
    pub y: i32,
    pub kind: CursorKind,
    /// Platform-private shape identity from the same snapshot as `x`/`y`,
    /// consumed by `take_cursor_sprite` so sprite pixels always match the
    /// sampled position. Windows: the live `HCURSOR`; macOS: 0 (identity is
    /// the cursor seed inside the classifier, not a handle).
    #[cfg_attr(not(target_os = "windows"), allow(dead_code))]
    pub handle: isize,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cursor_kind_strings_are_the_wire_contract() {
        // DESIGN §1: the full `c` value list, append-only.
        let all = [
            (CursorKind::Arrow, "arrow"),
            (CursorKind::IBeam, "ibeam"),
            (CursorKind::Wait, "wait"),
            (CursorKind::Cross, "cross"),
            (CursorKind::UpArrow, "uparrow"),
            (CursorKind::SizeNwse, "sizenwse"),
            (CursorKind::SizeNesw, "sizenesw"),
            (CursorKind::SizeWe, "sizewe"),
            (CursorKind::SizeNs, "sizens"),
            (CursorKind::SizeAll, "sizeall"),
            (CursorKind::No, "no"),
            (CursorKind::Hand, "hand"),
            (CursorKind::AppStarting, "appstarting"),
            (CursorKind::Help, "help"),
            (CursorKind::Pen, "pen"),
            (CursorKind::Person, "person"),
            (CursorKind::Custom, "custom"),
            (CursorKind::Hidden, "hidden"),
        ];
        for (kind, s) in all {
            assert_eq!(kind.as_str(), s);
        }
    }
}
