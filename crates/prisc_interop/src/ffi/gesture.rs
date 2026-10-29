//! Gesture parse, format and check (WP-KEYS; README section 4.9). The grammar, the display text
//! and the reserved tables are prisc_hotkeys': C# keeps no gesture parser of its own.

use prisc_hotkeys::reserved::{self, Os};
use prisc_hotkeys::{Gesture, Key, Modifiers};

use crate::error::{Error, Result};
use crate::ffi::status::PRISC_OK;
use crate::ffi::types::{PriscGestureRefusal, PriscString};
use crate::{panic, util};

/// A gesture: a `Key` value and `Modifiers` bits (8 bytes).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PriscGesture {
    pub key: i32,
    pub modifiers: u32,
}

/// Which text `prisc_gesture_format` writes.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscGestureStyle {
    /// The settings form (`Control+Alt+Shift+Meta+<Key>`), OS-independent.
    Serialized = 0,
    /// Labels plus the OS word for Meta.
    Display = 1,
    /// The modifier part only (`Ctrl+Alt+`).
    ModifiersPrefix = 2,
}

crate::convert::ffi_enum!(PriscGestureStyle: i32 { Serialized, Display, ModifiersPrefix });

/// The OS whose conventions apply.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscGestureOs {
    Current = 0,
    Windows = 1,
    MacOS = 2,
    Linux = 3,
}

crate::convert::ffi_enum!(PriscGestureOs: i32 { Current, Windows, MacOS, Linux });

impl PriscGestureOs {
    fn os(self) -> Os {
        match self {
            PriscGestureOs::Current => Os::current(),
            PriscGestureOs::Windows => Os::Windows,
            PriscGestureOs::MacOS => Os::MacOS,
            PriscGestureOs::Linux => Os::Linux,
        }
    }
}

/// The gesture raw C values name, or the refusal (and its text) for values that name none: a key
/// outside the key table, `Key::None`, or an unknown modifier bit. Registration and
/// `prisc_gesture_check` both read raw gestures through it (`hotkeys_host` imports it), so the
/// two refuse alike.
pub(crate) fn gesture_from_raw(key: i32, modifiers: u32) -> std::result::Result<Gesture, (PriscGestureRefusal, String)> {
    let Some(key) = Key::from_i32(key) else {
        return Err((
            PriscGestureRefusal::InvalidKey,
            format!("{key} is not a key of the hotkey key table"),
        ));
    };
    if key == Key::None {
        return Err((PriscGestureRefusal::NoKey, prisc_hotkeys::Error::NoKey.to_string()));
    }
    let Some(modifiers) = Modifiers::from_bits(modifiers) else {
        return Err((
            PriscGestureRefusal::InvalidModifiers,
            format!("the modifiers 0x{modifiers:X} carry an unknown bit"),
        ));
    };
    Ok(Gesture::new(key, modifiers))
}

/// The refusal a hotkey error is about a gesture, or None for one about the hook (it cannot start,
/// it stopped): the one table from prisc_hotkeys' validation errors onto `PriscGestureRefusal`.
pub(crate) fn refusal_of(error: &prisc_hotkeys::Error) -> Option<PriscGestureRefusal> {
    match error {
        prisc_hotkeys::Error::NoKey => Some(PriscGestureRefusal::NoKey),
        prisc_hotkeys::Error::ModifierKey => Some(PriscGestureRefusal::ModifierOnly),
        prisc_hotkeys::Error::UnsupportedKey(_) => Some(PriscGestureRefusal::UnsupportedKey),
        prisc_hotkeys::Error::Duplicate => Some(PriscGestureRefusal::Duplicate),
        prisc_hotkeys::Error::Unsupported
        | prisc_hotkeys::Error::PermissionDenied
        | prisc_hotkeys::Error::HookFailed(_)
        | prisc_hotkeys::Error::Stopped => None,
    }
}

/// What `prisc_gesture_check` answers: the first refusal in registration's order, then `os`'s
/// reserved table.
fn check(key: i32, modifiers: u32, os: Os) -> PriscGestureRefusal {
    let gesture = match gesture_from_raw(key, modifiers) {
        Ok(gesture) => gesture,
        Err((refusal, _)) => return refusal,
    };
    if let Err(error) = gesture.validate_on(os) {
        // validate_on only ever refuses the gesture itself
        return refusal_of(&error).unwrap_or(PriscGestureRefusal::InvalidKey);
    }
    if reserved::is_system_reserved_on(gesture, os) {
        return PriscGestureRefusal::Reserved;
    }
    PriscGestureRefusal::None
}

/// The text `prisc_gesture_format` writes; `PRISC_E_INVALID_ARG` for a key outside the key table
/// or an unknown modifier bit (`Key::None` is a key here: "None" serialized, empty displayed).
fn format(gesture: &PriscGesture, style: PriscGestureStyle, os: Os) -> Result<String> {
    let key =
        Key::from_i32(gesture.key).ok_or_else(|| Error::invalid_arg(format!("{} is not a key of the hotkey key table", gesture.key)))?;
    let modifiers = Modifiers::from_bits(gesture.modifiers)
        .ok_or_else(|| Error::invalid_arg(format!("the modifiers 0x{:X} carry an unknown bit", gesture.modifiers)))?;
    let gesture = Gesture::new(key, modifiers);
    Ok(match style {
        PriscGestureStyle::Serialized => gesture.to_string(),
        PriscGestureStyle::Display => gesture.display(os),
        PriscGestureStyle::ModifiersPrefix => modifiers.display_prefix(os),
    })
}

/// Parses UTF-8 gesture text with the one gesture grammar (`Gesture::from_str`): '+'-separated
/// parts, trimmed, empty parts dropped; the last is a key name, alias or label ("PrintScreen",
/// "Snapshot", "PrtScr"), every earlier one a modifier name or shorthand ("Control", "Ctrl",
/// "Cmd"), all ASCII case-insensitive; a trailing '+' after "Num" is the keypad plus. Both the
/// serialized and the display text parse back. "None" is a gesture with no key (`key` 0).
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for text that is no gesture (the reason, such as
/// "unknown key 'X'", is the last error), text that is not UTF-8, or a null pointer.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gesture_parse(text: *const u8, len: usize, out: *mut PriscGesture) -> i32 {
    panic::guard("prisc_gesture_parse", || {
        let out = util::out(out, "out")?;
        let text = util::utf8(text, len, "text")?;
        let gesture: Gesture = text
            .parse()
            .map_err(|e: prisc_hotkeys::ParseGestureError| Error::invalid_arg(e.to_string()))?;
        *out = PriscGesture {
            key: gesture.key as i32,
            modifiers: u32::from(gesture.modifiers.bits()),
        };
        Ok(PRISC_OK)
    })
}

/// Formats a gesture as UTF-8 text (mode 1 string, freed with `prisc_string_free`):
/// - `Serialized`: the settings form, OS-independent ("Control+Shift+PrintScreen"; the modifiers
///   in the order Control, Alt, Shift, Meta, then the key's canonical name; "None" for no key).
/// - `Display`: what a person reads, with the key's short label and `os`'s word for Meta
///   ("Ctrl+Shift+PrtScr", "Cmd+1"); empty for no key.
/// - `ModifiersPrefix`: the modifiers alone as `Display` writes them, each followed by '+'
///   ("Ctrl+Alt+"); empty for none. The key is not written.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null pointer, an unknown style or OS, a key
/// outside the key table or an unknown modifier bit.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gesture_format(gesture: *const PriscGesture, style: i32, os: i32, out_text: *mut PriscString) -> i32 {
    panic::guard("prisc_gesture_format", || {
        let out_text = util::out(out_text, "out_text")?;
        let gesture = util::input(gesture, "gesture")?;
        let style = PriscGestureStyle::from_raw(style)?;
        let os = PriscGestureOs::from_raw(os)?.os();
        *out_text = PriscString::from_string(format(gesture, style, os)?);
        Ok(PRISC_OK)
    })
}

/// Checks a gesture the way registration does on `os`, then against `os`'s reserved table, and
/// writes the first `PriscGestureRefusal` (`None` when the gesture is acceptable): `InvalidKey`,
/// `NoKey`, `InvalidModifiers`, `ModifierOnly`, `UnsupportedKey` (no key code on `os`), then
/// `Reserved` (Alt+Tab on Windows, Cmd+Q on macOS, ...). Never `Duplicate`, which only
/// registration knows. A gesture's own values are answered as refusals, never as a failure.
///
/// Status: `PRISC_OK`; `PRISC_E_INVALID_ARG` for a null pointer or an unknown OS.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gesture_check(gesture: *const PriscGesture, os: i32, out_refusal: *mut i32) -> i32 {
    panic::guard("prisc_gesture_check", || {
        let out_refusal = util::out(out_refusal, "out_refusal")?;
        let gesture = util::input(gesture, "gesture")?;
        let os = PriscGestureOs::from_raw(os)?.os();
        *out_refusal = check(gesture.key, gesture.modifiers, os) as i32;
        Ok(PRISC_OK)
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::{offset_of, size_of};

    #[test]
    fn layouts_are_pinned() {
        assert_eq!(size_of::<PriscGesture>(), 8);
        assert_eq!(offset_of!(PriscGesture, key), 0);
        assert_eq!(offset_of!(PriscGesture, modifiers), 4);
    }
}
