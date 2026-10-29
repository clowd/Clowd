//! The hotkey exports through the library's public surface (README section 4.8): argument checks,
//! refusals, the first Status, and a stop that drains and then closes. On macOS and Windows these
//! start a real supervised hook (on macOS without Accessibility it stays down and says so, which
//! the test accepts); it registers only gestures nobody presses during a test run.

use prisc_hotkeys::{Key, Modifiers};
use prisc_interop::ffi::error::prisc_last_error_message;
use prisc_interop::ffi::hotkeys::*;
use prisc_interop::ffi::status::PriscInteropStatus as S;
use prisc_interop::ffi::string::prisc_string_free;
use prisc_interop::ffi::types::{PriscGestureRefusal as R, PriscString};

fn last_message() -> String {
    let len = unsafe { prisc_last_error_message(std::ptr::null_mut(), 0) };
    let mut buf = vec![0u8; len];
    assert_eq!(unsafe { prisc_last_error_message(buf.as_mut_ptr(), buf.len()) }, len);
    String::from_utf8(buf).unwrap()
}

fn empty_event() -> PriscHotkeysEvent {
    PriscHotkeysEvent {
        kind: 0,
        id: 0,
        key: 0,
        modifiers: 0,
        raw_code: 0,
        ch: 0,
        flags: 0,
        hook_state: 0,
        reason: PriscString {
            ptr: std::ptr::null_mut(),
            len: 0,
        },
    }
}

#[test]
fn null_arguments_are_invalid_and_a_null_destroy_is_ok() {
    let null = std::ptr::null_mut();
    let (mut id, mut refusal, mut session) = (0u32, 0i32, 0u32);
    let mut event = empty_event();
    unsafe {
        assert_eq!(prisc_hotkeys_create(std::ptr::null_mut()), S::InvalidArg as i32);
        assert_eq!(
            prisc_hotkeys_register(null, Key::F19 as i32, 0, &mut id, &mut refusal),
            S::InvalidArg as i32
        );
        assert!(last_message().contains("hook"));
        assert_eq!(prisc_hotkeys_unregister(null, 1), S::InvalidArg as i32);
        assert_eq!(prisc_hotkeys_set_observe(null, 1), S::InvalidArg as i32);
        assert_eq!(prisc_hotkeys_start_recording(null, &mut session), S::InvalidArg as i32);
        assert_eq!(prisc_hotkeys_stop_recording(null, 1), S::InvalidArg as i32);
        assert_eq!(prisc_hotkeys_next_event(null, 0, &mut event), S::InvalidArg as i32);
        assert_eq!(prisc_hotkeys_stop(null), S::InvalidArg as i32);
        assert_eq!(prisc_hotkeys_destroy(null), S::Ok as i32);
    }
}

#[cfg(not(any(windows, target_os = "macos")))]
#[test]
fn no_backend_is_unsupported() {
    let mut hook = std::ptr::null_mut();
    assert_eq!(unsafe { prisc_hotkeys_create(&mut hook) }, S::Unsupported as i32);
    assert!(hook.is_null());
}

#[cfg(any(windows, target_os = "macos"))]
#[test]
fn a_real_hook_reports_its_state_refuses_bad_gestures_and_closes_after_stop() {
    let mut hook = std::ptr::null_mut();
    assert_eq!(unsafe { prisc_hotkeys_create(&mut hook) }, S::Ok as i32);
    assert!(!hook.is_null());

    // the first event is the initial state, queued before create returned
    let mut event = empty_event();
    assert_eq!(unsafe { prisc_hotkeys_next_event(hook, 0, &mut event) }, S::Ok as i32);
    assert_eq!(event.kind, PriscHotkeysEventKind::Status as u32);
    let running = event.hook_state == PriscHookState::Running as i32;
    if cfg!(windows) {
        assert!(running, "a Windows hook needs no permission");
    } else {
        // the test runner may lack Accessibility: then the hook is down, retrying, and says why
        assert!(
            running || event.hook_state == PriscHookState::PermissionMissing as i32,
            "{}",
            event.hook_state
        );
        assert_eq!(event.flags & PriscHotkeysEventFlags::Recoverable as u32 != 0, !running);
    }
    assert_eq!(event.reason.ptr.is_null(), running);
    assert_eq!(unsafe { prisc_string_free(&mut event.reason) }, S::Ok as i32);

    // refusals come back at once, whatever the hook's state
    let gesture = |key: i32, modifiers: u32| {
        let (mut id, mut refusal) = (7u32, -1i32);
        let status = unsafe { prisc_hotkeys_register(hook, key, modifiers, &mut id, &mut refusal) };
        (status, id, refusal)
    };
    let mods = (Modifiers::CONTROL | Modifiers::ALT | Modifiers::SHIFT).bits() as u32;
    assert_eq!(gesture(0, 0), (S::InvalidArg as i32, 0, R::NoKey as i32));
    assert_eq!(gesture(100_004, 0), (S::InvalidArg as i32, 0, R::InvalidKey as i32));
    assert_eq!(
        gesture(Key::F19 as i32, 0x10),
        (S::InvalidArg as i32, 0, R::InvalidModifiers as i32)
    );
    assert_eq!(gesture(Key::LeftShift as i32, 0), (S::InvalidArg as i32, 0, R::ModifierOnly as i32));
    // held while down, live while running: either way an id, stable and never reused
    assert_eq!(gesture(Key::F19 as i32, mods), (S::Ok as i32, 1, R::None as i32));
    assert_eq!(gesture(Key::F19 as i32, mods), (S::InvalidArg as i32, 0, R::Duplicate as i32));
    assert!(last_message().contains("duplicate"));
    assert_eq!(gesture(Key::F18 as i32, mods), (S::Ok as i32, 2, R::None as i32));
    unsafe {
        assert_eq!(prisc_hotkeys_unregister(hook, 1), S::Ok as i32);
        assert_eq!(prisc_hotkeys_unregister(hook, 1), S::Ok as i32);
        assert_eq!(prisc_hotkeys_set_observe(hook, 1), S::Ok as i32);
        assert_eq!(prisc_hotkeys_set_observe(hook, 0), S::Ok as i32);
    }
    assert_eq!(gesture(Key::F19 as i32, mods), (S::Ok as i32, 3, R::None as i32));

    let mut session = 0u32;
    let recording = unsafe { prisc_hotkeys_start_recording(hook, &mut session) };
    if running {
        assert_eq!((recording, session != 0), (S::Ok as i32, true));
        assert_eq!(unsafe { prisc_hotkeys_stop_recording(hook, session) }, S::Ok as i32);
    } else {
        assert_eq!((recording, session), (S::State as i32, 0));
    }

    // stop drains what is queued, then the stream reports closed, for good
    unsafe {
        assert_eq!(prisc_hotkeys_stop(hook), S::Ok as i32);
        assert_eq!(prisc_hotkeys_stop(hook), S::Ok as i32);
        let mut status;
        loop {
            event = empty_event();
            status = prisc_hotkeys_next_event(hook, 5_000, &mut event);
            if status != S::Ok as i32 {
                break;
            }
            prisc_string_free(&mut event.reason);
        }
        assert_eq!(status, S::Closed as i32);
        assert_eq!(event.kind, 0, "no event is written with Closed");
        assert_eq!(prisc_hotkeys_next_event(hook, 0, &mut event), S::Closed as i32);
        assert_eq!(prisc_hotkeys_start_recording(hook, &mut session), S::State as i32);
        assert_eq!(prisc_hotkeys_destroy(hook), S::Ok as i32);
    }
}

#[cfg(any(windows, target_os = "macos"))]
#[test]
fn a_next_event_that_times_out_says_so_and_destroy_stops_a_running_hook() {
    let mut hook = std::ptr::null_mut();
    assert_eq!(unsafe { prisc_hotkeys_create(&mut hook) }, S::Ok as i32);
    let mut event = empty_event();
    unsafe {
        assert_eq!(prisc_hotkeys_next_event(hook, 0, &mut event), S::Ok as i32);
        prisc_string_free(&mut event.reason);
        assert_eq!(prisc_hotkeys_next_event(hook, 1, &mut event), S::Timeout as i32);
        assert_eq!(event.kind, 0);
        // destroy without a stop: it stops first
        assert_eq!(prisc_hotkeys_destroy(hook), S::Ok as i32);
    }
}
