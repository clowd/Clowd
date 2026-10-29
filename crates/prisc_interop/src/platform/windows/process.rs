//! Windows halves of `prisc_process_*`: the kill-on-close job, the FFmpeg DLL directory and the
//! package identity. The foreground grant has no half here: it is `prisc_platform::foreground`.

use std::ffi::c_void;
use std::sync::OnceLock;

use windows::Win32::Foundation::{CloseHandle, APPMODEL_ERROR_NO_PACKAGE, ERROR_INSUFFICIENT_BUFFER, ERROR_SUCCESS, HANDLE};
use windows::Win32::Storage::Packaging::Appx::GetCurrentPackageFullName;
use windows::Win32::System::JobObjects::{
    AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation, SetInformationJobObject,
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK,
};
use windows::Win32::System::LibraryLoader::SetDllDirectoryW;
use windows_core::{PCWSTR, PWSTR};

use crate::error::{Error, Result};
use crate::platform::windows::os_error;

/// The process-wide job: its HANDLE as an integer (HANDLE is not `Sync`), or the latched failure
/// to create it. Never closed: the OS closes it when this process ends, which is the point.
static JOB: OnceLock<std::result::Result<isize, Error>> = OnceLock::new();

/// Assigns `process` to the job, creating the job on first use.
pub(crate) fn assign_kill_on_close_job(process: isize) -> Result<()> {
    let job = JOB.get_or_init(create_job).clone()?;
    // SAFETY: both handles are valid for the call: the job is never closed, and the caller lends
    // the process handle for the duration of the call.
    unsafe { AssignProcessToJobObject(HANDLE(job as *mut c_void), HANDLE(process as *mut c_void)) }
        .map_err(|e| os_error(&e, "AssignProcessToJobObject"))
}

fn create_job() -> std::result::Result<isize, Error> {
    // SAFETY: an anonymous job with default security; the handle is checked below.
    let job = unsafe { CreateJobObjectW(None, PCWSTR::null()) }.map_err(|e| os_error(&e, "CreateJobObjectW"))?;
    let mut info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
    // SAFETY: `info` is the structure the class names, with its exact size, alive for the call.
    let set = unsafe {
        SetInformationJobObject(
            job,
            JobObjectExtendedLimitInformation,
            std::ptr::from_ref(&info).cast::<c_void>(),
            std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
        )
    };
    if let Err(e) = set {
        // SAFETY: the job was created above and is not used again.
        let _ = unsafe { CloseHandle(job) };
        return Err(os_error(&e, "SetInformationJobObject(KILL_ON_JOB_CLOSE)"));
    }
    Ok(job.0 as isize)
}

/// `SetDllDirectoryW`; an empty path restores the default search order (`SetDllDirectoryW(NULL)`).
/// The caller has refused an interior NUL.
pub(crate) fn set_dll_directory(path: &str) -> Result<()> {
    if path.is_empty() {
        // SAFETY: NULL is the documented "restore the default" argument.
        return unsafe { SetDllDirectoryW(PCWSTR::null()) }.map_err(|e| os_error(&e, "SetDllDirectoryW(NULL)"));
    }
    let wide: Vec<u16> = path
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();
    // SAFETY: a NUL-terminated UTF-16 string alive for the call.
    unsafe { SetDllDirectoryW(PCWSTR(wide.as_ptr())) }.map_err(|e| os_error(&e, "SetDllDirectoryW"))
}

/// This process's package full name, or `None` without a package identity. Two passes: the sizing
/// call reports the length in UTF-16 units including the NUL.
pub(crate) fn package_full_name() -> Result<Option<String>> {
    let mut length = 0u32;
    // SAFETY: a sizing call with no buffer.
    match unsafe { GetCurrentPackageFullName(&mut length, None) } {
        APPMODEL_ERROR_NO_PACKAGE => return Ok(None),
        ERROR_INSUFFICIENT_BUFFER => {}
        // a packaged process with an empty name has nothing to report
        ERROR_SUCCESS => return Ok(None),
        other => return Err(package_error(other.0, "GetCurrentPackageFullName(size)")),
    }
    let mut buffer = vec![0u16; length as usize];
    // SAFETY: `buffer` holds `length` units, the size the sizing call asked for.
    match unsafe { GetCurrentPackageFullName(&mut length, Some(PWSTR(buffer.as_mut_ptr()))) } {
        ERROR_SUCCESS => {}
        APPMODEL_ERROR_NO_PACKAGE => return Ok(None),
        other => return Err(package_error(other.0, "GetCurrentPackageFullName")),
    }
    let end = buffer
        .iter()
        .position(|&unit| unit == 0)
        .unwrap_or(buffer.len());
    Ok(Some(String::from_utf16_lossy(&buffer[..end])))
}

/// `PRISC_E_OS` carrying the Win32 code `GetCurrentPackageFullName` returned (an
/// `APPMODEL_ERROR_*` or `ERROR_*` value, not an HRESULT), so the caller reports it as is.
fn package_error(code: u32, call: &str) -> Error {
    Error::os(i64::from(code), format!("{call} failed ({code})."))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn setting_then_clearing_the_dll_directory_both_succeed() {
        let temp = std::env::temp_dir();
        set_dll_directory(&temp.to_string_lossy()).unwrap();
        set_dll_directory("").unwrap();
    }

    #[test]
    fn the_test_host_has_no_package_identity() {
        // cargo test never runs packaged; a packaged run must at least name something.
        if let Some(name) = package_full_name().unwrap() {
            assert!(!name.is_empty());
        }
    }
}
