//! The one modal prompt the overlay ever raises: an action failed, retry
//! or give up.
//!
//! xdialog draws it on every platform with its default backend (Fluent on
//! Windows 10+, the macOS alert look, the Ubuntu look on Linux), hosted
//! inside our own winit event loop: winit allows one loop per process, so
//! xdialog can't run its own. [`install`] creates the process-wide
//! [`XDialogHost`] once, and every capture cycle's run of the loop is
//! wrapped with it (`run_cycle` in `main.rs`). The standby runs between
//! cycles are not wrapped — they never prompt — and xdialog queues nothing
//! for them to serve.
//!
//! The prompt cannot block: the event-loop thread is the one that has to
//! draw it (a blocking xdialog call there fails with
//! `BlockingCallOnUiThread`). So [`ask_retry_cancel`] returns at once and
//! the app polls [`RetryPrompt::answer`] from `about_to_wait`; xdialog
//! wakes the loop when the answer arrives.

use xdialog::host::XDialogHost;
use xdialog::{MessageDialogProxy, XDialogIcon, XDialogIconSource, XDialogOptions, XDialogResult};

/// Button indices, in xdialog's own order for retry/cancel prompts.
const BUTTONS: [&str; 2] = ["Cancel", "Retry"];
const RETRY: usize = 1;

/// The shell's app icon (the same file `Clowd.Ui.csproj` uses), so the
/// prompt's window and taskbar button read as Clowd's rather than a
/// generic one. xdialog applies it where the platform has a window icon
/// (Windows, X11).
const APP_ICON: &[u8] = include_bytes!("../../../clowd_ui/Clowd.Ui/Assets/clowd-default.ico");

/// Install xdialog in the process's event loop. `None` if no backend can
/// run here (logged): every prompt then reads as cancel, the outcome the
/// user could reach anyway.
pub fn install(event_loop: &winit::event_loop::EventLoop<()>) -> Option<XDialogHost> {
    // The wake-up arrives as `()` in whatever app the run has: the cycle app
    // ignores user events, and the standby app's handler is an idempotent
    // poll of its own channels.
    let proxy = event_loop.create_proxy();
    match xdialog::XDialogBuilder::new().into_host(move || {
        let _ = proxy.send_event(());
    }) {
        Ok(host) => Some(host),
        Err(e) => {
            log::warn!("xdialog unavailable, retry prompts will read as cancel: {e}");
            None
        }
    }
}

/// An open retry/cancel prompt. Dropping it closes the dialog.
pub struct RetryPrompt(MessageDialogProxy);

impl RetryPrompt {
    /// `Some(true)` = retry, `Some(false)` = cancel (or the prompt could not
    /// be shown at all), `None` while it is still open.
    pub fn answer(&self) -> Option<bool> {
        match self.0.try_result()? {
            Ok(result) => Some(result == XDialogResult::ButtonPressed(RETRY)),
            Err(e) => {
                log::warn!("retry prompt failed, treating as cancel: {e}");
                Some(false)
            }
        }
    }
}

/// Show an error prompt titled `title` with `message` as the body and
/// RETRY / CANCEL buttons, without waiting for the answer.
pub fn ask_retry_cancel(title: &str, message: &str) -> RetryPrompt {
    RetryPrompt(xdialog::show_message(XDialogOptions {
        title: "Clowd Capture".into(),
        main_instruction: title.into(),
        message: message.into(),
        icon: XDialogIcon::Error,
        icon_source: Some(XDialogIconSource::Bytes(APP_ICON.into())),
        buttons: BUTTONS
            .iter()
            .map(|b| b.to_string())
            .collect(),
    }))
}
