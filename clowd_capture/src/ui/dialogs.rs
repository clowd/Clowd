//! The one modal prompt the overlay ever raises: an action failed, retry
//! or give up.
//!
//! Windows and macOS go through xdialog's direct backends (Win32 task
//! dialog, CoreFoundation alert), initialised once in
//! `SystemInterop::init`. Linux has no such backend in the build — xdialog
//! would bring GTK along — so the same prompt goes through rfd, which asks
//! the desktop portal and falls back to zenity. The wrapper exists so the
//! command dispatcher has one call and no platform arms.

/// Show an error prompt titled `title` with `message` as the body and
/// RETRY / CANCEL buttons. `true` = retry. A prompt that could not be shown
/// at all reads as cancel: the failed action stays failed and the overlay
/// closes, which is the outcome the user could reach anyway.
#[cfg(any(windows, target_os = "macos"))]
pub fn show_retry_cancel(title: &str, message: &str) -> bool {
    xdialog::show_message_retry_cancel("Clowd Capture", title, message, xdialog::XDialogIcon::Error).unwrap_or(false)
}

/// See the Windows/macOS variant. The custom button pair maps onto
/// zenity's `--ok-label` / `--cancel-label`, and rfd answers with the
/// pressed label.
#[cfg(target_os = "linux")]
pub fn show_retry_cancel(title: &str, message: &str) -> bool {
    use rfd::{MessageButtons, MessageDialog, MessageDialogResult, MessageLevel};

    const RETRY: &str = "Retry";
    let result = MessageDialog::new()
        .set_level(MessageLevel::Error)
        .set_title(title)
        .set_description(message)
        .set_buttons(MessageButtons::OkCancelCustom(RETRY.to_string(), "Cancel".to_string()))
        .show();
    matches!(result, MessageDialogResult::Custom(label) if label == RETRY)
}
