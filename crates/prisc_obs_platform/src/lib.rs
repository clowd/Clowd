//! Shared platform layer (SHARE_REGION_PLAN §4.3): monitor enumeration, obs
//! paths, display-capture settings and region math, extracted from obs-express
//! so every binary in the workspace (obs-express, clowd_share_region) uses the
//! same primitives. Both platform modules expose identical public signatures
//! (DESIGN §2.2) and are re-exported at the crate root; shared,
//! platform-neutral types and the pure monitor-matching logic live here.
//!
//! Everything recorder-specific (cursor sprites, mouse sampling, audio/webcam
//! helpers) deliberately stays behind in obs-express.

use std::path::PathBuf;

pub mod region;

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

/// Which OS API backs display capture. Windows-only in effect: the macOS
/// screen-capture source exposes no equivalent knob and ignores the value.
///
/// The Windows mapping is win-capture's `method` property
/// (`enum display_capture_method` in
/// obs-studio/plugins/win-capture/duplicator-monitor-capture.c): 0 = auto,
/// 1 = DXGI desktop duplication, 2 = Windows Graphics Capture. `Auto` lets
/// win-capture's `choose_method()` pick, and every value force-falls back to
/// DXGI when WGC is unsupported.
///
/// The choice is visible to the user beyond performance: WGC makes Windows
/// draw a yellow capture border around the recorded display, which libobs
/// suppresses via `GraphicsCaptureSession::IsBorderRequired(false)`
/// (obs-studio/libobs-winrt/winrt-capture.cpp) — an API that exists only on
/// Windows 11. On Windows 10 the border is unavoidable under WGC, so `Dxgi`
/// is the way to get rid of it there.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum CaptureMethod {
    /// Let the capture plugin choose.
    Auto,
    /// DXGI desktop duplication. Draws no capture border on any Windows
    /// version.
    Dxgi,
    /// Windows Graphics Capture. The default: `Auto` prefers the DXGI
    /// duplicator, which was verified to produce black frames on a
    /// Win11 26H1 + NVIDIA machine, while WGC captures correctly there.
    #[default]
    Wgc,
}

impl CaptureMethod {
    /// The accepted spellings, in `--help` order.
    pub const VARIANTS: [&'static str; 3] = ["auto", "dxgi", "wgc"];

    /// win-capture's `method` property value.
    pub fn as_obs_method(self) -> i64 {
        match self {
            CaptureMethod::Auto => 0,
            CaptureMethod::Dxgi => 1,
            CaptureMethod::Wgc => 2,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            CaptureMethod::Auto => "auto",
            CaptureMethod::Dxgi => "dxgi",
            CaptureMethod::Wgc => "wgc",
        }
    }
}

impl std::str::FromStr for CaptureMethod {
    type Err = String;

    fn from_str(s: &str) -> Result<Self, Self::Err> {
        match s.to_ascii_lowercase().as_str() {
            "auto" => Ok(CaptureMethod::Auto),
            "dxgi" => Ok(CaptureMethod::Dxgi),
            "wgc" => Ok(CaptureMethod::Wgc),
            _ => Err(format!(
                "unknown capture method '{s}' (expected {})",
                CaptureMethod::VARIANTS.join(", ")
            )),
        }
    }
}

impl std::fmt::Display for CaptureMethod {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
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
    fn capture_method_round_trips_and_maps_to_obs_values() {
        for name in CaptureMethod::VARIANTS {
            let m: CaptureMethod = name.parse().unwrap();
            assert_eq!(m.as_str(), name);
        }
        assert_eq!(CaptureMethod::default(), CaptureMethod::Wgc);
        assert_eq!(CaptureMethod::Auto.as_obs_method(), 0);
        assert_eq!(CaptureMethod::Dxgi.as_obs_method(), 1);
        assert_eq!(CaptureMethod::Wgc.as_obs_method(), 2);
        assert_eq!("WGC".parse::<CaptureMethod>().unwrap(), CaptureMethod::Wgc);
        assert!("ddapi".parse::<CaptureMethod>().is_err());
    }

    #[test]
    fn no_match() {
        assert!(match_monitor("nope", &synthetic()).is_none());
    }
}
