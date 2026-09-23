//! Linux platform implementation (DESIGN §2.2), for x86_64 and aarch64 (the
//! OBS build in obs-sys refuses other architectures).
//!
//! Linux is two platforms in one: an X11 session and a Wayland session need
//! different libobs setup and a different display-capture source, and which
//! one applies is only known at runtime. [`session`] decides once per process
//! from the environment; everything session-dependent below branches on it.
//!
//! - **X11**: `xshm_input_v2` (linux-capture) records one RandR monitor per
//!   source, so the monitor/region planner works exactly as on Windows —
//!   coordinates are root-window pixels, scale 1.0.
//! - **Wayland**: `pipewire-screen-capture-source` (linux-pipewire). The
//!   compositor will not let a client choose what to capture, so the source
//!   asks xdg-desktop-portal, whose picker lets the user choose a monitor or a
//!   window ([`DisplayCaptureMode::Picker`]). There is no monitor list and no
//!   region planning. The X11 source is never used here even though XWayland
//!   would accept the connection: XWayland only sees X clients' surfaces and
//!   captures black for everything else.
//!
//! The X11, Xrandr, wayland-client and glib-2.0 libraries are linked by this
//! crate's build script; the handful of entry points used are declared by hand
//! below (the same approach as the macOS module's CoreGraphics externs), which
//! is far smaller than pulling in bindgen for eight functions.

use std::env;
use std::ffi::{c_char, c_int, c_ulong, c_void, CStr};
use std::path::Path;
use std::sync::OnceLock;

use obs::data::ObsData;

use super::region::{Rect, RegionPlan};
use super::{CaptureMethod, DisplayCaptureMode, MonitorInfo, ObsPaths};

/// `platform` field of the input-capture header (wire contract). The sidecars
/// are rejected on Linux, but the constant is part of the shared surface.
pub const PLATFORM_NAME: &str = "linux";

/// libobs `dlopen`s the graphics module by exactly this name, resolved through
/// libobs.so's own RUNPATH (`$ORIGIN`, the bundle dir). OBS's Linux build
/// stages only the versioned file (`set_target_properties_obs` forces
/// VERSION/SOVERSION 30 in cmake/linux/helpers.cmake, and rundir gets no
/// unversioned namelink), and libobs appends `.so` only to names that carry
/// none — so the bare `libobs-opengl` the other platforms' naming would
/// suggest resolves nowhere. This is also what the OBS frontend passes
/// (`DL_OPENGL`, the target's SONAME file name).
pub const GRAPHICS_MODULE: &CStr = c"libobs-opengl.so.30";

/// X11 display-capture source (plugins/linux-capture/xshm-input.c). Only
/// registered when libobs runs on the X11-EGL platform.
const XSHM_SOURCE_ID: &str = "xshm_input_v2";
/// Wayland display-capture source (plugins/linux-pipewire/screencast-portal.c):
/// the unified monitor-or-window portal source. Registered only when
/// xdg-desktop-portal answers with a non-empty `AvailableSourceTypes`.
const PIPEWIRE_SOURCE_ID: &str = "pipewire-screen-capture-source";

// ---------------------------------------------------------------------------
// Session detection
// ---------------------------------------------------------------------------

/// The graphical session this process runs in. See the module docs for what
/// each implies.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Session {
    X11,
    Wayland,
}

/// The session, detected once from the environment (it cannot change while
/// the process runs). Pure environment inspection — nothing is connected — so
/// it is safe to call before [`init_process`], which is how the recorder
/// rejects `--monitor` / `--region` under Wayland before touching libobs.
pub fn session() -> Session {
    static CACHED: OnceLock<Session> = OnceLock::new();
    *CACHED.get_or_init(|| {
        detect_session(
            env::var("XDG_SESSION_TYPE").ok().as_deref(),
            env::var("WAYLAND_DISPLAY").ok().as_deref(),
        )
    })
}

/// Wayland when the session manager says so (`XDG_SESSION_TYPE=wayland`) or a
/// Wayland socket is advertised (`WAYLAND_DISPLAY`), X11 otherwise.
///
/// Wayland wins whenever there is any sign of it, even with `DISPLAY` also
/// set: every Wayland desktop exports `DISPLAY` for XWayland, and choosing X11
/// there records black (see the module docs). The converse mistake — treating
/// a real X11 session as Wayland — needs a stray `WAYLAND_DISPLAY`, which X11
/// sessions do not set. An X11 answer with no `DISPLAY` either is reported by
/// [`init_process`] when the connection fails.
fn detect_session(xdg_session_type: Option<&str>, wayland_display: Option<&str>) -> Session {
    let xdg_wayland = xdg_session_type.is_some_and(|t| t.eq_ignore_ascii_case("wayland"));
    let wayland_socket = wayland_display.is_some_and(|d| !d.is_empty());
    if xdg_wayland || wayland_socket {
        Session::Wayland
    } else {
        Session::X11
    }
}

/// X11 names the monitor to capture; Wayland's portal picks it (see
/// [`DisplayCaptureMode::Picker`]).
pub fn display_capture_mode() -> DisplayCaptureMode {
    match session() {
        Session::X11 => DisplayCaptureMode::Monitors,
        Session::Wayland => DisplayCaptureMode::Picker,
    }
}

/// `xshm_input_v2` on X11, the PipeWire portal source on Wayland.
pub fn display_capture_id() -> &'static str {
    match session() {
        Session::X11 => XSHM_SOURCE_ID,
        Session::Wayland => PIPEWIRE_SOURCE_ID,
    }
}

// ---------------------------------------------------------------------------
// FFI: Xlib + Xrandr, wayland-client, GLib, libobs' nix platform
// ---------------------------------------------------------------------------

/// Opaque Xlib `Display`.
#[repr(C)]
struct XDisplay {
    _private: [u8; 0],
}

/// Xlib `XID`-sized handles (`Window`, `Atom`) are `unsigned long`.
type XWindow = c_ulong;
type XAtom = c_ulong;

/// `XRRMonitorInfo` from `<X11/extensions/Xrandr.h>` (RandR 1.5):
///
/// ```c
/// typedef struct _XRRMonitorInfo {
///     Atom name; Bool primary; Bool automatic; int noutput;
///     int x; int y; int width; int height; int mwidth; int mheight;
///     RROutput *outputs;
/// } XRRMonitorInfo;
/// ```
///
/// `Bool` is `int`. Only read, never constructed; the layout is pinned by
/// `xrr_monitor_info_layout` below.
#[repr(C)]
struct XRRMonitorInfo {
    name: XAtom,
    primary: c_int,
    automatic: c_int,
    noutput: c_int,
    x: c_int,
    y: c_int,
    width: c_int,
    height: c_int,
    mwidth: c_int,
    mheight: c_int,
    outputs: *mut c_ulong,
}

// Linked by build.rs (`cargo:rustc-link-lib`), which also propagates the
// libraries to every dependent's final link.
extern "C" {
    // libX11
    fn XInitThreads() -> c_int;
    fn XOpenDisplay(name: *const c_char) -> *mut XDisplay;
    fn XCloseDisplay(display: *mut XDisplay) -> c_int;
    fn XDefaultRootWindow(display: *mut XDisplay) -> XWindow;
    fn XGetAtomName(display: *mut XDisplay, atom: XAtom) -> *mut c_char;
    fn XFree(data: *mut c_void) -> c_int;

    // libXrandr
    fn XRRQueryVersion(display: *mut XDisplay, major: *mut c_int, minor: *mut c_int) -> c_int;
    fn XRRGetMonitors(
        display: *mut XDisplay,
        window: XWindow,
        get_active: c_int,
        nmonitors: *mut c_int,
    ) -> *mut XRRMonitorInfo;
    fn XRRFreeMonitors(monitors: *mut XRRMonitorInfo);

    // libwayland-client
    fn wl_display_connect(name: *const c_char) -> *mut c_void;

    // libglib-2.0
    fn g_main_loop_new(context: *mut c_void, is_running: c_int) -> *mut c_void;
    fn g_main_loop_run(main_loop: *mut c_void);
}

/// Handles libobs was given, as integers so the static is `Sync`. Kept only
/// to make [`init_process`] idempotent; the connections themselves are leaked
/// on purpose (see there).
static PLATFORM_DISPLAY: OnceLock<usize> = OnceLock::new();

/// Must run before `obs_startup` (every binary calls it right before
/// `ObsContext::new`): tells libobs which windowing platform it is on and
/// hands it the display connection, per `libobs/obs-nix-platform.h`.
///
/// - **X11**: `XInitThreads` first — libobs-opengl drives the connection from
///   its graphics thread while other threads use Xlib too — then a dedicated
///   `XOpenDisplay`. Dedicated because libobs-opengl takes it over
///   (`XSetEventQueueOwner(XCBOwnsEventQueue)` and a process-wide
///   `XSetErrorHandler` in gl-x11-egl.c); [`enumerate_monitors`] opens its own
///   short-lived connection instead of sharing this one.
/// - **Wayland**: `wl_display_connect`, which libobs uses as-is for EGL and
///   its hotkey registry (a NULL display crashes both). Then a GLib main loop
///   thread — see [`spawn_glib_main_loop`] for why the portal source cannot
///   work without one.
///
/// The connection is deliberately never closed: libobs holds the raw pointer
/// until `obs_shutdown`, and every exit path here is `_exit` without libobs
/// teardown (§1.4), so the process end is its only safe lifetime.
///
/// Failure to connect is fatal and reported here (stderr, exit 1): the shared
/// signature returns nothing, and `obs_startup` would otherwise fail later
/// with a far less useful message (its X11 hotkey init opens `$DISPLAY`
/// itself) or crash (Wayland, NULL display).
pub fn init_process() {
    PLATFORM_DISPLAY.get_or_init(|| match session() {
        Session::X11 => {
            let display = unsafe {
                XInitThreads();
                XOpenDisplay(std::ptr::null())
            };
            if display.is_null() {
                fatal(&format!(
                    "cannot connect to the X server (DISPLAY={}); obs-express needs a \
                     graphical X11 or Wayland session",
                    env::var("DISPLAY").unwrap_or_else(|_| "<unset>".to_string())
                ));
            }
            unsafe {
                obs_sys::obs_set_nix_platform(
                    obs_sys::obs_nix_platform_type_OBS_NIX_PLATFORM_X11_EGL,
                );
                obs_sys::obs_set_nix_platform_display(display.cast());
            }
            display as usize
        }
        Session::Wayland => {
            let display = unsafe { wl_display_connect(std::ptr::null()) };
            if display.is_null() {
                fatal(&format!(
                    "cannot connect to the Wayland compositor (WAYLAND_DISPLAY={})",
                    env::var("WAYLAND_DISPLAY").unwrap_or_else(|_| "<unset>".to_string())
                ));
            }
            unsafe {
                obs_sys::obs_set_nix_platform(
                    obs_sys::obs_nix_platform_type_OBS_NIX_PLATFORM_WAYLAND,
                );
                obs_sys::obs_set_nix_platform_display(display);
            }
            spawn_glib_main_loop();
            display as usize
        }
    });
}

/// Runs GLib's global default main context on a dedicated thread, forever.
///
/// The portal source drives xdg-desktop-portal entirely through async GDBus
/// calls and signal subscriptions (`CreateSession` → `SelectSources` →
/// `Start` → `OpenPipeWireRemote`, plugins/linux-pipewire/screencast-portal.c
/// and portal.c). GLib delivers their replies on the main context that was
/// thread-default when each call was made — the global default context, as
/// nothing in obs-express pushes another — and only while some thread
/// iterates it. OBS Studio gets that for free from Qt's GLib event
/// dispatcher; libobs and linux-pipewire never iterate a context themselves.
/// Without this thread the picker never gets past `CreateSession` and the
/// source stays 0x0 forever.
///
/// `g_main_loop_run` acquires the default context for this thread; nothing
/// else in the process iterates it, so there is no contention. PipeWire's own
/// stream I/O runs on its separate `pw_thread_loop`.
fn spawn_glib_main_loop() {
    let spawned = std::thread::Builder::new()
        .name("glib-main-loop".to_string())
        .spawn(|| unsafe {
            let main_loop = g_main_loop_new(std::ptr::null_mut(), 0);
            g_main_loop_run(main_loop);
        });
    if let Err(e) = spawned {
        fatal(&format!("cannot start the GLib main loop thread: {e}"));
    }
}

fn fatal(msg: &str) -> ! {
    eprintln!("Fatal: {msg}");
    exit_process(1)
}

// ---------------------------------------------------------------------------
// Monitors
// ---------------------------------------------------------------------------

/// X11: every active RandR 1.5 monitor, in the order the X server lists them —
/// the exact list and order xshm_input_v2 indexes its `screen` setting into
/// (`xcb_randr_get_monitors(root, get_active=1)` in
/// plugins/linux-capture/xhelpers.c). Keeping the orders identical is what
/// makes `alt_id` (the index) a valid `screen` value.
///
/// - `id` is the RandR monitor name (`DP-1`, `HDMI-A-0`, ...): stable across
///   runs as long as the cabling is, unlike the index.
/// - Geometry is root-window pixels; X11 has no logical/physical split, so
///   `scale` is 1.0 and the region planner behaves exactly as on Windows.
///
/// A server without RandR 1.5 (X.Org older than 1.18, 2015) yields an empty
/// list with a warning — xshm would fall back to CRTC or Xinerama indices
/// there, which this enumeration does not mirror, so guessing would record
/// the wrong monitor.
///
/// Wayland: always empty. Outputs cannot be addressed by a capture client;
/// the portal picker chooses (see [`DisplayCaptureMode::Picker`]), and the
/// recorder never asks for monitors in that mode.
pub fn enumerate_monitors() -> Vec<MonitorInfo> {
    match session() {
        Session::X11 => x11_monitors(),
        Session::Wayland => Vec::new(),
    }
}

fn x11_monitors() -> Vec<MonitorInfo> {
    let display = unsafe { XOpenDisplay(std::ptr::null()) };
    if display.is_null() {
        return Vec::new();
    }
    let monitors = unsafe { read_randr_monitors(display) };
    unsafe { XCloseDisplay(display) };
    monitors
}

/// # Safety
/// `display` must be a live Xlib connection.
unsafe fn read_randr_monitors(display: *mut XDisplay) -> Vec<MonitorInfo> {
    let (mut major, mut minor) = (0, 0);
    if XRRQueryVersion(display, &mut major, &mut minor) == 0 || (major, minor) < (1, 5) {
        eprintln!(
            "Warning: the X server lacks RandR 1.5 (reports {major}.{minor}); \
             monitors cannot be enumerated"
        );
        return Vec::new();
    }

    let mut count: c_int = 0;
    let list = XRRGetMonitors(display, XDefaultRootWindow(display), 1, &mut count);
    if list.is_null() {
        return Vec::new();
    }
    let entries = std::slice::from_raw_parts(list, count.max(0) as usize);
    let monitors = entries
        .iter()
        .enumerate()
        .map(|(index, m)| {
            let name_ptr = XGetAtomName(display, m.name);
            let name = if name_ptr.is_null() {
                String::new()
            } else {
                let name = CStr::from_ptr(name_ptr).to_string_lossy().into_owned();
                XFree(name_ptr.cast());
                name
            };
            MonitorInfo {
                // A nameless monitor (never seen in practice) falls back to
                // its index, which match_monitor resolves the same way.
                id: if name.is_empty() {
                    index.to_string()
                } else {
                    name
                },
                alt_id: Some(index.to_string()),
                x: m.x,
                y: m.y,
                width: m.width.max(0) as u32,
                height: m.height.max(0) as u32,
                scale: 1.0,
                is_primary: m.primary != 0,
            }
        })
        .collect();
    XRRFreeMonitors(list);
    monitors
}

pub fn find_monitor(id: &str) -> Option<MonitorInfo> {
    super::match_monitor(id, &enumerate_monitors())
}

/// The input-capture header's per-monitor `scale` (DPI zoom). X11 exposes no
/// per-monitor zoom to clients (toolkits scale by their own settings), and the
/// input-capture sidecar is rejected on Linux anyway, so 1.0.
pub fn monitor_display_scale(_m: &MonitorInfo) -> f64 {
    1.0
}

/// Always adapter 0. libobs-opengl renders through whichever GPU the EGL
/// display resolves to and ignores `obs_video_info.adapter` entirely, so there
/// is no choice to make — and answering `Some(0)` rather than `None` keeps the
/// recorder from printing its "no adapter could be matched" warning, which is
/// about the Windows DXGI duplicator and would only mislead here.
pub fn region_adapter_index(
    _region: Rect,
    _plan: &RegionPlan,
    _monitors: &[MonitorInfo],
) -> Option<u32> {
    Some(0)
}

/// Signature parity with the Windows module (DESIGN §2.2). Nothing to
/// resolve: neither Linux capture source has a capture-method knob, so every
/// value passes through unchanged (and `display_capture_settings` ignores it).
pub fn resolve_capture_method(method: CaptureMethod) -> CaptureMethod {
    method
}

/// X11: `xshm_input_v2` settings capturing monitor `m`. `screen` is the RandR
/// monitor index [`enumerate_monitors`] stored in `alt_id` — it must be set
/// explicitly, because the v2 source defaults it to -1 and captures nothing.
/// The `cut_*` crop keys stay at 0: regions are cut by offsetting the scene
/// item on a region-sized canvas, the same mechanism as every other platform.
///
/// Wayland: the portal source ignores which monitor was asked for (the user
/// picks in the dialog), so this is the cursor setting alone — the recorder
/// does not build per-monitor sources in that mode at all.
///
/// `method` is accepted for signature parity and ignored (see
/// [`resolve_capture_method`]).
pub fn display_capture_settings(
    m: &MonitorInfo,
    show_cursor: bool,
    _method: CaptureMethod,
) -> ObsData {
    match session() {
        Session::X11 => {
            let screen: i64 = m
                .alt_id
                .as_deref()
                .and_then(|s| s.parse().ok())
                .unwrap_or_else(|| {
                    panic!(
                        "monitor '{}' carries no RandR index; X11 MonitorInfo values must \
                         come from enumerate_monitors",
                        m.id
                    )
                });
            let settings = ObsData::new();
            settings.set_int("screen", screen);
            settings.set_bool("show_cursor", show_cursor);
            settings
        }
        Session::Wayland => cursor_update_settings(show_cursor),
    }
}

/// Partial `obs_source_update` payload toggling cursor capture on an existing
/// display source; both sources apply it live. The key differs per source:
/// xshm's `show_cursor`, the portal source's `ShowCursor`. On Wayland this is
/// also the portal source's complete creation settings (the only other key,
/// `RestoreToken`, is out of scope), which is why the recorder creates it from
/// this payload. When the portal supports cursor metadata — every current
/// backend — OBS always requests it and draws the pointer itself, so
/// `ShowCursor=false` reliably hides it.
pub fn cursor_update_settings(show_cursor: bool) -> ObsData {
    let settings = ObsData::new();
    match session() {
        Session::X11 => settings.set_bool("show_cursor", show_cursor),
        Session::Wayland => settings.set_bool("ShowCursor", show_cursor),
    }
    settings
}

/// The self-contained bundle layout obs-express's build script stages (and
/// the release zip ships), mirroring Windows minus its `64bit` level:
///
/// ```text
/// <exe dir>/obs-plugins/<plugin>.so
/// <exe dir>/data/obs-plugins/<plugin>/...
/// <exe dir>/data/libobs/...
/// ```
///
/// with the same `OBS_PLUGIN_PATH` / `OBS_PLUGIN_DATA_PATH` / `OBS_DATA_PATH`
/// overrides. libobs globs `<module_bin>/*.so` when the bin path carries no
/// `%module%` token (obs-module.c), which is exactly the flat plugin dir.
pub fn default_obs_paths(exe_dir: &Path) -> ObsPaths {
    let module_bin = env::var("OBS_PLUGIN_PATH")
        .unwrap_or_else(|_| exe_dir.join("obs-plugins").to_string_lossy().into_owned());
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
        module_data: format!("{module_data_base}/%module%"),
        libobs_data: Some(libobs_data),
    }
}

pub fn exit_process(code: i32) -> ! {
    unsafe { libc::_exit(code) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn wayland_wins_whenever_it_is_advertised() {
        assert_eq!(detect_session(Some("wayland"), None), Session::Wayland);
        assert_eq!(detect_session(Some("Wayland"), None), Session::Wayland);
        assert_eq!(detect_session(None, Some("wayland-0")), Session::Wayland);
        // XWayland-style: a Wayland socket plus a (non-wayland) session type.
        assert_eq!(
            detect_session(Some("x11"), Some("wayland-1")),
            Session::Wayland
        );
    }

    #[test]
    fn x11_otherwise() {
        assert_eq!(detect_session(Some("x11"), None), Session::X11);
        assert_eq!(detect_session(None, None), Session::X11);
        assert_eq!(detect_session(Some("tty"), Some("")), Session::X11);
    }

    #[test]
    fn xrr_monitor_info_layout() {
        // name (8) + 9 ints (36) + padding (4) + outputs pointer (8), as in
        // Xrandr.h on LP64.
        assert_eq!(std::mem::size_of::<XRRMonitorInfo>(), 56);
        assert_eq!(std::mem::offset_of!(XRRMonitorInfo, x), 20);
        assert_eq!(std::mem::offset_of!(XRRMonitorInfo, outputs), 48);
    }

    #[test]
    fn default_paths_follow_the_bundle_layout() {
        // Only meaningful without the env overrides; skip the assertions a
        // developer's override would legitimately change.
        let paths = default_obs_paths(Path::new("/opt/obs-express"));
        if env::var("OBS_PLUGIN_PATH").is_err() {
            assert_eq!(paths.module_bin, "/opt/obs-express/obs-plugins");
        }
        if env::var("OBS_PLUGIN_DATA_PATH").is_err() {
            assert_eq!(
                paths.module_data,
                "/opt/obs-express/data/obs-plugins/%module%"
            );
        }
        if env::var("OBS_DATA_PATH").is_err() {
            assert_eq!(
                paths.libobs_data.as_deref(),
                Some(Path::new("/opt/obs-express/data/libobs"))
            );
        }
    }
}
