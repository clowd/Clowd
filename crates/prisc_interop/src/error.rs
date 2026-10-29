//! The crate's error type and this thread's last-error slot.
//!
//! Every export body returns [`Result`]: `Ok(status)` for success and the benign statuses
//! (`PRISC_OK`, `PRISC_S_NONE`, `PRISC_S_TIMEOUT`, `PRISC_S_CLOSED`), `Err(Error)` for a failure.
//! `panic::guard` records an `Err` in the slot and returns its status, so a failing export always
//! leaves a message (and an OS code when there was one) for `prisc_last_error_message` /
//! `prisc_last_error_code`.
//!
//! **No accessor of the slot can panic.** Each goes through `LAST_ERROR.try_with(...)` and then
//! `try_borrow_mut()` / `try_borrow()`, degrading to a no-op on write and to an empty message and
//! a zero code on read. A thread-local access after its destructor has run panics inside
//! `LocalKey::with`, and exports run on finalizer threads and on OS callback threads that may be
//! detaching.

use std::cell::RefCell;

use crate::ffi::status::PriscInteropStatus;

/// A failed export: the status it returns, the raw OS code behind it (0 when none) and a message
/// naming what failed.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Error {
    pub(crate) status: PriscInteropStatus,
    /// HRESULT / OSStatus / errno, sign-extended from i32. 0 when the failure was not an OS call.
    pub(crate) code: i64,
    pub(crate) message: String,
}

/// What every export body returns. `Ok` carries a non-negative status.
pub(crate) type Result<T = i32> = std::result::Result<T, Error>;

#[allow(dead_code)] // the constructors the area exports use
impl Error {
    pub(crate) fn new(status: PriscInteropStatus, message: impl Into<String>) -> Self {
        Self {
            status,
            code: 0,
            message: message.into(),
        }
    }

    /// `PRISC_E_UNSUPPORTED`: not on this OS or not in this build.
    pub(crate) fn unsupported(message: impl Into<String>) -> Self {
        Self::new(PriscInteropStatus::Unsupported, message)
    }

    /// `PRISC_E_INVALID_ARG`: null pointer, out of range, bad UTF-8, unknown enum value.
    pub(crate) fn invalid_arg(message: impl Into<String>) -> Self {
        Self::new(PriscInteropStatus::InvalidArg, message)
    }

    /// `PRISC_E_INVALID_ARG` for a null pointer parameter named `name`.
    pub(crate) fn null(name: &str) -> Self {
        Self::invalid_arg(format!("{name} was null"))
    }

    /// `PRISC_E_OS` with the raw OS code (sign-extended from i32).
    pub(crate) fn os(code: i64, message: impl Into<String>) -> Self {
        Self {
            status: PriscInteropStatus::Os,
            code,
            message: message.into(),
        }
    }

    /// `PRISC_E_STATE`: wrong thread, handle stopped or destroyed, called twice.
    pub(crate) fn state(message: impl Into<String>) -> Self {
        Self::new(PriscInteropStatus::State, message)
    }

    /// `PRISC_E_PERMISSION`: a TCC / OS permission is missing.
    pub(crate) fn permission(message: impl Into<String>) -> Self {
        Self::new(PriscInteropStatus::Permission, message)
    }

    /// `PRISC_E_CAPABILITY`: the hardware or driver lacks a needed feature.
    pub(crate) fn capability(message: impl Into<String>) -> Self {
        Self::new(PriscInteropStatus::Capability, message)
    }

    /// Records this error as the thread's last error and returns its status.
    pub(crate) fn record(self) -> i32 {
        set(self.status as i32, self.code, self.message)
    }
}

/// The slot's contents. Also what a nested guard entry saves and restores.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub(crate) struct Slot {
    pub(crate) message: String,
    pub(crate) code: i64,
}

thread_local! {
    static LAST_ERROR: RefCell<Slot> = RefCell::new(Slot::default());
}

/// Empties this thread's slot. `panic::guard` calls it before every export body.
pub(crate) fn clear() {
    let _ = LAST_ERROR.try_with(|slot| {
        if let Ok(mut slot) = slot.try_borrow_mut() {
            slot.message.clear();
            slot.code = 0;
        }
    });
}

/// Records a failure and returns `status`.
pub(crate) fn set(status: i32, code: i64, message: impl Into<String>) -> i32 {
    let message = message.into();
    let _ = LAST_ERROR.try_with(|slot| {
        if let Ok(mut slot) = slot.try_borrow_mut() {
            slot.message = message;
            slot.code = code;
        }
    });
    status
}

/// Takes the slot's contents, leaving it empty (a nested guard entry saves the outer error).
pub(crate) fn take() -> Slot {
    LAST_ERROR
        .try_with(|slot| {
            slot.try_borrow_mut()
                .map(|mut slot| std::mem::take(&mut *slot))
                .unwrap_or_default()
        })
        .unwrap_or_default()
}

/// Puts `saved` back (a nested guard exit restores the outer error).
pub(crate) fn restore(saved: Slot) {
    let _ = LAST_ERROR.try_with(|slot| {
        if let Ok(mut slot) = slot.try_borrow_mut() {
            *slot = saved;
        }
    });
}

/// Reads the slot without disturbing it; `fallback` when it is gone or busy.
pub(crate) fn read<R>(reader: impl FnOnce(&Slot) -> R, fallback: impl Fn() -> R) -> R {
    LAST_ERROR
        .try_with(|slot| match slot.try_borrow() {
            Ok(slot) => reader(&slot),
            Err(_) => fallback(),
        })
        .unwrap_or_else(|_| fallback())
}

/// A copy of the slot, for tests.
#[cfg(test)]
pub(crate) fn snapshot() -> Slot {
    read(Slot::clone, Slot::default)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn record_sets_message_code_and_returns_the_status() {
        clear();
        let status = Error::os(0x8000_4005u32 as i32 as i64, "E_FAIL").record();
        assert_eq!(status, PriscInteropStatus::Os as i32);
        assert_eq!(
            snapshot(),
            Slot {
                message: "E_FAIL".into(),
                code: -2147467259,
            }
        );
    }

    #[test]
    fn take_empties_and_restore_puts_back() {
        set(-5, 7, "outer");
        let saved = take();
        assert_eq!(snapshot(), Slot::default());
        set(-2, 0, "inner");
        restore(saved);
        assert_eq!(
            snapshot(),
            Slot {
                message: "outer".into(),
                code: 7,
            }
        );
        clear();
        assert_eq!(snapshot(), Slot::default());
    }

    #[test]
    fn the_slot_is_thread_local() {
        set(-3, 1, "this thread");
        let other = std::thread::spawn(snapshot).join().unwrap();
        assert_eq!(other, Slot::default());
        assert_eq!(snapshot().message, "this thread");
    }

    #[test]
    fn constructors_pick_their_status() {
        assert_eq!(Error::unsupported("x").status, PriscInteropStatus::Unsupported);
        assert_eq!(Error::invalid_arg("x").status, PriscInteropStatus::InvalidArg);
        assert_eq!(Error::null("out").message, "out was null");
        assert_eq!(Error::state("x").status, PriscInteropStatus::State);
        assert_eq!(Error::permission("x").status, PriscInteropStatus::Permission);
        assert_eq!(Error::capability("x").status, PriscInteropStatus::Capability);
        assert_eq!(Error::os(5, "x").code, 5);
    }
}
