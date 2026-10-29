//! Process-level helpers (WP-IPC; README section 4.4).

use crate::error::Error;
use crate::ffi::types::PriscString;
use crate::panic;

/// Windows: puts the process into the one process-wide kill-on-close job (created on first use,
/// never closed; a creation failure is latched).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_OS`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_process_assign_kill_on_close_job_windows(process_handle: isize) -> i32 {
    panic::guard("prisc_process_assign_kill_on_close_job_windows", || {
        let _ = process_handle;
        Err(Error::unsupported(
            "prisc_process_assign_kill_on_close_job_windows is not implemented yet",
        ))
    })
}

/// Windows: lets `process_id` (0 = any process) take the foreground once
/// (`prisc_platform::foreground::grant`).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_OS`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_process_allow_foreground_windows(process_id: u32) -> i32 {
    panic::guard("prisc_process_allow_foreground_windows", || {
        let _ = process_id;
        Err(Error::unsupported("prisc_process_allow_foreground_windows is not implemented yet"))
    })
}

/// Makes the FFmpeg libraries in the UTF-8 directory `path` loadable: `SetDllDirectoryW` on
/// Windows (empty clears it), the Mach-O dependency closure plus `dlopen` on macOS (`out_loaded` =
/// images loaded), nothing on Linux.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`, `PRISC_E_OS`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_process_prepare_ffmpeg_dir(path: *const u8, len: usize, out_loaded: *mut u32) -> i32 {
    panic::guard("prisc_process_prepare_ffmpeg_dir", || {
        let _ = (path, len, out_loaded);
        Err(Error::unsupported("prisc_process_prepare_ffmpeg_dir is not implemented yet"))
    })
}

/// Windows: the package full name of this process (mode 1, free with `prisc_string_free`).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_S_NONE` (no package identity), `PRISC_E_OS`, `PRISC_E_UNSUPPORTED` off Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_process_package_full_name_windows(out_name: *mut PriscString) -> i32 {
    panic::guard("prisc_process_package_full_name_windows", || {
        let _ = out_name;
        Err(Error::unsupported("prisc_process_package_full_name_windows is not implemented yet"))
    })
}
