#[cfg(windows)]
mod win_capture;

#[cfg(windows)]
mod win_dialog;

#[cfg(windows)]
mod win_monitor;

#[cfg(windows)]
mod win_mouse;

#[cfg(windows)]
mod win_walker;

#[cfg(not(windows))]
mod xcap_impl;

#[cfg(windows)]
pub use win_walker::WindowWalker;

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

    pub fn capture_desktop() -> CapturedDesktop {
        let bitmap = win_capture::capture_desktop().expect("Unable to capture desktop");
        CapturedDesktop {
            bgra: bitmap.bgra,
            width: bitmap.width,
            height: bitmap.height,
            bounds: bitmap.bounds,
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

    /// Initialize dialog subsystem. Must be called once at startup.
    pub fn init_dialogs() {
        win_dialog::init();
    }

    /// Show a retry/cancel error dialog. Returns true if Retry, false if Cancel.
    pub fn show_error_retry_cancel(title: &str, message: &str) -> bool {
        win_dialog::show_error_retry_cancel(title, message)
    }

    /// Enumerate visible top-level windows on the current virtual desktop.
    /// Call once at capture startup, after the desktop bitmap is grabbed but
    /// before overlay windows are created.
    pub fn snapshot_windows() -> WindowWalker {
        WindowWalker::snapshot()
    }
}

#[cfg(not(windows))]
impl SystemInterop {
    pub fn get_mouse_position() -> ScreenPoint {}

    pub fn set_mouse_position(pos: ScreenPoint) {}

    pub fn capture_desktop() -> CapturedDesktop {
        let _ = xcap_impl::capture_desktop();
        unimplemented!("xcap path not wired to BGRA capture");
    }

    /// Initialize dialog subsystem. Must be called once at startup.
    pub fn init_dialogs() {
        // TODO: macOS implementation using CFUserNotificationDisplayAlert
    }

    /// Show a retry/cancel error dialog. Returns true if Retry, false if Cancel.
    pub fn show_error_retry_cancel(_title: &str, _message: &str) -> bool {
        // TODO: macOS implementation using CFUserNotificationDisplayAlert
        false
    }
}
