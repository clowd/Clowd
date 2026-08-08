//! Handing our foreground rights back to the shell.
//!
//! Windows' foreground lock lets only the process that currently owns the
//! foreground window call `SetForegroundWindow` — and lets that process
//! hand the right on, once, with `AllowSetForegroundWindow`. Clowd.Ui uses
//! that to pass its rights to us when the hotkey fires
//! (`ScreenCaptureService`), which is what lets the overlay take focus the
//! instant it appears.
//!
//! This is the return leg. When a cycle ends, the overlay is the foreground
//! window and the shell is about to act on what the user chose — open an
//! editor, or spawn `clowd_scroll_driver` and grant *it* foreground rights
//! in turn. The shell cannot grant what it no longer holds, so without this
//! the chain breaks the moment our window goes away: the scrolling capture
//! then cannot raise the window the user selected, and refuses to run
//! rather than photograph whatever is covering it.

use std::sync::OnceLock;

use windows::Win32::UI::WindowsAndMessaging::{AllowSetForegroundWindow, ASFW_ANY};

/// The shell's pid, from `--shell-pid`. Set once during startup, before any
/// window exists; `None` in a standalone run, where there is no shell.
static SHELL_PID: OnceLock<Option<u32>> = OnceLock::new();

/// Record who to hand foreground rights back to. Called once from `main`
/// with whatever the shell passed on the command line.
///
/// The pid comes from the shell rather than from walking the process table
/// for our parent: the shell trivially knows its own id, and because the
/// capturer does not outlive it the two can never disagree — no stale
/// handle, no recycled pid, nothing to re-resolve.
pub fn set_shell_pid(pid: Option<u32>) {
    match pid {
        Some(0) | None => info!("no shell pid supplied; foreground rights will not be handed back"),
        Some(pid) => info!("foreground rights will be handed back to shell process {pid}"),
    }
    let _ = SHELL_PID.set(pid.filter(|p| *p != 0));
}

/// Let the shell take the foreground next.
///
/// Best-effort in both directions — it fails if we do not hold foreground
/// rights at the moment of the call, which is why callers make it *before*
/// hiding the overlay.
pub fn hand_to_shell() {
    let Some(pid) = SHELL_PID.get().copied().flatten() else {
        return;
    };
    // The grant is consumed by a single SetForegroundWindow, so re-granting
    // per cycle is the intended usage rather than a leak.
    if let Err(e) = unsafe { AllowSetForegroundWindow(pid) } {
        debug!("AllowSetForegroundWindow({pid}) refused ({e}); the shell may not be able to raise its next window");
    }
}

/// Give the NEXT foreground-taker the grant, whoever it turns out to be.
///
/// Used by the OCR SEARCH action. `ShellExecuteW` may hand the URL to an
/// **already running** browser process — one we did not start and which
/// therefore holds no activation rights of its own. [`hand_to_shell`] is
/// no help there: it names a single pid, and it names the wrong one, so
/// the browser would raise a tab we cannot see and merely flash in the
/// taskbar. `ASFW_ANY` lets *any* process come forward, exactly once.
///
/// Same best-effort caveat as [`hand_to_shell`]: it only works while we
/// still hold the foreground, so call it before hiding the overlay.
///
/// Windows-only by design — macOS has no foreground lock, so there is
/// nothing to grant; the mac side of `SystemInterop` is a no-op.
pub fn allow_any_foreground() {
    if let Err(e) = unsafe { AllowSetForegroundWindow(ASFW_ANY) } {
        debug!("AllowSetForegroundWindow(ASFW_ANY) refused ({e}); the browser may only flash in the taskbar");
    }
}
