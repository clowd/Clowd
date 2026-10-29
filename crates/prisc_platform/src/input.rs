//! Physical input state, read the same way by every crate.

/// Whether the key or mouse button `raw_code` is physically down right now.
///
/// `raw_code` is the OS's own numbering, the one prisc_hotkeys passes through as `raw_code`:
/// a Windows virtual-key code (1..=254, the mouse-button VKs 1, 2, 4, 5 and 6 included), read
/// with `GetAsyncKeyState`'s high bit; or a macOS CGKeyCode (0..=0xFFFF), read with
/// `CGEventSourceKeyState(CombinedSessionState, code)`. `None` for a code outside that range and
/// on an OS without a backend (Linux). Never blocks; any thread.
pub fn is_key_down(raw_code: u32) -> Option<bool> {
    imp::is_key_down(raw_code)
}

/// Whether macOS Secure Event Input is on (a focused password field, a terminal's "Secure
/// Keyboard Entry"): while it is, the OS hides every key from event taps. `false` off macOS,
/// which is the truthful answer there. Any thread.
pub fn secure_input_enabled() -> bool {
    imp::secure_input_enabled()
}

#[cfg(windows)]
mod imp {
    use windows::Win32::UI::Input::KeyboardAndMouse::GetAsyncKeyState;

    pub(super) fn is_key_down(raw_code: u32) -> Option<bool> {
        if !(1..=254).contains(&raw_code) {
            return None;
        }
        // The high bit is the current physical state; the low bit ("pressed since the last
        // call") is shared process-wide and racy, so it is ignored.
        Some(unsafe { GetAsyncKeyState(raw_code as i32) } as u16 & 0x8000 != 0)
    }

    pub(super) fn secure_input_enabled() -> bool {
        false
    }
}

#[cfg(target_os = "macos")]
mod imp {
    use objc2_core_graphics::{CGEventSource, CGEventSourceStateID};

    #[link(name = "Carbon", kind = "framework")]
    extern "C" {
        // Carbon's Boolean is an unsigned char.
        fn IsSecureEventInputEnabled() -> u8;
    }

    pub(super) fn is_key_down(raw_code: u32) -> Option<bool> {
        let code = u16::try_from(raw_code).ok()?;
        Some(CGEventSource::key_state(CGEventSourceStateID::CombinedSessionState, code))
    }

    pub(super) fn secure_input_enabled() -> bool {
        unsafe { IsSecureEventInputEnabled() != 0 }
    }
}

#[cfg(not(any(windows, target_os = "macos")))]
mod imp {
    pub(super) fn is_key_down(_raw_code: u32) -> Option<bool> {
        None
    }

    pub(super) fn secure_input_enabled() -> bool {
        false
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn out_of_range_codes_have_no_answer() {
        assert_eq!(is_key_down(0x1_0000), None);
        if cfg!(windows) {
            assert_eq!(is_key_down(0), None);
            assert_eq!(is_key_down(255), None);
        }
    }

    #[test]
    fn in_range_codes_answer_on_supported_oses() {
        // Escape: VK 0x1B / kVK_Escape 0x35. Nobody holds it during a test run, but the answer
        // itself is what is checked: Some on Windows and macOS, None elsewhere.
        let code = if cfg!(windows) { 0x1B } else { 0x35 };
        let answer = is_key_down(code);
        if cfg!(any(windows, target_os = "macos")) {
            assert!(answer.is_some());
        } else {
            assert_eq!(answer, None);
        }
    }

    #[test]
    fn secure_input_is_off_where_it_does_not_exist() {
        let on = secure_input_enabled();
        if !cfg!(target_os = "macos") {
            assert!(!on);
        }
    }
}
