//! Gesture parse, format and check (WP-KEYS; README section 4.9). The grammar, the display text
//! and the reserved tables are prisc_hotkeys'.

use crate::error::Error;
use crate::ffi::types::PriscString;
use crate::panic;

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

/// Parses UTF-8 gesture text (ASCII case-insensitive); a parse error is `PRISC_E_INVALID_ARG` with
/// the reason as the last error.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gesture_parse(text: *const u8, len: usize, out: *mut PriscGesture) -> i32 {
    panic::guard("prisc_gesture_parse", || {
        let _ = (text, len, out);
        Err(Error::unsupported("prisc_gesture_parse is not implemented yet"))
    })
}

/// Formats a gesture in a `PriscGestureStyle` for a `PriscGestureOs` (mode 1 string).
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gesture_format(gesture: *const PriscGesture, style: i32, os: i32, out_text: *mut PriscString) -> i32 {
    panic::guard("prisc_gesture_format", || {
        let _ = (gesture, style, os, out_text);
        Err(Error::unsupported("prisc_gesture_format is not implemented yet"))
    })
}

/// Checks a gesture the way registration does, then against `os`'s reserved table, writing a
/// `PriscGestureRefusal`.
///
/// Status: `PRISC_E_UNSUPPORTED` until the area work package implements it; then `PRISC_OK`,
/// `PRISC_E_INVALID_ARG`.
///
/// Thread: any thread.
#[no_mangle]
pub unsafe extern "C" fn prisc_gesture_check(gesture: *const PriscGesture, os: i32, out_refusal: *mut i32) -> i32 {
    panic::guard("prisc_gesture_check", || {
        let _ = (gesture, os, out_refusal);
        Err(Error::unsupported("prisc_gesture_check is not implemented yet"))
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
