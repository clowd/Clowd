//! Windows halves of the exports, one module per area.

pub(crate) mod com;
pub(crate) mod devices;
pub(crate) mod gpu;
pub(crate) mod idle;
pub(crate) mod process;
pub(crate) mod system;
pub(crate) mod window;

use crate::error::Error;

/// `PRISC_E_OS` for a failed Windows call: the HRESULT as the last-error code (sign-extended from
/// i32), and `what` plus the system's message as the text.
#[allow(dead_code)] // the area exports use it
pub(crate) fn os_error(error: &windows_core::Error, what: &str) -> Error {
    let hr = error.code().0;
    Error::os(i64::from(hr), format!("{what} failed (0x{:08X}): {}", hr as u32, error.message()))
}

/// [`os_error`] for a bare HRESULT.
#[allow(dead_code)] // the area exports use it
pub(crate) fn hresult_error(hr: windows_core::HRESULT, what: &str) -> Error {
    os_error(&windows_core::Error::from(hr), what)
}
