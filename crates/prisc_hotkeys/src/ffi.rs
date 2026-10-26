//! The C ABI Clowd.Ui P/Invokes. Events are pulled (`clowd_hotkeys_next_event` on one dedicated
//! thread), never pushed through callbacks, so the hook can never enter managed code.
//!
//! Rust never hands out an allocated string: all text is copied into caller buffers (the dll is
//! built `+crt-static` and has its own heap). The C# side gets the constants and the event struct
//! generated from `ABI_CONSTS` / `EVENT_FIELDS` (`csharp.rs`); only the function signatures are
//! hand-written there, guarded by `ABI_REVISION`, which folds into `clowd_hotkeys_abi_version`.

use std::fmt::Write as _;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::Ordering;
use std::sync::mpsc::RecvTimeoutError;
use std::sync::Mutex;
use std::time::Duration;

use crate::hotkeys::lock;
use crate::{Error, Event, Events, Gesture, HotkeyId, Hotkeys, Key, Modifiers};

/// Bump whenever an exported function signature changes (the only part of the ABI not hashed).
pub(crate) const ABI_REVISION: u32 = 1;

const CH_OK: i32 = 0;
const CH_ERR_UNSUPPORTED: i32 = -1;
const CH_ERR_PERMISSION: i32 = -2;
const CH_ERR_HOOK_FAILED: i32 = -3;
const CH_ERR_NO_KEY: i32 = -10;
const CH_ERR_INVALID_KEY: i32 = -11;
const CH_ERR_INVALID_MODS: i32 = -12;
const CH_ERR_MODIFIER_KEY: i32 = -13;
const CH_ERR_UNSUPPORTED_KEY: i32 = -14;
const CH_ERR_DUPLICATE: i32 = -15;
const CH_ERR_STOPPED: i32 = -20;
const CH_ERR_NULL: i32 = -98;
const CH_ERR_PANIC: i32 = -99;

const CH_EVENT_HOTKEY: u32 = 1;
const CH_EVENT_RECORDING_MODIFIERS: u32 = 2;
const CH_EVENT_RECORDED: u32 = 3;
const CH_EVENT_KEY: u32 = 4;
const CH_EVENT_STOPPED: u32 = 5;

/// Every return code and event kind, as the generator renders them and the ABI hash folds them.
pub(crate) const ABI_CONSTS: &[(&str, i32)] = &[
    ("CH_OK", CH_OK),
    ("CH_ERR_UNSUPPORTED", CH_ERR_UNSUPPORTED),
    ("CH_ERR_PERMISSION", CH_ERR_PERMISSION),
    ("CH_ERR_HOOK_FAILED", CH_ERR_HOOK_FAILED),
    ("CH_ERR_NO_KEY", CH_ERR_NO_KEY),
    ("CH_ERR_INVALID_KEY", CH_ERR_INVALID_KEY),
    ("CH_ERR_INVALID_MODS", CH_ERR_INVALID_MODS),
    ("CH_ERR_MODIFIER_KEY", CH_ERR_MODIFIER_KEY),
    ("CH_ERR_UNSUPPORTED_KEY", CH_ERR_UNSUPPORTED_KEY),
    ("CH_ERR_DUPLICATE", CH_ERR_DUPLICATE),
    ("CH_ERR_STOPPED", CH_ERR_STOPPED),
    ("CH_ERR_NULL", CH_ERR_NULL),
    ("CH_ERR_PANIC", CH_ERR_PANIC),
    ("CH_EVENT_HOTKEY", CH_EVENT_HOTKEY as i32),
    ("CH_EVENT_RECORDING_MODIFIERS", CH_EVENT_RECORDING_MODIFIERS as i32),
    ("CH_EVENT_RECORDED", CH_EVENT_RECORDED as i32),
    ("CH_EVENT_KEY", CH_EVENT_KEY as i32),
    ("CH_EVENT_STOPPED", CH_EVENT_STOPPED as i32),
];

/// `ClowdHotkeysEvent`'s fields in order, with their C types (the generator's source for the C# struct).
pub(crate) const EVENT_FIELDS: &[(&str, &str)] = &[
    ("kind", "uint32_t"),
    ("id", "uint32_t"),
    ("key", "int32_t"),
    ("modifiers", "uint32_t"),
    ("raw_code", "uint32_t"),
    ("ch", "uint32_t"),
    ("flags", "uint32_t"),
];

const FLAG_DOWN: u32 = 1;
const FLAG_REPEAT: u32 = 2;

/// One event as the C side sees it: 28 bytes, all fields 4-byte.
#[repr(C)]
#[derive(Copy, Clone, Debug, Default, PartialEq, Eq)]
pub struct ClowdHotkeysEvent {
    pub kind: u32,
    /// HOTKEY: the HotkeyId; RECORDING_MODIFIERS / RECORDED: the session token; else 0.
    pub id: u32,
    /// Key value (Avalonia-compatible int), 0 = None.
    pub key: i32,
    /// Modifiers bits (Alt=1 Control=2 Shift=4 Meta=8).
    pub modifiers: u32,
    /// VK / CGKeyCode (KEY only).
    pub raw_code: u32,
    /// Unicode scalar typed by a KEY down, 0 = none.
    pub ch: u32,
    /// bit0 down, bit1 repeat.
    pub flags: u32,
}

/// The opaque `ClowdHotkeys*` handle.
pub struct Handle {
    /// `take()`n on stop.
    hotkeys: Mutex<Option<Hotkeys>>,
    events: Mutex<Events>,
    /// Set by next_event when it dequeues `Event::Stopped`.
    stop_reason: Mutex<Option<String>>,
}

impl Handle {
    fn new(hotkeys: Option<Hotkeys>, events: Events) -> Self {
        Self {
            hotkeys: Mutex::new(hotkeys),
            events: Mutex::new(events),
            stop_reason: Mutex::new(None),
        }
    }
}

/// 32-bit FNV-1a over every part of the ABI that is data: the revision, the codes, the event
/// struct's field names and the whole key table (names, values, aliases).
pub(crate) fn abi_version() -> u32 {
    let mut text = format!("rev={ABI_REVISION};");
    for (name, value) in ABI_CONSTS {
        let _ = write!(text, "{name}={value};");
    }
    let fields: Vec<&str> = EVENT_FIELDS
        .iter()
        .map(|(name, _)| *name)
        .collect();
    let _ = write!(text, "event={};", fields.join(","));
    for &key in Key::ALL {
        let _ = write!(text, "{}={};", key.name(), key as i32);
    }
    for &key in Key::ALL {
        for alias in key.aliases() {
            let _ = write!(text, "{alias}={};", key as i32);
        }
    }
    text.bytes()
        .fold(0x811C_9DC5u32, |hash, byte| (hash ^ byte as u32).wrapping_mul(0x0100_0193))
}

fn code_of(error: &Error) -> i32 {
    match error {
        Error::Unsupported => CH_ERR_UNSUPPORTED,
        Error::PermissionDenied => CH_ERR_PERMISSION,
        Error::HookFailed(_) => CH_ERR_HOOK_FAILED,
        Error::NoKey => CH_ERR_NO_KEY,
        Error::ModifierKey => CH_ERR_MODIFIER_KEY,
        Error::UnsupportedKey(_) => CH_ERR_UNSUPPORTED_KEY,
        Error::Duplicate => CH_ERR_DUPLICATE,
        Error::Stopped => CH_ERR_STOPPED,
    }
}

fn to_native(event: &Event) -> ClowdHotkeysEvent {
    let repeat_flag = |repeat: bool| if repeat { FLAG_REPEAT } else { 0 };
    match *event {
        Event::Hotkey {
            id,
            repeat,
        } => ClowdHotkeysEvent {
            kind: CH_EVENT_HOTKEY,
            id: id.get(),
            flags: repeat_flag(repeat),
            ..Default::default()
        },
        Event::RecordingModifiers {
            session,
            modifiers,
        } => ClowdHotkeysEvent {
            kind: CH_EVENT_RECORDING_MODIFIERS,
            id: session,
            modifiers: modifiers.bits() as u32,
            ..Default::default()
        },
        Event::Recorded {
            session,
            gesture,
        } => ClowdHotkeysEvent {
            kind: CH_EVENT_RECORDED,
            id: session,
            key: gesture.key as i32,
            modifiers: gesture.modifiers.bits() as u32,
            ..Default::default()
        },
        Event::Key(key) => ClowdHotkeysEvent {
            kind: CH_EVENT_KEY,
            key: key.key as i32,
            modifiers: key.modifiers.bits() as u32,
            raw_code: key.raw_code,
            ch: key.ch.map_or(0, u32::from),
            flags: if key.down { FLAG_DOWN } else { 0 } | repeat_flag(key.repeat),
            ..Default::default()
        },
        Event::Stopped(_) => ClowdHotkeysEvent {
            kind: CH_EVENT_STOPPED,
            ..Default::default()
        },
    }
}

/// Copies `text` into `buf` as UTF-8, truncated on a char boundary to `cap - 1` bytes and
/// NUL-terminated. Returns the bytes written excluding the NUL (0 when nothing fits).
///
/// # Safety
/// `buf` is null or valid for `cap` bytes.
unsafe fn copy_out(text: &str, buf: *mut u8, cap: u32) -> u32 {
    if buf.is_null() || cap == 0 {
        return 0;
    }
    let mut len = text.len().min(cap as usize - 1);
    while !text.is_char_boundary(len) {
        len -= 1;
    }
    std::ptr::copy_nonoverlapping(text.as_ptr(), buf, len);
    *buf.add(len) = 0;
    len as u32
}

fn guard(body: impl FnOnce() -> i32) -> i32 {
    catch_unwind(AssertUnwindSafe(body)).unwrap_or(CH_ERR_PANIC)
}

/// The ABI fingerprint; Clowd.Ui refuses a library whose value differs from its generated mirror.
///
/// # Safety
/// Always safe to call.
#[no_mangle]
pub unsafe extern "C" fn clowd_hotkeys_abi_version() -> u32 {
    catch_unwind(abi_version).unwrap_or(0)
}

/// Starts the hook. Returns `CH_OK` with `*out` set, or an error code with a UTF-8 message copied
/// into `err_buf` (NUL-terminated, truncated on a char boundary to `err_cap - 1` bytes).
///
/// # Safety
/// `out` is null or valid for a write; `err_buf` is null or valid for `err_cap` bytes.
#[no_mangle]
pub unsafe extern "C" fn clowd_hotkeys_start(out: *mut *mut Handle, err_buf: *mut u8, err_cap: u32) -> i32 {
    guard(|| {
        if out.is_null() {
            return CH_ERR_NULL;
        }
        *out = std::ptr::null_mut();
        match Hotkeys::start() {
            Ok((hotkeys, events)) => {
                *out = Box::into_raw(Box::new(Handle::new(Some(hotkeys), events)));
                CH_OK
            }
            Err(error) => {
                copy_out(&error.to_string(), err_buf, err_cap);
                code_of(&error)
            }
        }
    })
}

/// Registers `key` + `modifiers`; `*out_id` = the HotkeyId on `CH_OK`.
///
/// # Safety
/// `h` is null or a live handle from `clowd_hotkeys_start`; `out_id` is null or valid for a write.
#[no_mangle]
pub unsafe extern "C" fn clowd_hotkeys_register(h: *mut Handle, key: i32, modifiers: u32, out_id: *mut u32) -> i32 {
    guard(|| {
        let (Some(handle), false) = (h.as_ref(), out_id.is_null()) else {
            return CH_ERR_NULL;
        };
        *out_id = 0;
        let Some(key) = Key::from_i32(key) else {
            return CH_ERR_INVALID_KEY;
        };
        if key == Key::None {
            return CH_ERR_NO_KEY;
        }
        let Some(modifiers) = Modifiers::from_bits(modifiers) else {
            return CH_ERR_INVALID_MODS;
        };
        let hotkeys = lock(&handle.hotkeys);
        let Some(hotkeys) = hotkeys.as_ref() else {
            return CH_ERR_STOPPED;
        };
        match hotkeys.register(Gesture::new(key, modifiers)) {
            Ok(id) => {
                *out_id = id.get();
                CH_OK
            }
            Err(error) => code_of(&error),
        }
    })
}

/// Unregisters `id`. A no-op for an unknown id or a stopped hook.
///
/// # Safety
/// `h` is null or a live handle from `clowd_hotkeys_start`.
#[no_mangle]
pub unsafe extern "C" fn clowd_hotkeys_unregister(h: *mut Handle, id: u32) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Some(handle) = h.as_ref() {
            if let Some(hotkeys) = lock(&handle.hotkeys).as_ref() {
                hotkeys.unregister(HotkeyId(id));
            }
        }
    }));
}

/// Turns the observe stream (`CH_EVENT_KEY`) on (nonzero) or off.
///
/// # Safety
/// `h` is null or a live handle from `clowd_hotkeys_start`.
#[no_mangle]
pub unsafe extern "C" fn clowd_hotkeys_set_observe(h: *mut Handle, observe: u32) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Some(handle) = h.as_ref() {
            if let Some(hotkeys) = lock(&handle.hotkeys).as_ref() {
                hotkeys.set_observe(observe != 0);
            }
        }
    }));
}

/// Arms a recording session: `CH_OK` with `*out_session` = its nonzero token, `CH_ERR_STOPPED`
/// or `CH_ERR_NULL`.
///
/// # Safety
/// `h` is null or a live handle from `clowd_hotkeys_start`; `out_session` is null or valid for a write.
#[no_mangle]
pub unsafe extern "C" fn clowd_hotkeys_start_recording(h: *mut Handle, out_session: *mut u32) -> i32 {
    guard(|| {
        let (Some(handle), false) = (h.as_ref(), out_session.is_null()) else {
            return CH_ERR_NULL;
        };
        *out_session = 0;
        match lock(&handle.hotkeys).as_ref() {
            Some(hotkeys) if hotkeys.shared.alive.load(Ordering::Acquire) => {
                *out_session = hotkeys.start_recording();
                CH_OK
            }
            _ => CH_ERR_STOPPED,
        }
    })
}

/// Cancels `session` if it is still armed; a no-op otherwise.
///
/// # Safety
/// `h` is null or a live handle from `clowd_hotkeys_start`.
#[no_mangle]
pub unsafe extern "C" fn clowd_hotkeys_stop_recording(h: *mut Handle, session: u32) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Some(handle) = h.as_ref() {
            if let Some(hotkeys) = lock(&handle.hotkeys).as_ref() {
                hotkeys.stop_recording(session);
            }
        }
    }));
}

/// Blocks up to `timeout_ms`. 1 = `*out` filled; 0 = timeout; -1 = closed (hook gone and queue
/// drained). Callers are serialized internally; intended for ONE dedicated thread.
///
/// # Safety
/// `h` is null or a live handle from `clowd_hotkeys_start`; `out` is null or valid for a write.
#[no_mangle]
pub unsafe extern "C" fn clowd_hotkeys_next_event(h: *mut Handle, out: *mut ClowdHotkeysEvent, timeout_ms: u32) -> i32 {
    guard(|| {
        let (Some(handle), false) = (h.as_ref(), out.is_null()) else {
            return CH_ERR_NULL;
        };
        let received = lock(&handle.events).recv_timeout(Duration::from_millis(timeout_ms as u64));
        match received {
            Ok(event) => {
                if let Event::Stopped(reason) = &event {
                    *lock(&handle.stop_reason) = Some(reason.clone());
                }
                *out = to_native(&event);
                1
            }
            Err(RecvTimeoutError::Timeout) => 0,
            Err(RecvTimeoutError::Disconnected) => -1,
        }
    })
}

/// Copies the Stopped reason (UTF-8, NUL-terminated, truncated); returns the bytes written
/// excluding the NUL, 0 if there is none.
///
/// # Safety
/// `h` is null or a live handle from `clowd_hotkeys_start`; `buf` is null or valid for `cap` bytes.
#[no_mangle]
pub unsafe extern "C" fn clowd_hotkeys_stop_reason(h: *mut Handle, buf: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| match h.as_ref() {
        Some(handle) => lock(&handle.stop_reason)
            .as_deref()
            .map_or(0, |reason| copy_out(reason, buf, cap)),
        None => 0,
    }))
    .unwrap_or(0)
}

/// Stops the hook (joins its thread). Idempotent and thread-safe; a blocked `next_event` then
/// returns -1 once drained. Afterwards every other call except free is a safe no-op / `CH_ERR_STOPPED`.
///
/// # Safety
/// `h` is null or a live handle from `clowd_hotkeys_start`.
#[no_mangle]
pub unsafe extern "C" fn clowd_hotkeys_stop(h: *mut Handle) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Some(handle) = h.as_ref() {
            // Dropped outside the lock: the join can take a moment.
            let hotkeys = lock(&handle.hotkeys).take();
            drop(hotkeys);
        }
    }));
}

/// Frees the handle (stopping first if needed). NOT concurrent-safe: no other call may be in
/// flight or follow. Kept separate from stop because the pump may be inside next_event.
///
/// # Safety
/// `h` is null or a live handle from `clowd_hotkeys_start`, with no other call in flight or to follow.
#[no_mangle]
pub unsafe extern "C" fn clowd_hotkeys_free(h: *mut Handle) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if !h.is_null() {
            drop(Box::from_raw(h));
        }
    }));
}

#[cfg(test)]
mod tests {
    use std::mem::{offset_of, size_of};
    use std::ptr::null_mut;
    use std::sync::mpsc;

    use super::*;
    use crate::KeyEvent;

    fn handle_without_hook() -> (*mut Handle, mpsc::Sender<Event>) {
        let (tx, rx) = mpsc::channel();
        (Box::into_raw(Box::new(Handle::new(None, Events::from_receiver(rx)))), tx)
    }

    #[test]
    fn event_struct_layout() {
        assert_eq!(size_of::<ClowdHotkeysEvent>(), 28);
        assert_eq!(size_of::<ClowdHotkeysEvent>(), EVENT_FIELDS.len() * 4);
        let offsets = [
            offset_of!(ClowdHotkeysEvent, kind),
            offset_of!(ClowdHotkeysEvent, id),
            offset_of!(ClowdHotkeysEvent, key),
            offset_of!(ClowdHotkeysEvent, modifiers),
            offset_of!(ClowdHotkeysEvent, raw_code),
            offset_of!(ClowdHotkeysEvent, ch),
            offset_of!(ClowdHotkeysEvent, flags),
        ];
        assert_eq!(offsets, [0, 4, 8, 12, 16, 20, 24]);
    }

    #[test]
    fn copy_out_truncates_on_a_char_boundary() {
        let mut buf = [0xAAu8; 8];
        unsafe {
            assert_eq!(copy_out("abc", buf.as_mut_ptr(), 8), 3);
            assert_eq!(&buf[..4], b"abc\0");
            // "é" is two bytes: cap 3 leaves room for "a" plus the NUL only.
            assert_eq!(copy_out("aé", buf.as_mut_ptr(), 3), 1);
            assert_eq!(&buf[..2], b"a\0");
            assert_eq!(copy_out("aé", buf.as_mut_ptr(), 4), 3);
            assert_eq!(&buf[..4], "aé\0".as_bytes());
            buf[0] = 0xAA;
            assert_eq!(copy_out("abc", buf.as_mut_ptr(), 1), 0);
            assert_eq!(buf[0], 0);
            buf[0] = 0xAA;
            assert_eq!(copy_out("abc", buf.as_mut_ptr(), 0), 0);
            assert_eq!(buf[0], 0xAA);
            assert_eq!(copy_out("abc", null_mut(), 8), 0);
        }
    }

    #[test]
    fn null_handles_and_out_pointers() {
        let (h, _tx) = handle_without_hook();
        let mut id = 0u32;
        let mut event = ClowdHotkeysEvent::default();
        let mut buf = [0u8; 8];
        unsafe {
            assert_eq!(clowd_hotkeys_start(null_mut(), null_mut(), 0), CH_ERR_NULL);
            assert_eq!(clowd_hotkeys_register(null_mut(), Key::A as i32, 0, &mut id), CH_ERR_NULL);
            assert_eq!(clowd_hotkeys_register(h, Key::A as i32, 0, null_mut()), CH_ERR_NULL);
            assert_eq!(clowd_hotkeys_start_recording(null_mut(), &mut id), CH_ERR_NULL);
            assert_eq!(clowd_hotkeys_start_recording(h, null_mut()), CH_ERR_NULL);
            assert_eq!(clowd_hotkeys_next_event(null_mut(), &mut event, 0), CH_ERR_NULL);
            assert_eq!(clowd_hotkeys_next_event(h, null_mut(), 0), CH_ERR_NULL);
            assert_eq!(clowd_hotkeys_stop_reason(null_mut(), buf.as_mut_ptr(), 8), 0);
            clowd_hotkeys_unregister(null_mut(), 1);
            clowd_hotkeys_set_observe(null_mut(), 1);
            clowd_hotkeys_stop_recording(null_mut(), 1);
            clowd_hotkeys_stop(null_mut());
            clowd_hotkeys_free(null_mut());
            clowd_hotkeys_free(h);
        }
    }

    #[test]
    fn stopped_handle_refuses_or_ignores() {
        let (h, tx) = handle_without_hook();
        let mut id = 7u32;
        unsafe {
            assert_eq!(
                clowd_hotkeys_register(h, Key::A as i32, Modifiers::CONTROL.bits() as u32, &mut id),
                CH_ERR_STOPPED
            );
            assert_eq!(id, 0);
            // Argument errors come first.
            assert_eq!(clowd_hotkeys_register(h, 0, 0, &mut id), CH_ERR_NO_KEY);
            assert_eq!(clowd_hotkeys_register(h, 100004, 0, &mut id), CH_ERR_INVALID_KEY);
            assert_eq!(clowd_hotkeys_register(h, Key::A as i32, 0x10, &mut id), CH_ERR_INVALID_MODS);
            let mut session = 7u32;
            assert_eq!(clowd_hotkeys_start_recording(h, &mut session), CH_ERR_STOPPED);
            assert_eq!(session, 0);
            clowd_hotkeys_unregister(h, 1);
            clowd_hotkeys_set_observe(h, 1);
            clowd_hotkeys_stop_recording(h, 1);
            clowd_hotkeys_stop(h);
            clowd_hotkeys_stop(h);

            // The queue still drains, then reports closed; the Stopped reason is kept.
            tx.send(Event::Hotkey {
                id: HotkeyId(3),
                repeat: true,
            })
            .unwrap();
            tx.send(Event::Stopped("the event tap was invalidated".into()))
                .unwrap();
            drop(tx);
            let mut event = ClowdHotkeysEvent::default();
            let mut buf = [0u8; 64];
            assert_eq!(clowd_hotkeys_stop_reason(h, buf.as_mut_ptr(), 64), 0);
            assert_eq!(clowd_hotkeys_next_event(h, &mut event, 0), 1);
            assert_eq!(
                event,
                ClowdHotkeysEvent {
                    kind: CH_EVENT_HOTKEY,
                    id: 3,
                    flags: FLAG_REPEAT,
                    ..Default::default()
                }
            );
            assert_eq!(clowd_hotkeys_next_event(h, &mut event, 0), 1);
            assert_eq!(event.kind, CH_EVENT_STOPPED);
            assert_eq!(clowd_hotkeys_next_event(h, &mut event, 0), -1);
            let n = clowd_hotkeys_stop_reason(h, buf.as_mut_ptr(), 64) as usize;
            assert_eq!(&buf[..=n], b"the event tap was invalidated\0");
            assert_eq!(clowd_hotkeys_stop_reason(h, buf.as_mut_ptr(), 8), 7);
            assert_eq!(&buf[..8], b"the eve\0");
            clowd_hotkeys_free(h);
        }
    }

    #[test]
    fn next_event_times_out() {
        let (h, _tx) = handle_without_hook();
        let mut event = ClowdHotkeysEvent::default();
        unsafe {
            assert_eq!(clowd_hotkeys_next_event(h, &mut event, 1), 0);
            clowd_hotkeys_free(h);
        }
    }

    #[test]
    fn event_mapping_for_every_kind() {
        let gesture = Gesture::new(Key::PrintScreen, Modifiers::CONTROL | Modifiers::SHIFT);
        assert_eq!(
            to_native(&Event::Hotkey {
                id: HotkeyId(9),
                repeat: false
            }),
            ClowdHotkeysEvent {
                kind: 1,
                id: 9,
                ..Default::default()
            }
        );
        assert_eq!(
            to_native(&Event::RecordingModifiers {
                session: 4,
                modifiers: Modifiers::ALT | Modifiers::META
            }),
            ClowdHotkeysEvent {
                kind: 2,
                id: 4,
                modifiers: 9,
                ..Default::default()
            }
        );
        assert_eq!(
            to_native(&Event::Recorded {
                session: 5,
                gesture
            }),
            ClowdHotkeysEvent {
                kind: 3,
                id: 5,
                key: 30,
                modifiers: 6,
                ..Default::default()
            }
        );
        let key = KeyEvent {
            down: true,
            repeat: true,
            key: Key::E,
            modifiers: Modifiers::SHIFT,
            raw_code: 0x45,
            ch: Some('€'),
        };
        assert_eq!(
            to_native(&Event::Key(key)),
            ClowdHotkeysEvent {
                kind: 4,
                key: 48,
                modifiers: 4,
                raw_code: 0x45,
                ch: 0x20AC,
                flags: 3,
                ..Default::default()
            }
        );
        let up = KeyEvent {
            down: false,
            repeat: false,
            ch: None,
            key: Key::None,
            ..key
        };
        assert_eq!(
            to_native(&Event::Key(up)),
            ClowdHotkeysEvent {
                kind: 4,
                raw_code: 0x45,
                modifiers: 4,
                ..Default::default()
            }
        );
        assert_eq!(
            to_native(&Event::Stopped("x".into())),
            ClowdHotkeysEvent {
                kind: 5,
                ..Default::default()
            }
        );
    }

    #[test]
    fn abi_version_is_stable_and_exported() {
        assert_eq!(abi_version(), abi_version());
        assert_eq!(unsafe { clowd_hotkeys_abi_version() }, abi_version());
        // Error codes the C side switches on.
        assert_eq!(code_of(&Error::Duplicate), CH_ERR_DUPLICATE);
        assert_eq!(code_of(&Error::HookFailed(String::new())), CH_ERR_HOOK_FAILED);
    }
}
