//! Opening a URL in whatever the user has set as their browser.
//!
//! `ShellExecuteW` is the whole implementation on purpose. The obvious
//! alternative — shelling out to `cmd /c start <url>` — flashes a console
//! window on a `windows_subsystem = "windows"` binary and, worse, `start`
//! treats `&` as a command separator, so the first query parameter of a
//! search URL would be swallowed and the rest run as a command. Handing
//! the string to the shell API directly has neither problem.

use windows::core::{w, HSTRING};
use windows::Win32::UI::Shell::ShellExecuteW;
use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

/// Ask the shell to open `url`, returning whether it accepted it.
///
/// Must be called from the winit thread: `ShellExecuteW` requires COM on
/// the calling thread, and `SystemInterop::init` is what provides it
/// (`CoInitializeEx(COINIT_APARTMENTTHREADED)`, once, at startup). Called
/// from a worker thread that never initialised COM this fails rather than
/// crashes, but it fails every time.
pub fn open_url(url: &str) -> bool {
    // ShellExecuteW's HINSTANCE return is not a handle despite the type:
    // it is an error code widened to pointer size, and the documented
    // success test is "greater than 32". Values at or below that are the
    // SE_ERR_* / ERROR_* codes (2 = file not found, 31 = no application
    // is associated with this protocol, …).
    let result = unsafe { ShellExecuteW(None, w!("open"), &HSTRING::from(url), None, None, SW_SHOWNORMAL) };
    let code = result.0 as isize;
    if code > 32 {
        return true;
    }
    // The URL is deliberately not logged: for the OCR search action it
    // carries text lifted off the user's screen, and these logs are
    // mirrored into Sentry.
    warn!("ShellExecuteW(open) refused a url, code {code}");
    false
}
