//! Platform abstraction. Both platform modules expose identical public
//! signatures (DESIGN §2.2); shared, platform-neutral types and the pure
//! monitor-matching logic live here.

use std::path::PathBuf;

#[cfg(windows)]
mod windows;
#[cfg(windows)]
pub use self::windows::*;

#[cfg(target_os = "macos")]
mod macos;
#[cfg(target_os = "macos")]
pub use self::macos::*;

#[derive(Debug, Clone)]
pub struct MonitorInfo {
    /// Stable id: Windows device interface path (`\\?\DISPLAY#…`) / mac display UUID.
    pub id: String,
    /// Windows: GDI device name (`\\.\DISPLAY1`); mac: CGDirectDisplayID as string.
    pub alt_id: Option<String>,
    /// Origin in the platform capture coordinate space (§1.1):
    /// Windows = physical px, virtual desktop; macOS = CG points.
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
    /// Capture pixels per coordinate-space unit: 1.0 on Windows (coords are
    /// already physical px); the Retina backing scale on macOS, where coords
    /// are CG points but the capture source emits pixel-sized frames.
    pub scale: f64,
    pub is_primary: bool,
}

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
    pub handle: isize,
}

/// Paths handed to `obs_add_module_path` / `obs_add_data_path`. `module_bin` /
/// `module_data` are passed to libobs verbatim (they may contain the
/// `%module%` template token).
pub struct ObsPaths {
    pub module_bin: String,
    pub module_data: String,
    pub libobs_data: Option<PathBuf>,
}

/// Pure monitor matching: exact `id`, then `alt_id`, then 0-based index.
/// Platform `find_monitor` implementations delegate here.
pub(crate) fn match_monitor(id: &str, monitors: &[MonitorInfo]) -> Option<MonitorInfo> {
    if let Some(m) = monitors.iter().find(|m| m.id == id) {
        return Some(m.clone());
    }
    if let Some(m) = monitors.iter().find(|m| m.alt_id.as_deref() == Some(id)) {
        return Some(m.clone());
    }
    if let Ok(index) = id.parse::<usize>() {
        return monitors.get(index).cloned();
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    fn synthetic() -> Vec<MonitorInfo> {
        vec![
            MonitorInfo {
                id: r"\\?\DISPLAY#DELA1E2#5&fef00e1&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}".to_string(),
                alt_id: Some(r"\\.\DISPLAY1".to_string()),
                x: 0,
                y: 0,
                width: 2560,
                height: 1440,
                scale: 1.0,
                is_primary: true,
            },
            MonitorInfo {
                id: r"\\?\DISPLAY#GSM5B08#5&fef00e1&0&UID4357#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}".to_string(),
                alt_id: Some(r"\\.\DISPLAY2".to_string()),
                x: -1920,
                y: 200,
                width: 1920,
                height: 1080,
                scale: 1.0,
                is_primary: false,
            },
        ]
    }

    #[test]
    fn matches_device_interface_path() {
        let mons = synthetic();
        let m = match_monitor(&mons[1].id.clone(), &mons).unwrap();
        assert_eq!(m.alt_id.as_deref(), Some(r"\\.\DISPLAY2"));
    }

    #[test]
    fn matches_alt_id() {
        let mons = synthetic();
        let m = match_monitor(r"\\.\DISPLAY1", &mons).unwrap();
        assert!(m.is_primary);
    }

    #[test]
    fn matches_zero_based_index() {
        let mons = synthetic();
        let m = match_monitor("1", &mons).unwrap();
        assert_eq!(m.x, -1920);
        assert!(match_monitor("2", &mons).is_none());
    }

    #[test]
    fn id_takes_priority_over_index() {
        // A monitor whose id happens to be a digit must match by id, not index.
        let mons = vec![
            MonitorInfo {
                id: "1".to_string(),
                alt_id: None,
                x: 0,
                y: 0,
                width: 100,
                height: 100,
                scale: 1.0,
                is_primary: true,
            },
            MonitorInfo {
                id: "0".to_string(),
                alt_id: None,
                x: 100,
                y: 0,
                width: 100,
                height: 100,
                scale: 1.0,
                is_primary: false,
            },
        ];
        assert_eq!(match_monitor("1", &mons).unwrap().x, 0);
    }

    #[test]
    fn no_match() {
        assert!(match_monitor("nope", &synthetic()).is_none());
    }

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
