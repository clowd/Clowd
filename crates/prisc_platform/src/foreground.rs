//! The Windows foreground grant.
//!
//! Windows lets only the process that owns the foreground (or one it handed the right to) bring
//! a window to the front. A process about to make another process show a window hands the right
//! on, once, with `AllowSetForegroundWindow`: Prisc.Ui to a helper it just spawned, prisc_capture
//! to the shell and the browser, the shell extension to Prisc.Ui.

use std::io;

/// Lets `process_id` (or, with `None`, any process: `ASFW_ANY`) take the foreground once.
///
/// Windows: `AllowSetForegroundWindow`; an error carries the Win32 code (Windows refuses unless
/// this process may itself set the foreground right now). Off Windows the concept does not exist
/// and the answer is [`io::ErrorKind::Unsupported`]. Any thread.
pub fn grant(process_id: Option<u32>) -> io::Result<()> {
    imp::grant(process_id)
}

#[cfg(windows)]
mod imp {
    use std::io;
    use windows::Win32::UI::WindowsAndMessaging::{AllowSetForegroundWindow, ASFW_ANY};

    pub(super) fn grant(process_id: Option<u32>) -> io::Result<()> {
        let pid = process_id.unwrap_or(ASFW_ANY);
        unsafe { AllowSetForegroundWindow(pid) }.map_err(|e| {
            // HRESULT_FROM_WIN32 keeps the Win32 code in the low 16 bits.
            let code = e.code().0;
            if (code as u32) & 0xFFFF_0000 == 0x8007_0000 {
                io::Error::from_raw_os_error(code & 0xFFFF)
            } else {
                io::Error::other(e.message())
            }
        })
    }
}

#[cfg(not(windows))]
mod imp {
    use std::io;

    pub(super) fn grant(_process_id: Option<u32>) -> io::Result<()> {
        Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "the foreground grant exists only on Windows",
        ))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn grant_is_unsupported_off_windows() {
        let result = grant(None);
        if cfg!(windows) {
            // Windows answers either way depending on who owns the foreground; it must not panic
            // and an error must carry a code.
            if let Err(e) = result {
                assert!(e.raw_os_error().is_some() || e.kind() == io::ErrorKind::Other);
            }
        } else {
            assert_eq!(result.unwrap_err().kind(), io::ErrorKind::Unsupported);
        }
    }
}
