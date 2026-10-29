//! The supervised global hotkey hook (WP-HOTKEYS; README section 4.8). The hook, its macOS watch
//! and its supervisor run on threads the handle owns (README D4), started by
//! `prisc_hotkeys_create` and joined by `prisc_hotkeys_stop` / `prisc_hotkeys_destroy`.

use crate::error::Error;
use crate::ffi::types::PriscString;
use crate::panic;

/// The hotkey hook handle.
pub struct PriscHotkeys {
    _private: [u8; 0],
}

/// What a `PriscHotkeysEvent` reports.
#[repr(u32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscHotkeysEventKind {
    Hotkey = 1,
    RecordingModifiers = 2,
    Recorded = 3,
    Key = 4,
    Status = 5,
}

/// The hook's state in a Status event.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscHookState {
    Running = 0,
    PermissionMissing = 1,
    PermissionStale = 2,
    Failed = 3,
}

/// One hook event (48 bytes on 64-bit).
#[repr(C)]
#[derive(Debug)]
pub struct PriscHotkeysEvent {
    /// A `PriscHotkeysEventKind`.
    pub kind: u32,
    pub id: u32,
    pub key: i32,
    pub modifiers: u32,
    pub raw_code: u32,
    pub ch: u32,
    /// bit0 down, bit1 repeat, bit2 recoverable (Status only).
    pub flags: u32,
    /// A `PriscHookState` (Status only).
    pub hook_state: i32,
    /// Status with a down state only; mode 1, the receiver frees it.
    pub reason: PriscString,
}

/// Starts a supervised hook. The first event is a Status with the initial state.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_UNSUPPORTED` (Linux).
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_hotkeys_create(out_hook: *mut *mut PriscHotkeys) -> i32 {
    panic::guard("prisc_hotkeys_create", || {
        let _ = out_hook;
        Err(Error::unsupported("prisc_hotkeys_create is not implemented yet"))
    })
}

/// Registers a gesture; a validation refusal comes back at once as `PRISC_E_INVALID_ARG` with a
/// `PriscGestureRefusal`. Ids stay stable across hook restarts.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_hotkeys_register(
    hook: *mut PriscHotkeys,
    key: i32,
    modifiers: u32,
    out_id: *mut u32,
    out_refusal: *mut i32,
) -> i32 {
    panic::guard("prisc_hotkeys_register", || {
        let _ = (hook, key, modifiers, out_id, out_refusal);
        Err(Error::unsupported("prisc_hotkeys_register is not implemented yet"))
    })
}

/// Unregisters `id` (OK in every state).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_hotkeys_unregister(hook: *mut PriscHotkeys, id: u32) -> i32 {
    panic::guard("prisc_hotkeys_unregister", || {
        let _ = (hook, id);
        Err(Error::unsupported("prisc_hotkeys_unregister is not implemented yet"))
    })
}

/// Turns the observe stream (Key events) on or off; persists across restarts.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_hotkeys_set_observe(hook: *mut PriscHotkeys, observe: u8) -> i32 {
    panic::guard("prisc_hotkeys_set_observe", || {
        let _ = (hook, observe);
        Err(Error::unsupported("prisc_hotkeys_set_observe is not implemented yet"))
    })
}

/// Arms a one-shot gesture recording.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_STATE` (hook down).
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_hotkeys_start_recording(hook: *mut PriscHotkeys, out_session: *mut u32) -> i32 {
    panic::guard("prisc_hotkeys_start_recording", || {
        let _ = (hook, out_session);
        Err(Error::unsupported("prisc_hotkeys_start_recording is not implemented yet"))
    })
}

/// Cancels `session` if it is still armed.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_hotkeys_stop_recording(hook: *mut PriscHotkeys, session: u32) -> i32 {
    panic::guard("prisc_hotkeys_stop_recording", || {
        let _ = (hook, session);
        Err(Error::unsupported("prisc_hotkeys_stop_recording is not implemented yet"))
    })
}

/// Waits up to `timeout_ms` for the next event.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_S_TIMEOUT`, `PRISC_S_CLOSED` (stopped and drained), `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_hotkeys_next_event(hook: *mut PriscHotkeys, timeout_ms: u32, out_event: *mut PriscHotkeysEvent) -> i32 {
    panic::guard("prisc_hotkeys_next_event", || {
        let _ = (hook, timeout_ms, out_event);
        Err(Error::unsupported("prisc_hotkeys_next_event is not implemented yet"))
    })
}

/// Stops the hook and joins its threads; idempotent, safe concurrently with `prisc_hotkeys_next_event`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_hotkeys_stop(hook: *mut PriscHotkeys) -> i32 {
    panic::guard("prisc_hotkeys_stop", || {
        let _ = hook;
        Err(Error::unsupported("prisc_hotkeys_stop is not implemented yet"))
    })
}

/// Stops (if needed) and frees the hook; null is `PRISC_OK`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`.
///
/// Thread: any thread, never concurrently with another call on the same handle.
#[no_mangle]
pub unsafe extern "C" fn prisc_hotkeys_destroy(hook: *mut PriscHotkeys) -> i32 {
    panic::guard("prisc_hotkeys_destroy", || {
        let _ = hook;
        Err(Error::unsupported("prisc_hotkeys_destroy is not implemented yet"))
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::{offset_of, size_of};

    #[test]
    fn layouts_are_pinned() {
        assert_eq!(size_of::<PriscHotkeysEvent>(), 48);
        assert_eq!(offset_of!(PriscHotkeysEvent, kind), 0);
        assert_eq!(offset_of!(PriscHotkeysEvent, id), 4);
        assert_eq!(offset_of!(PriscHotkeysEvent, key), 8);
        assert_eq!(offset_of!(PriscHotkeysEvent, modifiers), 12);
        assert_eq!(offset_of!(PriscHotkeysEvent, raw_code), 16);
        assert_eq!(offset_of!(PriscHotkeysEvent, ch), 20);
        assert_eq!(offset_of!(PriscHotkeysEvent, flags), 24);
        assert_eq!(offset_of!(PriscHotkeysEvent, hook_state), 28);
        assert_eq!(offset_of!(PriscHotkeysEvent, reason), 32);
    }
}
