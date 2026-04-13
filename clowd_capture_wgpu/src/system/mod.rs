#[cfg(windows)]
mod win_capture;

#[cfg(windows)]
mod win_monitor;

#[cfg(windows)]
mod win_mouse;

#[cfg(windows)]
mod win_walker;

#[cfg(windows)]
pub use win_walker::WindowWalker;

#[cfg(target_os = "macos")]
mod mac_capture;

#[cfg(target_os = "macos")]
mod mac_monitor;

#[cfg(target_os = "macos")]
mod mac_mouse;

#[cfg(target_os = "macos")]
mod mac_walker;

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
    /// PCI vendor + device IDs of the DXGI adapter driving this monitor.
    /// Used by `bootstrap_window_gpu` to select the correct wgpu adapter
    /// per window, matching the C++ version's per-monitor `AdapterIdx`.
    /// `None` if DXGI enumeration failed (fallback to wgpu's default).
    pub adapter_id: Option<(u32, u32)>,
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
        // Build a map of GDI device name → (vendor_id, device_id) from DXGI.
        let dxgi_map = win_monitor::build_dxgi_adapter_map();

        win_monitor::all()
            .expect("Unable to enumerate monitors")
            .into_iter()
            .map(|m| {
                let adapter_id = dxgi_map.get(&m.name).copied();
                MonitorInfo {
                    bounds: m.bounds(),
                    scale_factor: m.scale_factor,
                    is_primary: m.is_primary,
                    refresh_hz: m.frequency,
                    adapter_id,
                }
            })
            .collect()
    }

    /// One-time platform init. Must be called early in `main()` before
    /// any other `SystemInterop` methods. Initializes COM and the
    /// native dialog subsystem.
    pub fn init() {
        use windows::Win32::System::Com::{CoInitializeEx, COINIT_APARTMENTTHREADED};
        let _ = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
        xdialog::init_win32_direct();
    }

    /// Enumerate visible top-level windows on the current virtual desktop.
    /// Call once at capture startup, after the desktop bitmap is grabbed but
    /// before overlay windows are created.
    pub fn snapshot_windows() -> WindowWalker {
        WindowWalker::snapshot()
    }
}

#[cfg(target_os = "macos")]
pub use mac_walker::WindowWalker;

#[cfg(target_os = "macos")]
impl SystemInterop {
    /// One-time platform init. Must be called early in `main()`.
    pub fn init() {
        xdialog::init_maccf_direct();
    }

    pub fn get_mouse_position() -> ScreenPoint {
        mac_mouse::get_position()
    }

    pub fn set_mouse_position(pos: ScreenPoint) {
        mac_mouse::set_position(pos)
    }

    pub fn capture_desktop() -> CapturedDesktop {
        let (bitmap, monitors) = mac_capture::capture_desktop().expect("Unable to capture desktop");
        CapturedDesktop {
            bgra: bitmap.bgra,
            width: bitmap.width,
            height: bitmap.height,
            bounds: bitmap.bounds,
            monitors,
        }
    }

    #[allow(dead_code)]
    pub fn all_monitors() -> Vec<MonitorInfo> {
        mac_monitor::all_monitors().expect("Unable to enumerate monitors")
    }

    pub fn snapshot_windows() -> WindowWalker {
        WindowWalker::snapshot()
    }
}
