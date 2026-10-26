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
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, OnceLock};

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
    {
        let stderr = std::io::stderr();
        let mut lock = stderr.lock();
        let _ = writeln!(lock, "[obs {tag}] {msg}");
    }
    notify_watches(&msg);
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

// ---------------------------------------------------------------------------
// Log watches
// ---------------------------------------------------------------------------

/// A registration made by [`watch`]: every libobs log line containing one of
/// `needles` is captured until the watch is dropped.
///
/// This exists because some plugins report an outcome *only* as a log line.
/// The motivating case is linux-pipewire's portal source: when the user
/// cancels or the portal refuses the screen-share dialog, the plugin logs
/// "denied or cancelled by user" and simply stops — no signal, no state the
/// host can query, the source just stays 0x0 forever
/// (plugins/linux-pipewire/screencast-portal.c). Watching the log is the only
/// way to tell "the user said no" from "the user has not answered yet".
pub struct LogWatch {
    id: usize,
    hit: Arc<Mutex<Option<String>>>,
}

struct WatchEntry {
    id: usize,
    needles: &'static [&'static str],
    hit: Arc<Mutex<Option<String>>>,
}

/// Registered watches. The count lets the log handler skip the lock entirely
/// in the normal case of no watches.
static WATCHES: OnceLock<Mutex<Vec<WatchEntry>>> = OnceLock::new();
static WATCH_COUNT: AtomicUsize = AtomicUsize::new(0);
static NEXT_WATCH_ID: AtomicUsize = AtomicUsize::new(0);

fn watches() -> &'static Mutex<Vec<WatchEntry>> {
    WATCHES.get_or_init(|| Mutex::new(Vec::new()))
}

/// Starts capturing log lines that contain any of `needles` (plain substring
/// match on the formatted message). Only the first match is kept. Register it
/// before triggering whatever may log, since earlier lines are not replayed.
pub fn watch(needles: &'static [&'static str]) -> LogWatch {
    let id = NEXT_WATCH_ID.fetch_add(1, Ordering::Relaxed);
    let hit = Arc::new(Mutex::new(None));
    let mut list = watches().lock().unwrap_or_else(|e| e.into_inner());
    list.push(WatchEntry {
        id,
        needles,
        hit: hit.clone(),
    });
    WATCH_COUNT.store(list.len(), Ordering::Release);
    LogWatch { id, hit }
}

impl LogWatch {
    /// The first matching line seen so far, if any.
    pub fn matched(&self) -> Option<String> {
        self.hit.lock().unwrap_or_else(|e| e.into_inner()).clone()
    }
}

impl Drop for LogWatch {
    fn drop(&mut self) {
        let mut list = watches().lock().unwrap_or_else(|e| e.into_inner());
        list.retain(|w| w.id != self.id);
        WATCH_COUNT.store(list.len(), Ordering::Release);
    }
}

/// Called by the log handler after a line is written. Runs on whichever
/// thread logged; never logs itself (that would re-enter the handler).
fn notify_watches(msg: &str) {
    if WATCH_COUNT.load(Ordering::Acquire) == 0 {
        return;
    }
    let list = watches().lock().unwrap_or_else(|e| e.into_inner());
    for entry in list.iter() {
        if entry.needles.iter().any(|n| msg.contains(n)) {
            let mut hit = entry.hit.lock().unwrap_or_else(|e| e.into_inner());
            if hit.is_none() {
                *hit = Some(msg.to_string());
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn watches_capture_the_first_matching_line_until_dropped() {
        static NEEDLES: &[&str] = &["cancelled by user", "Error selecting"];
        let w = watch(NEEDLES);
        notify_watches("[pipewire] Screencast session created");
        assert_eq!(w.matched(), None);
        notify_watches("[pipewire] Failed to start screencast, denied or cancelled by user");
        notify_watches("[pipewire] Error selecting screencast source: x");
        assert_eq!(
            w.matched().as_deref(),
            Some("[pipewire] Failed to start screencast, denied or cancelled by user")
        );
        drop(w);
        assert!(watches()
            .lock()
            .unwrap()
            .iter()
            .all(|e| e.needles.as_ptr() != NEEDLES.as_ptr()));
    }
}
