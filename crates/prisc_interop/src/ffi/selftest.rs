//! One export that produces a chosen outcome on demand, so the status, last-error, log, panic and
//! re-entry machinery can be tested from Rust and from C# with no OS, GPU or permission behind it.
//! It ships in the release library on purpose: a diagnostic that exists only in debug builds is
//! missing exactly when it is needed.

use crate::error::{self, Error, Result};
use crate::ffi::log::{self, PriscInteropLogLevel};
use crate::ffi::status::{PriscInteropStatus, PRISC_OK, PRISC_S_CLOSED, PRISC_S_NONE, PRISC_S_TIMEOUT};
use crate::{convert, panic, util};

/// What `prisc_interop_selftest` does.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscSelftestKind {
    /// `PRISC_OK`, writing 42 to `out_value`.
    Ok = 0,
    /// `PRISC_S_NONE`, writing nothing.
    None = 1,
    /// `PRISC_S_TIMEOUT`.
    Timeout = 2,
    /// `PRISC_S_CLOSED`.
    Closed = 3,
    /// `PRISC_E_UNSUPPORTED` with a message.
    Unsupported = 4,
    /// `PRISC_E_INVALID_ARG` with a message.
    InvalidArg = 5,
    /// `PRISC_E_OS` with last-error code -2147467259 (`0x80004005` sign-extended from i32).
    Os = 6,
    /// A panic, which the guard turns into `PRISC_E_PANIC` naming this export.
    Panic = 7,
    /// `PRISC_E_STATE` with a message.
    State = 8,
    /// `PRISC_E_PERMISSION` with a message.
    Permission = 9,
    /// `PRISC_E_CAPABILITY` with a message.
    Capability = 10,
    /// `PRISC_E_STATE` with a message longer than 1 KiB (the buffer-growth path).
    LongMessage = 11,
    /// One log line at each `PriscInteropLogLevel`, then `PRISC_OK`.
    Log = 12,
    /// A nested guard entry: records `PRISC_E_STATE` "outer", runs an inner guard that fails with
    /// `PRISC_E_INVALID_ARG`, and returns `PRISC_E_STATE`; the last error must still be "outer".
    Nested = 13,
    /// A panic in an OS-callback trampoline that has no handle, caught onto the process slot, then
    /// `PRISC_OK`: the next export of any kind reports it to the log sink as one error line.
    CallbackPanic = 14,
}

convert::ffi_enum!(PriscSelftestKind: i32 {
    Ok, None, Timeout, Closed, Unsupported, InvalidArg, Os, Panic, State, Permission, Capability, LongMessage, Log, Nested,
    CallbackPanic
});

/// The message every failing selftest kind leaves; `LongMessage` repeats it.
const MESSAGE: &str = "the selftest was asked for this outcome";

/// Produces the outcome `kind` (a `PriscSelftestKind`) names; see that enum. `out_value` may be
/// null. Tests and diagnostics only.
///
/// Status: the one `kind` asks for; `PRISC_E_INVALID_ARG` for an unknown `kind`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_interop_selftest(kind: i32, out_value: *mut i64) -> i32 {
    panic::guard("prisc_interop_selftest", || run(kind, out_value))
}

unsafe fn run(kind: i32, out_value: *mut i64) -> Result {
    match PriscSelftestKind::from_raw(kind)? {
        PriscSelftestKind::Ok => {
            util::write_optional(out_value, 42);
            Ok(PRISC_OK)
        }
        PriscSelftestKind::None => Ok(PRISC_S_NONE),
        PriscSelftestKind::Timeout => Ok(PRISC_S_TIMEOUT),
        PriscSelftestKind::Closed => Ok(PRISC_S_CLOSED),
        PriscSelftestKind::Unsupported => Err(Error::unsupported(MESSAGE)),
        PriscSelftestKind::InvalidArg => Err(Error::invalid_arg(MESSAGE)),
        PriscSelftestKind::Os => Err(Error::os(0x8000_4005u32 as i32 as i64, "SelftestOsCall failed (0x80004005)")),
        PriscSelftestKind::Panic => panic!("the selftest was asked to panic"),
        PriscSelftestKind::State => Err(Error::state(MESSAGE)),
        PriscSelftestKind::Permission => Err(Error::permission(MESSAGE)),
        PriscSelftestKind::Capability => Err(Error::capability(MESSAGE)),
        PriscSelftestKind::LongMessage => Err(Error::state(format!("{MESSAGE}. ").repeat(40))),
        PriscSelftestKind::Log => {
            log::emit(PriscInteropLogLevel::Error, "selftest: error");
            log::emit(PriscInteropLogLevel::Warn, "selftest: warn");
            log::emit(PriscInteropLogLevel::Info, "selftest: info");
            log::emit(PriscInteropLogLevel::Debug, "selftest: debug");
            Ok(PRISC_OK)
        }
        PriscSelftestKind::Nested => {
            let state = PriscInteropStatus::State as i32;
            error::set(state, 0, "outer");
            let inner = panic::guard("prisc_interop_selftest", || Err(Error::invalid_arg("inner")));
            debug_assert_eq!(inner, PriscInteropStatus::InvalidArg as i32);
            Ok(state)
        }
        PriscSelftestKind::CallbackPanic => {
            panic::callback_guard(panic::CallbackPanic::process(), (), || {
                panic!("the selftest callback was asked to panic")
            });
            Ok(PRISC_OK)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::error::Slot;

    #[test]
    fn every_kind_produces_its_status() {
        use PriscInteropStatus as S;
        let expected = [
            (0, S::Ok),
            (1, S::None),
            (2, S::Timeout),
            (3, S::Closed),
            (4, S::Unsupported),
            (5, S::InvalidArg),
            (6, S::Os),
            (7, S::Panic),
            (8, S::State),
            (9, S::Permission),
            (10, S::Capability),
            (11, S::State),
            (12, S::Ok),
            (13, S::State),
            // 14 (CallbackPanic) leaves a panic for the next export to log; tests/infra.rs has it
        ];
        for (kind, status) in expected {
            let mut value = 0i64;
            assert_eq!(unsafe { prisc_interop_selftest(kind, &mut value) }, status as i32, "kind {kind}");
            assert_eq!(value, if kind == 0 { 42 } else { 0 }, "kind {kind}");
        }
        assert_eq!(unsafe { prisc_interop_selftest(99, std::ptr::null_mut()) }, S::InvalidArg as i32);
        assert!(error::snapshot().message.contains("99"));
    }

    #[test]
    fn failures_leave_a_message_and_the_os_code() {
        unsafe { prisc_interop_selftest(6, std::ptr::null_mut()) };
        assert_eq!(
            error::snapshot(),
            Slot {
                message: "SelftestOsCall failed (0x80004005)".into(),
                code: -2147467259,
            }
        );
        unsafe { prisc_interop_selftest(11, std::ptr::null_mut()) };
        assert!(error::snapshot().message.len() > 1024);
        unsafe { prisc_interop_selftest(7, std::ptr::null_mut()) };
        assert!(error::snapshot()
            .message
            .contains("prisc_interop_selftest panicked"));
    }

    #[test]
    fn a_nested_entry_keeps_the_outer_error() {
        assert_eq!(
            unsafe { prisc_interop_selftest(13, std::ptr::null_mut()) },
            PriscInteropStatus::State as i32
        );
        assert_eq!(error::snapshot().message, "outer");
    }
}
