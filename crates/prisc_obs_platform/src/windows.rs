//! Windows platform implementation (DESIGN §2.2).

use std::env;
use std::ffi::CStr;
use std::mem;
use std::path::Path;

use obs::data::ObsData;
use windows::core::{BOOL, PCWSTR};
use windows::Win32::Foundation::{LPARAM, POINT, RECT, TRUE};
use windows::Win32::Graphics::Dxgi::{CreateDXGIFactory1, IDXGIFactory1};
use windows::Win32::Graphics::Gdi::{
    EnumDisplayDevicesW, EnumDisplayMonitors, GetMonitorInfoW, MonitorFromPoint, DISPLAY_DEVICEW,
    HDC, HMONITOR, MONITORINFO, MONITORINFOEXW, MONITOR_DEFAULTTONEAREST,
};
use windows::Win32::System::Threading::ExitProcess;
use windows::Win32::UI::HiDpi::{
    GetDpiForMonitor, SetProcessDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
    MDT_DEFAULT,
};
use windows::Win32::UI::WindowsAndMessaging::{EDD_GET_DEVICE_INTERFACE_NAME, MONITORINFOF_PRIMARY};

use super::region::{self, Rect, RegionPlan};
use super::{CaptureMethod, MonitorInfo, ObsPaths};

/// `platform` field of the input-capture header (wire contract).
pub const PLATFORM_NAME: &str = "windows";

pub const GRAPHICS_MODULE: &CStr = c"libobs-d3d11";
pub const DISPLAY_CAPTURE_ID: &str = "monitor_capture";

/// Must run before any monitor enumeration so `EnumDisplayMonitors` rects are
/// physical pixels (per-monitor-v2 DPI awareness).
pub fn init_process() {
    // Failure (already set, or pre-1703 Windows) was always ignored; the
    // windows crate wraps the raw BOOL in a #[must_use] Result, so ignore it
    // explicitly.
    let _ = unsafe { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
}

pub fn enumerate_monitors() -> Vec<MonitorInfo> {
    unsafe extern "system" fn enum_proc(
        hmonitor: HMONITOR,
        _hdc: HDC,
        _rect: *mut RECT,
        lparam: LPARAM,
    ) -> BOOL {
        let list = &mut *(lparam.0 as *mut Vec<HMONITOR>);
        list.push(hmonitor);
        TRUE
    }

    let mut handles: Vec<HMONITOR> = Vec::new();
    // Return BOOL is #[must_use] in the windows crate; a failed enumeration
    // simply yields an empty list, as before.
    let _ = unsafe {
        EnumDisplayMonitors(
            None,
            None,
            Some(enum_proc),
            LPARAM(&mut handles as *mut Vec<HMONITOR> as isize),
        )
    };

    let mut monitors = Vec::new();
    for hmonitor in handles {
        let mut info: MONITORINFOEXW = unsafe { mem::zeroed() };
        info.monitorInfo.cbSize = mem::size_of::<MONITORINFOEXW>() as u32;
        let ok = unsafe {
            GetMonitorInfoW(
                hmonitor,
                &mut info as *mut MONITORINFOEXW as *mut MONITORINFO,
            )
        };
        if !ok.as_bool() {
            continue;
        }

        let device_name = wide_to_string(&info.szDevice);
        let rc = info.monitorInfo.rcMonitor;

        // Resolve the stable device interface path; mirrors win-capture, which
        // matches DeviceID first and falls back to szDevice.
        let mut dd: DISPLAY_DEVICEW = unsafe { mem::zeroed() };
        dd.cb = mem::size_of::<DISPLAY_DEVICEW>() as u32;
        let dd_ok = unsafe {
            EnumDisplayDevicesW(
                PCWSTR(info.szDevice.as_ptr()),
                0,
                &mut dd,
                EDD_GET_DEVICE_INTERFACE_NAME,
            )
        };

        let (id, alt_id) = if dd_ok.as_bool() {
            let device_id = wide_to_string(&dd.DeviceID);
            if device_id.is_empty() {
                (device_name.clone(), None)
            } else {
                (device_id, Some(device_name.clone()))
            }
        } else {
            (device_name.clone(), None)
        };

        monitors.push(MonitorInfo {
            id,
            alt_id,
            x: rc.left,
            y: rc.top,
            width: (rc.right - rc.left).max(0) as u32,
            height: (rc.bottom - rc.top).max(0) as u32,
            scale: 1.0,
            is_primary: info.monitorInfo.dwFlags & MONITORINFOF_PRIMARY != 0,
        });
    }
    monitors
}

pub fn find_monitor(id: &str) -> Option<MonitorInfo> {
    super::match_monitor(id, &enumerate_monitors())
}

/// The display's DPI zoom (dpi/96) — the input-capture header's per-monitor
/// `scale`, which the editor uses to size themed cursors like the OS does.
/// Distinct from `MonitorInfo::scale` (capture px per coordinate unit, always
/// 1.0 on Windows).
pub fn monitor_display_scale(m: &MonitorInfo) -> f64 {
    let center = POINT {
        x: m.x + (m.width as i32 / 2),
        y: m.y + (m.height as i32 / 2),
    };
    let hmon = unsafe { MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST) };
    if hmon.is_invalid() {
        return 1.0;
    }
    let mut dpi_x: u32 = 96;
    let mut dpi_y: u32 = 96;
    // The windows crate folds the raw HRESULT into a Result (failed HRESULT ->
    // Err), so is_err() is the old `hr < 0` check.
    let hr = unsafe { GetDpiForMonitor(hmon, MDT_DEFAULT, &mut dpi_x, &mut dpi_y) };
    if hr.is_err() || dpi_x == 0 {
        return 1.0;
    }
    dpi_x as f64 / 96.0
}

/// The graphics-adapter index that drives the display the region mostly sits
/// on, ready for `obs_video_info.adapter`. `None` when no planned monitor
/// could be matched to an adapter output, in which case callers keep libobs's
/// default of 0.
///
/// This matters most for `CaptureMethod::Dxgi`: desktop duplication resolves a
/// monitor by walking the *current device's* adapter outputs
/// (`device_duplicator_get_monitor_index` in
/// obs-studio/libobs-d3d11/d3d11-duplicator.cpp), so a monitor hanging off a
/// second GPU is simply not found while libobs runs on adapter 0 — the
/// duplicator never starts and the recording stays black. WGC has no such
/// constraint, but capturing on the GPU that already owns the surface avoids a
/// cross-adapter copy per frame either way, so the index is applied
/// unconditionally.
///
/// The index space is `CreateDXGIFactory1` + `EnumAdapters1` order, which is
/// exactly what libobs-d3d11's `gs_device::InitAdapter` indexes into.
///
/// A region spanning two GPUs can only pick one: the most-covered display
/// wins, and under DXGI the displays on the other adapter will not capture.
pub fn region_adapter_index(
    region: Rect,
    plan: &RegionPlan,
    monitors: &[MonitorInfo],
) -> Option<u32> {
    let ranked = region::monitors_by_coverage(region, plan, monitors);
    let adapters = adapter_outputs()?;
    for index in ranked {
        // GDI device name (`\.\DISPLAY1`) is the join key: it is both
        // MonitorInfo::alt_id and DXGI_OUTPUT_DESC::DeviceName.
        let Some(name) = monitors.get(index).and_then(|m| m.alt_id.as_deref()) else {
            continue;
        };
        if let Some((adapter, _)) = adapters.iter().find(|(_, output)| output == name) {
            return Some(*adapter);
        }
    }
    None
}

/// Every (adapter index, GDI device name) pair DXGI reports, in enumeration
/// order. `None` only when the factory itself cannot be created.
fn adapter_outputs() -> Option<Vec<(u32, String)>> {
    let factory: IDXGIFactory1 = unsafe { CreateDXGIFactory1() }.ok()?;
    let mut pairs = Vec::new();
    for adapter_index in 0.. {
        // EnumAdapters1 ends with DXGI_ERROR_NOT_FOUND; any other error is
        // treated the same way (stop, keep what we have).
        let Ok(adapter) = (unsafe { factory.EnumAdapters1(adapter_index) }) else {
            break;
        };
        for output_index in 0.. {
            let Ok(output) = (unsafe { adapter.EnumOutputs(output_index) }) else {
                break;
            };
            let Ok(desc) = (unsafe { output.GetDesc() }) else {
                continue;
            };
            let len = desc
                .DeviceName
                .iter()
                .position(|&c| c == 0)
                .unwrap_or(desc.DeviceName.len());
            pairs.push((
                adapter_index,
                String::from_utf16_lossy(&desc.DeviceName[..len]),
            ));
        }
    }
    Some(pairs)
}

pub fn display_capture_settings(
    m: &MonitorInfo,
    show_cursor: bool,
    method: CaptureMethod,
) -> ObsData {
    let settings = ObsData::new();
    settings.set_string("monitor_id", &m.id);
    // Deliberate deviation from the design's `0` (auto), which is why the
    // default here is WGC rather than auto: auto prefers the DXGI duplicator,
    // which was verified to produce black frames on this Win11 26H1 + NVIDIA
    // machine, while WGC captures correctly. Requesting WGC is safe
    // everywhere — win-capture's choose_method() force-falls back to DXGI when
    // WGC is unsupported (duplicator-monitor-capture.c).
    settings.set_int("method", method.as_obs_method());
    settings.set_bool("capture_cursor", show_cursor);
    settings
}

/// Partial `obs_source_update` payload toggling cursor capture on an existing
/// display source (win-capture applies it live).
pub fn cursor_update_settings(show_cursor: bool) -> ObsData {
    let settings = ObsData::new();
    settings.set_bool("capture_cursor", show_cursor);
    settings
}

pub fn default_obs_paths(exe_dir: &Path) -> ObsPaths {
    let module_bin = env::var("OBS_PLUGIN_PATH").unwrap_or_else(|_| {
        exe_dir
            .join("obs-plugins")
            .join("64bit")
            .to_string_lossy()
            .into_owned()
    });
    let module_data_base = env::var("OBS_PLUGIN_DATA_PATH").unwrap_or_else(|_| {
        exe_dir
            .join("data")
            .join("obs-plugins")
            .to_string_lossy()
            .into_owned()
    });
    let libobs_data = env::var("OBS_DATA_PATH")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|_| exe_dir.join("data").join("libobs"));
    ObsPaths {
        module_bin,
        // The %module% suffix is appended internally (§1.6).
        module_data: format!("{module_data_base}/%module%"),
        libobs_data: Some(libobs_data),
    }
}

pub fn exit_process(code: i32) -> ! {
    // The windows crate declares ExitProcess `-> !`, so no unreachable
    // park-loop fallback is needed (windows-sys declared it `-> ()`).
    unsafe { ExitProcess(code as u32) }
}

fn wide_to_string(buf: &[u16]) -> String {
    let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    String::from_utf16_lossy(&buf[..len])
}
