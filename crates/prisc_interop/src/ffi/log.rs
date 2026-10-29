//! The optional process-global log sink (README D3). The crate installs no `log` logger and has
//! no telemetry; its own diagnostics go only to the host's sink, when one is set.

use std::ffi::c_void;
use std::sync::{PoisonError, RwLock};

use crate::error::Result;
use crate::ffi::status::PRISC_OK;
use crate::panic;

/// How serious a log line is.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscInteropLogLevel {
    /// Something failed, and the caller is told so through a status as well.
    Error = 0,
    /// Something unexpected that the call recovered from.
    Warn = 1,
    /// A one-off fact worth a log line: a device opened, a backend chosen.
    Info = 2,
    /// Detail only useful while chasing a specific problem.
    Debug = 3,
}

/// The log sink: `level` is a `PriscInteropLogLevel`, `msg` is UTF-8 of `len` bytes, not
/// NUL-terminated, valid only during the call. `user` is what `prisc_log_set_callback` was given.
pub type PriscLogCallback = Option<unsafe extern "C" fn(level: i32, msg: *const u8, len: usize, user: *mut c_void)>;

struct Sink {
    callback: unsafe extern "C" fn(level: i32, msg: *const u8, len: usize, user: *mut c_void),
    user: *mut c_void,
}

// `user` is an opaque token handed back verbatim and never dereferenced; the host may not free
// it until prisc_log_set_callback has returned after replacing or removing the sink.
unsafe impl Send for Sink {}
unsafe impl Sync for Sink {}

static SINK: RwLock<Option<Sink>> = RwLock::new(None);

/// Hands one line to the sink, if there is one. One lock read when there is none. Runs on the
/// thread that called the export; never called from an OS callback thread.
pub(crate) fn emit(level: PriscInteropLogLevel, message: &str) {
    let sink = SINK
        .read()
        .unwrap_or_else(PoisonError::into_inner);
    if let Some(sink) = sink.as_ref() {
        let bytes = message.as_bytes();
        unsafe { (sink.callback)(level as i32, bytes.as_ptr(), bytes.len(), sink.user) };
    }
}

/// Installs (or, with a null `callback`, removes) the process-global log sink. Takes the sink
/// exclusively, so it returns only after every in-flight call of the previous callback has
/// returned; afterwards the previous `user` may be freed. The callback runs on the thread of the
/// export that logs, must not block, must not let an exception escape and must not call any
/// export (the sink lock is not reentrant).
///
/// Status: `PRISC_OK` always.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_log_set_callback(callback: PriscLogCallback, user: *mut c_void) -> i32 {
    panic::guard("prisc_log_set_callback", || set_callback(callback, user))
}

fn set_callback(callback: PriscLogCallback, user: *mut c_void) -> Result {
    let mut sink = SINK
        .write()
        .unwrap_or_else(PoisonError::into_inner);
    *sink = callback.map(|callback| Sink {
        callback,
        user,
    });
    Ok(PRISC_OK)
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use std::sync::Mutex;

    /// The sink is process-global: tests that install one take this in turn.
    pub(crate) static SINK_TESTS: Mutex<()> = Mutex::new(());
    static LINES: Mutex<Vec<(i32, String, usize)>> = Mutex::new(Vec::new());

    /// The recording sink. Nested in a function so csbindgen (which reads these files) never sees
    /// an extern fn without `#[no_mangle]`.
    fn recorder() -> PriscLogCallback {
        unsafe extern "C" fn record(level: i32, msg: *const u8, len: usize, user: *mut c_void) {
            let text = String::from_utf8_lossy(std::slice::from_raw_parts(msg, len)).into_owned();
            LINES
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .push((level, text, user as usize));
        }
        Some(record)
    }

    fn matching(needle: &str) -> Vec<(i32, String, usize)> {
        LINES
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .iter()
            .filter(|(_, text, _)| text.contains(needle))
            .cloned()
            .collect()
    }

    #[test]
    fn the_sink_receives_level_text_and_user_until_removed() {
        let _lock = SINK_TESTS
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        assert_eq!(unsafe { prisc_log_set_callback(recorder(), 0x1234 as *mut c_void) }, 0);
        emit(PriscInteropLogLevel::Warn, "log-probe-one é");
        assert_eq!(unsafe { prisc_log_set_callback(None, std::ptr::null_mut()) }, 0);
        emit(PriscInteropLogLevel::Info, "log-probe-two");
        assert_eq!(matching("log-probe-one"), [(1, "log-probe-one é".to_string(), 0x1234)]);
        assert!(matching("log-probe-two").is_empty());
    }

    #[test]
    fn a_caught_panic_is_one_error_line() {
        let _lock = SINK_TESTS
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        unsafe { prisc_log_set_callback(recorder(), std::ptr::null_mut()) };
        let status = panic::guard("prisc_interop_selftest", || panic!("log-probe-three"));
        unsafe { prisc_log_set_callback(None, std::ptr::null_mut()) };
        assert_eq!(status, crate::ffi::status::PriscInteropStatus::Panic as i32);
        let seen = matching("log-probe-three");
        assert_eq!(seen.len(), 1);
        assert_eq!(seen[0].0, PriscInteropLogLevel::Error as i32);
        assert!(seen[0].1.contains("prisc_interop_selftest"));
    }
}
