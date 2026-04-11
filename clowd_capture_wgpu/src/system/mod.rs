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

    pub fn all_monitor_bounds() -> Vec<(ScreenRect, f32, bool)> {
        win_monitor::Monitor::all()
            .expect("Unable to enumerate monitors")
            .iter()
            .map(|m| (m.bounds(), m.scale_factor(), m.is_primary()))
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

    pub fn all_monitor_bounds() -> Vec<(ScreenRect, f32, bool)> {
        xcap::Monitor::all()
            .expect("Unable to enumerate monitors")
            .iter()
            .map(|m| (m.bounds(), m.scale_factor()))
            .collect()
    }
}
