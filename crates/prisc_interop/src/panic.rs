//! The two boundary guards (README sections A6 and C5).
//!
//! [`guard`] wraps every export body: it clears this thread's last error, runs the body, records an
//! `Err` and turns a panic into `PRISC_E_PANIC` with the export's name in the message. An unwind
//! that reached the C ABI would abort the host process, and this library runs inside Prisc.Ui.
//!
//! Re-entry (R20): an OS callback delivered from the OS loop outside any export may call exports
//! (depth 0, nothing special). A host that calls an export while another export is still on the
//! stack of the same thread (a log sink that should not, a callback inside an export) gets a
//! nested entry: the guard keeps a thread-local depth, saves the outer call's last-error slot on a
//! nested entry and restores it on exit, so the outer call's error survives. Rust code never calls
//! an export itself (prisc_codegen's lint).
//!
//! [`callback_guard`] wraps every OS-called trampoline under `src/platform` (CoreAudio render and
//! input procs, HAL listeners, the WinEvent proc, `define_class!` methods): it catches a panic,
//! remembers it for the next export that asks ([`take_callback_panic`]) and returns a benign
//! value. It logs nothing (audio threads must not), and an unwind never reaches the OS frame.
//!
//! The workspace keeps `panic = "unwind"`; with `abort` there would be nothing to catch.

use std::any::Any;
use std::cell::Cell;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::{Mutex, PoisonError};

use crate::error::{self, Result};
use crate::ffi::log::{self, PriscInteropLogLevel};
use crate::ffi::status::PriscInteropStatus;

thread_local! {
    static DEPTH: Cell<u32> = const { Cell::new(0) };
}

fn depth() -> u32 {
    DEPTH.try_with(Cell::get).unwrap_or(0)
}

fn set_depth(value: u32) {
    let _ = DEPTH.try_with(|depth| depth.set(value));
}

/// Runs an export body. `export` is the export's own name, spelled exactly (prisc_codegen's lint
/// checks it), so a panic report names the right function.
pub(crate) fn guard(export: &'static str, body: impl FnOnce() -> Result<i32>) -> i32 {
    let outer_depth = depth();
    set_depth(outer_depth + 1);
    let saved = (outer_depth > 0).then(error::take);
    error::clear();

    let status = match catch_unwind(AssertUnwindSafe(body)) {
        Ok(Ok(status)) => status,
        Ok(Err(e)) => e.record(),
        Err(payload) => report(export, payload.as_ref()),
    };

    set_depth(outer_depth);
    if let Some(saved) = saved {
        error::restore(saved);
    }
    status
}

/// The failure path of [`guard`], itself caught: if reporting panics (a host log sink that
/// unwound, a thread-local past its destructor) the caller still gets a status.
fn report(export: &'static str, payload: &(dyn Any + Send)) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let text = format!("{export} panicked: {}", payload_text(payload));
        log::emit(PriscInteropLogLevel::Error, &text);
        error::set(PriscInteropStatus::Panic as i32, 0, text)
    }))
    .unwrap_or(PriscInteropStatus::Panic as i32)
}

fn payload_text(payload: &(dyn Any + Send)) -> String {
    if let Some(text) = payload.downcast_ref::<&'static str>() {
        (*text).to_string()
    } else if let Some(text) = payload.downcast_ref::<String>() {
        text.clone()
    } else {
        "a panic payload of an unknown type".to_string()
    }
}

static CALLBACK_PANIC: Mutex<Option<String>> = Mutex::new(None);

/// Runs an OS-called trampoline body; on a panic returns `benign` and remembers the panic text
/// for [`take_callback_panic`]. Never logs, never unwinds.
#[allow(dead_code)] // the OS trampolines of the area work packages use it
pub(crate) fn callback_guard<R>(benign: R, body: impl FnOnce() -> R) -> R {
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(value) => value,
        Err(payload) => {
            let _ = catch_unwind(AssertUnwindSafe(|| {
                let text = format!("an OS callback panicked: {}", payload_text(payload.as_ref()));
                *CALLBACK_PANIC
                    .lock()
                    .unwrap_or_else(PoisonError::into_inner) = Some(text);
            }));
            benign
        }
    }
}

/// The most recent panic a [`callback_guard`] caught, cleared by reading it. An export on the
/// affected handle reports it (as `PRISC_E_PANIC`) instead of carrying on silently.
#[allow(dead_code)] // the area exports read it
pub(crate) fn take_callback_panic() -> Option<String> {
    CALLBACK_PANIC
        .lock()
        .unwrap_or_else(PoisonError::into_inner)
        .take()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::error::{Error, Slot};

    #[test]
    fn guard_returns_ok_statuses_unchanged_and_records_errors() {
        assert_eq!(guard("prisc_interop_selftest", || Ok(1)), 1);
        assert_eq!(error::snapshot(), Slot::default());
        let status = guard("prisc_interop_selftest", || Err(Error::state("wrong state")));
        assert_eq!(status, PriscInteropStatus::State as i32);
        assert_eq!(error::snapshot().message, "wrong state");
    }

    #[test]
    fn guard_clears_the_previous_error_before_running() {
        error::set(-3, 9, "an earlier failure");
        let status = guard("prisc_interop_selftest", || {
            assert_eq!(error::snapshot(), Slot::default());
            Ok(0)
        });
        assert_eq!(status, 0);
    }

    #[test]
    fn guard_turns_a_panic_into_e_panic_naming_the_export() {
        let status = guard("prisc_interop_selftest", || panic!("asked to panic"));
        assert_eq!(status, PriscInteropStatus::Panic as i32);
        let message = error::snapshot().message;
        assert!(message.contains("prisc_interop_selftest"), "{message}");
        assert!(message.contains("asked to panic"), "{message}");
        let status = guard("prisc_interop_selftest", || std::panic::panic_any(7u32));
        assert_eq!(status, PriscInteropStatus::Panic as i32);
        assert!(error::snapshot()
            .message
            .contains("unknown type"));
        assert_eq!(depth(), 0, "a panic must not leak a depth");
    }

    #[test]
    fn a_nested_entry_keeps_the_outer_error() {
        let status = guard("prisc_interop_selftest", || {
            error::set(PriscInteropStatus::State as i32, 0, "outer");
            let inner = guard("prisc_interop_selftest", || Err(Error::invalid_arg("inner")));
            assert_eq!(inner, PriscInteropStatus::InvalidArg as i32);
            assert_eq!(error::snapshot().message, "outer");
            Ok(PriscInteropStatus::State as i32)
        });
        assert_eq!(status, PriscInteropStatus::State as i32);
        assert_eq!(error::snapshot().message, "outer");
        assert_eq!(depth(), 0);
    }

    #[test]
    fn callback_guard_returns_the_benign_value_and_remembers_the_panic() {
        let _ = take_callback_panic();
        assert_eq!(callback_guard(0u8, || 1u8), 1);
        assert_eq!(take_callback_panic(), None);
        assert_eq!(callback_guard(0u8, || panic!("render proc failed")), 0);
        let text = take_callback_panic().expect("recorded");
        assert!(text.contains("render proc failed"), "{text}");
        assert_eq!(take_callback_panic(), None);
    }
}
