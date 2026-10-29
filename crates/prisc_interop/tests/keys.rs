//! The gesture and keyboard exports through the library's public surface (README section 4.9):
//! parse, format and check with every OS's table on this machine, the key-state mask and Secure
//! Event Input. Every export here is `Thread: any thread`.

use std::ptr;

use prisc_interop::ffi::error::prisc_last_error_message;
use prisc_interop::ffi::gesture::{
    prisc_gesture_check, prisc_gesture_format, prisc_gesture_parse, PriscGesture, PriscGestureOs as Os, PriscGestureStyle as Style,
};
use prisc_interop::ffi::keyboard::{prisc_keyboard_keys_down, prisc_keyboard_secure_input_enabled};
use prisc_interop::ffi::status::PriscInteropStatus as S;
use prisc_interop::ffi::string::prisc_string_free;
use prisc_interop::ffi::types::{PriscGestureRefusal as R, PriscString};

const OK: i32 = S::Ok as i32;
const INVALID_ARG: i32 = S::InvalidArg as i32;

// Avalonia KeyModifiers bits, the ABI's.
const ALT: u32 = 1;
const CONTROL: u32 = 2;
const SHIFT: u32 = 4;
const META: u32 = 8;

fn last_message() -> String {
    let len = unsafe { prisc_last_error_message(ptr::null_mut(), 0) };
    let mut buf = vec![0u8; len];
    assert_eq!(unsafe { prisc_last_error_message(buf.as_mut_ptr(), buf.len()) }, len);
    String::from_utf8(buf).unwrap()
}

fn key(name: &str) -> i32 {
    prisc_hotkeys::Key::from_name(name).unwrap_or_else(|| panic!("{name}")) as i32
}

fn parse(text: &str) -> Result<PriscGesture, String> {
    let mut out = PriscGesture::default();
    match unsafe { prisc_gesture_parse(text.as_ptr(), text.len(), &mut out) } {
        OK => Ok(out),
        INVALID_ARG => Err(last_message()),
        other => panic!("prisc_gesture_parse({text:?}) answered {other}"),
    }
}

fn format(gesture: PriscGesture, style: Style, os: Os) -> String {
    let mut text = PriscString {
        ptr: ptr::null_mut(),
        len: 0,
    };
    assert_eq!(
        unsafe { prisc_gesture_format(&gesture, style as i32, os as i32, &mut text) },
        OK,
        "{}",
        last_message()
    );
    let value = if text.len == 0 {
        String::new()
    } else {
        String::from_utf8(unsafe { std::slice::from_raw_parts(text.ptr, text.len) }.to_vec()).unwrap()
    };
    assert_eq!(unsafe { prisc_string_free(&mut text) }, OK);
    assert!(text.ptr.is_null() && text.len == 0, "the free zeroes the caller's copy");
    value
}

fn check(gesture: PriscGesture, os: Os) -> i32 {
    let mut refusal = -1;
    assert_eq!(
        unsafe { prisc_gesture_check(&gesture, os as i32, &mut refusal) },
        OK,
        "{}",
        last_message()
    );
    refusal
}

fn g(key_name: &str, modifiers: u32) -> PriscGesture {
    PriscGesture {
        key: key(key_name),
        modifiers,
    }
}

#[test]
fn parse_reads_the_one_grammar_and_reports_the_reason() {
    assert_eq!(parse("Ctrl+Shift+PrtScr"), Ok(g("PrintScreen", CONTROL | SHIFT)));
    assert_eq!(parse(" control + snapshot "), Ok(g("PrintScreen", CONTROL)));
    assert_eq!(parse("Cmd+Num+"), Ok(g("Add", META)));
    assert_eq!(parse("None"), Ok(g("None", 0)));
    assert_eq!(parse("Hyper+A"), Err("unknown modifier 'Hyper'".into()));
    assert_eq!(parse("Control+Shift"), Err("unknown key 'Shift'".into()));
    assert_eq!(parse(""), Err("empty gesture".into()));
    // the grammar folds ASCII case only: the Kelvin sign is no K
    assert!(parse("Ctrl+\u{212A}").is_err());

    let bad = [0xFFu8];
    let mut out = PriscGesture::default();
    assert_eq!(unsafe { prisc_gesture_parse(bad.as_ptr(), 1, &mut out) }, INVALID_ARG);
    assert!(last_message().starts_with("text is not UTF-8"));
    assert_eq!(unsafe { prisc_gesture_parse(ptr::null(), 0, ptr::null_mut()) }, INVALID_ARG);
    assert_eq!(last_message(), "out was null");
}

#[test]
fn format_writes_each_style_for_each_os() {
    let all = g("X", CONTROL | ALT | SHIFT | META);
    assert_eq!(format(all, Style::Serialized, Os::MacOS), "Control+Alt+Shift+Meta+X");
    assert_eq!(format(all, Style::Display, Os::Windows), "Ctrl+Alt+Shift+Win+X");
    assert_eq!(format(all, Style::Display, Os::MacOS), "Ctrl+Alt+Shift+Cmd+X");
    assert_eq!(format(all, Style::Display, Os::Linux), "Ctrl+Alt+Shift+Super+X");
    assert_eq!(format(all, Style::ModifiersPrefix, Os::Windows), "Ctrl+Alt+Shift+Win+");
    assert_eq!(format(g("None", 0), Style::ModifiersPrefix, Os::MacOS), "");
    assert_eq!(format(g("None", CONTROL), Style::Display, Os::MacOS), "");
    assert_eq!(format(g("None", 0), Style::Serialized, Os::MacOS), "None");
    assert_eq!(format(g("D1", META | SHIFT), Style::Serialized, Os::Current), "Shift+Meta+D1");
    let meta = if cfg!(windows) {
        "Win"
    } else if cfg!(target_os = "macos") {
        "Cmd"
    } else {
        "Super"
    };
    assert_eq!(format(g("D1", META), Style::Display, Os::Current), format!("{meta}+1"));

    let mut text = PriscString {
        ptr: ptr::null_mut(),
        len: 0,
    };
    let bad_key = PriscGesture {
        key: 100_004,
        modifiers: 0,
    };
    assert_eq!(
        unsafe { prisc_gesture_format(&bad_key, Style::Serialized as i32, 0, &mut text) },
        INVALID_ARG
    );
    assert_eq!(last_message(), "100004 is not a key of the hotkey key table");
    assert_eq!(
        unsafe { prisc_gesture_format(&g("A", 0x10), Style::Serialized as i32, 0, &mut text) },
        INVALID_ARG
    );
    assert_eq!(unsafe { prisc_gesture_format(&g("A", 0), 3, 0, &mut text) }, INVALID_ARG);
    assert_eq!(unsafe { prisc_gesture_format(&g("A", 0), 0, 4, &mut text) }, INVALID_ARG);
    assert_eq!(unsafe { prisc_gesture_format(ptr::null(), 0, 0, &mut text) }, INVALID_ARG);
    assert!(text.ptr.is_null(), "a failed format writes nothing to free");
}

#[test]
fn check_follows_registration_then_the_reserved_table_of_the_os_asked() {
    assert_eq!(check(g("Tab", ALT), Os::Windows), R::Reserved as i32);
    assert_eq!(check(g("Tab", ALT), Os::MacOS), R::None as i32);
    assert_eq!(check(g("Q", META), Os::MacOS), R::Reserved as i32);
    assert_eq!(check(g("Q", META), Os::Windows), R::None as i32);
    assert_eq!(check(g("Q", META), Os::Linux), R::None as i32);
    assert_eq!(check(g("A", CONTROL), Os::Current), R::None as i32);

    assert_eq!(check(g("None", CONTROL), Os::Windows), R::NoKey as i32);
    assert_eq!(
        check(
            PriscGesture {
                key: -1,
                modifiers: 0
            },
            Os::Windows
        ),
        R::InvalidKey as i32
    );
    assert_eq!(check(g("A", 0x10), Os::Windows), R::InvalidModifiers as i32);
    assert_eq!(check(g("LeftShift", 0), Os::Windows), R::ModifierOnly as i32);
    // PrintScreen and CapsLock have no usable code on macOS; Windows reports both
    assert_eq!(check(g("PrintScreen", 0), Os::MacOS), R::UnsupportedKey as i32);
    assert_eq!(check(g("CapsLock", 0), Os::MacOS), R::UnsupportedKey as i32);
    assert_eq!(check(g("PrintScreen", 0), Os::Windows), R::None as i32);
    assert_eq!(check(g("LineFeed", 0), Os::Windows), R::UnsupportedKey as i32);

    let mut refusal = 0;
    assert_eq!(unsafe { prisc_gesture_check(&g("A", 0), 9, &mut refusal) }, INVALID_ARG);
    assert_eq!(last_message(), "9 is not a PriscGestureOs value");
    assert_eq!(unsafe { prisc_gesture_check(&g("A", 0), 0, ptr::null_mut()) }, INVALID_ARG);
}

#[test]
fn keys_down_masks_the_codes_asked_and_caps_them_at_64() {
    let mut mask = u64::MAX;
    assert_eq!(unsafe { prisc_keyboard_keys_down(ptr::null(), 0, &mut mask) }, OK);
    assert_eq!(mask, 0);

    // Codes outside every OS's numbering cannot be answered, so they read as down.
    let unknown = [0x1_0000u32, 0x2_0000];
    assert_eq!(unsafe { prisc_keyboard_keys_down(unknown.as_ptr(), 2, &mut mask) }, OK);
    assert_eq!(mask, 0b11);

    // A real key answers on Windows and macOS; nobody holds Escape during a test run.
    let escape: u32 = if cfg!(windows) { 0x1B } else { 0x35 };
    assert_eq!(unsafe { prisc_keyboard_keys_down(&escape, 1, &mut mask) }, OK);
    if cfg!(any(windows, target_os = "macos")) {
        assert_eq!(mask, 0, "Escape reads as held");
    } else {
        assert_eq!(mask, 1, "no backend: unknown reads as down");
    }

    let many = [0u32; 65];
    assert_eq!(unsafe { prisc_keyboard_keys_down(many.as_ptr(), 65, &mut mask) }, INVALID_ARG);
    assert_eq!(last_message(), "65 codes asked at once; at most 64");
    assert_eq!(unsafe { prisc_keyboard_keys_down(ptr::null(), 1, &mut mask) }, INVALID_ARG);
    assert_eq!(unsafe { prisc_keyboard_keys_down(many.as_ptr(), 1, ptr::null_mut()) }, INVALID_ARG);
}

#[test]
fn secure_input_answers_on_every_os() {
    let mut enabled = 0xAAu8;
    assert_eq!(unsafe { prisc_keyboard_secure_input_enabled(&mut enabled) }, OK);
    assert!(enabled <= 1);
    assert_eq!(enabled != 0, prisc_platform::input::secure_input_enabled());
    if !cfg!(target_os = "macos") {
        assert_eq!(enabled, 0);
    }
    assert_eq!(unsafe { prisc_keyboard_secure_input_enabled(ptr::null_mut()) }, INVALID_ARG);
}
