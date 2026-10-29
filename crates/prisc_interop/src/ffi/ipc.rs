//! The IPC child's stdout guard (WP-IPC; README section 4.4).

use crate::error::Error;
use crate::panic;

/// Claims the process stdout for the protocol (`prisc_ipc::guard::claim_stdout`): a private,
/// non-inheritable copy of it goes to `out_handle` (a HANDLE on Windows, an fd elsewhere; ownership
/// passes to the caller, README C2), and fd 1 is redirected to stderr. A failed fd 1 redirect is
/// `out_redirected = 0`, not an error.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_STATE` (second call), `PRISC_E_OS`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_ipc_claim_stdout(out_handle: *mut isize, out_redirected: *mut u8) -> i32 {
    panic::guard("prisc_ipc_claim_stdout", || {
        let _ = (out_handle, out_redirected);
        Err(Error::unsupported("prisc_ipc_claim_stdout is not implemented yet"))
    })
}
