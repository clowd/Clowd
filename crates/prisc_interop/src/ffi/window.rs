//! Window chrome and the foreground watch (WP-WINDOW; README section 4.3). `handle` is an HWND on
//! Windows and an `NSWindow*` on macOS, borrowed for the duration of the call (README C2).

use std::ffi::c_void;

use crate::error::Error;
use crate::ffi::types::PriscRect;
use crate::panic;

/// The foreground watch handle (Windows). Destroy it on the thread that created it.
pub struct PriscForegroundWatch {
    _private: [u8; 0],
}

/// Called (synchronously, on the creating thread's message loop) whenever the foreground window
/// changes. May call exports (README R20).
pub type PriscForegroundCallback = Option<unsafe extern "C" fn(user: *mut c_void)>;

/// The open-time setup of an overlay window, as `PriscOverlayFlags` bits: `CoverMenuBar` (macOS
/// overlay level and collection behaviour), `OpaqueLayered` (Windows), `ClickThrough` (both). A bit
/// that does not apply on an OS is a truthful no-op.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_STATE` (macOS off the main thread), `PRISC_E_OS`.
///
/// Thread: must be the macOS main thread; any thread elsewhere.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_prepare_overlay(handle: isize, flags: u32) -> i32 {
    panic::guard("prisc_window_prepare_overlay", || {
        let _ = (handle, flags);
        Err(Error::unsupported("prisc_window_prepare_overlay is not implemented yet"))
    })
}

/// Turns mouse pass-through on or off live (`WS_EX_TRANSPARENT` with `SWP_FRAMECHANGED` /
/// `setIgnoresMouseEvents:`).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_STATE`, `PRISC_E_OS`.
///
/// Thread: must be the macOS main thread; any thread elsewhere.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_set_click_through(handle: isize, on: u8) -> i32 {
    panic::guard("prisc_window_set_click_through", || {
        let _ = (handle, on);
        Err(Error::unsupported("prisc_window_set_click_through is not implemented yet"))
    })
}

/// Raises the window above every overlay without activating it (`HWND_TOPMOST` + `SWP_NOACTIVATE` /
/// the above-overlay level + `orderFrontRegardless`).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_STATE`, `PRISC_E_OS`.
///
/// Thread: must be the macOS main thread; any thread elsewhere.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_raise_topmost_no_activate(handle: isize) -> i32 {
    panic::guard("prisc_window_raise_topmost_no_activate", || {
        let _ = handle;
        Err(Error::unsupported("prisc_window_raise_topmost_no_activate is not implemented yet"))
    })
}

/// Excludes the window from screen capture or includes it again (`WDA_EXCLUDEFROMCAPTURE` /
/// `setSharingType:`).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_STATE`, `PRISC_E_OS`.
///
/// Thread: must be the macOS main thread; any thread elsewhere.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_set_excluded_from_capture(handle: isize, excluded: u8) -> i32 {
    panic::guard("prisc_window_set_excluded_from_capture", || {
        let _ = (handle, excluded);
        Err(Error::unsupported("prisc_window_set_excluded_from_capture is not implemented yet"))
    })
}

/// Whether the window has the keyboard: `GetForegroundWindow() == hwnd` / app active and window key.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_STATE`.
///
/// Thread: must be the macOS main thread; any thread elsewhere.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_is_foreground(handle: isize, out_foreground: *mut u8) -> i32 {
    panic::guard("prisc_window_is_foreground", || {
        let _ = (handle, out_foreground);
        Err(Error::unsupported("prisc_window_is_foreground is not implemented yet"))
    })
}

/// Brings the window to the front with the keyboard, in one call (the Alt-key foreground unlock on
/// Windows). macOS answers `PRISC_E_UNSUPPORTED` this phase; callers keep their Avalonia path.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_UNSUPPORTED` (macOS).
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_take_foreground(handle: isize, out_in_front: *mut u8) -> i32 {
    panic::guard("prisc_window_take_foreground", || {
        let _ = (handle, out_in_front);
        Err(Error::unsupported("prisc_window_take_foreground is not implemented yet"))
    })
}

/// Moves and resizes the window in one step (`SetWindowPos` with `SWP_NOZORDER | SWP_NOACTIVATE`).
/// macOS answers `PRISC_E_UNSUPPORTED` this phase.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_OS`, `PRISC_E_UNSUPPORTED` (macOS).
///
/// Thread: must be the macOS main thread; any thread elsewhere.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_set_bounds(handle: isize, rect: *const PriscRect) -> i32 {
    panic::guard("prisc_window_set_bounds", || {
        let _ = (handle, rect);
        Err(Error::unsupported("prisc_window_set_bounds is not implemented yet"))
    })
}

/// Windows: `DefWindowProcW` for a message a window procedure hook passes on.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: the thread that owns the window (inside its window procedure).
#[no_mangle]
pub unsafe extern "C" fn prisc_window_default_proc_windows(
    hwnd: isize,
    msg: u32,
    wparam: usize,
    lparam: isize,
    out_result: *mut isize,
) -> i32 {
    panic::guard("prisc_window_default_proc_windows", || {
        let _ = (hwnd, msg, wparam, lparam, out_result);
        Err(Error::unsupported("prisc_window_default_proc_windows is not implemented yet"))
    })
}

/// Windows: watches foreground changes (`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`, out of
/// context). The registry lock is released before `callback` runs (README C3).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_OS`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: a thread that pumps window messages; the callback runs on that thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_foreground_watch_create_windows(
    callback: PriscForegroundCallback,
    user: *mut c_void,
    out_watch: *mut *mut PriscForegroundWatch,
) -> i32 {
    panic::guard("prisc_window_foreground_watch_create_windows", || {
        let _ = (callback, user, out_watch);
        Err(Error::unsupported(
            "prisc_window_foreground_watch_create_windows is not implemented yet",
        ))
    })
}

/// Windows: ends a foreground watch (`UnhookWinEvent`) on its creating thread; null is `PRISC_OK`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_STATE` (wrong thread), `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: a thread that pumps window messages; the callback runs on that thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_foreground_watch_destroy_windows(watch: *mut PriscForegroundWatch) -> i32 {
    panic::guard("prisc_window_foreground_watch_destroy_windows", || {
        let _ = watch;
        Err(Error::unsupported(
            "prisc_window_foreground_watch_destroy_windows is not implemented yet",
        ))
    })
}

/// macOS: moves the close / minimise / zoom buttons to the given insets from the top-left corner.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_STATE`, `PRISC_E_UNSUPPORTED` off macOS.
///
/// Thread: must be the macOS main thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_set_traffic_light_position_macos(nswindow: isize, left_inset: f64, top_inset: f64) -> i32 {
    panic::guard("prisc_window_set_traffic_light_position_macos", || {
        let _ = (nswindow, left_inset, top_inset);
        Err(Error::unsupported(
            "prisc_window_set_traffic_light_position_macos is not implemented yet",
        ))
    })
}
