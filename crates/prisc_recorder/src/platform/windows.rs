//! Windows platform implementation (DESIGN §2.2).

use std::env;
use std::ffi::CStr;
use std::mem;
use std::path::Path;

use obs::data::ObsData;
use windows_sys::core::BOOL;
use windows_sys::Win32::Foundation::{LPARAM, POINT, RECT, TRUE};
use windows_sys::Win32::Graphics::Gdi::{
    EnumDisplayDevicesW, EnumDisplayMonitors, GetMonitorInfoW, MonitorFromPoint, DISPLAY_DEVICEW,
    HDC, HMONITOR, MONITORINFO, MONITORINFOEXW, MONITOR_DEFAULTTONEAREST,
};
use windows_sys::Win32::System::Threading::ExitProcess;
use windows_sys::Win32::UI::HiDpi::{
    GetDpiForMonitor, SetProcessDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
    MDT_DEFAULT,
};
use windows_sys::Win32::UI::Input::KeyboardAndMouse::{GetAsyncKeyState, VK_LBUTTON, VK_RBUTTON};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    GetCursorInfo, GetCursorPos, LoadCursorW, CURSORINFO, CURSOR_SHOWING, IDC_APPSTARTING,
    IDC_ARROW, IDC_CROSS, IDC_HAND, IDC_HELP, IDC_IBEAM, IDC_NO, IDC_PERSON, IDC_PIN, IDC_SIZEALL,
    IDC_SIZENESW, IDC_SIZENS, IDC_SIZENWSE, IDC_SIZEWE, IDC_UPARROW, IDC_WAIT,
    MONITORINFOF_PRIMARY,
};

use super::{CursorKind, CursorState, MonitorInfo, MouseInfo, ObsPaths};

/// `platform` field of the input-capture header (wire contract).
pub const PLATFORM_NAME: &str = "windows";

pub const GRAPHICS_MODULE: &CStr = c"libobs-d3d11";
pub const DISPLAY_CAPTURE_ID: &str = "monitor_capture";
pub const AUDIO_INPUT_CAPTURE_ID: &str = "wasapi_input_capture";
/// Webcam capture source (`--webcam` / `--list-cameras`): DirectShow.
pub const WEBCAM_SOURCE_ID: &str = "dshow_input";
/// The `WEBCAM_SOURCE_ID` settings key (and property) holding the device id.
pub const WEBCAM_DEVICE_KEY: &str = "video_device_id";

/// `EDD_GET_DEVICE_INTERFACE_NAME` — request the device interface path in
/// `DISPLAY_DEVICEW.DeviceID`.
const EDD_GET_DEVICE_INTERFACE_NAME: u32 = 0x0000_0001;

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

/// Cursor position (physical px — the process is per-monitor-v2 DPI aware),
/// button state, and the DPI zoom of the monitor under the cursor.
///
/// `GetAsyncKeyState`'s high bit is the *current* physical down state, so a
/// click is caught as long as the button is still held on some tick — the same
/// sampling the C++ original does.
pub fn get_mouse_info() -> MouseInfo {
    let mut p = POINT { x: 0, y: 0 };
    let got = unsafe { GetCursorPos(&mut p) };
    if got == 0 {
        return MouseInfo {
            x: 0.0,
            y: 0.0,
            pressed: false,
            scale: 1.0,
        };
    }

    let down = |vk: i32| unsafe { (GetAsyncKeyState(vk) as u16 & 0x8000) != 0 };
    let pressed = down(VK_LBUTTON as i32) || down(VK_RBUTTON as i32);

    // Per-monitor DPI: 96 = 100% scaling, so dpi/96 is the zoom factor.
    let mut dpi_x: u32 = 96;
    let mut dpi_y: u32 = 96;
    let hmon = unsafe { MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST) };
    if !hmon.is_null() {
        let hr = unsafe { GetDpiForMonitor(hmon, MDT_DEFAULT, &mut dpi_x, &mut dpi_y) };
        if hr < 0 || dpi_x == 0 {
            dpi_x = 96;
        }
    }

    MouseInfo {
        x: p.x as f64,
        y: p.y as f64,
        pressed,
        scale: dpi_x as f64 / 96.0,
    }
}

/// The stock cursor table: `(HCURSOR, kind)` pairs cached from
/// `LoadCursorW(NULL, IDC_*)`. Stock cursor handles are process-global
/// constants (LoadCursorW with a null module returns the same shared handle
/// every call), so caching once is sound. Entries whose cursor fails to load
/// are skipped (IDC_PERSON / IDC_PIN are Win10-era additions and may be
/// absent from some cursor themes) — an unmatched handle reads as `custom`.
fn stock_cursor_table() -> &'static Vec<(isize, CursorKind)> {
    use std::sync::OnceLock;
    static TABLE: OnceLock<Vec<(isize, CursorKind)>> = OnceLock::new();
    TABLE.get_or_init(|| {
        // IDC_PIN (the Win10 "location/pin" cursor) is the closest stock match
        // for the wire contract's `pen` kind — there is no IDC_PEN.
        let ids: [(windows_sys::core::PCWSTR, CursorKind); 16] = [
            (IDC_ARROW, CursorKind::Arrow),
            (IDC_IBEAM, CursorKind::IBeam),
            (IDC_WAIT, CursorKind::Wait),
            (IDC_CROSS, CursorKind::Cross),
            (IDC_UPARROW, CursorKind::UpArrow),
            (IDC_SIZENWSE, CursorKind::SizeNwse),
            (IDC_SIZENESW, CursorKind::SizeNesw),
            (IDC_SIZEWE, CursorKind::SizeWe),
            (IDC_SIZENS, CursorKind::SizeNs),
            (IDC_SIZEALL, CursorKind::SizeAll),
            (IDC_NO, CursorKind::No),
            (IDC_HAND, CursorKind::Hand),
            (IDC_APPSTARTING, CursorKind::AppStarting),
            (IDC_HELP, CursorKind::Help),
            (IDC_PIN, CursorKind::Pen),
            (IDC_PERSON, CursorKind::Person),
        ];
        ids.into_iter()
            .filter_map(|(id, kind)| {
                let h = unsafe { LoadCursorW(std::ptr::null_mut(), id) };
                (!h.is_null()).then_some((h as isize, kind))
            })
            .collect()
    })
}

/// Cursor position + classified shape in one `GetCursorInfo` call (position,
/// showing flag and the live HCURSOR come from the same snapshot, so the
/// classification can never disagree with the sampled position).
pub fn get_cursor_state() -> CursorState {
    let mut info: CURSORINFO = unsafe { mem::zeroed() };
    info.cbSize = mem::size_of::<CURSORINFO>() as u32;
    if unsafe { GetCursorInfo(&mut info) } == 0 {
        return CursorState {
            x: 0,
            y: 0,
            kind: CursorKind::Hidden,
        };
    }
    let kind = if info.flags & CURSOR_SHOWING == 0 {
        CursorKind::Hidden
    } else {
        let h = info.hCursor as isize;
        stock_cursor_table()
            .iter()
            .find(|(handle, _)| *handle == h)
            .map(|(_, kind)| *kind)
            .unwrap_or(CursorKind::Custom)
    };
    CursorState {
        x: info.ptScreenPos.x,
        y: info.ptScreenPos.y,
        kind,
    }
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
    if hmon.is_null() {
        return 1.0;
    }
    let mut dpi_x: u32 = 96;
    let mut dpi_y: u32 = 96;
    let hr = unsafe { GetDpiForMonitor(hmon, MDT_DEFAULT, &mut dpi_x, &mut dpi_y) };
    if hr < 0 || dpi_x == 0 {
        return 1.0;
    }
    dpi_x as f64 / 96.0
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

/// Partial `obs_source_update` payload toggling cursor capture on an existing
/// display source (win-capture applies it live).
pub fn cursor_update_settings(show_cursor: bool) -> ObsData {
    let settings = ObsData::new();
    settings.set_bool("capture_cursor", show_cursor);
    settings
}

/// Source id + settings for a speaker (output) capture source. Must be called
/// after modules are loaded.
pub fn audio_output_capture(device_id: &str) -> (&'static str, ObsData) {
    let settings = ObsData::new();
    settings.set_string("device_id", device_id);
    ("wasapi_output_capture", settings)
}

/// Settings for a `WEBCAM_SOURCE_ID` instance capturing `device_id` (a value
/// exactly as printed by `--list-cameras`, i.e. win-dshow's escaped
/// `<name>:<path>` form — no re-escaping happens here).
pub fn webcam_settings(device_id: &str) -> ObsData {
    let settings = ObsData::new();
    settings.set_string(WEBCAM_DEVICE_KEY, device_id);
    // 0 = the device's preferred resolution/format.
    settings.set_int("res_type", 0);
    settings.set_bool("active", true);
    // Hidden win-dshow knob: block source creation until the device is
    // actually running, so the caller's frame-size poll usually succeeds at
    // once.
    settings.set_bool("synchronous_activate", true);
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

/// Linear gain that undoes the endpoint's software master volume for a
/// loopback (speaker) capture. On endpoints without hardware volume, Windows
/// applies the volume slider in the audio engine *before* the loopback tap, so
/// every recorder receives pre-attenuated samples; multiplying the source by
/// the inverse restores the played content's level. Returns `1.0` (no
/// compensation) when the device has hardware volume, is muted, or cannot be
/// queried.
pub fn speaker_compensation_gain(device_id: &str) -> f32 {
    match endpoint_volume::software_master_volume_db(device_id) {
        Some(db) => compensation_gain_from_db(db),
        None => 1.0,
    }
}

/// ±30 dB compensation cap: keeps a near-zero volume slider from requesting an
/// absurd boost (the captured signal is float, so the math is lossless, but a
/// >30 dB "restoration" of a slider someone parked at 2% is not what they
/// meant).
const MAX_COMPENSATION_DB: f32 = 30.0;

fn compensation_gain_from_db(master_db: f32) -> f32 {
    if !master_db.is_finite() {
        return 1.0;
    }
    let boost_db = (-master_db).clamp(-MAX_COMPENSATION_DB, MAX_COMPENSATION_DB);
    10f32.powf(boost_db / 20.0)
}

/// Minimal hand-rolled Core Audio endpoint COM client. windows-sys exposes the
/// plain-C COM entry points but not interface vtables, and the full `windows`
/// crate is a heavy dependency for three method calls — so the three vtable
/// prefixes used here are declared manually.
mod endpoint_volume {
    use std::cell::Cell;
    use std::ffi::c_void;
    use std::ptr;

    use windows_sys::core::{GUID, HRESULT, PCWSTR};
    use windows_sys::Win32::System::Com::{
        CoCreateInstance, CoInitializeEx, CLSCTX_ALL, COINIT_MULTITHREADED,
    };

    const CLSID_MM_DEVICE_ENUMERATOR: GUID = GUID {
        data1: 0xBCDE0395,
        data2: 0xE52F,
        data3: 0x467C,
        data4: [0x8E, 0x3D, 0xC4, 0x57, 0x92, 0x91, 0x69, 0x2E],
    };
    const IID_IMM_DEVICE_ENUMERATOR: GUID = GUID {
        data1: 0xA95664D2,
        data2: 0x9614,
        data3: 0x4F35,
        data4: [0xA7, 0x46, 0xDE, 0x8D, 0xB6, 0x36, 0x17, 0xE6],
    };
    const IID_IAUDIO_ENDPOINT_VOLUME: GUID = GUID {
        data1: 0x5CDF2C82,
        data2: 0x841E,
        data3: 0x4546,
        data4: [0x97, 0x22, 0x0C, 0xF7, 0x40, 0x78, 0x22, 0x9A],
    };

    const E_RENDER: i32 = 0; // EDataFlow::eRender
    /// ERole::eConsole — the role win-wasapi resolves "default" with, so both
    /// sides always talk about the same device.
    const E_CONSOLE: i32 = 0;
    const ENDPOINT_HARDWARE_SUPPORT_VOLUME: u32 = 0x1;
    const RPC_E_CHANGED_MODE: HRESULT = 0x80010106u32 as HRESULT;

    /// Leading vtable entries shared by every COM interface.
    #[repr(C)]
    struct IUnknownPrefix {
        query_interface: usize,
        add_ref: usize,
        release: unsafe extern "system" fn(*mut c_void) -> u32,
    }

    #[repr(C)]
    struct IMMDeviceEnumeratorVtbl {
        unknown: IUnknownPrefix,
        enum_audio_endpoints: usize,
        get_default_audio_endpoint:
            unsafe extern "system" fn(*mut c_void, i32, i32, *mut *mut c_void) -> HRESULT,
        get_device: unsafe extern "system" fn(*mut c_void, PCWSTR, *mut *mut c_void) -> HRESULT,
    }

    #[repr(C)]
    struct IMMDeviceVtbl {
        unknown: IUnknownPrefix,
        activate: unsafe extern "system" fn(
            *mut c_void,
            *const GUID,
            u32,
            *mut c_void,
            *mut *mut c_void,
        ) -> HRESULT,
    }

    #[repr(C)]
    struct IAudioEndpointVolumeVtbl {
        unknown: IUnknownPrefix,
        register_control_change_notify: usize,
        unregister_control_change_notify: usize,
        get_channel_count: usize,
        set_master_volume_level: usize,
        set_master_volume_level_scalar: usize,
        get_master_volume_level: unsafe extern "system" fn(*mut c_void, *mut f32) -> HRESULT,
        get_master_volume_level_scalar: usize,
        set_channel_volume_level: usize,
        set_channel_volume_level_scalar: usize,
        get_channel_volume_level: usize,
        get_channel_volume_level_scalar: usize,
        set_mute: usize,
        get_mute: unsafe extern "system" fn(*mut c_void, *mut i32) -> HRESULT,
        get_volume_step_info: usize,
        volume_step_up: usize,
        volume_step_down: usize,
        query_hardware_support: unsafe extern "system" fn(*mut c_void, *mut u32) -> HRESULT,
    }

    unsafe fn vtbl<T>(obj: *mut c_void) -> *const T {
        *(obj as *mut *const T)
    }

    unsafe fn com_release(obj: *mut c_void) {
        ((*vtbl::<IUnknownPrefix>(obj)).release)(obj);
    }

    /// Owned COM pointer so every early return releases.
    struct ComPtr(*mut c_void);
    impl Drop for ComPtr {
        fn drop(&mut self) {
            if !self.0.is_null() {
                unsafe { com_release(self.0) };
            }
        }
    }

    /// Per-thread one-time CoInitializeEx. `RPC_E_CHANGED_MODE` (already
    /// initialized STA by someone else) still leaves COM usable. Never
    /// uninitialized — callers are process-lifetime threads and the process
    /// exits via `ExitProcess` anyway.
    fn com_ready() -> bool {
        thread_local! {
            static STATE: Cell<Option<bool>> = const { Cell::new(None) };
        }
        STATE.with(|state| {
            if let Some(ready) = state.get() {
                return ready;
            }
            let hr = unsafe { CoInitializeEx(ptr::null(), COINIT_MULTITHREADED as u32) };
            let ready = hr >= 0 || hr == RPC_E_CHANGED_MODE;
            state.set(Some(ready));
            ready
        })
    }

    /// The endpoint's master volume in dB — but only when Windows applies that
    /// volume in software (no `ENDPOINT_HARDWARE_SUPPORT_VOLUME`), which is
    /// exactly when it lands inside the loopback stream. `None` means "do not
    /// compensate": hardware volume, muted (the capture is silent anyway),
    /// unknown device id, or any COM failure.
    pub fn software_master_volume_db(device_id: &str) -> Option<f32> {
        if !com_ready() {
            return None;
        }
        unsafe {
            let mut enumerator: *mut c_void = ptr::null_mut();
            let hr = CoCreateInstance(
                &CLSID_MM_DEVICE_ENUMERATOR,
                ptr::null_mut(),
                CLSCTX_ALL,
                &IID_IMM_DEVICE_ENUMERATOR,
                &mut enumerator,
            );
            if hr < 0 || enumerator.is_null() {
                return None;
            }
            let enumerator = ComPtr(enumerator);

            let mut device: *mut c_void = ptr::null_mut();
            let ev = vtbl::<IMMDeviceEnumeratorVtbl>(enumerator.0);
            let hr = if device_id == "default" {
                ((*ev).get_default_audio_endpoint)(enumerator.0, E_RENDER, E_CONSOLE, &mut device)
            } else {
                let wide: Vec<u16> = device_id.encode_utf16().chain(Some(0)).collect();
                ((*ev).get_device)(enumerator.0, wide.as_ptr(), &mut device)
            };
            if hr < 0 || device.is_null() {
                return None;
            }
            let device = ComPtr(device);

            let mut volume: *mut c_void = ptr::null_mut();
            let hr = ((*vtbl::<IMMDeviceVtbl>(device.0)).activate)(
                device.0,
                &IID_IAUDIO_ENDPOINT_VOLUME,
                CLSCTX_ALL,
                ptr::null_mut(),
                &mut volume,
            );
            if hr < 0 || volume.is_null() {
                return None;
            }
            let volume = ComPtr(volume);
            let vv = vtbl::<IAudioEndpointVolumeVtbl>(volume.0);

            let mut hw_mask: u32 = 0;
            if ((*vv).query_hardware_support)(volume.0, &mut hw_mask) < 0
                || hw_mask & ENDPOINT_HARDWARE_SUPPORT_VOLUME != 0
            {
                return None;
            }
            let mut muted: i32 = 0;
            if ((*vv).get_mute)(volume.0, &mut muted) < 0 || muted != 0 {
                return None;
            }
            let mut db: f32 = 0.0;
            if ((*vv).get_master_volume_level)(volume.0, &mut db) < 0 {
                return None;
            }
            Some(db)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::compensation_gain_from_db;

    #[test]
    fn compensation_inverts_the_master_volume() {
        // 0 dB (full volume) → unity.
        assert!((compensation_gain_from_db(0.0) - 1.0).abs() < 1e-6);
        // -6.02 dB → ×2.
        assert!((compensation_gain_from_db(-6.0206) - 2.0).abs() < 1e-3);
        // -12.44 dB (the 44% slider that motivated this) → ×4.18.
        assert!((compensation_gain_from_db(-12.444) - 4.18).abs() < 0.01);
        // A boosted endpoint (positive dB) is compensated *down*.
        assert!((compensation_gain_from_db(6.0206) - 0.5).abs() < 1e-3);
    }

    #[test]
    fn compensation_is_capped_at_30_db() {
        assert!((compensation_gain_from_db(-96.0) - 31.6228).abs() < 1e-3);
        assert!((compensation_gain_from_db(96.0) - 0.0316228).abs() < 1e-6);
        // Non-finite input must not produce a NaN gain.
        assert_eq!(compensation_gain_from_db(f32::NAN), 1.0);
        assert_eq!(compensation_gain_from_db(f32::NEG_INFINITY), 1.0);
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
