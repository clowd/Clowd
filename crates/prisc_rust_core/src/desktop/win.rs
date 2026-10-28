//! Windows: `EnumWindows`, `EnumDisplayMonitors`, DXGI outputs and the
//! cursor, all in physical virtual-desktop pixels.

use std::collections::HashMap;
use std::ffi::c_void;
use std::mem;
use std::sync::{Mutex, OnceLock};

use windows::core::{w, BOOL, PCWSTR, PWSTR};
use windows::Win32::Foundation::{CloseHandle, HWND, LPARAM, POINT, RECT, TRUE};
use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DWMWA_CLOAKED, DWMWA_EXTENDED_FRAME_BOUNDS, DWMWINDOWATTRIBUTE};
use windows::Win32::Graphics::Dxgi::{CreateDXGIFactory1, IDXGIFactory1};
use windows::Win32::Graphics::Gdi::{
    CreateCompatibleDC, CreateDIBSection, DeleteDC, DeleteObject, EnumDisplayDevicesW, EnumDisplayMonitors, EnumDisplaySettingsW, GdiFlush,
    GetDC, GetDIBits, GetMonitorInfoW, GetObjectW, MonitorFromPoint, MonitorFromWindow, ReleaseDC, SelectObject, BITMAP, BITMAPINFO,
    BITMAPINFOHEADER, BI_RGB, DEVMODEW, DIB_RGB_COLORS, DISPLAY_DEVICEW, DMDO_180, DMDO_270, DMDO_90, ENUM_CURRENT_SETTINGS, HBITMAP, HDC,
    HMONITOR, MONITORINFO, MONITORINFOEXW, MONITOR_DEFAULTTONEAREST, RGBQUAD,
};
use windows::Win32::System::Com::{CoCreateInstance, CoInitializeEx, CLSCTX_ALL, COINIT_APARTMENTTHREADED};
use windows::Win32::System::Threading::{
    GetCurrentProcessId, OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows::Win32::UI::HiDpi::{GetDpiForMonitor, MDT_EFFECTIVE_DPI};
use windows::Win32::UI::Shell::{IVirtualDesktopManager, VirtualDesktopManager};
use windows::Win32::UI::WindowsAndMessaging::{
    CopyIcon, DestroyIcon, DrawIconEx, EnumWindows, FindWindowExW, GetClassNameW, GetCursorInfo, GetCursorPos, GetIconInfo,
    GetWindowLongPtrW, GetWindowRect, GetWindowTextW, GetWindowThreadProcessId, IsIconic, IsWindowVisible, IsZoomed, SetCursorPos,
    CURSORINFO, CURSOR_SHOWING, DI_NORMAL, EDD_GET_DEVICE_INTERFACE_NAME, GWL_EXSTYLE, GWL_STYLE, HCURSOR, HICON, ICONINFO,
    MONITORINFOF_PRIMARY, WS_CAPTION, WS_EX_LAYERED, WS_VISIBLE,
};

use super::{has_alpha, is_shell_window_class, mono_stride, split_mono_planes, IconBitmap, IconPixels, MIN_WINDOW_SIZE};
use crate::geometry::{RectExt, ScreenPoint, ScreenRect};

// ---------------------------------------------------------------------------
// Top-level windows
// ---------------------------------------------------------------------------

/// What [`top_level_windows`] keeps beyond the filter every caller shares.
///
/// Deliberately NOT filtered, for anyone: `WS_EX_TOOLWINDOW`. That is the
/// alt-tab convention and the wrong question here: a floating tool palette
/// (Photoshop panels, Electron/Qt secondary windows, IDE float docks) is
/// plainly visible, so the overlay must be able to select it and the recorder
/// must track it. [`SHELL_WINDOW_CLASSES`](super::SHELL_WINDOW_CLASSES)
/// removes the taskbar and tooltips without that collateral.
#[derive(Debug, Clone, Copy, Default)]
pub struct WindowFilter {
    /// Drop windows that live on another virtual desktop
    /// (`IVirtualDesktopManager`, which initializes COM on the calling
    /// thread). The overlay snapshots once and wants this; the recorder polls
    /// up to once a frame and relies on DWM cloaking alone, which catches
    /// the same windows a poll or two later.
    pub current_virtual_desktop_only: bool,
    /// Drop this process's own windows.
    pub exclude_own_process: bool,
}

/// One real, visible application window, as [`top_level_windows`] reports it.
#[derive(Debug, Clone)]
pub struct TopLevelWindow {
    pub hwnd: HWND,
    pub pid: u32,
    /// The bounds a user would actually point at: the DWM extended frame
    /// bounds, or the monitor work area for a maximized window (see
    /// [`true_bounds`]).
    pub rect: ScreenRect,
    /// `GetWindowRect`, invisible resize border included: the size
    /// `PrintWindow` renders at.
    pub raw_rect: ScreenRect,
    /// `GWL_STYLE`.
    pub style: u32,
    pub title: String,
}

/// Visible application windows, topmost first: `EnumWindows` walks the
/// top-level z-order front to back and the filter preserves that order.
///
/// The filter's membership is load-bearing. `IsWindowVisible && !IsIconic
/// && !cloaked` alone admits a large population of windows that draw nothing
/// a user would call a window: transparent full-screen overlays, popup menus,
/// IME hosts, legacy shims. A one-shot picker shows such an artifact once;
/// the recorder polls up to 60 times a second and would write it to the
/// sidecar hundreds of times.
pub fn top_level_windows(filter: &WindowFilter) -> Vec<TopLevelWindow> {
    let vdm = filter
        .current_virtual_desktop_only
        .then(virtual_desktop_manager)
        .flatten();
    let own_pid = filter
        .exclude_own_process
        .then(|| unsafe { GetCurrentProcessId() });

    let mut hwnds: Vec<HWND> = Vec::new();
    // A failed enumeration (the callback never returns FALSE, so this only
    // fires if the window list changed underneath us) yields whatever was
    // collected: every caller re-reads the world on its next look anyway.
    let _ = unsafe { EnumWindows(Some(collect_hwnd), LPARAM(&mut hwnds as *mut Vec<HWND> as isize)) };

    hwnds
        .into_iter()
        .filter_map(|hwnd| describe_window(hwnd, vdm.as_ref(), own_pid))
        .collect()
}

extern "system" fn collect_hwnd(hwnd: HWND, lparam: LPARAM) -> BOOL {
    // EnumWindows is synchronous, so the Vec behind `lparam` outlives every
    // call.
    unsafe { (*(lparam.0 as *mut Vec<HWND>)).push(hwnd) };
    TRUE
}

/// COM is per-thread and the caller may be on a background thread, hence the
/// initialization here. `None` (with a warning) when the manager cannot be
/// created; the filter then simply admits every desktop's windows.
fn virtual_desktop_manager() -> Option<IVirtualDesktopManager> {
    unsafe {
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        match CoCreateInstance(&VirtualDesktopManager, None, CLSCTX_ALL) {
            Ok(vdm) => Some(vdm),
            Err(e) => {
                log::warn!(
                    "Could not create IVirtualDesktopManager (0x{:08X}: {}): virtual-desktop filtering disabled",
                    e.code().0,
                    e
                );
                None
            }
        }
    }
}

/// Classifies one top-level window, returning `None` for everything that is
/// not a real, visible application window.
fn describe_window(hwnd: HWND, vdm: Option<&IVirtualDesktopManager>, own_pid: Option<u32>) -> Option<TopLevelWindow> {
    unsafe {
        if !IsWindowVisible(hwnd).as_bool() || IsIconic(hwnd).as_bool() {
            return None;
        }

        let style = GetWindowLongPtrW(hwnd, GWL_STYLE) as u32;
        let ex_style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE) as u32;
        if style & WS_VISIBLE.0 == 0 {
            return None;
        }
        // Layered without a caption = a transparent overlay (Discord/Steam/
        // NVIDIA overlays, Rainmeter, magnifiers, annotation tools). These are
        // desktop-sized and topmost, so left in they would sit at the top of
        // the z-order over every real window.
        if style & WS_CAPTION.0 == 0 && ex_style & WS_EX_LAYERED.0 != 0 {
            return None;
        }

        let mut rc = RECT::default();
        GetWindowRect(hwnd, &mut rc).ok()?;
        if rc.right <= rc.left || rc.bottom <= rc.top {
            return None;
        }
        let raw_rect = ScreenRect::from_exact(rc.left, rc.top, rc.right, rc.bottom);

        if let Some(vdm) = vdm {
            // An error reads as "on the current desktop".
            if matches!(vdm.IsWindowOnCurrentVirtualDesktop(hwnd), Ok(on) if !on.as_bool()) {
                return None;
            }
        }

        // Nonzero = cloaked. Absent (DWM composition off) counts as visible.
        // DWM cloaks asynchronously, so a virtual-desktop switch briefly
        // leaves the outgoing desktop's windows uncloaked.
        if dwm_attribute::<u32>(hwnd, DWMWA_CLOAKED).unwrap_or(0) != 0 {
            return None;
        }

        let mut pid: u32 = 0;
        GetWindowThreadProcessId(hwnd, Some(&mut pid));
        if pid == 0 || Some(pid) == own_pid {
            return None;
        }

        let rect = true_bounds(hwnd)?;
        if rect.width() < MIN_WINDOW_SIZE || rect.height() < MIN_WINDOW_SIZE {
            return None;
        }

        let class = window_class(hwnd);
        if is_shell_window_class(&class) {
            return None;
        }

        // Titleless top-level windows are overwhelmingly not windows: popup
        // menus and combo dropdowns (class #32768), `Default IME`,
        // `MSCTFIME UI`, `Chrome Legacy Window`, DDE helpers. Menus are the
        // costly case: they sit at the top of the z-order and come and go
        // constantly.
        let title = window_text(hwnd);
        if title.is_empty() {
            return None;
        }

        if class == "ApplicationFrameWindow" && !has_core_window_child(hwnd) {
            return None;
        }

        Some(TopLevelWindow {
            hwnd,
            pid,
            rect,
            raw_rect,
            style,
            title,
        })
    }
}

/// The bounds a user would actually point at.
///
/// Maximized windows are special-cased to the monitor *work area*: DWM reports
/// a maximized window's extended frame bounds as the full monitor rect, so
/// without this every maximized window overhangs the taskbar. Otherwise the
/// extended frame bounds win over `GetWindowRect`, which includes the
/// invisible resize border (~7 px a side on Win10+). `None` for an empty
/// rect.
pub fn true_bounds(hwnd: HWND) -> Option<ScreenRect> {
    let rc = unsafe {
        let mut work = None;
        if IsZoomed(hwnd).as_bool() {
            let mut mi = MONITORINFO {
                cbSize: mem::size_of::<MONITORINFO>() as u32,
                ..Default::default()
            };
            if GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), &mut mi).as_bool() {
                work = Some(mi.rcWork);
            }
        }
        match work.or_else(|| dwm_attribute::<RECT>(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS)) {
            Some(rc) => rc,
            None => {
                let mut rc = RECT::default();
                GetWindowRect(hwnd, &mut rc).ok()?;
                rc
            }
        }
    };
    let rect = ScreenRect::from_exact(rc.left, rc.top, rc.right, rc.bottom);
    (rect.width() > 0 && rect.height() > 0).then_some(rect)
}

/// Reads a fixed-size `DwmGetWindowAttribute` value; `None` when DWM declines
/// (composition off, or the window is already gone).
fn dwm_attribute<T: Copy + Default>(hwnd: HWND, attribute: DWMWINDOWATTRIBUTE) -> Option<T> {
    let mut value = T::default();
    unsafe { DwmGetWindowAttribute(hwnd, attribute, &mut value as *mut T as *mut c_void, mem::size_of::<T>() as u32) }
        .ok()
        .map(|_| value)
}

pub fn window_text(hwnd: HWND) -> String {
    let mut buf = [0u16; 512];
    let len = unsafe { GetWindowTextW(hwnd, &mut buf) };
    String::from_utf16_lossy(&buf[..len.max(0) as usize])
}

pub fn window_class(hwnd: HWND) -> String {
    let mut buf = [0u16; 256];
    let len = unsafe { GetClassNameW(hwnd, &mut buf) };
    String::from_utf16_lossy(&buf[..len.max(0) as usize])
}

/// True when an `ApplicationFrameWindow` hosts a live UWP app. One whose app
/// has been terminated lingers as an uncloaked, correctly sized phantom; the
/// presence of a `Windows.UI.Core.CoreWindow` child is what separates the
/// two.
fn has_core_window_child(hwnd: HWND) -> bool {
    unsafe { FindWindowExW(Some(hwnd), None, w!("Windows.UI.Core.CoreWindow"), PCWSTR::null()) }.is_ok_and(|child| !child.is_invalid())
}

/// The executable file name (not the path) of process `pid`, or empty when it
/// cannot be resolved.
///
/// Cached per pid: resolving a name costs an `OpenProcess` +
/// `QueryFullProcessImageNameW` round trip, and the recorder asks for every
/// on-screen window up to once a frame. A pid's image name cannot change while
/// the process lives. A recycled pid can outlive its entry and mislabel a
/// window; nothing corrects that within a process, which is acceptable for a
/// display label and is why no identity decision is made on it.
pub fn process_image_name(pid: u32) -> String {
    static CACHE: OnceLock<Mutex<HashMap<u32, String>>> = OnceLock::new();
    let cache = CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    if let Some(name) = cache
        .lock()
        .ok()
        .and_then(|c| c.get(&pid).cloned())
    {
        return name;
    }

    // PROCESS_QUERY_LIMITED_INFORMATION is the right-sized access: it is
    // granted for processes at a higher integrity level (elevated apps), where
    // PROCESS_QUERY_INFORMATION would be denied and every such window would
    // report no name.
    let name = unsafe {
        match OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) {
            Ok(handle) => {
                // Comfortably past MAX_PATH: a long-path executable would
                // otherwise fail the query outright.
                let mut buf = [0u16; 1024];
                let mut len = buf.len() as u32;
                let ok = QueryFullProcessImageNameW(handle, PROCESS_NAME_WIN32, PWSTR(buf.as_mut_ptr()), &mut len).is_ok();
                let _ = CloseHandle(handle);
                if ok {
                    // Just the file name: a full path leaks the user's
                    // install layout to whoever reads it.
                    let full = String::from_utf16_lossy(&buf[..len as usize]);
                    full.rsplit(['\\', '/'])
                        .next()
                        .unwrap_or("")
                        .to_string()
                } else {
                    String::new()
                }
            }
            Err(_) => String::new(),
        }
    };

    if let Ok(mut cache) = cache.lock() {
        cache.insert(pid, name.clone());
    }
    name
}

// ---------------------------------------------------------------------------
// Monitors
// ---------------------------------------------------------------------------

/// One display, as `EnumDisplayMonitors` + `GetMonitorInfoW` report it. The
/// costlier lookups (device path, display mode, DPI) are methods, so callers
/// pay only for what they use.
#[derive(Debug, Clone)]
pub struct Monitor {
    pub handle: HMONITOR,
    /// GDI device name, e.g. `\\.\DISPLAY1`: the key DXGI outputs and
    /// `EnumDisplaySettingsW` are matched on.
    pub device_name: String,
    /// `rcMonitor`.
    pub bounds: ScreenRect,
    /// `rcWork`: the monitor minus the taskbar and docked app bars.
    pub work_area: ScreenRect,
    pub is_primary: bool,
}

/// `EnumDisplaySettingsW`'s current mode for a monitor.
#[derive(Debug, Clone, Copy)]
pub struct DisplayMode {
    /// `dmPosition`: the monitor's origin in the virtual desktop.
    pub origin: ScreenPoint,
    pub width: u32,
    pub height: u32,
    /// `dmDisplayOrientation` in degrees (0, 90, 180 or 270).
    pub rotation: f32,
    pub refresh_hz: f32,
}

impl Monitor {
    /// The stable device interface path (`\\?\DISPLAY#...`) that win-capture
    /// matches monitors on first, or `None` when Windows reports none.
    pub fn device_interface_path(&self) -> Option<String> {
        let mut dd = DISPLAY_DEVICEW {
            cb: mem::size_of::<DISPLAY_DEVICEW>() as u32,
            ..Default::default()
        };
        let name: Vec<u16> = self
            .device_name
            .encode_utf16()
            .chain(Some(0))
            .collect();
        let ok = unsafe { EnumDisplayDevicesW(PCWSTR(name.as_ptr()), 0, &mut dd, EDD_GET_DEVICE_INTERFACE_NAME) };
        let path = wide_to_string(&dd.DeviceID);
        (ok.as_bool() && !path.is_empty()).then_some(path)
    }

    /// The monitor's current display mode, or `None` if Windows will not say.
    pub fn display_mode(&self) -> Option<DisplayMode> {
        let name: Vec<u16> = self
            .device_name
            .encode_utf16()
            .chain(Some(0))
            .collect();
        let mut dm = DEVMODEW {
            dmSize: mem::size_of::<DEVMODEW>() as u16,
            ..Default::default()
        };
        if !unsafe { EnumDisplaySettingsW(PCWSTR(name.as_ptr()), ENUM_CURRENT_SETTINGS, &mut dm) }.as_bool() {
            return None;
        }
        let (position, orientation) = unsafe { (dm.Anonymous1.Anonymous2.dmPosition, dm.Anonymous1.Anonymous2.dmDisplayOrientation) };
        let rotation = match orientation {
            DMDO_90 => 90.0,
            DMDO_180 => 180.0,
            DMDO_270 => 270.0,
            _ => 0.0,
        };
        Some(DisplayMode {
            origin: ScreenPoint::new(position.x, position.y),
            width: dm.dmPelsWidth,
            height: dm.dmPelsHeight,
            rotation,
            refresh_hz: dm.dmDisplayFrequency as f32,
        })
    }

    /// The monitor's effective DPI (96 = 100%), or `None` if
    /// `GetDpiForMonitor` fails.
    pub fn dpi(&self) -> Option<u32> {
        monitor_dpi(self.handle)
    }
}

/// Every connected monitor, in `EnumDisplayMonitors` order. A monitor whose
/// `GetMonitorInfoW` fails is skipped; `Err` only when the enumeration itself
/// fails.
pub fn monitors() -> windows::core::Result<Vec<Monitor>> {
    extern "system" fn collect(hmonitor: HMONITOR, _: HDC, _: *mut RECT, lparam: LPARAM) -> BOOL {
        // EnumDisplayMonitors is synchronous, so the Vec outlives every call.
        unsafe { (*(lparam.0 as *mut Vec<HMONITOR>)).push(hmonitor) };
        TRUE
    }

    let mut handles: Vec<HMONITOR> = Vec::new();
    unsafe { EnumDisplayMonitors(None, None, Some(collect), LPARAM(&mut handles as *mut Vec<HMONITOR> as isize)) }.ok()?;

    Ok(handles
        .into_iter()
        .filter_map(|handle| {
            let mut info = MONITORINFOEXW::default();
            info.monitorInfo.cbSize = mem::size_of::<MONITORINFOEXW>() as u32;
            if !unsafe { GetMonitorInfoW(handle, &mut info as *mut MONITORINFOEXW as *mut MONITORINFO) }.as_bool() {
                return None;
            }
            let (rc, work) = (info.monitorInfo.rcMonitor, info.monitorInfo.rcWork);
            Some(Monitor {
                handle,
                device_name: wide_to_string(&info.szDevice),
                bounds: ScreenRect::from_exact(rc.left, rc.top, rc.right, rc.bottom),
                work_area: ScreenRect::from_exact(work.left, work.top, work.right, work.bottom),
                is_primary: info.monitorInfo.dwFlags & MONITORINFOF_PRIMARY != 0,
            })
        })
        .collect())
}

fn monitor_dpi(handle: HMONITOR) -> Option<u32> {
    let (mut dpi_x, mut dpi_y) = (0u32, 0u32);
    unsafe { GetDpiForMonitor(handle, MDT_EFFECTIVE_DPI, &mut dpi_x, &mut dpi_y) }.ok()?;
    Some(dpi_x)
}

/// The DPI zoom (effective DPI / 96) of the monitor nearest `point`; 1.0 when
/// it cannot be read.
pub fn dpi_scale_at(point: ScreenPoint) -> f64 {
    let hmon = unsafe {
        MonitorFromPoint(
            POINT {
                x: point.x,
                y: point.y,
            },
            MONITOR_DEFAULTTONEAREST,
        )
    };
    if hmon.is_invalid() {
        return 1.0;
    }
    match monitor_dpi(hmon) {
        Some(dpi) if dpi != 0 => dpi as f64 / 96.0,
        _ => 1.0,
    }
}

/// The graphics adapter behind one DXGI output.
#[derive(Debug, Clone, Copy)]
pub struct AdapterInfo {
    pub vendor_id: u32,
    pub device_id: u32,
    pub dedicated_video_memory: u64,
}

/// One monitor output as DXGI enumerates it.
#[derive(Debug, Clone)]
pub struct DxgiOutput {
    /// `EnumAdapters1` index: what libobs-d3d11 and the overlay's GPU
    /// selection index into.
    pub adapter_index: u32,
    /// `None` when the adapter would not describe itself.
    pub adapter: Option<AdapterInfo>,
    /// GDI device name, matching [`Monitor::device_name`].
    pub device_name: String,
}

/// Every DXGI output, adapter by adapter in enumeration order. `None` only
/// when the factory itself cannot be created.
pub fn dxgi_outputs() -> Option<Vec<DxgiOutput>> {
    let factory: IDXGIFactory1 = match unsafe { CreateDXGIFactory1() } {
        Ok(f) => f,
        Err(e) => {
            log::error!("CreateDXGIFactory1 failed: {e:?}");
            return None;
        }
    };
    let mut outputs = Vec::new();
    for adapter_index in 0.. {
        // EnumAdapters1 ends with DXGI_ERROR_NOT_FOUND; any other error is
        // treated the same way (stop, keep what we have).
        let Ok(adapter) = (unsafe { factory.EnumAdapters1(adapter_index) }) else {
            break;
        };
        let info = unsafe { adapter.GetDesc1() }
            .ok()
            .map(|d| AdapterInfo {
                vendor_id: d.VendorId,
                device_id: d.DeviceId,
                dedicated_video_memory: d.DedicatedVideoMemory as u64,
            });
        for output_index in 0.. {
            let Ok(output) = (unsafe { adapter.EnumOutputs(output_index) }) else {
                break;
            };
            let Ok(desc) = (unsafe { output.GetDesc() }) else {
                continue;
            };
            outputs.push(DxgiOutput {
                adapter_index,
                adapter: info,
                device_name: wide_to_string(&desc.DeviceName),
            });
        }
    }
    Some(outputs)
}

fn wide_to_string(buf: &[u16]) -> String {
    let len = buf
        .iter()
        .position(|&c| c == 0)
        .unwrap_or(buf.len());
    String::from_utf16_lossy(&buf[..len])
}

// ---------------------------------------------------------------------------
// Cursor
// ---------------------------------------------------------------------------

pub fn cursor_position() -> Option<ScreenPoint> {
    let mut p = POINT::default();
    unsafe { GetCursorPos(&mut p) }.ok()?;
    Some(ScreenPoint::new(p.x, p.y))
}

/// Moves the cursor; `false` when Windows refused.
pub fn set_cursor_position(point: ScreenPoint) -> bool {
    unsafe { SetCursorPos(point.x, point.y) }.is_ok()
}

/// One `GetCursorInfo` sample: position, visibility and shape come from the
/// same snapshot, so a shape read from `handle` always matches `position`.
#[derive(Debug, Clone, Copy)]
pub struct CursorSnapshot {
    pub position: ScreenPoint,
    /// `CURSOR_SHOWING`.
    pub showing: bool,
    /// The live `HCURSOR`, 0 when there is none. It belongs to the foreground
    /// app, which may destroy it at any moment; [`read_cursor`] copies it
    /// first.
    pub handle: isize,
}

pub fn cursor_snapshot() -> Option<CursorSnapshot> {
    let mut info = CURSORINFO {
        cbSize: mem::size_of::<CURSORINFO>() as u32,
        ..Default::default()
    };
    unsafe { GetCursorInfo(&mut info) }.ok()?;
    Some(CursorSnapshot {
        position: ScreenPoint::new(info.ptScreenPos.x, info.ptScreenPos.y),
        showing: info.flags.0 & CURSOR_SHOWING.0 != 0,
        handle: info.hCursor.0 as isize,
    })
}

/// Reads the pixels of cursor `handle` (an `HCURSOR`, e.g.
/// [`CursorSnapshot::handle`]). The handle is `CopyIcon`'d first: it belongs
/// to the foreground app, which may destroy it at any moment.
///
/// DPI: `GetIconInfo` yields the size the shared handle holds, which is the
/// system cursor size (logon DPI times the accessibility size multiplier).
pub fn read_cursor(handle: isize) -> Option<IconBitmap> {
    if handle == 0 {
        return None;
    }
    let copy = unsafe { CopyIcon(HCURSOR(handle as *mut c_void).into()) }.ok()?;
    let bitmap = read_icon(copy);
    let _ = unsafe { DestroyIcon(copy) };
    bitmap
}

/// Exact-pixel read via `GetIconInfo`. The ICONINFO bitmaps are copies owned
/// by the caller, so both are deleted on every path.
fn read_icon(icon: HICON) -> Option<IconBitmap> {
    unsafe {
        let mut ii = ICONINFO::default();
        GetIconInfo(icon, &mut ii).ok()?;
        let hdc = GetDC(None);
        let bitmap = if hdc.is_invalid() {
            None
        } else {
            let bitmap = read_icon_planes(hdc, icon, &ii);
            ReleaseDC(None, hdc);
            bitmap
        };
        if !ii.hbmMask.is_invalid() {
            let _ = DeleteObject(ii.hbmMask.into());
        }
        if !ii.hbmColor.is_invalid() {
            let _ = DeleteObject(ii.hbmColor.into());
        }
        bitmap
    }
}

unsafe fn read_icon_planes(hdc: HDC, icon: HICON, ii: &ICONINFO) -> Option<IconBitmap> {
    let bitmap = |width: u32, height: u32, pixels: IconPixels| IconBitmap {
        width,
        height,
        hotspot_x: ii.xHotspot as i32,
        hotspot_y: ii.yHotspot as i32,
        pixels,
    };

    if ii.hbmColor.is_invalid() {
        // Mono cursor: hbmMask is double height, the AND plane stacked on top
        // of the XOR plane.
        let (w, full_h) = bitmap_size(ii.hbmMask)?;
        let h = full_h / 2;
        if h == 0 {
            return None;
        }
        let stride = mono_stride(w);
        let mut planes = vec![0u8; stride * full_h as usize];
        if !get_dibits(hdc, ii.hbmMask, w, full_h, 1, &mut planes) {
            return Some(bitmap(w, h, render_both(hdc, icon, w, h)?));
        }
        let (and, xor) = split_mono_planes(&planes, h, stride)?;
        let pixels = IconPixels::Mono {
            and: and.to_vec(),
            xor: xor.to_vec(),
        };
        return Some(bitmap(w, h, pixels));
    }

    let (w, h) = bitmap_size(ii.hbmColor)?;
    let mut bgra = vec![0u8; (w as usize) * (h as usize) * 4];
    if !get_dibits(hdc, ii.hbmColor, w, h, 32, &mut bgra) {
        return Some(bitmap(w, h, render_both(hdc, icon, w, h)?));
    }
    if has_alpha(&bgra) {
        return Some(bitmap(
            w,
            h,
            IconPixels::Color {
                bgra,
                and_mask: None,
            },
        ));
    }
    // Legacy masked color cursor: a single-height AND mask decides pixel
    // ownership.
    let mut and = vec![0u8; mono_stride(w) * h as usize];
    if !get_dibits(hdc, ii.hbmMask, w, h, 1, &mut and) {
        return Some(bitmap(w, h, render_both(hdc, icon, w, h)?));
    }
    Some(bitmap(
        w,
        h,
        IconPixels::Color {
            bgra,
            and_mask: Some(and),
        },
    ))
}

/// Width/height off `GetObjectW`: the only reliable dimension source for a
/// handle whose creator we do not control.
unsafe fn bitmap_size(hbm: HBITMAP) -> Option<(u32, u32)> {
    let mut bm = BITMAP::default();
    let read = GetObjectW(
        hbm.into(),
        mem::size_of::<BITMAP>() as i32,
        Some(&mut bm as *mut BITMAP as *mut c_void),
    );
    if read == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0 {
        return None;
    }
    Some((bm.bmWidth as u32, bm.bmHeight as u32))
}

/// `BITMAPINFO` with room for the 2-entry color table a 1bpp `GetDIBits`
/// writes back (the `windows`-crate struct only reserves one entry).
#[repr(C)]
struct BitmapInfo2 {
    header: BITMAPINFOHEADER,
    colors: [RGBQUAD; 2],
}

fn top_down_info(w: u32, h: u32, bpp: u16) -> BitmapInfo2 {
    BitmapInfo2 {
        header: BITMAPINFOHEADER {
            biSize: mem::size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: w as i32,
            biHeight: -(h as i32),
            biPlanes: 1,
            biBitCount: bpp,
            biCompression: BI_RGB.0,
            ..Default::default()
        },
        colors: [RGBQUAD::default(); 2],
    }
}

/// Reads a bitmap's pixels top-down at the given bit depth into `buf`, which
/// must already be stride x height bytes.
unsafe fn get_dibits(hdc: HDC, hbm: HBITMAP, w: u32, h: u32, bpp: u16, buf: &mut [u8]) -> bool {
    let mut bi = top_down_info(w, h, bpp);
    GetDIBits(
        hdc,
        hbm,
        0,
        h,
        Some(buf.as_mut_ptr() as *mut c_void),
        &mut bi as *mut BitmapInfo2 as *mut BITMAPINFO,
        DIB_RGB_COLORS,
    ) as u32
        == h
}

unsafe fn render_both(hdc: HDC, icon: HICON, w: u32, h: u32) -> Option<IconPixels> {
    Some(IconPixels::Rendered {
        over_black: render_on(hdc, icon, w, h, 0x00)?,
        over_white: render_on(hdc, icon, w, h, 0xFF)?,
    })
}

/// Draws the icon over a solid background (`bg` per channel) into a 32bpp
/// top-down DIB section and returns its BGRA bytes.
unsafe fn render_on(hdc: HDC, icon: HICON, w: u32, h: u32, bg: u8) -> Option<Vec<u8>> {
    let memdc = CreateCompatibleDC(Some(hdc));
    if memdc.is_invalid() {
        return None;
    }
    let result = (|| {
        let bi = top_down_info(w, h, 32);
        let mut bits: *mut c_void = std::ptr::null_mut();
        let dib = CreateDIBSection(
            Some(memdc),
            &bi as *const BitmapInfo2 as *const BITMAPINFO,
            DIB_RGB_COLORS,
            &mut bits,
            None,
            0,
        )
        .ok()?;
        if bits.is_null() {
            let _ = DeleteObject(dib.into());
            return None;
        }
        let len = (w as usize) * (h as usize) * 4;
        let old = SelectObject(memdc, dib.into());
        std::ptr::write_bytes(bits as *mut u8, bg, len);
        let drawn = DrawIconEx(memdc, 0, 0, icon, w as i32, h as i32, 0, None, DI_NORMAL);
        let _ = GdiFlush();
        let out = drawn
            .is_ok()
            .then(|| std::slice::from_raw_parts(bits as *const u8, len).to_vec());
        SelectObject(memdc, old);
        let _ = DeleteObject(dib.into());
        out
    })();
    let _ = DeleteDC(memdc);
    result
}
