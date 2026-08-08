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

use windows::Win32::System::{
    Diagnostics::ToolHelp::{CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W, TH32CS_SNAPPROCESS},
    Threading::GetCurrentProcessId,
};
use windows::Win32::UI::WindowsAndMessaging::AllowSetForegroundWindow;

/// Resolved once: the shell that spawned us does not change, and walking
/// the process table on every cycle would be silly.
static PARENT_PID: OnceLock<Option<u32>> = OnceLock::new();

/// Let the process that spawned us take the foreground next.
///
/// Best-effort in both directions — it fails if we do not hold foreground
/// rights at the moment of the call, which is why callers make it *before*
/// hiding the overlay. The parent is deliberately not verified to be
/// Clowd.Ui: whoever launched this capture is who gets to act on its
/// result, and that is true of the test harnesses too.
pub fn hand_to_parent() {
    let Some(pid) = *PARENT_PID.get_or_init(parent_pid) else {
        return;
    };
    // The grant is consumed by a single SetForegroundWindow, so re-granting
    // per cycle is the intended usage rather than a leak.
    if let Err(e) = unsafe { AllowSetForegroundWindow(pid) } {
        debug!("AllowSetForegroundWindow({pid}) refused ({e}); the shell may not be able to raise its next window");
    }
}

/// Our parent's process id, from a toolhelp snapshot. Safe to trust here
/// because the parent is alive for as long as we are — it is either
/// blocked on our exit or holding our host protocol pipes open — so the
/// pid cannot have been recycled onto someone else.
fn parent_pid() -> Option<u32> {
    let ours = unsafe { GetCurrentProcessId() };
    let snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) }
        .inspect_err(|e| warn!("could not snapshot the process list: {e}"))
        .ok()?;

    let mut entry = PROCESSENTRY32W {
        dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
        ..Default::default()
    };
    let mut found = None;
    unsafe {
        if Process32FirstW(snapshot, &mut entry).is_ok() {
            loop {
                if entry.th32ProcessID == ours {
                    found = Some(entry.th32ParentProcessID);
                    break;
                }
                if Process32NextW(snapshot, &mut entry).is_err() {
                    break;
                }
            }
        }
        let _ = windows::Win32::Foundation::CloseHandle(snapshot);
    }

    match found {
        Some(0) | None => {
            warn!("could not resolve our parent process; foreground rights will not be handed back");
            None
        }
        Some(pid) => {
            info!("resolved shell process {pid} for foreground handback");
            Some(pid)
        }
    }
}
