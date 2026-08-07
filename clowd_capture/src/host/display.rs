//! Display-topology change observers for persistent-host mode.
//!
//! The warm state (per-monitor workers, hidden windows, configured
//! surfaces) is built for one topology; when it changes the host must
//! restart rather than re-init in place (see `App::handle_display_change`
//! for the debounce and the actual emit + exit). Every observer here
//! funnels into [`notify_display_change`], which wakes the event loop via
//! the proxy stashed by [`install`] — safe from any thread, including the
//! loop thread itself mid-dispatch.
//!
//! Windows: WM_DISPLAYCHANGE / WM_DPICHANGED are *sent* (not posted) to
//! our hidden top-level windows, so they bypass the message queue and
//! never reach winit's msg_hook (which only sees `PeekMessageW` results).
//! A `WH_CALLWNDPROC` hook on the event-loop thread observes sent-message
//! delivery reliably; the msg_hook is wired as well (`win_msg_hook`, from
//! `main.rs`) as a belt-and-braces for any posted duplicates.
//!
//! macOS: `CGDisplayRegisterReconfigurationCallback`, invoked by the run
//! loop that winit already pumps.
//!
//! None of this is installed in one-shot mode — its display-change
//! behaviour is unchanged.

use std::sync::Mutex;

use winit::event_loop::EventLoopProxy;

use super::AppEvent;

/// Proxy handed to [`install`]. `Mutex<Option<..>>` rather than `OnceLock`
/// because `EventLoopProxy` is `Send` but not `Sync`; the C callbacks
/// below have no capture slot, so a process global is the only channel.
static PROXY: Mutex<Option<EventLoopProxy<AppEvent>>> = Mutex::new(None);

/// Register the platform display-change observers. Persistent mode only;
/// call once, on the event-loop thread, before `run_app`.
pub fn install(proxy: EventLoopProxy<AppEvent>) {
    *PROXY.lock().unwrap() = Some(proxy);
    #[cfg(windows)]
    install_windows_hook();
    #[cfg(target_os = "macos")]
    install_macos_callback();
}

/// Forward one OS notification to the event loop. `send_event` both wakes
/// the loop (it may be parked in `ControlFlow::Wait`) and delivers the
/// event on the loop thread; a send error means the loop is already gone
/// and there is nothing left to notify.
fn notify_display_change() {
    if let Some(proxy) = PROXY.lock().unwrap().as_ref() {
        let _ = proxy.send_event(AppEvent::DisplayChange);
    }
}

/// WM_DEVICECHANGE is deliberately not watched: it fires for every USB /
/// media event, and any GPU or monitor arrival that matters to us also
/// raises WM_DISPLAYCHANGE (and is caught by the show-time topology
/// check regardless).
#[cfg(windows)]
fn is_display_change_message(message: u32) -> bool {
    use windows::Win32::UI::WindowsAndMessaging::{WM_DISPLAYCHANGE, WM_DPICHANGED};
    message == WM_DISPLAYCHANGE || message == WM_DPICHANGED
}

/// Hooked into winit's dispatch loop via
/// `EventLoopBuilderExtWindows::with_msg_hook` (`main.rs`, persistent
/// mode only). Sees posted messages just before `DispatchMessageW`;
/// always returns `false` so winit's own dispatching is never disturbed.
#[cfg(windows)]
pub fn win_msg_hook(msg: *const std::ffi::c_void) -> bool {
    use windows::Win32::UI::WindowsAndMessaging::MSG;
    let msg = unsafe { &*(msg as *const MSG) };
    if is_display_change_message(msg.message) {
        notify_display_change();
    }
    false
}

/// Install the `WH_CALLWNDPROC` hook that observes *sent* messages —
/// the delivery path WM_DISPLAYCHANGE actually takes to our hidden
/// top-level windows. Thread-scoped to the event-loop thread (which owns
/// every window) and process-lifetime: the `HHOOK` is intentionally
/// never unhooked.
#[cfg(windows)]
fn install_windows_hook() {
    use windows::Win32::Foundation::{LPARAM, LRESULT, WPARAM};
    use windows::Win32::System::Threading::GetCurrentThreadId;
    use windows::Win32::UI::WindowsAndMessaging::{CallNextHookEx, SetWindowsHookExW, CWPSTRUCT, WH_CALLWNDPROC};

    unsafe extern "system" fn call_wnd_proc_hook(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
        if code >= 0 && lparam.0 != 0 {
            let cwp = unsafe { &*(lparam.0 as *const CWPSTRUCT) };
            if is_display_change_message(cwp.message) {
                notify_display_change();
            }
        }
        unsafe { CallNextHookEx(None, code, wparam, lparam) }
    }

    match unsafe { SetWindowsHookExW(WH_CALLWNDPROC, Some(call_wnd_proc_hook), None, GetCurrentThreadId()) } {
        Ok(_) => info!("display-change hook installed"),
        // Not fatal: the show-time topology check still catches a change,
        // just without the background respawn.
        Err(e) => warn!("failed to install the display-change hook: {e:?}"),
    }
}

#[cfg(target_os = "macos")]
fn install_macos_callback() {
    use core_graphics::display::{CGDirectDisplayID, CGDisplayChangeSummaryFlags, CGDisplayRegisterReconfigurationCallback};

    unsafe extern "C" fn reconfig_callback(_display: CGDirectDisplayID, flags: u32, _user_info: *const std::ffi::c_void) {
        // Each display gets two passes: a begin pass (flag bit 0) and a
        // completion pass carrying the actual change flags. Only a
        // completion pass with a real change is interesting — empty flags
        // mean "this display was not affected".
        let flags = CGDisplayChangeSummaryFlags::from_bits_truncate(flags);
        if flags.is_empty() || flags.contains(CGDisplayChangeSummaryFlags::kCGDisplayBeginConfigurationFlag) {
            return;
        }
        notify_display_change();
    }

    let err = unsafe { CGDisplayRegisterReconfigurationCallback(reconfig_callback, std::ptr::null()) };
    if err == 0 {
        info!("display-reconfiguration callback registered");
    } else {
        // Not fatal: the show-time topology check still catches a change,
        // just without the background respawn.
        warn!("CGDisplayRegisterReconfigurationCallback failed: {err}");
    }
}
