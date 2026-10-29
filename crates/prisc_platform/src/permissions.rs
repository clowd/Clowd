//! macOS privacy permissions, checked one way for every crate.
//!
//! Accessibility is `AXIsProcessTrusted() && CGPreflightPostEventAccess()`. AXIsProcessTrusted is
//! answered from a per-process cache after its first call, so on its own it may never see a
//! revocation; CGPreflightPostEventAccess asks tccd every call (about 8 ms) and is fresh. Both are
//! non-prompting. Event taps and posted events need both, so this is the rule the hotkey hook, the
//! scrolling driver and the UI share.
//!
//! Screen Recording is `CGPreflightScreenCaptureAccess()`.
//!
//! Off macOS every permission is [`State::NotApplicable`] and the convenience checks answer
//! `true` (nothing stands in the way).

/// A permission this module knows how to check.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Permission {
    /// Capturing other apps' windows and the screen (macOS TCC `ScreenCapture`).
    ScreenRecording,
    /// Event taps and posted events (macOS TCC `Accessibility` plus post-event access).
    Accessibility,
}

/// What the OS says about a permission right now.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum State {
    Granted,
    Denied,
    /// The OS has no such permission (every permission off macOS).
    NotApplicable,
}

impl State {
    /// Whether nothing stands in the way: granted, or not a concept on this OS.
    pub fn is_usable(self) -> bool {
        self != State::Denied
    }
}

/// The current state. Never prompts. Any thread.
pub fn state(permission: Permission) -> State {
    imp::state(permission)
}

/// Makes sure the permission is granted, prompting only when it is not. When [`state`] already
/// answers Granted this returns Granted without touching the OS prompt, so it is safe to call on
/// every use (an "ensure"). Otherwise:
///
/// - Screen Recording: `CGRequestScreenCaptureAccess` (the OS shows its prompt at most once per
///   app; later calls only re-read).
/// - Accessibility: `AXIsProcessTrustedWithOptions({kAXTrustedCheckOptionPrompt: true})` when AX
///   is not trusted, `CGRequestPostEventAccess` when AX is trusted but post-event access is
///   missing.
///
/// Then the state is read again with the same rule as [`state`] and returned. Any thread (the
/// prompts are posted by the OS on its own).
pub fn request(permission: Permission) -> State {
    let current = state(permission);
    if current != State::Denied {
        return current;
    }
    imp::prompt(permission);
    state(permission)
}

/// Screen Recording is usable (true off macOS). Never prompts.
pub fn screen_recording() -> bool {
    state(Permission::ScreenRecording).is_usable()
}

/// Accessibility is usable by the unified rule (true off macOS). Never prompts.
pub fn accessibility() -> bool {
    state(Permission::Accessibility).is_usable()
}

#[cfg(target_os = "macos")]
mod imp {
    use super::{Permission, State};
    use objc2_core_foundation::{kCFBooleanTrue, CFDictionary, CFString};
    use objc2_core_graphics::{
        CGPreflightPostEventAccess, CGPreflightScreenCaptureAccess, CGRequestPostEventAccess, CGRequestScreenCaptureAccess,
    };

    #[link(name = "ApplicationServices", kind = "framework")]
    extern "C" {
        fn AXIsProcessTrusted() -> bool;
        fn AXIsProcessTrustedWithOptions(options: *const std::ffi::c_void) -> bool;
        static kAXTrustedCheckOptionPrompt: &'static CFString;
    }

    fn ax_trusted() -> bool {
        unsafe { AXIsProcessTrusted() }
    }

    pub(super) fn state(permission: Permission) -> State {
        let granted = match permission {
            Permission::ScreenRecording => CGPreflightScreenCaptureAccess(),
            Permission::Accessibility => ax_trusted() && CGPreflightPostEventAccess(),
        };
        if granted {
            State::Granted
        } else {
            State::Denied
        }
    }

    pub(super) fn prompt(permission: Permission) {
        match permission {
            Permission::ScreenRecording => {
                CGRequestScreenCaptureAccess();
            }
            Permission::Accessibility => {
                if !ax_trusted() {
                    prompt_ax();
                } else if !CGPreflightPostEventAccess() {
                    CGRequestPostEventAccess();
                }
            }
        }
    }

    /// AXIsProcessTrustedWithOptions with the prompt option. The dictionary is built with the
    /// kCFType key and value callbacks (`CFDictionary::from_slices`), which retain the key and
    /// value: a dictionary built with null callbacks crashed the C# version once (3947ce3d3).
    pub(super) fn prompt_ax() -> bool {
        let Some(yes) = (unsafe { kCFBooleanTrue }) else {
            return ax_trusted();
        };
        let key: &CFString = unsafe { kAXTrustedCheckOptionPrompt };
        let options = prompt_options(key, yes);
        let ptr: *const CFDictionary<CFString, objc2_core_foundation::CFBoolean> = &*options;
        unsafe { AXIsProcessTrustedWithOptions(ptr.cast()) }
    }

    pub(super) fn prompt_options(
        key: &CFString,
        value: &objc2_core_foundation::CFBoolean,
    ) -> objc2_core_foundation::CFRetained<CFDictionary<CFString, objc2_core_foundation::CFBoolean>> {
        CFDictionary::from_slices(&[key], &[value])
    }
}

#[cfg(not(target_os = "macos"))]
mod imp {
    use super::{Permission, State};

    pub(super) fn state(_permission: Permission) -> State {
        State::NotApplicable
    }

    pub(super) fn prompt(_permission: Permission) {}
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn state_answers_for_every_kind_and_agrees_with_the_convenience_checks() {
        for permission in [Permission::ScreenRecording, Permission::Accessibility] {
            let state = state(permission);
            if cfg!(target_os = "macos") {
                assert_ne!(state, State::NotApplicable, "{permission:?}");
            } else {
                assert_eq!(state, State::NotApplicable, "{permission:?}");
            }
        }
        assert_eq!(screen_recording(), state(Permission::ScreenRecording).is_usable());
        assert_eq!(accessibility(), state(Permission::Accessibility).is_usable());
    }

    #[test]
    fn usable_means_not_denied() {
        assert!(State::Granted.is_usable());
        assert!(State::NotApplicable.is_usable());
        assert!(!State::Denied.is_usable());
    }

    /// Only an already granted (or not applicable) permission is requested here, which by
    /// contract never reaches a prompt, so the test is safe on a developer machine and in CI.
    #[test]
    fn request_on_a_usable_permission_returns_its_state_without_prompting() {
        for permission in [Permission::ScreenRecording, Permission::Accessibility] {
            let before = state(permission);
            if before.is_usable() {
                assert_eq!(request(permission), before, "{permission:?}");
            }
        }
    }

    /// The options dictionary of the AX prompt carries the prompt key with kCFBooleanTrue and
    /// retains both (kCFType callbacks): the count and the lookup must survive the inputs.
    #[cfg(target_os = "macos")]
    #[test]
    fn ax_prompt_options_use_cftype_callbacks() {
        use objc2_core_foundation::{kCFBooleanTrue, CFString};
        let key = CFString::from_str("AXTrustedCheckOptionPrompt");
        let yes = unsafe { kCFBooleanTrue }.expect("kCFBooleanTrue");
        let options = imp::prompt_options(&key, yes);
        drop(key);
        assert_eq!(options.len(), 1);
        let lookup = CFString::from_str("AXTrustedCheckOptionPrompt");
        assert!(options.get(&lookup).is_some());
    }
}
