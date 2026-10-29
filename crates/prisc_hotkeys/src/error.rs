use crate::Key;

/// Why the hook could not start, or a gesture could not be registered. The Display texts are part
/// of the capturer's protocol: it echoes them verbatim into the `settings_status` event
/// (HELPER_PROTOCOL.md §4.5).
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

/// The code the supervisor reports for a stale grant: Accessibility reads as granted, but the
/// event tap it allowed delivers no key events (macOS keeps a stale TCC entry after an app update;
/// the cure is removing and re-adding the app). Not an [`Error`] variant: the hook itself only
/// knows "permission", and the supervisor decides it is stale.
pub const STALE_GRANT_CODE: &str = "stale_grant";

/// Every machine-readable hotkey status code, `(C# name, code)`: one per [`Error`] variant in
/// declaration order, then [`STALE_GRANT_CODE`]. The capturer's `settings_status` event carries
/// these codes (HELPER_PROTOCOL.md section 4.5) and C# gets them generated as `HotkeyErrorCodes`
/// (`cargo codegen`), so a code is spelled once.
pub const ERROR_CODES: &[(&str, &str)] = &[
    ("Unsupported", "unsupported"),
    ("Permission", "permission"),
    ("HookFailed", "hook_failed"),
    ("NoKey", "no_key"),
    ("ModifierKey", "modifier_key"),
    ("UnsupportedKey", "unsupported_key"),
    ("Duplicate", "duplicate"),
    ("HookStopped", "hook_stopped"),
    ("StaleGrant", STALE_GRANT_CODE),
];

impl Error {
    /// This error's machine-readable code (one of [`ERROR_CODES`]).
    pub fn code(&self) -> &'static str {
        match self {
            Error::Unsupported => "unsupported",
            Error::PermissionDenied => "permission",
            Error::HookFailed(_) => "hook_failed",
            Error::NoKey => "no_key",
            Error::ModifierKey => "modifier_key",
            Error::UnsupportedKey(_) => "unsupported_key",
            Error::Duplicate => "duplicate",
            Error::Stopped => "hook_stopped",
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_variant_has_a_listed_code_and_codes_are_unique() {
        let all = [
            Error::Unsupported,
            Error::PermissionDenied,
            Error::HookFailed(String::new()),
            Error::NoKey,
            Error::ModifierKey,
            Error::UnsupportedKey(Key::A),
            Error::Duplicate,
            Error::Stopped,
        ];
        for (i, error) in all.iter().enumerate() {
            assert_eq!(ERROR_CODES[i].1, error.code(), "{error:?}");
        }
        assert_eq!(ERROR_CODES.len(), all.len() + 1);
        assert_eq!(ERROR_CODES.last().unwrap().1, STALE_GRANT_CODE);
        for (i, (name, code)) in ERROR_CODES.iter().enumerate() {
            for (other_name, other_code) in &ERROR_CODES[i + 1..] {
                assert_ne!(name, other_name);
                assert_ne!(code, other_code);
            }
        }
    }
}
