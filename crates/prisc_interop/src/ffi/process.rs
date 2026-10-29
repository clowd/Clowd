//! Process-level helpers (WP-IPC; README section 4.4): the kill-on-close job and the foreground
//! grant for spawned helpers, the FFmpeg library directory, and the package identity.

use std::io;

use crate::error::Error;
use crate::ffi::status::PRISC_OK;
use crate::ffi::types::PriscString;
use crate::{panic, util};

#[cfg(target_os = "macos")]
use crate::platform::macos::dylib;
#[cfg(windows)]
use crate::platform::windows::process as imp;

/// Windows: puts the process behind `process_handle` (a HANDLE with `PROCESS_SET_QUOTA` and
/// `PROCESS_TERMINATE`, borrowed for the call) into the one process-wide kill-on-close job, so it
/// dies when this process exits, crashes or is killed (HELPER_PROTOCOL.md section 7.4). The job is
/// created on first use with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK`
/// (whatever the child spawns stays out of it) and never closed; a failure to create it is
/// latched, so every later call answers the same error without trying again.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a zero handle; `PRISC_E_OS` when the job could
/// not be created or the process could not be assigned (a host job that forbids nesting or
/// breakaway); `PRISC_E_UNSUPPORTED` off Windows (stdin EOF and EPIPE end a helper there).
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_process_assign_kill_on_close_job_windows(process_handle: isize) -> i32 {
    panic::guard("prisc_process_assign_kill_on_close_job_windows", || {
        if process_handle == 0 {
            return Err(Error::null("process_handle"));
        }
        #[cfg(windows)]
        {
            imp::assign_kill_on_close_job(process_handle)?;
            Ok(PRISC_OK)
        }
        #[cfg(not(windows))]
        {
            Err(Error::unsupported("there are no job objects off Windows"))
        }
    })
}

/// Windows: lets `process_id` (0 = any process, `ASFW_ANY`) take the foreground once
/// (`prisc_platform::foreground::grant`, the same call prisc_capture and the shell extension
/// make). Windows refuses unless this process may itself set the foreground right now, so callers
/// treat a refusal as harmless.
///
/// Status: `PRISC_OK`; `PRISC_E_OS` when Windows refused, with the Win32 code;
/// `PRISC_E_UNSUPPORTED` off Windows (there is no foreground lock to hand over).
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_process_allow_foreground_windows(process_id: u32) -> i32 {
    panic::guard("prisc_process_allow_foreground_windows", || {
        let target = (process_id != 0).then_some(process_id);
        match prisc_platform::foreground::grant(target) {
            Ok(()) => Ok(PRISC_OK),
            Err(e) if e.kind() == io::ErrorKind::Unsupported => Err(Error::unsupported(e.to_string())),
            Err(e) => Err(Error::os(
                i64::from(e.raw_os_error().unwrap_or(0)),
                format!("AllowSetForegroundWindow({process_id}) failed: {e}"),
            )),
        }
    })
}

/// Makes the FFmpeg libraries in the UTF-8 directory `path` loadable by absolute path, one call on
/// every OS:
/// - Windows: `SetDllDirectoryW(path)`, so avcodec's own imports (libx264, zlib, srt) resolve
///   beside it; an empty path restores the default search order. Process-wide, last caller wins.
///   `out_loaded` = 0.
/// - macOS: the payload's dylibs ask for each other by `@rpath` and carry no `LC_RPATH`, so they
///   are loaded here first, by absolute path: the closure of the seven FFmpeg roots
///   (`libavutil.dylib` ... `libavdevice.dylib`, unversioned) over their `@rpath`
///   `LC_LOAD_DYLIB` / `LC_LOAD_WEAK_DYLIB` / `LC_REEXPORT_DYLIB` commands (fat and thin Mach-O),
///   then `dlopen` in passes until a pass loads nothing. Best-effort: what will not load is left for
///   the later load to fail on with its own message. `out_loaded` = images loaded; 0 for a
///   directory that is missing or holds no root.
/// - Elsewhere: nothing to do, `out_loaded` = 0.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null output, a null path with a non-zero length,
/// bytes that are not UTF-8 or an interior NUL; `PRISC_E_OS` when `SetDllDirectoryW` failed.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_process_prepare_ffmpeg_dir(path: *const u8, len: usize, out_loaded: *mut u32) -> i32 {
    panic::guard("prisc_process_prepare_ffmpeg_dir", || {
        let out_loaded = util::out(out_loaded, "out_loaded")?;
        let path = util::utf8(path, len, "path")?;
        if path.contains('\0') {
            return Err(Error::invalid_arg("path contains an interior NUL"));
        }
        #[cfg(windows)]
        let loaded = {
            imp::set_dll_directory(path)?;
            0
        };
        #[cfg(target_os = "macos")]
        let loaded = dylib::prepare(std::path::Path::new(path));
        #[cfg(not(any(windows, target_os = "macos")))]
        let loaded = {
            let _ = path;
            0
        };
        *out_loaded = loaded;
        Ok(PRISC_OK)
    })
}

/// Windows: the MSIX package full name this process was created under (the `package_identity`
/// diagnostics tag), sized in two passes so no name is too long. Written to `out_name` as a mode 1
/// string (free with `prisc_string_free`); the empty string with `PRISC_S_NONE`.
///
/// Status: `PRISC_OK`; `PRISC_S_NONE` when the process has no package identity
/// (`APPMODEL_ERROR_NO_PACKAGE`, a normal launch); `PRISC_E_INVALID_ARG` for a null output;
/// `PRISC_E_OS` for any other `APPMODEL_ERROR_*`, with the Win32 code; `PRISC_E_UNSUPPORTED` off
/// Windows.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_process_package_full_name_windows(out_name: *mut PriscString) -> i32 {
    panic::guard("prisc_process_package_full_name_windows", || {
        let out_name = util::out(out_name, "out_name")?;
        *out_name = PriscString::empty();
        #[cfg(windows)]
        {
            match imp::package_full_name()? {
                Some(name) => {
                    *out_name = PriscString::from_string(name);
                    Ok(PRISC_OK)
                }
                None => Ok(crate::ffi::status::PRISC_S_NONE),
            }
        }
        #[cfg(not(windows))]
        {
            Err(Error::unsupported("package identity exists only on Windows"))
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status::PriscInteropStatus as S;

    #[test]
    fn null_arguments_are_invalid_arg() {
        let mut loaded = 7u32;
        assert_eq!(unsafe { prisc_process_assign_kill_on_close_job_windows(0) }, S::InvalidArg as i32);
        assert_eq!(
            unsafe { prisc_process_prepare_ffmpeg_dir(std::ptr::null(), 0, std::ptr::null_mut()) },
            S::InvalidArg as i32
        );
        assert_eq!(
            unsafe { prisc_process_prepare_ffmpeg_dir(std::ptr::null(), 4, &mut loaded) },
            S::InvalidArg as i32
        );
        assert_eq!(
            unsafe { prisc_process_package_full_name_windows(std::ptr::null_mut()) },
            S::InvalidArg as i32
        );
        assert_eq!(loaded, 7, "a refused call writes nothing");
    }

    #[test]
    fn bad_paths_are_invalid_arg() {
        let mut loaded = 0u32;
        let invalid = [0xFFu8, 0xFE];
        assert_eq!(
            unsafe { prisc_process_prepare_ffmpeg_dir(invalid.as_ptr(), invalid.len(), &mut loaded) },
            S::InvalidArg as i32
        );
        let nul = b"/tmp\0/x";
        assert_eq!(
            unsafe { prisc_process_prepare_ffmpeg_dir(nul.as_ptr(), nul.len(), &mut loaded) },
            S::InvalidArg as i32
        );
    }
}
