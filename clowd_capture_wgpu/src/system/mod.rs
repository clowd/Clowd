#[cfg(windows)]
mod win_capture;

#[cfg(windows)]
mod win_monitor;

#[cfg(windows)]
mod win_mouse;

#[cfg(not(windows))]
mod xcap_impl;

use crate::geometry::{ScreenPoint, ScreenRect};
use image::DynamicImage;

/// Information about a single monitor, bundled together so callers don't
/// have to juggle parallel vectors of fields.
#[derive(Debug, Clone)]
pub struct MonitorInfo {
    pub bounds: ScreenRect,
    pub scale_factor: f32,
    pub is_primary: bool,
    pub refresh_hz: f32,
}

pub struct SystemInterop;

#[cfg(windows)]
impl SystemInterop {
    pub fn get_mouse_position() -> ScreenPoint {
        win_mouse::get_position()
    }

    pub fn set_mouse_position(pos: ScreenPoint) {
        win_mouse::set_position(pos)
    }

    pub fn virtual_desktop_bounds() -> ScreenRect {
        win_capture::virtual_desktop()
    }

    pub fn capture_desktop() -> (DynamicImage, DynamicImage) {
        win_capture::capture_desktop().expect("Unable to capture desktop")
    }

    pub fn all_monitors() -> Vec<MonitorInfo> {
        win_monitor::all()
            .expect("Unable to enumerate monitors")
            .into_iter()
            .map(|m| MonitorInfo {
                bounds: m.bounds(),
                scale_factor: m.scale_factor,
                is_primary: m.is_primary,
                refresh_hz: m.frequency,
            })
            .collect()
    }
}

#[cfg(not(windows))]
impl SystemInterop {
    pub fn get_mouse_position() -> ScreenPoint {}

    pub fn set_mouse_position(pos: ScreenPoint) {}

    pub fn virtual_desktop_bounds() -> ScreenRect {
        xcap_impl::virtual_desktop_and_monitors().0
    }

    pub fn capture_desktop() -> (DynamicImage, DynamicImage) {
        xcap_impl::capture_desktop().expect("Unable to capture desktop")
    }
}
