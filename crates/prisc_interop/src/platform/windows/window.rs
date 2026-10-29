//! Windows: window chrome and the foreground watch (`prisc_window_*`).
//!
//! The HWND is borrowed for the call; the export prologue has already refused a zero handle. The
//! user32 calls here are callable from any thread except `DefWindowProcW` (inside the window's
//! procedure) and the WinEvent hook (on the thread whose message loop delivers it).

use std::collections::HashMap;
use std::ffi::c_void;
use std::sync::{Arc, Mutex, PoisonError};

use windows::Win32::Foundation::{GetLastError, SetLastError, COLORREF, HWND, LPARAM, WIN32_ERROR, WPARAM};
use windows::Win32::System::Threading::GetCurrentThreadId;
use windows::Win32::UI::Accessibility::{SetWinEventHook, UnhookWinEvent, HWINEVENTHOOK};
use windows::Win32::UI::Input::KeyboardAndMouse::{keybd_event, KEYBD_EVENT_FLAGS, KEYEVENTF_KEYUP, VK_MENU};
use windows::Win32::UI::WindowsAndMessaging::{
    DefWindowProcW, GetForegroundWindow, GetWindowLongPtrW, SetForegroundWindow, SetLayeredWindowAttributes, SetWindowDisplayAffinity,
    SetWindowLongPtrW, SetWindowPos, EVENT_SYSTEM_FOREGROUND, GWL_EXSTYLE, HWND_TOPMOST, LWA_ALPHA, SWP_FRAMECHANGED, SWP_NOACTIVATE,
    SWP_NOMOVE, SWP_NOOWNERZORDER, SWP_NOSIZE, SWP_NOZORDER, WDA_EXCLUDEFROMCAPTURE, WDA_NONE, WINEVENT_OUTOFCONTEXT, WS_EX_TRANSPARENT,
};

use crate::error::{Error, Result};
use crate::ffi::types::PriscRect;
use crate::panic::{self, CallbackPanic};
use crate::platform::windows::os_error;

fn hwnd(handle: isize) -> HWND {
    HWND(handle as *mut c_void)
}

/// `OpaqueLayered` then the mouse state (`Some(true)` = `ClickThrough`, `Some(false)` =
/// `TakesMouse`); `CoverMenuBar` is a macOS concept and a no-op here.
pub(crate) fn prepare_overlay(handle: isize, _cover_menu_bar: bool, opaque_layered: bool, click_through: Option<bool>) -> Result<()> {
    if opaque_layered {
        unsafe { SetLayeredWindowAttributes(hwnd(handle), COLORREF(0), 255, LWA_ALPHA) }
            .map_err(|e| os_error(&e, "SetLayeredWindowAttributes(255, LWA_ALPHA)"))?;
    }
    match click_through {
        Some(on) => set_click_through(handle, on),
        None => Ok(()),
    }
}

/// Sets or clears `WS_EX_TRANSPARENT` on the live window and makes it take effect now.
pub(crate) fn set_click_through(handle: isize, on: bool) -> Result<()> {
    let window = hwnd(handle);
    let mask = WS_EX_TRANSPARENT.0 as isize;
    // 0 is also a legal style, so a failure is told apart by the thread's last error
    unsafe { SetLastError(WIN32_ERROR(0)) };
    let ex = unsafe { GetWindowLongPtrW(window, GWL_EXSTYLE) };
    if ex == 0 {
        last_error("GetWindowLongPtrW(GWL_EXSTYLE)")?;
    }
    let next = if on { ex | mask } else { ex & !mask };
    if next == ex {
        return Ok(());
    }
    unsafe { SetLastError(WIN32_ERROR(0)) };
    if unsafe { SetWindowLongPtrW(window, GWL_EXSTYLE, next) } == 0 {
        last_error("SetWindowLongPtrW(GWL_EXSTYLE)")?;
    }
    unsafe {
        SetWindowPos(
            window,
            None,
            0,
            0,
            0,
            0,
            SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE,
        )
    }
    .map_err(|e| os_error(&e, "SetWindowPos(SWP_FRAMECHANGED)"))
}

/// `Err(PRISC_E_OS)` when the thread's last error is set (a call that answered 0 failed).
fn last_error(what: &str) -> Result<()> {
    let code = unsafe { GetLastError() };
    if code.0 == 0 {
        Ok(())
    } else {
        Err(os_error(&windows_core::Error::from(code.to_hresult()), what))
    }
}

pub(crate) fn raise_topmost_no_activate(handle: isize) -> Result<()> {
    unsafe {
        SetWindowPos(
            hwnd(handle),
            Some(HWND_TOPMOST),
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
        )
    }
    .map_err(|e| os_error(&e, "SetWindowPos(HWND_TOPMOST)"))
}

pub(crate) fn set_excluded_from_capture(handle: isize, excluded: bool) -> Result<()> {
    let affinity = if excluded { WDA_EXCLUDEFROMCAPTURE } else { WDA_NONE };
    unsafe { SetWindowDisplayAffinity(hwnd(handle), affinity) }.map_err(|e| os_error(&e, "SetWindowDisplayAffinity"))
}

pub(crate) fn is_foreground(handle: isize) -> Result<bool> {
    Ok(unsafe { GetForegroundWindow() } == hwnd(handle))
}

/// Alt, the key the foreground lock reliably yields to.
const ALT: u8 = VK_MENU.0 as u8;
/// Unassigned: AutoHotkey's default `#MenuMaskKey`, pressed and released inside the Alt so no app
/// sees a lone Alt tap and enters menu mode. The same key as prisc_hotkeys' `MASK_VK`.
const VK_MENU_MASK: u8 = 0xE8;

/// The foreground-lock dance of the export. Whether the window ended up in front.
pub(crate) fn take_foreground(handle: isize) -> bool {
    let window = hwnd(handle);
    let in_front = || unsafe { GetForegroundWindow() } == window;
    if in_front() {
        return true;
    }

    // a click on one of our windows is input we did receive: no keys needed then, and none
    // injected into the gesture it starts
    if unsafe { SetForegroundWindow(window) }.as_bool() && in_front() {
        return true;
    }

    // an Alt the user is physically holding (part of the hotkey) is left alone: injecting its
    // release would tell Windows they let go
    let alt_held = prisc_platform::input::is_key_down(u32::from(ALT)).unwrap_or(false);
    unsafe {
        if !alt_held {
            keybd_event(ALT, 0, KEYBD_EVENT_FLAGS(0), 0);
        }
        keybd_event(VK_MENU_MASK, 0, KEYBD_EVENT_FLAGS(0), 0);
        keybd_event(VK_MENU_MASK, 0, KEYEVENTF_KEYUP, 0);
        let _ = SetForegroundWindow(window);
        if !alt_held {
            keybd_event(ALT, 0, KEYEVENTF_KEYUP, 0);
        }
    }
    in_front()
}

pub(crate) fn set_bounds(handle: isize, rect: PriscRect) -> Result<()> {
    unsafe {
        SetWindowPos(
            hwnd(handle),
            None,
            rect.x,
            rect.y,
            rect.width,
            rect.height,
            SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER,
        )
    }
    .map_err(|e| os_error(&e, "SetWindowPos(SWP_NOZORDER)"))
}

pub(crate) fn default_proc(handle: isize, msg: u32, wparam: usize, lparam: isize) -> isize {
    unsafe { DefWindowProcW(hwnd(handle), msg, WPARAM(wparam), LPARAM(lparam)) }.0
}

// ---------------------------------------------------------------------------
// The foreground watch
// ---------------------------------------------------------------------------

/// What the WinEvent proc reaches through the registry. `user` is the host's opaque pointer; the
/// crate never dereferences it.
struct WatchTarget {
    callback: unsafe extern "C" fn(user: *mut c_void),
    user: *mut c_void,
    /// A panic caught on this watch's callback path, reported by its destroy.
    panic: CallbackPanic,
}

// SAFETY: `user` is only handed back to the host's callback, on the thread that created the watch
// (an out-of-context hook delivers there); the registry needs Send to sit in a static.
unsafe impl Send for WatchTarget {}
unsafe impl Sync for WatchTarget {}

/// The handle behind `PriscForegroundWatch`.
pub(crate) struct ForegroundWatch {
    hook: HWINEVENTHOOK,
    thread: u32,
    target: Arc<WatchTarget>,
}

/// Hook to target. WINEVENTPROC carries no user parameter, so the proc looks its hook up here.
static WATCHES: Mutex<Option<HashMap<isize, Arc<WatchTarget>>>> = Mutex::new(None);

fn registry() -> std::sync::MutexGuard<'static, Option<HashMap<isize, Arc<WatchTarget>>>> {
    WATCHES
        .lock()
        .unwrap_or_else(PoisonError::into_inner)
}

pub(crate) fn foreground_watch_create(
    callback: unsafe extern "C" fn(user: *mut c_void),
    user: *mut c_void,
) -> Result<*mut ForegroundWatch> {
    let hook = unsafe {
        SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            None,
            Some(foreground_proc),
            0,
            0,
            WINEVENT_OUTOFCONTEXT,
        )
    };
    if hook.is_invalid() {
        let code = unsafe { GetLastError() };
        return Err(Error::os(
            i64::from(code.to_hresult().0),
            format!("SetWinEventHook(EVENT_SYSTEM_FOREGROUND) failed (Win32 error {})", code.0),
        ));
    }

    let target = Arc::new(WatchTarget {
        callback,
        user,
        panic: CallbackPanic::new(),
    });
    // Inserted before this returns, and so before the creating thread can pump the first event.
    registry()
        .get_or_insert_with(HashMap::new)
        .insert(hook.0 as isize, Arc::clone(&target));
    Ok(Box::into_raw(Box::new(ForegroundWatch {
        hook,
        thread: unsafe { GetCurrentThreadId() },
        target,
    })))
}

/// # Safety
/// `watch` came from [`foreground_watch_create`] and was not destroyed yet.
pub(crate) unsafe fn foreground_watch_destroy(watch: *mut ForegroundWatch) -> Result<()> {
    let current = GetCurrentThreadId();
    if (*watch).thread != current {
        return Err(Error::state(format!(
            "a foreground watch must be destroyed on the thread that created it (thread {}, called on {current})",
            (*watch).thread
        )));
    }
    let watch = Box::from_raw(watch);
    if let Some(map) = registry().as_mut() {
        map.remove(&(watch.hook.0 as isize));
    }
    let _ = UnhookWinEvent(watch.hook);
    watch.target.panic.check()
}

/// The WinEvent proc. The registry lookup runs under the process slot (no handle is known yet),
/// the host callback under the watch's own slot, and the registry lock is released before the
/// callback runs, so the host may destroy the watch from inside it.
unsafe extern "system" fn foreground_proc(
    hook: HWINEVENTHOOK,
    _event: u32,
    _hwnd: HWND,
    _id_object: i32,
    _id_child: i32,
    _event_thread: u32,
    _event_time: u32,
) {
    panic::callback_guard(CallbackPanic::process(), (), || {
        let target = registry()
            .as_ref()
            .and_then(|map| map.get(&(hook.0 as isize)).cloned());
        if let Some(target) = target {
            panic::callback_guard(&target.panic, (), || unsafe { (target.callback)(target.user) });
        }
    })
}

#[cfg(test)]
mod tests {
    use std::ptr::null_mut;
    use std::sync::atomic::{AtomicI32, AtomicPtr, AtomicUsize, Ordering};

    use super::*;
    use crate::ffi::status::PriscInteropStatus as S;
    use crate::ffi::window::prisc_window_foreground_watch_destroy_windows;

    /// What the test callbacks reach through `user`.
    #[derive(Default)]
    struct Seen {
        calls: AtomicUsize,
        /// The `user` each call received, to check it is the pointer the create was given.
        user: AtomicPtr<c_void>,
        /// Set for the case that destroys its own watch from inside the callback.
        watch: AtomicPtr<ForegroundWatch>,
        destroy_status: AtomicI32,
    }

    unsafe extern "C" fn count(user: *mut c_void) {
        let seen = &*(user as *const Seen);
        seen.calls.fetch_add(1, Ordering::SeqCst);
        seen.user.store(user, Ordering::SeqCst);
    }

    unsafe extern "C" fn count_then_destroy(user: *mut c_void) {
        count(user);
        let seen = &*(user as *const Seen);
        let watch = seen.watch.swap(null_mut(), Ordering::SeqCst);
        if !watch.is_null() {
            let status = prisc_window_foreground_watch_destroy_windows(watch.cast());
            seen.destroy_status
                .store(status, Ordering::SeqCst);
        }
    }

    /// What the OS does on a foreground change, without waiting for one: the proc with this hook.
    fn fire(hook: HWINEVENTHOOK) {
        unsafe { foreground_proc(hook, EVENT_SYSTEM_FOREGROUND, HWND::default(), 0, 0, 0, 0) }
    }

    fn registered(hook: HWINEVENTHOOK) -> bool {
        registry()
            .as_ref()
            .is_some_and(|map| map.contains_key(&(hook.0 as isize)))
    }

    #[test]
    fn the_proc_hands_the_callback_its_user_pointer_and_stops_after_destroy() {
        let seen = Seen::default();
        let user = &seen as *const Seen as *mut c_void;
        let watch = foreground_watch_create(count, user).expect("SetWinEventHook");
        let hook = unsafe { (*watch).hook };
        assert!(registered(hook), "the registry is keyed by the hook the proc receives");

        fire(hook);
        fire(hook);
        assert_eq!(seen.calls.load(Ordering::SeqCst), 2);
        assert_eq!(seen.user.load(Ordering::SeqCst), user);

        assert_eq!(unsafe { prisc_window_foreground_watch_destroy_windows(watch.cast()) }, S::Ok as i32);
        assert!(!registered(hook), "destroy leaves no stale registry entry");
        fire(hook);
        assert_eq!(seen.calls.load(Ordering::SeqCst), 2, "no callback after destroy");
    }

    #[test]
    fn a_callback_may_destroy_its_own_watch() {
        let seen = Seen::default();
        let user = &seen as *const Seen as *mut c_void;
        let watch = foreground_watch_create(count_then_destroy, user).expect("SetWinEventHook");
        let hook = unsafe { (*watch).hook };
        seen.watch.store(watch, Ordering::SeqCst);
        seen.destroy_status
            .store(i32::MIN, Ordering::SeqCst);

        // the registry lock is released before the callback, so this returns instead of deadlocking
        fire(hook);
        assert_eq!(seen.calls.load(Ordering::SeqCst), 1);
        assert_eq!(seen.destroy_status.load(Ordering::SeqCst), S::Ok as i32);
        assert!(!registered(hook));

        fire(hook);
        assert_eq!(seen.calls.load(Ordering::SeqCst), 1, "no callback after the in-callback destroy");
    }

    #[test]
    fn an_unknown_hook_calls_nothing() {
        let seen = Seen::default();
        let user = &seen as *const Seen as *mut c_void;
        let watch = foreground_watch_create(count, user).expect("SetWinEventHook");
        // a hook value no watch registered: another hook's event never reaches this callback
        fire(HWINEVENTHOOK(
            unsafe { (*watch).hook }
                .0
                .wrapping_byte_add(1),
        ));
        assert_eq!(seen.calls.load(Ordering::SeqCst), 0);
        assert_eq!(unsafe { prisc_window_foreground_watch_destroy_windows(watch.cast()) }, S::Ok as i32);
    }
}
