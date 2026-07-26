//! Windows platform implementation (DESIGN §2.2).

use std::env;
use std::ffi::CStr;
use std::mem;
use std::path::Path;

use obs::data::ObsData;
use windows_sys::core::BOOL;
use windows_sys::Win32::Foundation::{LPARAM, RECT, TRUE};
use windows_sys::Win32::Graphics::Gdi::{
    EnumDisplayDevicesW, EnumDisplayMonitors, GetMonitorInfoW, DISPLAY_DEVICEW, HDC, HMONITOR,
    MONITORINFO, MONITORINFOEXW,
};
use windows_sys::Win32::System::Threading::ExitProcess;
use windows_sys::Win32::UI::HiDpi::{
    SetProcessDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
};

use super::{MonitorInfo, ObsPaths};

pub const GRAPHICS_MODULE: &CStr = c"libobs-d3d11";
pub const DISPLAY_CAPTURE_ID: &str = "monitor_capture";
pub const AUDIO_OUTPUT_CAPTURE_ID: &str = "wasapi_output_capture";
pub const AUDIO_INPUT_CAPTURE_ID: &str = "wasapi_input_capture";

/// `EDD_GET_DEVICE_INTERFACE_NAME` — request the device interface path in
/// `DISPLAY_DEVICEW.DeviceID`.
const EDD_GET_DEVICE_INTERFACE_NAME: u32 = 0x0000_0001;

/// `MONITORINFOF_PRIMARY` — lives in `Win32_UI_WindowsAndMessaging`, outside
/// the feature set this crate enables; the value is contractual.
const MONITORINFOF_PRIMARY: u32 = 0x0000_0001;

/// Must run before any monitor enumeration so `EnumDisplayMonitors` rects are
/// physical pixels (per-monitor-v2 DPI awareness).
pub fn init_process() {
    unsafe {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
}

pub fn enumerate_monitors() -> Vec<MonitorInfo> {
    unsafe extern "system" fn enum_proc(
        hmonitor: HMONITOR,
        _hdc: HDC,
        _rect: *mut RECT,
        lparam: LPARAM,
    ) -> BOOL {
        let list = &mut *(lparam as *mut Vec<HMONITOR>);
        list.push(hmonitor);
        TRUE
    }

    let mut handles: Vec<HMONITOR> = Vec::new();
    unsafe {
        EnumDisplayMonitors(
            std::ptr::null_mut(),
            std::ptr::null(),
            Some(enum_proc),
            &mut handles as *mut Vec<HMONITOR> as LPARAM,
        );
    }

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
        if ok == 0 {
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
                info.szDevice.as_ptr(),
                0,
                &mut dd,
                EDD_GET_DEVICE_INTERFACE_NAME,
            )
        };

        let (id, alt_id) = if dd_ok != 0 {
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

pub fn display_capture_settings(m: &MonitorInfo, show_cursor: bool) -> ObsData {
    let settings = ObsData::new();
    settings.set_string("monitor_id", &m.id);
    // 2 = WGC. Deliberate deviation from the design's `0` (auto): auto prefers
    // the DXGI duplicator, which was verified to produce black frames on this
    // Win11 26H1 + NVIDIA machine, while WGC captures correctly. Requesting
    // WGC is safe everywhere — win-capture's choose_method() force-falls back
    // to DXGI when WGC is unsupported (duplicator-monitor-capture.c).
    settings.set_int("method", 2);
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
    unsafe {
        ExitProcess(code as u32);
    }
    #[allow(unreachable_code)]
    loop {
        std::thread::park();
    }
}

fn wide_to_string(buf: &[u16]) -> String {
    let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    String::from_utf16_lossy(&buf[..len])
}
