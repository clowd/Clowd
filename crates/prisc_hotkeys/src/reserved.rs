//! The gestures each OS keeps for itself, and the OS a per-OS question is asked for.
//!
//! The reserved gestures are never bound: recording one is treated as a cancel (Prisc.Ui's
//! `GestureField`), and one already in the settings is refused at registration and left off the
//! standby capturer's command line. The recorder's hook swallows the key it records, so these are
//! exactly what a user presses to get away from a field that is listening, and a binding would
//! then swallow them in every app.
//!
//! Every function takes the [`Os`] it answers for, so each OS's table can be tested on any one
//! machine; [`Os::current`] is the one the process runs on.

use crate::{Gesture, Key, Modifiers};

/// The OS whose conventions a question is asked for: its reserved gestures, the word it uses for
/// the Meta modifier, the keys its hook can report and the legends on its keyboards.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash)]
pub enum Os {
    Windows,
    MacOS,
    /// Every other OS. It has no reserved gestures, calls Meta "Super", refuses no key (there is
    /// no hook backend to refuse one) and has PC keyboards, so its keycaps are the Windows ones.
    Linux,
}

impl Os {
    /// The OS this process runs on.
    pub const fn current() -> Os {
        if cfg!(windows) {
            Os::Windows
        } else if cfg!(target_os = "macos") {
            Os::MacOS
        } else {
            Os::Linux
        }
    }
}

const ALT: u8 = Modifiers::ALT.bits();
const CONTROL: u8 = Modifiers::CONTROL.bits();
const CMD: u8 = Modifiers::META.bits();
const ALT_SHIFT: u8 = ALT | Modifiers::SHIFT.bits();
const CONTROL_SHIFT: u8 = CONTROL | Modifiers::SHIFT.bits();
const CMD_SHIFT: u8 = CMD | Modifiers::SHIFT.bits();
const CMD_ALT: u8 = CMD | ALT;
const CMD_CONTROL: u8 = CMD | CONTROL;

/// Whether `os` needs `gesture` for itself. The modifiers must match exactly: Ctrl+Alt+Tab is not
/// Alt+Tab.
///
/// - Windows: task switching (Alt+Tab, Alt+Shift+Tab, Alt+Esc, Ctrl+Esc) and Task Manager
///   (Ctrl+Shift+Esc).
/// - macOS: app and window switching (Cmd+Tab, Cmd+Shift+Tab, Cmd+`, Cmd+Shift+`; window cycling
///   sits beside left Shift on an ISO keyboard, so `OemBackslash` too), Spotlight (Cmd+Space),
///   leaving the window (Cmd+Q, Cmd+W, Cmd+H, Cmd+M) and the ways out of a hung app (Force Quit,
///   Cmd+Opt+Esc; Lock Screen, Ctrl+Cmd+Q). Cmd+Shift+3/4/5 stay bindable: taking over the
///   screenshot keys is a screenshot tool's business.
/// - Linux: none.
pub fn is_system_reserved_on(gesture: Gesture, os: Os) -> bool {
    let combination = (gesture.key, gesture.modifiers.bits());
    match os {
        Os::Windows => matches!(
            combination,
            (Key::Tab, ALT | ALT_SHIFT) | (Key::Escape, ALT | CONTROL | CONTROL_SHIFT)
        ),
        Os::MacOS => matches!(
            combination,
            (Key::Tab | Key::Oem3 | Key::OemBackslash, CMD | CMD_SHIFT)
                | (Key::Space | Key::Q | Key::W | Key::H | Key::M, CMD)
                | (Key::Escape, CMD_ALT)
                | (Key::Q, CMD_CONTROL)
        ),
        Os::Linux => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn g(text: &str) -> Gesture {
        text.parse()
            .unwrap_or_else(|e| panic!("{text:?}: {e}"))
    }

    /// The Windows cases of the C# table this replaced (HotkeyGestureTests), kept verbatim.
    #[test]
    fn windows_reserves_task_switching_and_task_manager_only() {
        for text in ["Alt+Tab", "Alt+Shift+Tab", "Alt+Escape", "Control+Escape", "Control+Shift+Escape"] {
            assert!(is_system_reserved_on(g(text), Os::Windows), "{text}");
            assert!(!is_system_reserved_on(g(text), Os::MacOS), "{text}");
            assert!(!is_system_reserved_on(g(text), Os::Linux), "{text}");
        }
        for text in [
            "Tab",
            "Control+Tab",
            "Escape",
            "Meta+Q",
            "Meta+L",
            "Control+Alt+Tab",
            "Control+Shift+A",
        ] {
            assert!(!is_system_reserved_on(g(text), Os::Windows), "{text}");
        }
    }

    /// The macOS cases of the C# table this replaced (HotkeyGestureTests), kept verbatim.
    #[test]
    fn macos_reserves_switching_spotlight_window_keys_and_the_ways_out() {
        for text in [
            "Meta+Tab",
            "Shift+Meta+Tab",
            "Meta+Oem3",
            "Shift+Meta+Oem3",
            "Meta+OemBackslash",
            "Shift+Meta+OemBackslash",
            "Meta+Space",
            "Meta+Q",
            "Meta+W",
            "Meta+H",
            "Meta+M",
            "Alt+Meta+Escape",
            "Control+Meta+Q",
        ] {
            assert!(is_system_reserved_on(g(text), Os::MacOS), "{text}");
            assert!(!is_system_reserved_on(g(text), Os::Windows), "{text}");
            assert!(!is_system_reserved_on(g(text), Os::Linux), "{text}");
        }
        for text in [
            "Shift+Meta+D3",
            "Shift+Meta+D4",
            "Shift+Meta+D5",
            "Shift+Meta+Q",
            "Alt+Tab",
            "Meta+A",
            "Control+Meta+W",
        ] {
            assert!(!is_system_reserved_on(g(text), Os::MacOS), "{text}");
        }
    }

    #[test]
    fn current_is_the_compile_target() {
        let expected = if cfg!(windows) {
            Os::Windows
        } else if cfg!(target_os = "macos") {
            Os::MacOS
        } else {
            Os::Linux
        };
        assert_eq!(Os::current(), expected);
    }
}
