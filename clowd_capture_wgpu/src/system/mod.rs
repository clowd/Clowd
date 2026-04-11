#[cfg(windows)]
mod win_capture;

#[cfg(windows)]
mod win_monitor;

#[cfg(windows)]
mod win_mouse;

#[cfg(not(windows))]
mod xcap_impl;

use crate::geometry::{ScreenPoint, ScreenRect};

/// Information about a single monitor, bundled together so callers don't
/// have to juggle parallel vectors of fields. `bounds` is in raw physical
/// pixels in the same coordinate space as `CapturedDesktop::bounds`;
/// `scale_factor` is this monitor's DPI scale (1.0 = 100% / 96 DPI,
/// 1.5 = 150% / 144 DPI, 2.0 = 200% / 192 DPI, …) so callers can map raw
/// pixels to logical units when they need to.
#[derive(Debug, Clone)]
pub struct MonitorInfo {
    pub bounds: ScreenRect,
    pub scale_factor: f32,
    pub is_primary: bool,
    pub refresh_hz: f32,
}

/// Raw virtual-desktop snapshot. The pixel data is in BGRA byte order
/// exactly as `GetDIBits` produces it — no CPU swizzle. The GPU uploads it
/// directly into a `Bgra8UnormSrgb` texture and the sampler hardware
/// reorders to RGBA at fetch time, which is free.
///
/// All sizes / coordinates are in raw physical pixels; nothing here is
/// scaled. `scale_factor` and `monitors[i].scale_factor` exist purely so
/// callers can convert to logical units when they need to.
pub struct CapturedDesktop {
    pub bgra: Vec<u8>,
    /// Width in raw physical pixels (one byte quad per pixel in `bgra`).
    pub width: u32,
    /// Height in raw physical pixels.
    pub height: u32,
    /// Virtual-desktop rect in raw physical pixels at the moment of
    /// capture. May have negative origin coordinates when secondary
    /// monitors extend left/up of the primary.
    pub bounds: ScreenRect,
    /// System DPI scale — equal to the *primary* monitor's scale on
    /// per-monitor DPI aware processes (which winit configures by default).
    /// 1.0 = 100% (96 DPI), 1.5 = 150%, 2.0 = 200%, etc. For the scale
    /// of any other monitor, see `monitors`.
    pub scale_factor: f32,
    /// Snapshot of the monitor topology at the same instant as the
    /// bitmap. Each entry carries that monitor's bounds (in the same
    /// raw-pixel virtual-desktop coordinate space as `bounds`) and its
    /// own DPI scale. Bundling them with the bitmap avoids any race
    /// where the topology could change between capture and enumeration.
    pub monitors: Vec<MonitorInfo>,
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

    pub fn capture_desktop() -> CapturedDesktop {
        let bitmap = win_capture::capture_desktop().expect("Unable to capture desktop");
        CapturedDesktop {
            bgra: bitmap.bgra,
            width: bitmap.width,
            height: bitmap.height,
            bounds: bitmap.bounds,
            scale_factor: win_capture::system_scale_factor(),
            monitors: Self::all_monitors(),
        }
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

    pub fn capture_desktop() -> CapturedDesktop {
        let _ = xcap_impl::capture_desktop();
        unimplemented!("xcap path not wired to BGRA capture");
    }
}
