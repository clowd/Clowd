//! Windows platform implementation (DESIGN §2.2).

use std::env;
use std::ffi::CStr;
use std::mem;
use std::path::Path;
use std::sync::OnceLock;

use prisc_obs::data::ObsData;
use prisc_rust_core::desktop;
use prisc_rust_core::geometry::ScreenPoint;
use windows::Wdk::System::SystemServices::RtlGetVersion;
use windows::Win32::System::SystemInformation::OSVERSIONINFOW;
use windows::Win32::System::Threading::ExitProcess;
use windows::Win32::UI::HiDpi::{
    SetProcessDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
};
use windows::Win32::UI::Input::KeyboardAndMouse::{GetAsyncKeyState, VK_LBUTTON, VK_RBUTTON};
use windows::Win32::UI::WindowsAndMessaging::{
    LoadCursorW, IDC_APPSTARTING, IDC_ARROW, IDC_CROSS,
    IDC_HAND, IDC_HELP, IDC_IBEAM, IDC_NO, IDC_PERSON, IDC_PIN, IDC_SIZEALL, IDC_SIZENESW,
    IDC_SIZENS, IDC_SIZENWSE, IDC_SIZEWE, IDC_UPARROW, IDC_WAIT,
};

use super::cursor_sprite::SpriteEvent;
use super::region::{self, Rect, RegionPlan};
use super::{
    CaptureMethod, CursorKind, CursorState, DisplayCaptureMode, MonitorInfo, MouseInfo, ObsPaths,
    WindowInfo,
};

/// `platform` field of the input-capture header (wire contract).
pub const PLATFORM_NAME: &str = "windows";

pub const GRAPHICS_MODULE: &CStr = c"libobs-d3d11";

/// The display-capture source id. A function rather than a constant only
/// because Linux decides between two sources at runtime (X11 vs Wayland
/// session); here it is fixed.
pub fn display_capture_id() -> &'static str {
    "monitor_capture"
}

/// Always [`DisplayCaptureMode::Monitors`]: win-capture captures the monitor
/// the caller names (see the enum for the Linux Wayland exception).
pub fn display_capture_mode() -> DisplayCaptureMode {
    DisplayCaptureMode::Monitors
}

/// Must run before any monitor enumeration so `EnumDisplayMonitors` rects are
/// physical pixels (per-monitor-v2 DPI awareness). Never fails here; the
/// Result is the Linux display connection's.
pub fn init_process() -> Result<(), String> {
    // Failure (already set, or pre-1703 Windows) was always ignored; the
    // windows crate wraps the raw BOOL in a #[must_use] Result, so ignore it
    // explicitly.
    let _ = unsafe { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    Ok(())
}

pub fn enumerate_monitors() -> Vec<MonitorInfo> {
    // A failed enumeration simply yields an empty list.
    desktop::monitors()
        .unwrap_or_default()
        .into_iter()
        .map(|m| {
            // The stable device interface path wins; this mirrors win-capture,
            // which matches DeviceID first and falls back to szDevice.
            let (id, alt_id) = match m.device_interface_path() {
                Some(path) => (path, Some(m.device_name)),
                None => (m.device_name, None),
            };
            MonitorInfo {
                id,
                alt_id,
                x: m.bounds.origin.x,
                y: m.bounds.origin.y,
                width: m.bounds.size.width.max(0) as u32,
                height: m.bounds.size.height.max(0) as u32,
                scale: 1.0,
                is_primary: m.is_primary,
            }
        })
        .collect()
}

pub fn find_monitor(id: &str) -> Option<MonitorInfo> {
    super::match_monitor(id, &enumerate_monitors())
}

/// The display's DPI zoom (dpi/96) — the input-capture header's per-monitor
/// `scale`, which the editor uses to size themed cursors like the OS does.
/// Distinct from `MonitorInfo::scale` (capture px per coordinate unit, always
/// 1.0 on Windows).
pub fn monitor_display_scale(m: &MonitorInfo) -> f64 {
    desktop::dpi_scale_at(ScreenPoint::new(
        m.x + (m.width as i32 / 2),
        m.y + (m.height as i32 / 2),
    ))
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
    let outputs = desktop::dxgi_outputs()?;
    for index in ranked {
        // GDI device name (`\.\DISPLAY1`) is the join key: it is both
        // MonitorInfo::alt_id and DXGI_OUTPUT_DESC::DeviceName.
        let Some(name) = monitors.get(index).and_then(|m| m.alt_id.as_deref()) else {
            continue;
        };
        if let Some(output) = outputs.iter().find(|o| o.device_name == name) {
            return Some(output.adapter_index);
        }
    }
    None
}

/// The first component of the Windows 11 build range. Windows 11 kept
/// `dwMajorVersion` at 10 and is told apart from Windows 10 only by the build
/// number: 21H2, the first release, is build 22000.
const WINDOWS_11_BUILD: u32 = 22000;

/// Whether the running OS is Windows 11 or newer, from `RtlGetVersion`.
///
/// `GetVersionExW` and `VerifyVersionInfoW` are shimmed by the compatibility
/// layer and report 6.2 (Windows 8) unless the executable's manifest opts in
/// per-version, so ntdll's `RtlGetVersion` — which never lies — is the one to
/// ask. Probed once: the answer cannot change while the process runs.
fn is_windows_11_or_newer() -> bool {
    static CACHED: OnceLock<bool> = OnceLock::new();
    *CACHED.get_or_init(|| {
        let mut info = OSVERSIONINFOW {
            dwOSVersionInfoSize: mem::size_of::<OSVERSIONINFOW>() as u32,
            ..Default::default()
        };
        // NTSTATUS: only STATUS_SUCCESS (0) is documented, but treat any
        // failure as "not Windows 11" so an unreadable version falls back to
        // win-capture's own heuristic rather than forcing WGC blindly.
        if unsafe { RtlGetVersion(&mut info) }.is_err() {
            return false;
        }
        (info.dwMajorVersion, info.dwBuildNumber) >= (10, WINDOWS_11_BUILD)
    })
}

/// `method` with [`CaptureMethod::Auto`] resolved for this machine: WGC on
/// Windows 11+, and left to win-capture's `choose_method()` on Windows 10.
///
/// The Win11 preference is deliberate. WGC is the API Microsoft still develops;
/// it captures a monitor regardless of which adapter drives it, where the DXGI
/// duplicator only finds monitors on libobs's own adapter (see
/// `region_adapter_index`) and was verified to produce black frames on a
/// Win11 26H1 + NVIDIA machine; and Win11 is exactly where libobs can suppress
/// the yellow WGC capture border via
/// `GraphicsCaptureSession::IsBorderRequired(false)`
/// (obs-studio/libobs-winrt/winrt-capture.cpp), so preferring it costs nothing
/// visible. On Windows 10 that border is unavoidable under WGC, so `auto` stays
/// out of the way there and win-capture decides.
///
/// Explicit `dxgi` / `wgc` pass through untouched.
pub fn resolve_capture_method(method: CaptureMethod) -> CaptureMethod {
    method.resolve(is_windows_11_or_newer())
}

pub fn display_capture_settings(
    m: &MonitorInfo,
    show_cursor: bool,
    method: CaptureMethod,
) -> ObsData {
    let settings = ObsData::new();
    settings.set_string("monitor_id", &m.id);
    // `auto` is resolved here rather than passed to win-capture as `method: 0`
    // — see resolve_capture_method for why Windows 11 always takes WGC.
    // Requesting WGC is safe everywhere: win-capture's choose_method()
    // force-falls back to DXGI when WGC is unsupported
    // (duplicator-monitor-capture.c).
    settings.set_int("method", resolve_capture_method(method).as_obs_method());
    settings.set_bool("capture_cursor", show_cursor);
    // The output is always SDR (NV12 / Rec.709), so ask win-capture for an SDR
    // picture of an HDR monitor instead of the scRGB texture it would tone-map
    // itself — the same as ticking "Force SDR" on OBS Studio's display capture
    // source. Under WGC this requests a B8G8R8A8 capture (libobs-winrt
    // get_pixel_format); under DXGI win-capture keeps the scRGB duplication but
    // scales it by the monitor's own SDR white level, which Windows reports as
    // >= 80 nits (duplicator-monitor-capture.c duplicator_capture_render,
    // d3d11-subsystem.cpp GetSdrMaxNits). HDR highlights brighter than the
    // monitor's SDR white clip instead of being tone-mapped, which is the same
    // trade-off OBS Studio's option makes. Without it both backends draw an
    // HDR monitor through DrawMultiplyTonemap with
    // `80 / obs_get_video_sdr_white_level()`, which divides by zero unless the
    // host has set the video levels (see ObsContext::reset_video) and records
    // solid black.
    settings.set_bool("force_sdr", true);
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

pub const AUDIO_INPUT_CAPTURE_ID: &str = "wasapi_input_capture";
/// Webcam capture source (`--webcam` / `--list-cameras`): DirectShow.
pub const WEBCAM_SOURCE_ID: &str = "dshow_input";
/// The `WEBCAM_SOURCE_ID` settings key (and property) holding the device id.
pub const WEBCAM_DEVICE_KEY: &str = "video_device_id";

/// Cursor position (physical px; the process is per-monitor-v2 DPI aware),
/// button state, and the DPI zoom of the monitor under the cursor.
///
/// `GetAsyncKeyState`'s high bit is the *current* physical down state, so a
/// click is caught as long as the button is still held on some tick, the same
/// sampling the C++ original does.
pub fn get_mouse_info() -> MouseInfo {
    let Some(p) = desktop::cursor_position() else {
        return MouseInfo {
            x: 0.0,
            y: 0.0,
            pressed: false,
            scale: 1.0,
        };
    };

    let down = |vk: i32| unsafe { (GetAsyncKeyState(vk) as u16 & 0x8000) != 0 };
    let pressed = down(VK_LBUTTON.0 as i32) || down(VK_RBUTTON.0 as i32);

    MouseInfo {
        x: p.x as f64,
        y: p.y as f64,
        pressed,
        // Per-monitor DPI: 96 = 100% scaling, so dpi/96 is the zoom factor.
        scale: desktop::dpi_scale_at(p),
    }
}

/// The stock cursor table: `(HCURSOR, kind)` pairs cached from
/// `LoadCursorW(NULL, IDC_*)`. Stock cursor handles are process-global
/// constants (LoadCursorW with a null module returns the same shared handle
/// every call), so caching once is sound. Entries whose cursor fails to load
/// are skipped (IDC_PERSON / IDC_PIN are Win10-era additions and may be
/// absent from some cursor themes); an unmatched handle reads as `custom`.
fn stock_cursor_table() -> &'static Vec<(isize, CursorKind)> {
    use std::sync::OnceLock;
    static TABLE: OnceLock<Vec<(isize, CursorKind)>> = OnceLock::new();
    TABLE.get_or_init(|| {
        // IDC_PIN (the Win10 "location/pin" cursor) is the closest stock match
        // for the wire contract's `pen` kind; there is no IDC_PEN.
        let ids: [(windows::core::PCWSTR, CursorKind); 16] = [
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
                let h = unsafe { LoadCursorW(None, id) }.ok()?;
                Some((h.0 as isize, kind))
            })
            .collect()
    })
}

/// Cursor position + classified shape in one `GetCursorInfo` call (position,
/// showing flag and the live HCURSOR come from the same snapshot, so the
/// classification (and the sprite rasterized from `handle`) can never
/// disagree with the sampled position).
pub fn get_cursor_state() -> CursorState {
    let Some(cursor) = desktop::cursor_snapshot() else {
        return CursorState {
            x: 0,
            y: 0,
            kind: CursorKind::Hidden,
            handle: 0,
        };
    };
    let kind = if !cursor.showing {
        CursorKind::Hidden
    } else {
        stock_cursor_table()
            .iter()
            .find(|(handle, _)| *handle == cursor.handle)
            .map(|(_, kind)| *kind)
            .unwrap_or(CursorKind::Custom)
    };
    CursorState {
        x: cursor.position.x,
        y: cursor.position.y,
        kind,
        handle: cursor.handle,
    }
}

/// Rasterizes the sampled cursor into a sprite event; see [`sprite`] for the
/// mechanics. Called once per tick with the state returned by
/// [`get_cursor_state`] on the same tick.
pub fn take_cursor_sprite(state: &CursorState) -> SpriteEvent {
    sprite::take(state)
}

/// HCURSOR → [`RawSprite`](crate::cursor_sprite::RawSprite) rasterization
/// for the sidecar's `cursor_image` rows.
///
/// The tick thread calls [`take`] once per rendered frame with the same
/// `GetCursorInfo` snapshot the frame row records. Steady state is a handle
/// comparison plus one `GetCursorFrameInfo` probe; pixels are only read when
/// the handle changes or the cursor is animated (~50–150 µs; the writer
/// thread's content-hash dedupe keeps repeated animation frames to one row
/// each).
///
/// DPI: `GetIconInfo` yields the size the shared handle holds: the system
/// cursor size (logon DPI × the accessibility size multiplier), which is
/// physical pixels on uniform-DPI machines. Mixed-DPI parity is an open
/// verification item, deliberately not compensated for here.
mod sprite {
    use std::mem;
    use std::sync::{Mutex, OnceLock};
    use std::time::Instant;

    use prisc_rust_core::desktop;
    use windows::core::{s, w};
    use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};
    use windows::Win32::UI::WindowsAndMessaging::HCURSOR;

    use super::{CursorKind, CursorState};
    use crate::cursor_sprite::{self, SpriteEvent};

    /// The last handle whose sprite actually reached the writer, plus when it
    /// was first seen (the animation clock's epoch, so `istep` starts at
    /// frame 0 whenever the cursor changes). Tick-thread-only in practice; the
    /// mutex just satisfies `static` soundness.
    struct Tracker {
        handle: isize,
        since: Instant,
    }

    static TRACKER: Mutex<Option<Tracker>> = Mutex::new(None);

    pub fn take(state: &CursorState) -> SpriteEvent {
        if state.kind == CursorKind::Hidden || state.handle == 0 {
            // A `Hidden` event makes the writer drop its `ci` ref, and only a
            // fresh `Candidate` can restore it, so the tracker must forget
            // the handle too. Otherwise a cursor that auto-hides and comes
            // back as the same HCURSOR (typing, fullscreen video) would read
            // as `Unchanged` and pin the ref absent until the shape changes.
            *TRACKER.lock().unwrap_or_else(|e| e.into_inner()) = None;
            return SpriteEvent::Hidden;
        }

        let tracker = TRACKER.lock().unwrap_or_else(|e| e.into_inner());
        let (changed, since) = match tracker.as_ref() {
            Some(t) if t.handle == state.handle => (false, t.since),
            _ => (true, Instant::now()),
        };
        drop(tracker);

        let hcur = HCURSOR(state.handle as *mut std::ffi::c_void);
        let event = match frame_info(hcur) {
            // A static cursor whose handle did not change is byte-identical to
            // what the writer already holds, so skip the pixel work entirely.
            AnimInfo::Static if !changed => return SpriteEvent::Unchanged,
            AnimInfo::Static => rasterize_copied(hcur, state.kind),
            AnimInfo::Animated {
                rate_jiffies,
                frames,
            } => {
                // Frame pick per the ANI clock: rate is in jiffies (1/60 s).
                let elapsed_ms = since.elapsed().as_millis() as u64;
                let istep =
                    ((elapsed_ms * 60) / (rate_jiffies.max(1) as u64 * 1000)) % frames as u64;
                let frame = animation_frame(hcur, istep as u32).unwrap_or(hcur);
                rasterize_copied(frame, state.kind)
            }
            // `GetCursorFrameInfo` unavailable: animation cannot be detected,
            // so degrade to rasterizing every tick; the writer's dedupe makes
            // the steady state one hash per frame, no extra rows.
            AnimInfo::Unknown => rasterize_copied(hcur, state.kind),
        };

        // The tracker only advances once pixels were actually produced; a
        // rasterization failure clears it instead, so the next tick retries
        // rather than reporting `Unchanged` for a sprite the writer never
        // received (or for a stale handle the cursor may flip back to).
        *TRACKER.lock().unwrap_or_else(|e| e.into_inner()) = match event {
            SpriteEvent::Candidate(_) => Some(Tracker {
                handle: state.handle,
                since,
            }),
            _ => None,
        };
        event
    }

    /// The cursor's pixels as a sprite (`desktop::read_cursor` copies the
    /// handle first: it belongs to the foreground app, which may destroy it
    /// at any moment). Failure degrades to `Hidden` (frame rows drop their
    /// `ci` ref, the wire contract's "unavailable" case) rather than
    /// `Unchanged`, which would pin the ref to a sprite of the *previous*
    /// shape.
    fn rasterize_copied(hcur: HCURSOR, kind: CursorKind) -> SpriteEvent {
        match desktop::read_cursor(hcur.0 as isize) {
            Some(icon) => SpriteEvent::Candidate(cursor_sprite::from_icon(icon, kind.as_str())),
            None => SpriteEvent::Hidden,
        }
    }

    // -- animation detection (GetCursorFrameInfo) ---------------------------

    enum AnimInfo {
        /// `GetCursorFrameInfo` is not exported on this Windows build.
        Unknown,
        Static,
        Animated {
            rate_jiffies: u32,
            frames: u32,
        },
    }

    /// `user32!GetCursorFrameInfo`: undocumented but stable since XP (prior
    /// art in OBS-adjacent recorders). Returns the HCURSOR of animation step
    /// `istep` and reports the step rate (jiffies) and step count. The export
    /// has no `windows`-crate binding, so it stays a GetProcAddress-resolved
    /// raw fn pointer.
    type GetCursorFrameInfoFn =
        unsafe extern "system" fn(HCURSOR, u32, u32, *mut u32, *mut u32) -> HCURSOR;

    /// Resolved once; `None` pins [`AnimInfo::Unknown`] for the process.
    fn frame_info_fn() -> Option<GetCursorFrameInfoFn> {
        static F: OnceLock<Option<GetCursorFrameInfoFn>> = OnceLock::new();
        *F.get_or_init(|| unsafe {
            // user32 is guaranteed resident (this module links GetCursorInfo).
            let user32 = GetModuleHandleW(w!("user32.dll")).ok()?;
            GetProcAddress(user32, s!("GetCursorFrameInfo")).map(|p| {
                mem::transmute::<unsafe extern "system" fn() -> isize, GetCursorFrameInfoFn>(p)
            })
        })
    }

    fn frame_info(hcur: HCURSOR) -> AnimInfo {
        let Some(f) = frame_info_fn() else {
            return AnimInfo::Unknown;
        };
        let mut rate_jiffies: u32 = 0;
        let mut frames: u32 = 0;
        let frame = unsafe { f(hcur, 0, 0, &mut rate_jiffies, &mut frames) };
        if frame.is_invalid() || frames <= 1 {
            AnimInfo::Static
        } else {
            AnimInfo::Animated {
                rate_jiffies,
                frames,
            }
        }
    }

    /// The HCURSOR of animation step `istep`, owned by the cursor (not
    /// destroyed here; only its `CopyIcon` copy is).
    fn animation_frame(hcur: HCURSOR, istep: u32) -> Option<HCURSOR> {
        let f = frame_info_fn()?;
        let mut rate: u32 = 0;
        let mut frames: u32 = 0;
        let frame = unsafe { f(hcur, 0, istep, &mut rate, &mut frames) };
        (!frame.is_invalid()).then_some(frame)
    }
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
/// `<name>:<path>` form; no re-escaping happens here).
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
/// \>30 dB "restoration" of a slider someone parked at 2% is not what they
/// meant).
const MAX_COMPENSATION_DB: f32 = 30.0;

fn compensation_gain_from_db(master_db: f32) -> f32 {
    if !master_db.is_finite() {
        return 1.0;
    }
    let boost_db = (-master_db).clamp(-MAX_COMPENSATION_DB, MAX_COMPENSATION_DB);
    10f32.powf(boost_db / 20.0)
}

/// Core Audio endpoint volume query via the `windows` crate's generated COM
/// bindings (which replaced the hand-rolled vtable prefixes windows-sys
/// forced). The interface wrappers `Release` on drop, so every early return
/// still balances the refcounts.
mod endpoint_volume {
    use std::cell::Cell;

    use windows::core::PCWSTR;
    use windows::Win32::Foundation::RPC_E_CHANGED_MODE;
    use windows::Win32::Media::Audio::Endpoints::IAudioEndpointVolume;
    use windows::Win32::Media::Audio::{
        eConsole, eRender, IMMDeviceEnumerator, MMDeviceEnumerator,
        ENDPOINT_HARDWARE_SUPPORT_VOLUME,
    };
    use windows::Win32::System::Com::{
        CoCreateInstance, CoInitializeEx, CLSCTX_ALL, COINIT_MULTITHREADED,
    };

    /// Per-thread one-time CoInitializeEx. `RPC_E_CHANGED_MODE` (already
    /// initialized STA by someone else) still leaves COM usable. Never
    /// uninitialized: callers are process-lifetime threads and the process
    /// exits via `ExitProcess` anyway.
    fn com_ready() -> bool {
        thread_local! {
            static STATE: Cell<Option<bool>> = const { Cell::new(None) };
        }
        STATE.with(|state| {
            if let Some(ready) = state.get() {
                return ready;
            }
            let hr = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
            let ready = hr.is_ok() || hr == RPC_E_CHANGED_MODE;
            state.set(Some(ready));
            ready
        })
    }

    /// The endpoint's master volume in dB, but only when Windows applies that
    /// volume in software (no `ENDPOINT_HARDWARE_SUPPORT_VOLUME`), which is
    /// exactly when it lands inside the loopback stream. `None` means "do not
    /// compensate": hardware volume, muted (the capture is silent anyway),
    /// unknown device id, or any COM failure.
    pub fn software_master_volume_db(device_id: &str) -> Option<f32> {
        if !com_ready() {
            return None;
        }
        unsafe {
            let enumerator: IMMDeviceEnumerator =
                CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL).ok()?;

            let device = if device_id == "default" {
                // eConsole: the role win-wasapi resolves "default" with, so
                // both sides always talk about the same device.
                enumerator.GetDefaultAudioEndpoint(eRender, eConsole).ok()?
            } else {
                let wide: Vec<u16> = device_id.encode_utf16().chain(Some(0)).collect();
                enumerator.GetDevice(PCWSTR::from_raw(wide.as_ptr())).ok()?
            };

            let volume: IAudioEndpointVolume = device.Activate(CLSCTX_ALL, None).ok()?;

            let hw_mask = volume.QueryHardwareSupport().ok()?;
            if hw_mask & ENDPOINT_HARDWARE_SUPPORT_VOLUME != 0 {
                return None;
            }
            if volume.GetMute().ok()?.as_bool() {
                return None;
            }
            volume.GetMasterVolumeLevel().ok()
        }
    }
}

// ---------------------------------------------------------------------------
// Window enumeration (--window-capture)
// ---------------------------------------------------------------------------

/// Visible top-level windows other than the recorder's own, topmost first,
/// through the filter the overlay's walker shares (`prisc_rust_core::desktop`).
/// The recorder runs this up to 60 times a second, so it skips the overlay's
/// virtual-desktop check (COM on this thread): DWM cloaks another desktop's
/// windows a poll or two after a switch anyway.
///
/// Coordinates are physical pixels (the process is per-monitor-v2 DPI aware),
/// i.e. the §1.1 capture space, so no conversion happens here.
pub fn enumerate_windows() -> Vec<WindowInfo> {
    let filter = desktop::WindowFilter {
        current_virtual_desktop_only: false,
        exclude_own_process: true,
    };
    desktop::top_level_windows(&filter)
        .into_iter()
        .map(|w| WindowInfo {
            id: w.hwnd.0 as usize as u64,
            pid: w.pid,
            x: w.rect.origin.x,
            y: w.rect.origin.y,
            w: w.rect.size.width as u32,
            h: w.rect.size.height as u32,
            title: w.title,
            app: desktop::process_image_name(w.pid),
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The probe itself: it must return an answer that agrees with what
    /// `resolve_capture_method` does with `Auto`, and must leave an explicit
    /// pin alone. The expected value depends on the host OS, so the Windows 11
    /// branch is asserted only when the probe says we are on one.
    #[test]
    fn auto_resolves_to_wgc_on_windows_11() {
        let expected = if is_windows_11_or_newer() {
            CaptureMethod::Wgc
        } else {
            CaptureMethod::Auto
        };
        assert_eq!(resolve_capture_method(CaptureMethod::Auto), expected);
        assert_eq!(
            resolve_capture_method(CaptureMethod::Dxgi),
            CaptureMethod::Dxgi
        );
        assert_eq!(
            resolve_capture_method(CaptureMethod::Wgc),
            CaptureMethod::Wgc
        );
    }

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
