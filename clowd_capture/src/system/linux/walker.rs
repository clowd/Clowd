//! Window walker stand-in for Linux: there is no window list yet.
//!
//! The first Linux pass captures regions and whole monitors only. Window
//! hover targets, the foreground window for `--capture-mode window`, the
//! scroll-target lookups and the peek captures all come off this walker,
//! so a snapshot that knows no windows switches every one of them off at
//! once: hovers fall through to the crosshair, window mode falls back to
//! the active screen, and nothing is ever obstructed. The `_NET_CLIENT_
//! LIST_STACKING` walk that would fill this in is a later pass.

use crate::system::MonitorInfo;
use crate::system::{HitTestResult, ObstructedWindow, WindowTarget};
use clowd_rust_core::geometry::ScreenPoint;

/// An empty snapshot of the window stack. Same surface as the Windows and
/// macOS walkers so the callers need no platform arms.
pub struct WindowWalker;

impl WindowWalker {
    pub fn snapshot(_monitors: &[MonitorInfo], _visibility_threshold: f32, _rounded_corners: bool) -> Self {
        info!("WindowWalker: no window enumeration on Linux; hover and window targets are off");
        WindowWalker
    }

    /// Nothing to probe: no windows, no corners.
    pub fn probe_corner_radii(&self, _monitors: &[MonitorInfo]) {}

    pub fn hit_test_target(&self, _point: ScreenPoint) -> Option<WindowTarget> {
        None
    }

    pub fn top_level_hwnd_at(&self, _point: ScreenPoint) -> Option<isize> {
        None
    }

    pub fn hwnd_at_index(&self, _window_index: usize) -> Option<isize> {
        None
    }

    pub fn hit_test_full(&self, _point: ScreenPoint) -> Option<HitTestResult> {
        None
    }

    pub fn obstructed_windows(&self) -> Vec<ObstructedWindow> {
        Vec::new()
    }

    /// `None` sends `--capture-mode window` to the active screen instead.
    pub fn foreground_capture_target(&self) -> Option<WindowTarget> {
        None
    }
}
