#[cfg(windows)]
pub(crate) mod win_capture;

#[cfg(windows)]
mod win_cursor;

#[cfg(windows)]
mod win_monitor;

#[cfg(windows)]
mod win_mouse;

#[cfg(windows)]
mod win_walker;

#[cfg(windows)]
pub use win_walker::WindowWalker;

#[cfg(target_os = "macos")]
pub(crate) mod mac_capture;

#[cfg(target_os = "macos")]
mod mac_cursor;

#[cfg(target_os = "macos")]
mod mac_monitor;

#[cfg(target_os = "macos")]
mod mac_mouse;

#[cfg(target_os = "macos")]
mod mac_walker;

#[cfg(target_os = "macos")]
use crate::geometry::{LogicalPoint, LogicalSize};
use crate::geometry::{RectExt, ScreenPoint, ScreenPointF, ScreenRect, WindowPoint};

/// Full hit-test result including peek metadata.
#[derive(Debug, Clone)]
pub struct HitTestResult {
    pub rect: ScreenRect,
    pub title: String,
    pub window_index: usize,
    pub obstructed: bool,
}

/// A window partially obstructed by higher-Z windows. Produced by
/// `WindowWalker::obstructed_windows`, consumed by the background
/// PrintWindow capture phase.
#[derive(Debug, Clone)]
pub struct ObstructedWindow {
    pub window_index: usize,
    #[cfg(windows)]
    capture_ref: WindowCaptureRef,
    #[cfg(target_os = "macos")]
    capture_ref: WindowCaptureRef,
    /// DWM extended frame bounds (true visual bounds).
    pub rect: ScreenRect,
    #[allow(dead_code)]
    pub raw_rect: ScreenRect,
    pub obstruction_rects: Vec<ScreenRect>,
}

#[derive(Debug, Clone, Copy)]
pub struct WindowCaptureRef {
    #[cfg(windows)]
    hwnd: windows::Win32::Foundation::HWND,
    #[cfg(target_os = "macos")]
    window_id: u32,
}

#[cfg(windows)]
impl WindowCaptureRef {
    pub(crate) fn from_hwnd(hwnd: windows::Win32::Foundation::HWND) -> Self {
        Self {
            hwnd,
        }
    }
}

#[cfg(target_os = "macos")]
impl WindowCaptureRef {
    pub(crate) fn from_window_id(window_id: u32) -> Self {
        Self {
            window_id,
        }
    }
}

unsafe impl Send for ObstructedWindow {}
unsafe impl Sync for ObstructedWindow {}

/// A captured window image ready for GPU upload by render workers.
#[derive(Debug)]
pub struct WindowPeekImage {
    pub window_index: usize,
    /// DWM extended frame bounds (true visual bounds).
    pub window_rect: ScreenRect,
    pub bgra: Vec<u8>,
    pub width: u32,
    pub height: u32,
    /// Pixel offset from raw bitmap origin to the visible content.
    /// `crop_x = true_rect.min_x - raw_rect.min_x`
    pub crop_x: i32,
    pub crop_y: i32,
    pub obstruction_rects: Vec<ScreenRect>,
}

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
    /// Human-readable display name (e.g. `\\.\DISPLAY1` on Windows, or
    /// `"Display 1"` on macOS). Used by the Tips & Hotkeys panel to show
    /// "Select monitor '[name]'" entries.
    #[allow(dead_code)]
    pub name: String,
    /// PCI vendor + device IDs of the DXGI adapter driving this monitor.
    /// Used by `bootstrap_window_gpu` to select the correct wgpu adapter
    /// per window, matching the C++ version's per-monitor `AdapterIdx`.
    /// `None` if DXGI enumeration failed (fallback to wgpu's default).
    pub adapter_id: Option<(u32, u32)>,
    /// CG-point origin for this display (macOS only). Used to convert
    /// between the CG logical coordinate space and physical pixels.
    #[cfg(target_os = "macos")]
    pub logical_origin: LogicalPoint,
}

#[allow(dead_code)]
impl MonitorInfo {
    pub fn window_to_screen(&self, pt: WindowPoint) -> ScreenPointF {
        ScreenPointF::new(pt.x + self.bounds.min_x() as f32, pt.y + self.bounds.min_y() as f32)
    }

    pub fn screen_to_window(&self, pt: ScreenPointF) -> WindowPoint {
        WindowPoint::new(pt.x - self.bounds.min_x() as f32, pt.y - self.bounds.min_y() as f32)
    }
}

#[cfg(target_os = "macos")]
impl MonitorInfo {
    pub fn logical_to_screen(&self, pt: LogicalPoint) -> ScreenPoint {
        let s = self.scale_factor as f64;
        ScreenPoint::new(
            self.bounds.min_x() + ((pt.x - self.logical_origin.x) * s).round() as i32,
            self.bounds.min_y() + ((pt.y - self.logical_origin.y) * s).round() as i32,
        )
    }

    pub fn screen_to_logical(&self, pt: ScreenPoint) -> LogicalPoint {
        let s = self.scale_factor as f64;
        LogicalPoint::new(
            self.logical_origin.x + (pt.x - self.bounds.min_x()) as f64 / s,
            self.logical_origin.y + (pt.y - self.bounds.min_y()) as f64 / s,
        )
    }

    pub fn physical_to_logical_size(&self, w: u32, h: u32) -> LogicalSize {
        let s = self.scale_factor as f64;
        LogicalSize::new(w as f64 / s, h as f64 / s)
    }
}

/// Bounding box of all monitor physical rects in virtual-desktop coordinates.
pub fn virtual_desktop_bounds(monitors: &[MonitorInfo]) -> ScreenRect {
    if monitors.is_empty() {
        return ScreenRect::from_xy_size(0, 0, 0, 0);
    }
    let mut min_x = i32::MAX;
    let mut min_y = i32::MAX;
    let mut max_x = i32::MIN;
    let mut max_y = i32::MIN;
    for m in monitors {
        min_x = min_x.min(m.bounds.min_x());
        min_y = min_y.min(m.bounds.min_y());
        max_x = max_x.max(m.bounds.max_x());
        max_y = max_y.max(m.bounds.max_y());
    }
    ScreenRect::from_exact(min_x, min_y, max_x, max_y)
}

/// How a captured cursor image should be composited onto the screen.
///
/// Preserves the raw AND/XOR mask data for monochrome and legacy cursors
/// so the GPU shader can render screen-inverse pixels correctly against
/// the underlying screenshot. Never pre-flatten to a single bitmap.
#[allow(dead_code)]
pub enum CursorImage {
    /// Modern cursor with per-pixel alpha (Windows Aero, all macOS cursors).
    /// Standard premultiplied alpha blending: `out = src + dst * (1 - src_a)`.
    AlphaBlended {
        bgra: Vec<u8>,
        width: u32,
        height: u32,
    },
    /// Legacy/monochrome cursor using AND/XOR compositing.
    /// Per-pixel formula: `output = (screen AND and_mask) XOR xor_color`.
    /// `and_mask_bgra` has each channel 0x00 or 0xFF.
    Masked {
        and_mask_bgra: Vec<u8>,
        xor_color_bgra: Vec<u8>,
        width: u32,
        height: u32,
    },
}


/// OS cursor snapshot captured at screenshot time. Always captured
/// regardless of user toggle — the toggle only controls rendering
/// and inclusion in saved/copied output.
pub struct CapturedCursor {
    pub position: ScreenPoint,
    pub hotspot_x: i32,
    pub hotspot_y: i32,
    pub visible: bool,
    pub image: CursorImage,
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
    #[allow(dead_code)]
    pub monitors: Vec<MonitorInfo>,
    /// OS cursor state at the instant of capture. `None` if cursor
    /// capture failed. The cursor is always captured; the user toggle
    /// controls only whether it is rendered/composited.
    pub cursor: Option<CapturedCursor>,
}

pub struct SystemInterop;

#[cfg(windows)]
impl SystemInterop {
    pub fn get_mouse_position(_monitors: &[MonitorInfo]) -> ScreenPoint {
        win_mouse::get_position()
    }

    pub fn set_mouse_position(pos: ScreenPoint, _monitors: &[MonitorInfo]) {
        win_mouse::set_position(pos)
    }

    pub fn capture_cursor(_monitors: &[MonitorInfo]) -> Option<CapturedCursor> {
        win_cursor::capture_cursor()
    }

    /// Capture the desktop bitmap using pre-enumerated monitors. The
    /// bitmap is a raw BitBlt of the virtual desktop; the monitors are
    /// bundled into the result for downstream consumers.
    pub fn capture_desktop_bitmap(monitors: Vec<MonitorInfo>, cursor: Option<CapturedCursor>) -> CapturedDesktop {
        let vd = virtual_desktop_bounds(&monitors);
        let bitmap = win_capture::capture_desktop(&vd).expect("Unable to capture desktop");
        CapturedDesktop {
            bgra: bitmap.bgra,
            width: bitmap.width,
            height: bitmap.height,
            bounds: bitmap.bounds,
            monitors,
            cursor,
        }
    }

    pub fn all_monitors() -> Vec<MonitorInfo> {
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
                    name: m.name,
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
    /// `visibility_threshold`: minimum visible fraction (0.0–1.0) for a
    /// window to be included. Windows with less visible area are dropped.
    pub fn snapshot_windows(monitors: &[MonitorInfo], visibility_threshold: f32) -> WindowWalker {
        WindowWalker::snapshot(monitors, visibility_threshold)
    }

    pub fn capture_peek_image(window: &ObstructedWindow) -> Option<(Vec<u8>, u32, u32)> {
        win_capture::capture_window_image(window.capture_ref.hwnd, &window.raw_rect)
    }

    pub fn install_pinch_monitor() -> Option<PinchMonitor> {
        None
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

    pub fn get_mouse_position(monitors: &[MonitorInfo]) -> ScreenPoint {
        mac_mouse::get_position(monitors)
    }

    pub fn set_mouse_position(pos: ScreenPoint, monitors: &[MonitorInfo]) {
        mac_mouse::set_position(pos, monitors)
    }

    pub fn capture_cursor(monitors: &[MonitorInfo]) -> Option<CapturedCursor> {
        mac_cursor::capture_cursor(monitors)
    }

    /// Capture the desktop bitmap using pre-enumerated monitors. On
    /// macOS the monitor topology is used to position each display's
    /// capture in the composite buffer.
    pub fn capture_desktop_bitmap(monitors: Vec<MonitorInfo>, cursor: Option<CapturedCursor>) -> CapturedDesktop {
        let bitmap = mac_capture::capture_bitmap(&monitors).expect("Unable to capture desktop");
        let vd = virtual_desktop_bounds(&monitors);
        CapturedDesktop {
            bgra: bitmap.bgra,
            width: bitmap.width,
            height: bitmap.height,
            bounds: vd,
            monitors,
            cursor,
        }
    }

    pub fn all_monitors() -> Vec<MonitorInfo> {
        mac_monitor::all_monitors().expect("Unable to enumerate monitors")
    }

    pub fn snapshot_windows(monitors: &[MonitorInfo], visibility_threshold: f32) -> WindowWalker {
        WindowWalker::snapshot(monitors, visibility_threshold)
    }

    pub fn capture_peek_image(window: &ObstructedWindow) -> Option<(Vec<u8>, u32, u32)> {
        mac_capture::capture_window_image(window.capture_ref.window_id)
    }

    pub fn install_pinch_monitor() -> Option<PinchMonitor> {
        crate::system::install_pinch_monitor()
    }
}

pub struct PinchMonitor {
    #[cfg(target_os = "macos")]
    accum: std::sync::Arc<std::sync::Mutex<f64>>,
    #[cfg(target_os = "macos")]
    _token: objc2::rc::Retained<objc2::runtime::AnyObject>,
}

#[cfg(target_os = "macos")]
fn install_pinch_monitor() -> Option<PinchMonitor> {
    use block2::RcBlock;
    use core::ptr::NonNull;
    use objc2_app_kit::{NSEvent, NSEventMask};
    use std::sync::{Arc, Mutex};

    let accum = Arc::new(Mutex::new(0.0f64));
    let accum_clone = accum.clone();

    let block = RcBlock::new(move |event: NonNull<NSEvent>| -> *mut NSEvent {
        let mag = unsafe { event.as_ref().magnification() };
        if let Ok(mut g) = accum_clone.lock() {
            *g += mag;
        }
        core::ptr::null_mut()
    });

    let token = unsafe { NSEvent::addLocalMonitorForEventsMatchingMask_handler(NSEventMask::Magnify, &block) }?;

    Some(PinchMonitor {
        accum,
        _token: token,
    })
}

impl PinchMonitor {
    #[cfg(target_os = "macos")]
    pub fn drain(&self) -> f64 {
        let mut g = self.accum.lock().unwrap();
        let v = *g;
        *g = 0.0;
        v
    }

    #[cfg(not(target_os = "macos"))]
    pub fn drain(&self) -> f64 {
        0.0
    }
}
