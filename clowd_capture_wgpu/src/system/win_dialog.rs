//! Windows-specific dialog implementation using xdialog (TaskDialog backend).

use xdialog::{init_win32_direct, show_message_retry_cancel, XDialogIcon};

/// Initialize the Windows dialog subsystem. Must be called once at startup
/// before any dialogs are shown.
pub fn init() {
    init_win32_direct();
}

/// Show a retry/cancel error dialog. Returns true if Retry was pressed,
/// false if Cancel was pressed.
pub fn show_error_retry_cancel(title: &str, message: &str) -> bool {
    show_message_retry_cancel(title, title, message, XDialogIcon::Error).unwrap_or(false)
}
