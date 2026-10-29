//! The last-error readers (README A5). The slot itself lives in `src/error.rs`; every export
//! clears it on entry (`panic::guard`) and sets it on every non-OK status, so after a failing call
//! it always describes that call. It is thread-local: read it on the thread that made the call.

use std::panic::catch_unwind;

use crate::error;

/// Copies this thread's last error message into `buf` as UTF-8 (not NUL-terminated) and returns
/// its full length in bytes, which may exceed `cap`. Nothing is written unless the whole message
/// fits, so a caller never sees a split UTF-8 sequence: call with (null, 0) to size, allocate,
/// call again. Returns 0 when there is no error (the state after any successful call). Never
/// fails, never allocates, never clears the slot.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_last_error_message(buf: *mut u8, cap: usize) -> usize {
    // Not panic::guard: the guard clears the very slot this reads. Nothing below can unwind (the
    // slot accessors use try_with and try_borrow); the catch costs nothing on the success path.
    catch_unwind(|| {
        error::read(
            |slot| {
                let bytes = slot.message.as_bytes();
                if !bytes.is_empty() && !buf.is_null() && bytes.len() <= cap {
                    std::ptr::copy_nonoverlapping(bytes.as_ptr(), buf, bytes.len());
                }
                bytes.len()
            },
            || 0,
        )
    })
    .unwrap_or(0)
}

/// The raw OS code behind this thread's last error: an HRESULT, an OSStatus or an errno,
/// sign-extended from i32 (so `0x80004005` arrives as -2147467259). 0 when the last failure was
/// not an OS call, and 0 when there was no failure. Never fails, never clears the slot.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_last_error_code() -> i64 {
    catch_unwind(|| error::read(|slot| slot.code, || 0)).unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn message_is_sized_then_copied_whole_or_not_at_all() {
        error::set(-3, 0, "x".repeat(2048));
        let mut small = [0xABu8; 16];
        assert_eq!(unsafe { prisc_last_error_message(small.as_mut_ptr(), small.len()) }, 2048);
        assert!(small.iter().all(|b| *b == 0xAB));
        error::set(-3, 0, "GetDpiForMonitor failed (0x80004005)");
        let len = unsafe { prisc_last_error_message(std::ptr::null_mut(), 0) };
        let mut buf = vec![0u8; len];
        assert_eq!(unsafe { prisc_last_error_message(buf.as_mut_ptr(), buf.len()) }, len);
        assert_eq!(String::from_utf8(buf).unwrap(), "GetDpiForMonitor failed (0x80004005)");
        error::clear();
        assert_eq!(unsafe { prisc_last_error_message(std::ptr::null_mut(), 0) }, 0);
    }

    #[test]
    fn code_is_sign_extended() {
        error::set(-3, 0x8000_4005u32 as i32 as i64, "E_FAIL");
        assert_eq!(unsafe { prisc_last_error_code() }, -2147467259);
        error::clear();
        assert_eq!(unsafe { prisc_last_error_code() }, 0);
    }
}
