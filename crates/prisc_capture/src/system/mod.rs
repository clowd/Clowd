pub(crate) mod corners;

#[cfg(windows)]
mod win_browser;

#[cfg(windows)]
mod win_corners;

#[cfg(windows)]
pub(crate) mod win_capture;

#[cfg(windows)]
mod win_cursor;

#[cfg(windows)]
mod win_foreground;

#[cfg(windows)]
mod win_walker;

#[cfg(windows)]
pub use win_walker::WindowWalker;

#[cfg(target_os = "macos")]
mod mac_browser;

#[cfg(target_os = "macos")]
mod mac_corners;

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

use prisc_rust_core::desktop;
#[cfg(target_os = "macos")]
use prisc_rust_core::geometry::{LogicalPoint, LogicalSize};
use prisc_rust_core::geometry::{RectExt, ScreenPoint, ScreenPointF, ScreenRect, WindowPoint};

/// What the walker suggests capturing for a point: the rect, plus the
/// corner radius (physical px, 0 = square) the OS composites that window
/// with. The radius is non-zero only when `rect` IS a top-level window's
/// own bounds — a child-window region on Windows has square corners
/// however round its parent is — and only when the walker was told to
/// look (`rounded_corners` in [`WindowWalker::snapshot`]).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WindowTarget {
    pub rect: ScreenRect,
    pub corner_radius: f32,
}

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
    /// "Select monitor '[name]'" entries, and by the debug panel's
    /// identity row.
    pub name: String,
    /// PCI vendor + device IDs of the DXGI adapter driving this monitor.
    /// Used by the render workers (`gxi::Device::create`'s adapter hint)
    /// to select the correct adapter per window, matching the C++
    /// version's per-monitor `AdapterIdx`. `None` if DXGI enumeration
    /// failed (fallback to the backend's default adapter).
    pub adapter_id: Option<(u32, u32)>,
    /// The adapter driving this monitor reports under 1 GB of dedicated
    /// VRAM — the DXGI-enumeration-time stand-in for an integrated-GPU
    /// check, chosen because it is known at ~40 ms (before the peek/blur
    /// jobs spawn), long before the render workers touch the device.
    /// iGPUs report a
    /// small carve-out (Intel: typically 128 MB) regardless of shared
    /// budget; the cosmetic peek feature is disabled on them
    /// (`CaptureSession::new`). Windows-only signal — always `false` on
    /// macOS, so Apple unified memory never trips it. An AMD APU with a
    /// large BIOS carve-out passes as capable, which is fine: it has the
    /// memory the carve-out claims.
    pub low_vram_adapter: bool,
    /// CG-point origin for this display (macOS only). Used to convert
    /// between the CG logical coordinate space and physical pixels.
    #[cfg(target_os = "macos")]
    pub logical_origin: LogicalPoint,
}

#[allow(dead_code)]
impl MonitorInfo {
    pub fn window_to_screen(&self, pt: WindowPoint) -> ScreenPointF {
        prisc_rust_core::geometry::window_to_screen(self.bounds, pt)
    }

    pub fn screen_to_window(&self, pt: ScreenPointF) -> WindowPoint {
        prisc_rust_core::geometry::screen_to_window(self.bounds, pt)
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
    AlphaBlended { bgra: Vec<u8>, width: u32, height: u32 },
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

impl CursorImage {
    /// The overlay's form of a cursor bitmap read off Windows
    /// (`desktop::read_cursor`). `None` for an empty bitmap.
    #[cfg_attr(not(windows), allow(dead_code))]
    fn from_icon(width: u32, height: u32, pixels: desktop::IconPixels) -> Option<Self> {
        if width == 0 || height == 0 {
            return None;
        }
        let (w, h) = (width as usize, height as usize);
        let stride = desktop::mono_stride(width);
        match pixels {
            desktop::IconPixels::Color {
                mut bgra,
                and_mask,
            } => {
                // A legacy masked cursor's color plane carries no alpha: its
                // AND mask decides which pixels are opaque (bit set =
                // transparent).
                if let Some(and) = and_mask {
                    for y in 0..h {
                        for x in 0..w {
                            bgra[(y * w + x) * 4 + 3] = if desktop::mono_bit(&and, stride, x, y) { 0 } else { 0xFF };
                        }
                    }
                }
                Some(CursorImage::AlphaBlended {
                    bgra,
                    width,
                    height,
                })
            }
            desktop::IconPixels::Mono {
                and,
                xor,
            } => {
                let fill = |plane: &[u8]| -> Vec<u8> {
                    let mut out = vec![0u8; w * h * 4];
                    for y in 0..h {
                        for x in 0..w {
                            if desktop::mono_bit(plane, stride, x, y) {
                                out[(y * w + x) * 4..][..4].fill(0xFF);
                            }
                        }
                    }
                    out
                };
                Some(CursorImage::Masked {
                    and_mask_bgra: fill(&and),
                    xor_color_bgra: fill(&xor),
                    width,
                    height,
                })
            }
            desktop::IconPixels::Rendered {
                over_black,
                over_white,
            } => Self::from_renders(width, height, &over_black, &over_white),
        }
    }

    /// The `DrawIconEx` fallback's two renders, over black (`b`) and over
    /// white (`w`). Over black is already the premultiplied color, and
    /// `w - b` is how much background shows through, so an alpha cursor is
    /// `AlphaBlended` with `alpha = 255 - (w - b)`. A channel darker over
    /// white (beyond `DrawIconEx` rounding) means the cursor inverts the
    /// screen, which alpha cannot say, so
    /// such a cursor becomes `Masked`: `(screen AND and) XOR xor` reproduces
    /// both renders exactly with `xor = b` and `and` set wherever they differ.
    fn from_renders(width: u32, height: u32, b: &[u8], w: &[u8]) -> Option<Self> {
        let len = (width as usize) * (height as usize) * 4;
        if b.len() < len || w.len() < len {
            return None;
        }
        let (b, w) = (&b[..len], &w[..len]);
        let inverts = b
            .iter()
            .zip(w)
            .any(|(&b, &w)| (w as i32) < b as i32 - 16);
        if inverts {
            let mut and_mask_bgra = vec![0u8; len];
            let mut xor_color_bgra = b.to_vec();
            for i in (0..len).step_by(4) {
                if b[i..i + 3] != w[i..i + 3] {
                    and_mask_bgra[i..i + 4].fill(0xFF);
                }
                xor_color_bgra[i + 3] = 0xFF;
            }
            return Some(CursorImage::Masked {
                and_mask_bgra,
                xor_color_bgra,
                width,
                height,
            });
        }
        let mut bgra = b.to_vec();
        for i in (0..len).step_by(4) {
            let seen: u32 = (0..3)
                .map(|c| w[i + c].saturating_sub(b[i + c]) as u32)
                .sum();
            bgra[i + 3] = 255 - (seen / 3) as u8;
        }
        Some(CursorImage::AlphaBlended {
            bgra,
            width,
            height,
        })
    }
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

/// Exit codes, re-exported from `prisc_rust_core::exit` so the existing
/// `system::EXIT_*` spelling keeps working. They are defined there because
/// the scrolling-capture driver exits with the same meanings and the shell
/// reads both processes' codes through one table.
pub use prisc_rust_core::exit::{CAPTURE_FAILED as EXIT_CAPTURE_FAILED, NO_SCREEN_PERMISSION as EXIT_NO_SCREEN_PERMISSION};

#[cfg(windows)]
impl SystemInterop {
    /// Windows has no screen-capture permission to ask for.
    pub fn has_screen_recording_permission() -> bool {
        true
    }

    pub fn get_mouse_position(_monitors: &[MonitorInfo]) -> ScreenPoint {
        desktop::cursor_position().unwrap_or_default()
    }

    pub fn set_mouse_position(pos: ScreenPoint, _monitors: &[MonitorInfo]) {
        desktop::set_cursor_position(pos);
    }

    /// Record the shell's pid (`--shell-pid`) for
    /// [`Self::hand_foreground_to_shell`]. Called once during startup.
    pub fn set_shell_pid(pid: Option<u32>) {
        win_foreground::set_shell_pid(pid)
    }

    /// Let the shell that spawned us take the foreground next. Called as a
    /// cycle ends, while the overlay is still the foreground window — see
    /// [`win_foreground`] for why the shell needs it back.
    pub fn hand_foreground_to_shell() {
        win_foreground::hand_to_shell()
    }

    /// Let whoever takes the foreground next have it, rather than naming
    /// the shell. Needed only by the OCR search action, whose browser may
    /// already be running — see [`win_foreground::allow_any_foreground`].
    /// Called while the overlay is still foreground, like its sibling.
    pub fn allow_any_foreground() {
        win_foreground::allow_any_foreground()
    }

    /// Open `url` in the user's default browser. `false` means the shell
    /// refused it and nothing was launched, so the caller still owns the
    /// screen and should stay where it is.
    pub fn open_url(url: &str) -> bool {
        win_browser::open_url(url)
    }

    pub fn capture_cursor(_monitors: &[MonitorInfo]) -> Option<CapturedCursor> {
        win_cursor::capture_cursor()
    }

    /// Capture the desktop bitmap using pre-enumerated monitors. The
    /// bitmap is a raw BitBlt of the virtual desktop; the monitors are
    /// bundled into the result for downstream consumers.
    pub fn capture_desktop_bitmap(monitors: Vec<MonitorInfo>, cursor: Option<CapturedCursor>) -> CapturedDesktop {
        let vd = virtual_desktop_bounds(&monitors);
        // Runs on the screenshot thread: a panic here would leave the main thread
        // blocked on the screenshot latch forever with nothing on screen, so treat
        // failure as fatal for the whole process and let the shell report it.
        let bitmap = match win_capture::capture_desktop(&vd) {
            Ok(bitmap) => bitmap,
            Err(err) => {
                error!("unable to capture the desktop: {err:#}");
                crate::protocol::fail_exit(
                    prisc_ipc::catalog::ErrorCode::Failed,
                    format_args!("unable to capture the desktop: {err:#}"),
                    EXIT_CAPTURE_FAILED,
                );
            }
        };
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
        const LOW_VRAM_BYTES: u64 = 1024 * 1024 * 1024;
        // GDI device name (`\\.\DISPLAY1`) -> the DXGI adapter driving it,
        // which is what the render workers pick their GPU by.
        let adapters: std::collections::HashMap<String, desktop::AdapterInfo> = desktop::dxgi_outputs()
            .unwrap_or_default()
            .into_iter()
            .filter_map(|o| {
                let a = o.adapter?;
                info!(
                    "DXGI output {:?} → adapter vendor=0x{:04X} device=0x{:04X} dedicated_vram={}MB",
                    o.device_name,
                    a.vendor_id,
                    a.device_id,
                    a.dedicated_video_memory / (1024 * 1024)
                );
                Some((o.device_name, a))
            })
            .collect();

        // The shared screen list (desktop::screens, the one Prisc.Ui reads too): rcMonitor bounds,
        // the effective DPI scale (1.0 when unreadable) and the refresh rate, unknown for the
        // 0 / 1 "hardware default" sentinels, where the overlay targets its own 60 Hz.
        desktop::screens::list()
            .expect("Unable to enumerate monitors")
            .into_iter()
            .map(|screen| {
                let adapter = adapters.get(&screen.device_name);
                MonitorInfo {
                    bounds: screen.bounds,
                    scale_factor: screen.scale as f32,
                    is_primary: screen.is_primary,
                    refresh_hz: screen
                        .refresh_hz
                        .map_or(60.0, |hz| hz as f32),
                    name: screen.device_name,
                    adapter_id: adapter.map(|a| (a.vendor_id, a.device_id)),
                    low_vram_adapter: adapter.is_some_and(|a| a.dedicated_video_memory < LOW_VRAM_BYTES),
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
    /// `rounded_corners`: whether to resolve each window's corner radius
    /// (see [`WindowTarget`]); off = every target is square.
    pub fn snapshot_windows(monitors: &[MonitorInfo], visibility_threshold: f32, rounded_corners: bool) -> WindowWalker {
        WindowWalker::snapshot(monitors, visibility_threshold, rounded_corners)
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

    pub fn has_screen_recording_permission() -> bool {
        mac_capture::has_screen_recording_permission()
    }

    pub fn get_mouse_position(monitors: &[MonitorInfo]) -> ScreenPoint {
        mac_mouse::get_position(monitors)
    }

    pub fn set_mouse_position(pos: ScreenPoint, monitors: &[MonitorInfo]) {
        mac_mouse::set_position(pos, monitors)
    }

    /// No foreground lock on macOS, so there is nothing to hand back and
    /// nobody to hand it to: activation is the app's own business.
    pub fn set_shell_pid(_pid: Option<u32>) {}

    /// See [`Self::set_shell_pid`].
    pub fn hand_foreground_to_shell() {}

    /// See [`Self::set_shell_pid`] — there is no foreground lock to hand
    /// out, so a launched browser comes forward on its own. Kept as a
    /// no-op rather than cfg'ing the call site, matching
    /// [`Self::hand_foreground_to_shell`].
    pub fn allow_any_foreground() {}

    /// Open `url` in the user's default browser. `false` means nothing was
    /// launched.
    pub fn open_url(url: &str) -> bool {
        mac_browser::open_url(url)
    }

    pub fn capture_cursor(monitors: &[MonitorInfo]) -> Option<CapturedCursor> {
        mac_cursor::capture_cursor(monitors)
    }

    /// Capture the desktop bitmap using pre-enumerated monitors. On
    /// macOS the monitor topology is used to position each display's
    /// capture in the composite buffer.
    pub fn capture_desktop_bitmap(monitors: Vec<MonitorInfo>, cursor: Option<CapturedCursor>) -> CapturedDesktop {
        // Runs on the screenshot thread: a panic here would leave the main thread
        // blocked on the screenshot latch forever with nothing on screen, so treat
        // failure as fatal for the whole process and let the shell report it.
        let bitmap = match mac_capture::capture_bitmap(&monitors) {
            Ok(bitmap) => bitmap,
            Err(err) => {
                error!("unable to capture the desktop: {err:#}");
                crate::protocol::fail_exit(
                    prisc_ipc::catalog::ErrorCode::Failed,
                    format_args!("unable to capture the desktop: {err:#}"),
                    EXIT_CAPTURE_FAILED,
                );
            }
        };
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

    pub fn snapshot_windows(monitors: &[MonitorInfo], visibility_threshold: f32, rounded_corners: bool) -> WindowWalker {
        WindowWalker::snapshot(monitors, visibility_threshold, rounded_corners)
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

/// Process-wide scheduling posture, called once early in main: high
/// priority class + high GPU scheduler class + MMCSS-scheduled DWM
/// composition on Windows; a latency-critical activity assertion (no App
/// Nap, no timer coalescing) on macOS.
/// The v3 C++ capturer shipped exactly this
/// (`SetPriorityClass(HIGH_PRIORITY_CLASS)` in `DxScreenCapture`'s
/// constructor) for years: a capture overlay is a short-lived, fullscreen,
/// latency-critical process, and while it is on screen it IS the
/// foreground experience. Base priority for our normal threads becomes 13
/// (vs 8), so even un-tiered threads outrank every other app.
///
/// Consequence to keep in mind: `lower_thread_priority`'s BELOW_NORMAL
/// inside the high class still lands ABOVE other processes' normal
/// threads — deliberate; it only has to yield to OUR render and event
/// threads. Work that must also yield to the rest of the system (and to
/// the disk) uses `background_thread_priority`, which drops out of the
/// class entirely.
pub fn raise_process_priority_class() {
    #[cfg(windows)]
    unsafe {
        use windows::Wdk::Graphics::Direct3D::{D3DKMTSetProcessSchedulingPriorityClass, D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH};
        use windows::Win32::Graphics::Dwm::DwmEnableMMCSS;
        use windows::Win32::System::Threading::{GetCurrentProcess, SetPriorityClass, HIGH_PRIORITY_CLASS};
        // Best-effort throughout: a failed raise just means default scheduling.
        let _ = SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
        // The GPU node is scheduled separately from the CPU: this raises our
        // command submissions over other processes' at the graphics
        // scheduler (DWM itself runs there at REALTIME). Cold starts JIT
        // shaders from the build threads on the same GPU our render workers
        // are presenting on — exactly the contention this settles. HIGH is
        // the strongest class that needs no privilege (REALTIME wants
        // SeIncreaseBasePriorityPrivilege).
        let _ = D3DKMTSetProcessSchedulingPriorityClass(GetCurrentProcess(), D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH);
        // Ask DWM to schedule its composition work for this process's
        // windows through MMCSS — the acquire path (frame-latency waitable)
        // is only as smooth as the compositor consuming our presents.
        let _ = DwmEnableMMCSS(true);
    }
    #[cfg(target_os = "macos")]
    {
        // Process-wide activity assertion: never App-Nap this process and
        // never coalesce its timers (`UserInteractive` = UserInitiated +
        // LatencyCritical; it also holds off idle system sleep, which is
        // what a user mid-capture wants anyway). The token object ends the
        // assertion when released, so it is deliberately leaked — the
        // assertion's scope IS the process lifetime.
        use objc2_foundation::{NSActivityOptions, NSProcessInfo, NSString};
        let token = NSProcessInfo::processInfo()
            .beginActivityWithOptions_reason(NSActivityOptions::UserInteractive, &NSString::from_str("screen capture overlay"));
        std::mem::forget(token);
    }
}

/// Drop the CALLING thread to the process's below-normal scheduling
/// priority — the middle "utility" tier, for CPU-heavy deferred work
/// (shader compiles, font shaping, SVG parses) that should lose contested
/// cores to the render workers but must NOT crawl: on a cold start the UI
/// chrome is waiting on it.
///
/// Windows does not inherit thread priority on spawn, so every spawned
/// thread — scoped ones included — has to call this itself at the top of
/// its closure. For disk-bound work see `background_thread_priority`,
/// which also drops I/O and memory priority.
pub fn lower_thread_priority() {
    #[cfg(windows)]
    unsafe {
        use windows::Win32::System::Threading::{GetCurrentThread, SetThreadPriority, THREAD_PRIORITY_BELOW_NORMAL};
        // Best-effort: a failed priority drop just means default scheduling.
        let _ = SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
    }
    #[cfg(target_os = "macos")]
    unsafe {
        // Utility, not Background: Background is E-core-jailed on Apple
        // Silicon and would visibly delay the deferred UI build.
        let _ = libc::pthread_set_qos_class_self_np(libc::qos_class_t::QOS_CLASS_UTILITY, 0);
    }
}

/// Drop the CALLING thread to true background scheduling — the lowest
/// tier, for disk-bound work nobody is waiting on (the system-font scan,
/// peek captures). The cold-start freeze was as much disk contention
/// (page-cache-evicted files, on-access AV scanning) as CPU, and plain
/// priority drops do not touch I/O scheduling.
///
/// Windows: `THREAD_MODE_BACKGROUND_BEGIN` — lowest CPU priority AND very
/// low I/O priority AND low memory priority, regardless of the process's
/// priority class — plus opting the thread INTO power throttling (EcoQoS),
/// which parks it on E-cores on hybrid CPUs. Never reverted: every caller
/// is a thread that exits when its one job is done.
///
/// macOS: `QOS_CLASS_BACKGROUND` — the direct analog (E-cores + low I/O
/// priority).
pub fn background_thread_priority() {
    #[cfg(windows)]
    unsafe {
        use windows::Win32::System::Threading::{
            GetCurrentThread, SetThreadInformation, SetThreadPriority, ThreadPowerThrottling, THREAD_MODE_BACKGROUND_BEGIN,
            THREAD_POWER_THROTTLING_CURRENT_VERSION, THREAD_POWER_THROTTLING_EXECUTION_SPEED, THREAD_POWER_THROTTLING_STATE,
        };
        // Best-effort throughout: a failed drop just means default scheduling.
        let _ = SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);
        let throttle = THREAD_POWER_THROTTLING_STATE {
            Version: THREAD_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask: THREAD_POWER_THROTTLING_EXECUTION_SPEED,
            // Mask set in ControlMask + set in StateMask = throttling ON.
            StateMask: THREAD_POWER_THROTTLING_EXECUTION_SPEED,
        };
        let _ = SetThreadInformation(
            GetCurrentThread(),
            ThreadPowerThrottling,
            (&throttle as *const THREAD_POWER_THROTTLING_STATE).cast(),
            std::mem::size_of::<THREAD_POWER_THROTTLING_STATE>() as u32,
        );
    }
    #[cfg(target_os = "macos")]
    unsafe {
        let _ = libc::pthread_set_qos_class_self_np(libc::qos_class_t::QOS_CLASS_BACKGROUND, 0);
    }
}

/// Raise the CALLING thread to the render tier: it paints a monitor every
/// vsync and must win every contested core, including against background
/// work whose priority is out of our hands (libblur's pool).
///
/// Windows, all three levers (the v3 C++ capturer used TIME_CRITICAL and
/// the high priority class; MMCSS and EcoQoS did not exist yet):
/// * `THREAD_PRIORITY_TIME_CRITICAL` — base 15 in the normal class, and
///   with `raise_process_priority_class` still bounded (this is priority
///   saturation within the class, not the realtime class).
/// * MMCSS registration as a "Games" task — the scheduler DWM and game
///   engines use: periodic boosts into the realtime band (16-26) with
///   MMCSS's own anti-starvation. The handle is deliberately never
///   reverted; the registration is for the thread's whole life.
/// * Power throttling OFF — the scheduler may never park this thread on
///   an E-core or dial its clocks down (EcoQoS).
///
/// macOS: `QOS_CLASS_USER_INTERACTIVE`, the top QoS band (P-cores).
pub fn raise_render_thread_priority() {
    #[cfg(windows)]
    unsafe {
        use windows::Win32::System::Threading::{
            AvSetMmThreadCharacteristicsW, AvSetMmThreadPriority, GetCurrentThread, SetThreadInformation, SetThreadPriority,
            ThreadPowerThrottling, AVRT_PRIORITY_CRITICAL, THREAD_POWER_THROTTLING_CURRENT_VERSION,
            THREAD_POWER_THROTTLING_EXECUTION_SPEED, THREAD_POWER_THROTTLING_STATE, THREAD_PRIORITY_TIME_CRITICAL,
        };
        // Best-effort throughout: a failed raise just means default scheduling.
        let _ = SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
        let mut task_index = 0u32;
        match AvSetMmThreadCharacteristicsW(windows::core::w!("Games"), &mut task_index) {
            // CRITICAL within the task class = the top of the band MMCSS
            // gives "Games" tasks. The handle is valid for the thread's
            // whole life (never reverted), so it is not kept.
            Ok(handle) => {
                let _ = AvSetMmThreadPriority(handle, AVRT_PRIORITY_CRITICAL);
            }
            // Non-fatal (MMCSS service disabled, registry key missing) but
            // worth a line: this thread runs without the multimedia boosts.
            Err(e) => log::info!("MMCSS registration failed: {e}"),
        }
        let throttle = THREAD_POWER_THROTTLING_STATE {
            Version: THREAD_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask: THREAD_POWER_THROTTLING_EXECUTION_SPEED,
            // Mask set in ControlMask + clear in StateMask = throttling OFF.
            StateMask: 0,
        };
        let _ = SetThreadInformation(
            GetCurrentThread(),
            ThreadPowerThrottling,
            (&throttle as *const THREAD_POWER_THROTTLING_STATE).cast(),
            std::mem::size_of::<THREAD_POWER_THROTTLING_STATE>() as u32,
        );
    }
    #[cfg(target_os = "macos")]
    unsafe {
        let _ = libc::pthread_set_qos_class_self_np(libc::qos_class_t::QOS_CLASS_USER_INTERACTIVE, 0);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn pixels(image: &CursorImage) -> (&[u8], Option<&[u8]>) {
        match image {
            CursorImage::AlphaBlended {
                bgra,
                ..
            } => (bgra, None),
            CursorImage::Masked {
                and_mask_bgra,
                xor_color_bgra,
                ..
            } => (and_mask_bgra, Some(xor_color_bgra)),
        }
    }

    #[test]
    fn an_alpha_cursor_is_blended_as_read() {
        let bgra = vec![1, 2, 3, 200, 4, 5, 6, 0];
        let image = CursorImage::from_icon(
            2,
            1,
            desktop::IconPixels::Color {
                bgra: bgra.clone(),
                and_mask: None,
            },
        )
        .unwrap();
        assert_eq!(pixels(&image), (&bgra[..], None));
    }

    #[test]
    fn a_masked_color_cursor_takes_its_alpha_from_the_and_mask() {
        // Two pixels: AND=0 (opaque), AND=1 (transparent).
        let mut and = vec![0u8; desktop::mono_stride(2)];
        and[0] = 0b0100_0000;
        let image = CursorImage::from_icon(
            2,
            1,
            desktop::IconPixels::Color {
                bgra: vec![1, 2, 3, 0, 4, 5, 6, 0],
                and_mask: Some(and),
            },
        )
        .unwrap();
        assert_eq!(pixels(&image).0, &[1, 2, 3, 0xFF, 4, 5, 6, 0][..]);
    }

    #[test]
    fn a_mono_cursor_keeps_its_and_and_xor_planes() {
        // 33 px wide, so the DWORD-aligned stride (8 bytes) differs from the
        // packed row (5 bytes); pixel 32 sits in the fifth byte.
        let stride = desktop::mono_stride(33);
        let mut and = vec![0u8; stride];
        let mut xor = vec![0u8; stride];
        and[4] = 0x80;
        xor[0] = 0x80;
        let image = CursorImage::from_icon(
            33,
            1,
            desktop::IconPixels::Mono {
                and,
                xor,
            },
        )
        .unwrap();
        let (and, xor) = pixels(&image);
        let xor = xor.unwrap();
        assert_eq!(&and[32 * 4..33 * 4], &[0xFF; 4]);
        assert_eq!(&and[..4], &[0; 4]);
        assert_eq!(&xor[..4], &[0xFF; 4]);
        assert_eq!(&xor[32 * 4..33 * 4], &[0; 4]);
    }

    #[test]
    fn an_alpha_cursor_is_recovered_from_its_two_renders() {
        // Opaque red, half-transparent black, fully transparent.
        let over_black = vec![0, 0, 200, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        let over_white = vec![0, 0, 200, 0, 128, 128, 128, 0, 255, 255, 255, 0];
        let image = CursorImage::from_icon(
            3,
            1,
            desktop::IconPixels::Rendered {
                over_black,
                over_white,
            },
        )
        .unwrap();
        assert_eq!(pixels(&image), (&[0, 0, 200, 255, 0, 0, 0, 127, 0, 0, 0, 0][..], None));
    }

    #[test]
    fn an_inverting_cursor_is_recovered_from_its_two_renders_as_and_xor() {
        // Opaque blue, transparent, screen invert.
        let over_black = vec![200, 0, 0, 0, 0, 0, 0, 0, 255, 255, 255, 0];
        let over_white = vec![200, 0, 0, 0, 255, 255, 255, 0, 0, 0, 0, 0];
        let image = CursorImage::from_icon(
            3,
            1,
            desktop::IconPixels::Rendered {
                over_black: over_black.clone(),
                over_white: over_white.clone(),
            },
        )
        .unwrap();
        let (and, xor) = pixels(&image);
        let xor = xor.unwrap();
        assert_eq!(and, &[0, 0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255][..]);
        assert_eq!(xor, &[200, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 255][..]);
        // The AND/XOR form reproduces both renders.
        for (bg, render) in [(0u8, &over_black), (255u8, &over_white)] {
            for i in (0..12).step_by(4) {
                for c in 0..3 {
                    assert_eq!((bg & and[i + c]) ^ xor[i + c], render[i + c]);
                }
            }
        }
    }

    #[test]
    fn short_renders_and_empty_bitmaps_are_not_composited() {
        let rendered = desktop::IconPixels::Rendered {
            over_black: vec![0; 4],
            over_white: vec![0; 4],
        };
        assert!(CursorImage::from_icon(2, 1, rendered).is_none());
        assert!(CursorImage::from_icon(
            0,
            1,
            desktop::IconPixels::Mono {
                and: vec![],
                xor: vec![]
            }
        )
        .is_none());
    }
}
