//! libobs log/crash handler installation.
//!
//! libobs's default log handler writes DEBUG/INFO/WARNING lines to **stdout**,
//! which would corrupt the line-delimited JSON protocol obs-express speaks on
//! stdout. `install_handlers` must therefore be called first thing in `main`,
//! before `obs_startup`, so every libobs line goes to stderr from the start.
//!
//! `base_set_log_handler` / `base_set_crash_handler` are plain exported symbols
//! of libobs but are not part of the bindgen allowlist, so they are declared
//! here directly.

use std::ffi::{c_char, c_int, c_void};
use std::io::Write;

/// `va_list` as passed by value on every ABI we target: a plain pointer on
/// x86_64-msvc and aarch64-apple, and the decayed `__va_list_tag*` on
/// x86_64 System V.
#[allow(non_camel_case_types)]
type va_list = *mut c_void;

extern "C" {
    fn base_set_log_handler(
        handler: Option<unsafe extern "C" fn(c_int, *const c_char, va_list, *mut c_void)>,
        param: *mut c_void,
    );
    fn base_set_crash_handler(
        handler: Option<unsafe extern "C" fn(*const c_char, va_list, *mut c_void)>,
        param: *mut c_void,
    );
}

// On MSVC, `vsnprintf` is a static-inline in stdio.h; legacy_stdio_definitions.lib
// provides a linkable object definition of it.
#[cfg_attr(
    all(windows, target_env = "msvc"),
    link(name = "legacy_stdio_definitions")
)]
extern "C" {
    fn vsnprintf(buf: *mut c_char, size: usize, fmt: *const c_char, args: va_list) -> c_int;
}

// libobs log levels (util/base.h).
const LOG_ERROR: c_int = 100;
const LOG_WARNING: c_int = 200;
const LOG_INFO: c_int = 300;

/// Formats `fmt`+`args` into a stack buffer. A single fixed buffer is used
/// (matching libobs's own 4096-byte def_log_handler) rather than a heap retry:
/// re-using a va_list requires va_copy, which is not portably expressible from
/// Rust, and libobs itself truncates at this size anyway.
unsafe fn format_message(fmt: *const c_char, args: va_list) -> String {
    let mut buf = [0u8; 4096];
    let ret = vsnprintf(buf.as_mut_ptr() as *mut c_char, buf.len(), fmt, args);
    if ret < 0 {
        // Formatting failed — fall back to the raw format string.
        return std::ffi::CStr::from_ptr(fmt).to_string_lossy().into_owned();
    }
    let len = (ret as usize).min(buf.len() - 1);
    String::from_utf8_lossy(&buf[..len]).into_owned()
}

unsafe extern "C" fn log_handler(
    lvl: c_int,
    fmt: *const c_char,
    args: va_list,
    _param: *mut c_void,
) {
    let tag = match lvl {
        l if l <= LOG_ERROR => "error",
        l if l <= LOG_WARNING => "warning",
        l if l <= LOG_INFO => "info",
        _ => "debug",
    };
    let msg = format_message(fmt, args);
    let stderr = std::io::stderr();
    let mut lock = stderr.lock();
    let _ = writeln!(lock, "[obs {tag}] {msg}");
}

unsafe extern "C" fn crash_handler(fmt: *const c_char, args: va_list, _param: *mut c_void) {
    let msg = format_message(fmt, args);
    let stderr = std::io::stderr();
    {
        let mut lock = stderr.lock();
        let _ = writeln!(lock, "[obs crash] {msg}");
        let _ = lock.flush();
    }
    // The libobs default crash handler exits 0 — exit 1 instead, skipping all
    // teardown (async-signal/crash context; matches the §1.4 exit policy).
    exit_now(1);
}

#[cfg(windows)]
fn exit_now(code: i32) -> ! {
    extern "system" {
        fn ExitProcess(exit_code: u32) -> !;
    }
    unsafe { ExitProcess(code as u32) }
}

#[cfg(not(windows))]
fn exit_now(code: i32) -> ! {
    extern "C" {
        fn _exit(code: c_int) -> !;
    }
    unsafe { _exit(code) }
}

/// Installs both handlers. Call before `obs_startup`.
pub fn install_handlers() {
    unsafe {
        base_set_log_handler(Some(log_handler), std::ptr::null_mut());
        base_set_crash_handler(Some(crash_handler), std::ptr::null_mut());
    }
}
