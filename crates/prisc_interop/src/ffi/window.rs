//! Window chrome and the foreground watch (WP-WINDOW; README section 4.3). `handle` is an HWND on
//! Windows and an `NSWindow*` on macOS, borrowed for the duration of the call (README C2): nothing
//! is retained, and the caller must not pass a window that has closed.
//!
//! Argument order is the same in every export here, and the tests depend on it: a zero handle is
//! refused first, in the platform-independent prologue, so it is `PRISC_E_INVALID_ARG` on every OS
//! and every thread; the macOS main-thread gate runs second, still before the pointer is touched,
//! so a non-zero handle on a worker thread is `PRISC_E_STATE` and is never dereferenced. The
//! `_windows` / `_macos` exports answer `PRISC_E_UNSUPPORTED` off their OS before checking anything
//! else (destroying a null watch is `PRISC_OK` everywhere, README C1).

use std::ffi::c_void;

use crate::error::{Error, Result};
use crate::ffi::status::PRISC_OK;
use crate::ffi::types::{PriscOverlayFlags, PriscRect};
use crate::{convert, panic, util};

#[cfg(target_os = "macos")]
use crate::platform::macos::window as imp;
#[cfg(windows)]
use crate::platform::windows::window as imp;

/// The foreground watch handle (Windows). Destroy it on the thread that created it.
pub struct PriscForegroundWatch {
    _private: [u8; 0],
}

/// Called (synchronously, on the creating thread's message loop) whenever the foreground window
/// changes. May call exports (README R20).
pub type PriscForegroundCallback = Option<unsafe extern "C" fn(user: *mut c_void)>;

/// Every bit `PriscOverlayFlags` defines.
pub(crate) const OVERLAY_FLAGS_KNOWN: u32 = PriscOverlayFlags::CoverMenuBar as u32
    | PriscOverlayFlags::OpaqueLayered as u32
    | PriscOverlayFlags::ClickThrough as u32
    | PriscOverlayFlags::TakesMouse as u32;

/// The open-time setup of an overlay window, as `PriscOverlayFlags` bits, applied in one call:
/// - `CoverMenuBar` (macOS): the overlay level (25) and collection behaviour (Stationary,
///   CanJoinAllSpaces, FullScreenAuxiliary, IgnoresCycle) of `prisc_rust_core::desktop::overlay`,
///   so the window may cover the menu bar, fullscreen apps and every Space. Apply it before
///   positioning the window: below the menu bar level AppKit constrains the frame away from the bar.
/// - `OpaqueLayered` (Windows): `SetLayeredWindowAttributes(255, LWA_ALPHA)`, the mandatory
///   follow-up to `WS_EX_LAYERED` (a layered window without it is never repainted).
/// - `ClickThrough` (both): mouse events pass through (`WS_EX_TRANSPARENT` set live /
///   `setIgnoresMouseEvents:YES`).
/// - `TakesMouse` (both): the window takes the mouse (`WS_EX_TRANSPARENT` cleared live /
///   `setIgnoresMouseEvents:NO`), over its transparent pixels too, which AppKit's never-set default
///   lets through. With neither mouse bit the window's mouse state is left alone.
///
/// A bit that does not apply on an OS is a truthful no-op there, so `None` is a valid no-op too.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a zero handle, an unknown bit or `ClickThrough`
/// with `TakesMouse`; `PRISC_E_STATE` off the macOS main thread; `PRISC_E_OS` when a Windows call
/// failed (the code is the Win32 error as an HRESULT); `PRISC_E_UNSUPPORTED` on a platform with
/// neither window system.
///
/// Thread: must be the macOS main thread; any thread elsewhere.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_prepare_overlay(handle: isize, flags: u32) -> i32 {
    panic::guard("prisc_window_prepare_overlay", || {
        window_handle(handle)?;
        let flags = convert::flags(flags, OVERLAY_FLAGS_KNOWN, "flags")?;
        let has = |bit: PriscOverlayFlags| flags & bit as u32 != 0;
        let click_through = match (has(PriscOverlayFlags::ClickThrough), has(PriscOverlayFlags::TakesMouse)) {
            (true, true) => return Err(Error::invalid_arg("flags: ClickThrough and TakesMouse are exclusive")),
            (true, false) => Some(true),
            (false, true) => Some(false),
            (false, false) => None,
        };
        #[cfg(any(windows, target_os = "macos"))]
        {
            imp::prepare_overlay(
                handle,
                has(PriscOverlayFlags::CoverMenuBar),
                has(PriscOverlayFlags::OpaqueLayered),
                click_through,
            )?;
            Ok(PRISC_OK)
        }
        #[cfg(not(any(windows, target_os = "macos")))]
        {
            let _ = click_through;
            Err(no_window_system("prisc_window_prepare_overlay"))
        }
    })
}

/// Turns mouse pass-through on or off live: `WS_EX_TRANSPARENT` set or cleared with
/// `SWP_FRAMECHANGED` (a no-op when the bit already has that value) / `setIgnoresMouseEvents:`.
/// Turning it off sends an explicit `NO` on macOS, which also takes the mouse over transparent
/// pixels (AppKit's never-set default lets those through, a third state a window does not return
/// to once `YES` was sent). On Windows the change lasts until Avalonia next re-applies the window
/// styles, so a window that toggles it keeps a styles callback that re-asserts the current state.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a zero handle; `PRISC_E_STATE` off the macOS main
/// thread; `PRISC_E_OS` when a Windows call failed; `PRISC_E_UNSUPPORTED` on a platform with neither
/// window system.
///
/// Thread: must be the macOS main thread; any thread elsewhere.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_set_click_through(handle: isize, on: u8) -> i32 {
    panic::guard("prisc_window_set_click_through", || {
        window_handle(handle)?;
        let on = util::flag(on);
        #[cfg(any(windows, target_os = "macos"))]
        {
            imp::set_click_through(handle, on)?;
            Ok(PRISC_OK)
        }
        #[cfg(not(any(windows, target_os = "macos")))]
        {
            let _ = on;
            Err(no_window_system("prisc_window_set_click_through"))
        }
    })
}

/// Raises the window above every overlay without activating it, moving it or resizing it:
/// `SetWindowPos(HWND_TOPMOST, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)` (among topmost peers the
/// later one wins) / the above-overlay level (26) + `orderFrontRegardless` (a window that can become
/// key orders itself to the front of its own level, so a same-level sibling would sink again on the
/// first click into it). The raise stands for the life of the window.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a zero handle; `PRISC_E_STATE` off the macOS main
/// thread; `PRISC_E_OS` when `SetWindowPos` failed; `PRISC_E_UNSUPPORTED` on a platform with neither
/// window system.
///
/// Thread: must be the macOS main thread; any thread elsewhere.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_raise_topmost_no_activate(handle: isize) -> i32 {
    panic::guard("prisc_window_raise_topmost_no_activate", || {
        window_handle(handle)?;
        #[cfg(any(windows, target_os = "macos"))]
        {
            imp::raise_topmost_no_activate(handle)?;
            Ok(PRISC_OK)
        }
        #[cfg(not(any(windows, target_os = "macos")))]
        {
            Err(no_window_system("prisc_window_raise_topmost_no_activate"))
        }
    })
}

/// Hides the window from screen capture or shows it again: `SetWindowDisplayAffinity` with
/// `WDA_EXCLUDEFROMCAPTURE` / `WDA_NONE` (honoured by every capture API from Windows 10 2004; older
/// builds refuse the exclude value, which is `PRISC_E_OS`, and `WDA_MONITOR` is deliberately not a
/// fallback because it blacks the window out instead of removing it) / `setSharingType:` with
/// `NSWindowSharingNone` / AppKit's default `NSWindowSharingReadOnly`.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a zero handle; `PRISC_E_STATE` off the macOS main
/// thread; `PRISC_E_OS` when `SetWindowDisplayAffinity` failed; `PRISC_E_UNSUPPORTED` on a platform
/// with neither window system.
///
/// Thread: must be the macOS main thread; any thread elsewhere.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_set_excluded_from_capture(handle: isize, excluded: u8) -> i32 {
    panic::guard("prisc_window_set_excluded_from_capture", || {
        window_handle(handle)?;
        let excluded = util::flag(excluded);
        #[cfg(any(windows, target_os = "macos"))]
        {
            imp::set_excluded_from_capture(handle, excluded)?;
            Ok(PRISC_OK)
        }
        #[cfg(not(any(windows, target_os = "macos")))]
        {
            let _ = excluded;
            Err(no_window_system("prisc_window_set_excluded_from_capture"))
        }
    })
}

/// Whether the window has the keyboard right now: `GetForegroundWindow() == hwnd` on Windows (not
/// the window's active state, which a `SetForegroundWindow` refused by the foreground lock still
/// sets) / `NSApp.isActive && window.isKeyWindow` on macOS. `out_foreground` is 1 or 0.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a zero handle or a null `out_foreground`;
/// `PRISC_E_STATE` off the macOS main thread; `PRISC_E_UNSUPPORTED` on a platform with neither
/// window system.
///
/// Thread: must be the macOS main thread; any thread elsewhere.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_is_foreground(handle: isize, out_foreground: *mut u8) -> i32 {
    panic::guard("prisc_window_is_foreground", || {
        window_handle(handle)?;
        let out_foreground = util::out(out_foreground, "out_foreground")?;
        #[cfg(any(windows, target_os = "macos"))]
        {
            *out_foreground = util::to_flag(imp::is_foreground(handle)?);
            Ok(PRISC_OK)
        }
        #[cfg(not(any(windows, target_os = "macos")))]
        {
            let _ = out_foreground;
            Err(no_window_system("prisc_window_is_foreground"))
        }
    })
}

/// Brings the window to the front with the keyboard, in one call, and says whether it ended up
/// there (`out_in_front`, 1 or 0). Windows: nothing when it already is; then a plain
/// `SetForegroundWindow` (allowed after a click on one of our windows); failing that, the
/// foreground lock is lifted by making this process the last to send input: Alt down (unless the
/// user is physically holding it, part of the hotkey), the unassigned key 0xE8 down and up so the
/// Alt reads as part of a chord and no app enters menu mode, `SetForegroundWindow`, Alt up. macOS
/// answers `PRISC_E_UNSUPPORTED` this phase; callers keep their Avalonia activation there.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a zero handle or a null `out_in_front`;
/// `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_take_foreground(handle: isize, out_in_front: *mut u8) -> i32 {
    panic::guard("prisc_window_take_foreground", || {
        window_handle(handle)?;
        let out_in_front = util::out(out_in_front, "out_in_front")?;
        #[cfg(windows)]
        {
            *out_in_front = util::to_flag(imp::take_foreground(handle));
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            *out_in_front = 0;
            Err(Error::unsupported(
                "prisc_window_take_foreground is Windows only this phase; activate the window through the toolkit",
            ))
        }
    })
}

/// Moves and resizes the window in one step: `SetWindowPos(x, y, width, height, SWP_NOZORDER |
/// SWP_NOACTIVATE | SWP_NOOWNERZORDER)`, `rect` in physical virtual-desktop pixels. Atomic where
/// a position set and a size set a layout pass apart would make the window slide and then snap.
/// macOS answers `PRISC_E_UNSUPPORTED` this phase (the caller keeps its toolkit path there).
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a zero handle, a null `rect` or a negative size;
/// `PRISC_E_OS` when `SetWindowPos` failed; `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: must be the macOS main thread; any thread elsewhere.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_set_bounds(handle: isize, rect: *const PriscRect) -> i32 {
    panic::guard("prisc_window_set_bounds", || {
        window_handle(handle)?;
        let rect = *util::input(rect, "rect")?;
        if rect.width < 0 || rect.height < 0 {
            return Err(Error::invalid_arg(format!(
                "rect has a negative size ({} x {})",
                rect.width, rect.height
            )));
        }
        #[cfg(windows)]
        {
            imp::set_bounds(handle, rect)?;
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            Err(Error::unsupported(
                "prisc_window_set_bounds is Windows only this phase; set the bounds through the toolkit",
            ))
        }
    })
}

/// Windows: `DefWindowProcW` for a message a window procedure hook hands on (the Alt+Space system
/// menu that Avalonia's own procedure would swallow); its `LRESULT` goes to `out_result`.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a zero `hwnd` or a null `out_result`;
/// `PRISC_E_UNSUPPORTED` off Windows.
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
        #[cfg(windows)]
        {
            window_handle(hwnd)?;
            let out_result = util::out(out_result, "out_result")?;
            *out_result = imp::default_proc(hwnd, msg, wparam, lparam);
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            let _ = (hwnd, msg, wparam, lparam, out_result);
            Err(windows_only("prisc_window_default_proc_windows"))
        }
    })
}

/// Windows: calls `callback(user)` whenever the system's foreground window changes, to any window
/// of any process, until destroyed (`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`, out of context, so
/// the events arrive through the creating thread's message loop and the callback runs there,
/// synchronously, outside any export: it may call exports, README R20). WINEVENTPROC has no user
/// parameter, so the crate keeps a hook-to-(callback, user) registry; its lock is released before
/// `callback` runs, so destroying the watch from inside the callback cannot deadlock (README C3).
/// `*out_watch` is written on `PRISC_OK` and set to null otherwise.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null `callback` or `out_watch`; `PRISC_E_OS` when
/// `SetWinEventHook` refused; `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: a thread that pumps window messages; the callback runs on that thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_foreground_watch_create_windows(
    callback: PriscForegroundCallback,
    user: *mut c_void,
    out_watch: *mut *mut PriscForegroundWatch,
) -> i32 {
    panic::guard("prisc_window_foreground_watch_create_windows", || {
        util::write_optional(out_watch, std::ptr::null_mut());
        #[cfg(windows)]
        {
            let out_watch = util::out(out_watch, "out_watch")?;
            let callback = callback.ok_or_else(|| Error::null("callback"))?;
            *out_watch = imp::foreground_watch_create(callback, user)?.cast();
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            let _ = (callback, user);
            Err(windows_only("prisc_window_foreground_watch_create_windows"))
        }
    })
}

/// Windows: ends a foreground watch (`UnhookWinEvent`) and frees it; no callback runs once this
/// returns. Must run on the thread that created the watch, since the hook belongs to that thread's
/// message loop. A null `watch` is `PRISC_OK` on every OS. A panic caught in the watch's callback
/// path is reported here, after the watch is gone, as `PRISC_E_PANIC`.
///
/// Status: `PRISC_OK`; `PRISC_E_STATE` on another thread (the watch is left intact);
/// `PRISC_E_PANIC` for a panic recorded by the callback path; `PRISC_E_UNSUPPORTED` off Windows for
/// a non-null `watch`.
///
/// Thread: a thread that pumps window messages; the callback runs on that thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_foreground_watch_destroy_windows(watch: *mut PriscForegroundWatch) -> i32 {
    panic::guard("prisc_window_foreground_watch_destroy_windows", || {
        if watch.is_null() {
            return Ok(PRISC_OK);
        }
        #[cfg(windows)]
        {
            imp::foreground_watch_destroy(watch.cast())?;
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            Err(windows_only("prisc_window_foreground_watch_destroy_windows"))
        }
    })
}

/// macOS: places the close, minimise and zoom buttons at `left_inset` (left edge of the close
/// button) and `top_inset` (top edge of all three), in points from the window's top-left, instead
/// of the (9, 9) AppKit gives a bare window. The three are translated as a unit, so AppKit's own
/// spacing and button size (the small variant under a title bar shorter than 32 pt) are kept, and a
/// repeated call computes a zero shift. AppKit resets the frames on every title bar relayout (a
/// resize, zoom, minimise and restore, either fullscreen transition), so callers re-apply after
/// those. objc2 handles the 32-byte `frame` struct return on both arm64 and x86_64.
///
/// Status: `PRISC_OK`; `PRISC_S_NONE` when the window has no standard buttons or no title bar
/// height, so nothing moved; `PRISC_E_INVALID_ARG` for a zero handle or a non-finite inset;
/// `PRISC_E_STATE` off the main thread; `PRISC_E_UNSUPPORTED` off macOS.
///
/// Thread: must be the macOS main thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_window_set_traffic_light_position_macos(nswindow: isize, left_inset: f64, top_inset: f64) -> i32 {
    panic::guard("prisc_window_set_traffic_light_position_macos", || {
        #[cfg(target_os = "macos")]
        {
            window_handle(nswindow)?;
            if !left_inset.is_finite() || !top_inset.is_finite() {
                return Err(Error::invalid_arg(format!(
                    "the traffic light insets must be finite ({left_inset}, {top_inset})"
                )));
            }
            imp::set_traffic_light_position(nswindow, left_inset, top_inset)
        }
        #[cfg(not(target_os = "macos"))]
        {
            let _ = (nswindow, left_inset, top_inset);
            Err(Error::unsupported("prisc_window_set_traffic_light_position_macos is macOS only"))
        }
    })
}

/// The shared prologue: a zero window handle is refused before anything else.
fn window_handle(handle: isize) -> Result<()> {
    if handle == 0 {
        Err(Error::invalid_arg("the window handle was zero"))
    } else {
        Ok(())
    }
}

#[allow(dead_code)] // the off-Windows arms use it
fn windows_only(export: &str) -> Error {
    Error::unsupported(format!("{export} is Windows only"))
}

#[allow(dead_code)] // the arms for a platform with neither window system use it
fn no_window_system(export: &str) -> Error {
    Error::unsupported(format!("{export}: this platform has no supported window system"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status::PriscInteropStatus as S;

    #[test]
    fn the_known_overlay_bits_are_the_enum_s() {
        assert_eq!(OVERLAY_FLAGS_KNOWN, 0b1111);
    }

    #[test]
    fn a_zero_handle_is_invalid_arg_on_every_thread() {
        // libtest runs this on a spawned thread, so on macOS the zero check must precede the
        // main-thread gate for these to read INVALID_ARG.
        let mut flag = 7u8;
        let rect = PriscRect::default();
        assert_eq!(unsafe { prisc_window_prepare_overlay(0, 0) }, S::InvalidArg as i32);
        assert_eq!(unsafe { prisc_window_set_click_through(0, 1) }, S::InvalidArg as i32);
        assert_eq!(unsafe { prisc_window_raise_topmost_no_activate(0) }, S::InvalidArg as i32);
        assert_eq!(unsafe { prisc_window_set_excluded_from_capture(0, 1) }, S::InvalidArg as i32);
        assert_eq!(unsafe { prisc_window_is_foreground(0, &mut flag) }, S::InvalidArg as i32);
        assert_eq!(unsafe { prisc_window_take_foreground(0, &mut flag) }, S::InvalidArg as i32);
        assert_eq!(unsafe { prisc_window_set_bounds(0, &rect) }, S::InvalidArg as i32);
    }

    #[test]
    fn unknown_overlay_bits_are_refused_before_the_window_is_touched() {
        // 1 is no window at all: the flag check must answer before any OS call sees it
        assert_eq!(unsafe { prisc_window_prepare_overlay(1, 16) }, S::InvalidArg as i32);
        // and so must the exclusive pair
        let both = PriscOverlayFlags::ClickThrough as u32 | PriscOverlayFlags::TakesMouse as u32;
        assert_eq!(unsafe { prisc_window_prepare_overlay(1, both) }, S::InvalidArg as i32);
    }

    #[test]
    fn null_outs_and_bad_rects_are_invalid_arg() {
        assert_eq!(unsafe { prisc_window_is_foreground(1, std::ptr::null_mut()) }, S::InvalidArg as i32);
        assert_eq!(
            unsafe { prisc_window_take_foreground(1, std::ptr::null_mut()) },
            S::InvalidArg as i32
        );
        assert_eq!(unsafe { prisc_window_set_bounds(1, std::ptr::null()) }, S::InvalidArg as i32);
        let negative = PriscRect {
            x: 0,
            y: 0,
            width: -1,
            height: 10,
        };
        assert_eq!(unsafe { prisc_window_set_bounds(1, &negative) }, S::InvalidArg as i32);
    }

    #[test]
    fn destroying_a_null_watch_is_ok_everywhere() {
        assert_eq!(
            unsafe { prisc_window_foreground_watch_destroy_windows(std::ptr::null_mut()) },
            PRISC_OK
        );
    }
}
