use crate::Key;

/// Why the hook could not start, or a gesture could not be registered. The Display texts are part
/// of the capturer's protocol: it echoes them verbatim into `CLOWD_SETTINGS_STATUS`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Error {
    /// No backend on this OS (Linux).
    Unsupported,
    /// macOS without the Accessibility permission.
    PermissionDenied,
    /// The OS refused the hook (the OS error text).
    HookFailed(String),
    /// The gesture's key is `Key::None`.
    NoKey,
    /// The gesture's key is a modifier key.
    ModifierKey,
    /// The key has no platform code on this OS, so it could never be seen.
    UnsupportedKey(Key),
    /// An equal gesture is already registered.
    Duplicate,
    /// The hook is no longer running.
    Stopped,
}

impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Error::Unsupported => f.write_str("global hotkeys are not supported on Linux yet"),
            Error::PermissionDenied => f.write_str("the Accessibility permission is required for global hotkeys"),
            Error::HookFailed(reason) => write!(f, "could not install the keyboard hook: {reason}"),
            Error::NoKey => f.write_str("gesture has no key"),
            Error::ModifierKey => f.write_str("a modifier key on its own cannot be a global hotkey"),
            Error::UnsupportedKey(key) => write!(f, "the key '{}' cannot be used as a global hotkey on this system", key.name()),
            Error::Duplicate => f.write_str("duplicate gesture"),
            Error::Stopped => f.write_str("the keyboard hook has stopped"),
        }
    }
}

impl std::error::Error for Error {}
