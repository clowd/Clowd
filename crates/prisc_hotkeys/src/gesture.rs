use std::str::FromStr;

use crate::reserved::Os;
use crate::{Error, Key, Modifiers};

/// A key plus the modifiers held with it. Its text form (`Display` / `FromStr`) is the one wire
/// format for gestures: settings, the capturer's `--hk-*` flags and the status echo.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash)]
pub struct Gesture {
    pub key: Key,
    pub modifiers: Modifiers,
}

impl Gesture {
    pub const fn new(key: Key, modifiers: Modifiers) -> Self {
        Self {
            key,
            modifiers,
        }
    }

    /// What `Hotkeys::register` checks before touching the hook, in the same order:
    /// [`validate_on`](Self::validate_on) for the OS this process runs on.
    pub fn validate(self) -> Result<(), Error> {
        self.validate_on(Os::current())
    }

    /// The registration checks as `os` would make them: a key at all, not a modifier on its own,
    /// and a key `os`'s hook can report.
    pub fn validate_on(self, os: Os) -> Result<(), Error> {
        if self.key == Key::None {
            return Err(Error::NoKey);
        }
        if self.key.is_modifier() {
            return Err(Error::ModifierKey);
        }
        if !seen_on(self.key, os) {
            return Err(Error::UnsupportedKey(self.key));
        }
        Ok(())
    }

    /// The text a person reads: the modifiers in `os`'s words ([`Modifiers::display_prefix`]),
    /// then the key's short label ("Ctrl+Shift+PrtScr", "Cmd+1"). Empty for a gesture with no key.
    /// It parses back, but settings store the `Display` form.
    pub fn display(self, os: Os) -> String {
        if self.key == Key::None {
            return String::new();
        }
        self.modifiers.display_prefix(os) + self.key.label()
    }
}

/// Whether `os`'s hook can ever report `key`.
fn seen_on(key: Key, os: Os) -> bool {
    match os {
        Os::Windows => key.to_vk().is_some(),
        // CapsLock only ever arrives as FlagsChanged, so it would register but never fire.
        Os::MacOS => key != Key::CapsLock && (key.to_mac().is_some() || key.to_nx().is_some()),
        // No backend: `Hotkeys::start` already failed, so there is nothing to refuse here.
        Os::Linux => true,
    }
}

/// The serialized form: the modifiers present in the fixed order `Control`, `Alt`, `Shift`,
/// `Meta`, then the key's canonical name, joined with `+` (`Control+Shift+D1`).
impl std::fmt::Display for Gesture {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        const ORDER: [(Modifiers, &str); 4] = [
            (Modifiers::CONTROL, "Control"),
            (Modifiers::ALT, "Alt"),
            (Modifiers::SHIFT, "Shift"),
            (Modifiers::META, "Meta"),
        ];
        for (modifier, name) in ORDER {
            if self.modifiers.contains(modifier) {
                write!(f, "{name}+")?;
            }
        }
        f.write_str(self.key.name())
    }
}

/// Why a gesture string did not parse.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ParseGestureError(String);

impl std::fmt::Display for ParseGestureError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for ParseGestureError {}

/// Tolerant: parts are trimmed and empty ones dropped, names are ASCII case-insensitive, key
/// aliases and labels are accepted (`control+snapshot`, `Ctrl+PrtScr`), modifier shorthands too
/// (`Ctrl`, `Cmd`, `Win`, `Option`, `Super`), and modifier order and repeats do not matter. A
/// trailing '+' after `Num` is the keypad plus (`Ctrl+Num+`). `"None"` parses to a gesture with
/// no key; `validate` decides what that means.
impl FromStr for Gesture {
    type Err = ParseGestureError;

    fn from_str(s: &str) -> Result<Self, Self::Err> {
        let mut parts: Vec<&str> = s
            .split('+')
            .map(str::trim)
            .filter(|part| !part.is_empty())
            .collect();
        let Some(key_part) = parts.pop() else {
            return Err(ParseGestureError("empty gesture".into()));
        };
        let key = Key::from_name(key_part).or_else(|| {
            s.trim_end()
                .ends_with('+')
                .then(|| Key::from_name(&format!("{key_part}+")))
                .flatten()
        });
        let Some(key) = key else {
            return Err(ParseGestureError(format!("unknown key '{key_part}'")));
        };
        let mut modifiers = Modifiers::NONE;
        for part in parts {
            let Some(modifier) = Modifiers::from_name(part) else {
                return Err(ParseGestureError(format!("unknown modifier '{part}'")));
            };
            modifiers |= modifier;
        }
        Ok(Gesture::new(key, modifiers))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_then_display_vectors() {
        for (input, expected) in [
            ("PrintScreen", "PrintScreen"),
            ("Snapshot", "PrintScreen"),
            ("control+snapshot", "Control+PrintScreen"),
            ("Shift+Control+A", "Control+Shift+A"),
            (" Control + Shift + D1 ", "Control+Shift+D1"),
            ("Meta+Alt+OemTilde", "Alt+Meta+Oem3"),
            ("Enter", "Return"),
            ("A+", "A"),
            ("None", "None"),
            ("Control+Control+A", "Control+A"),
            ("Ctrl+PrtScr", "Control+PrintScreen"),
            ("ctrl+shift+1", "Control+Shift+D1"),
            ("Cmd+Shift+3", "Shift+Meta+D3"),
            ("Win+Esc", "Meta+Escape"),
            ("Super+A", "Meta+A"),
            ("Option+Del", "Alt+Delete"),
            ("Ctrl+Num+", "Control+Add"),
            ("Num+", "Add"),
            ("Ctrl+Num-", "Control+Subtract"),
            ("Ctrl+=", "Control+OemPlus"),
            ("Alt+Num7", "Alt+NumPad7"),
        ] {
            let gesture: Gesture = input
                .parse()
                .unwrap_or_else(|e| panic!("{input:?}: {e}"));
            assert_eq!(gesture.to_string(), expected, "{input:?}");
        }
    }

    #[test]
    fn parse_failures() {
        for input in [
            "",
            " ",
            "+",
            "NotAKey",
            "Control+NotAKey",
            "Hyper+A",
            "Ctrl+",
            "A+B",
            "30",
            "A,B",
            "Control+Shift",
        ] {
            assert!(input.parse::<Gesture>().is_err(), "{input:?} parsed");
        }
        assert_eq!(
            "".parse::<Gesture>()
                .unwrap_err()
                .to_string(),
            "empty gesture"
        );
        assert_eq!(
            "+ +"
                .parse::<Gesture>()
                .unwrap_err()
                .to_string(),
            "empty gesture"
        );
        assert_eq!(
            "Control+Shift"
                .parse::<Gesture>()
                .unwrap_err()
                .to_string(),
            "unknown key 'Shift'"
        );
        assert_eq!(
            "Hyper+A"
                .parse::<Gesture>()
                .unwrap_err()
                .to_string(),
            "unknown modifier 'Hyper'"
        );
    }

    #[test]
    fn every_label_parses_back_with_shorthand_modifiers() {
        for &key in Key::ALL {
            let modifiers = Modifiers::CONTROL | Modifiers::SHIFT;
            let gesture: Gesture = format!("Ctrl+Shift+{}", key.label())
                .parse()
                .unwrap_or_else(|e| panic!("{key:?}: {e}"));
            assert_eq!(gesture, Gesture::new(key, modifiers));
        }
    }

    #[test]
    fn none_parses_to_no_key_and_modifier_order_is_fixed() {
        assert_eq!("None".parse::<Gesture>(), Ok(Gesture::new(Key::None, Modifiers::NONE)));
        assert_eq!("None+A".parse::<Gesture>(), Ok(Gesture::new(Key::A, Modifiers::NONE)));
        let all = Gesture::new(Key::F1, Modifiers::META | Modifiers::SHIFT | Modifiers::ALT | Modifiers::CONTROL);
        assert_eq!(all.to_string(), "Control+Alt+Shift+Meta+F1");
        assert_eq!(all.to_string().parse::<Gesture>(), Ok(all));
    }

    #[test]
    fn validate_order() {
        assert_eq!(Gesture::new(Key::None, Modifiers::CONTROL).validate(), Err(Error::NoKey));
        assert_eq!(Gesture::new(Key::LeftShift, Modifiers::NONE).validate(), Err(Error::ModifierKey));
        assert_eq!(Gesture::new(Key::RWin, Modifiers::META).validate(), Err(Error::ModifierKey));
        assert_eq!(Gesture::new(Key::A, Modifiers::CONTROL).validate(), Ok(()));
    }

    /// Every OS's checks run on every machine (`validate_on`), so a Windows refusal is tested on a
    /// Mac too.
    #[test]
    fn validate_refuses_keys_windows_never_reports() {
        let on = |key| Gesture::new(key, Modifiers::NONE).validate_on(Os::Windows);
        assert_eq!(on(Key::PrintScreen), Ok(()));
        assert_eq!(on(Key::MediaPlayPause), Ok(()));
        assert_eq!(on(Key::CapsLock), Ok(()));
        assert_eq!(on(Key::LineFeed), Err(Error::UnsupportedKey(Key::LineFeed)));
        assert_eq!(on(Key::MediaRecord), Err(Error::UnsupportedKey(Key::MediaRecord)));
        assert_eq!(
            Error::UnsupportedKey(Key::LineFeed).to_string(),
            "the key 'LineFeed' cannot be used as a global hotkey on this system"
        );
    }

    #[test]
    fn validate_refuses_keys_macos_never_reports() {
        let on = |key| Gesture::new(key, Modifiers::NONE).validate_on(Os::MacOS);
        assert_eq!(on(Key::F13), Ok(()));
        assert_eq!(on(Key::MediaPlayPause), Ok(()));
        assert_eq!(on(Key::PrintScreen), Err(Error::UnsupportedKey(Key::PrintScreen)));
        assert_eq!(on(Key::CapsLock), Err(Error::UnsupportedKey(Key::CapsLock)));
    }

    #[test]
    fn linux_refuses_no_key_and_validate_is_the_current_os() {
        assert_eq!(Gesture::new(Key::MediaRecord, Modifiers::NONE).validate_on(Os::Linux), Ok(()));
        assert_eq!(Gesture::new(Key::None, Modifiers::NONE).validate_on(Os::Linux), Err(Error::NoKey));
        for key in [Key::PrintScreen, Key::CapsLock, Key::LineFeed, Key::F13] {
            let gesture = Gesture::new(key, Modifiers::NONE);
            assert_eq!(gesture.validate(), gesture.validate_on(Os::current()), "{key:?}");
        }
    }

    /// The shell's macOS defaults (SettingsHotkey), in the text it sends them in: region on
    /// Cmd+Shift+2 and F13, the active screen on Cmd+Shift+1.
    #[test]
    fn validate_accepts_the_shells_macos_defaults() {
        for (text, key) in [("Shift+Meta+D2", Key::D2), ("F13", Key::F13), ("Shift+Meta+D1", Key::D1)] {
            let gesture: Gesture = text.parse().unwrap();
            assert_eq!(gesture.key, key, "{text}");
            assert_eq!(gesture.to_string(), text);
            assert_eq!(gesture.validate_on(Os::MacOS), Ok(()), "{text}");
        }
    }

    /// The display vectors of the C# HotkeyGestureTests this replaced.
    #[test]
    fn display_uses_labels_and_the_os_word_for_meta() {
        let all = Modifiers::CONTROL | Modifiers::ALT | Modifiers::SHIFT | Modifiers::META;
        for (key, modifiers, expected) in [
            (Key::PrintScreen, Modifiers::NONE, "PrtScr"),
            (Key::PrintScreen, Modifiers::CONTROL | Modifiers::SHIFT, "Ctrl+Shift+PrtScr"),
            (Key::Delete, Modifiers::CONTROL, "Ctrl+Del"),
            (Key::Escape, Modifiers::NONE, "Esc"),
            (Key::D1, Modifiers::CONTROL | Modifiers::SHIFT, "Ctrl+Shift+1"),
            (Key::D0, Modifiers::NONE, "0"),
            (Key::F4, Modifiers::ALT | Modifiers::CONTROL, "Ctrl+Alt+F4"),
            (Key::OemPlus, Modifiers::ALT, "Alt+="),
            (Key::PageDown, Modifiers::SHIFT, "Shift+PgDn"),
            (Key::NumPad7, Modifiers::NONE, "Num7"),
            (Key::Add, Modifiers::CONTROL, "Ctrl+Num+"),
            (Key::MediaPlayPause, Modifiers::NONE, "Play/Pause"),
        ] {
            for os in [Os::Windows, Os::MacOS, Os::Linux] {
                assert_eq!(Gesture::new(key, modifiers).display(os), expected, "{key:?} {os:?}");
            }
        }
        for (os, meta) in [(Os::Windows, "Win"), (Os::MacOS, "Cmd"), (Os::Linux, "Super")] {
            assert_eq!(Gesture::new(Key::X, all).display(os), format!("Ctrl+Alt+Shift+{meta}+X"));
        }
        assert_eq!(Gesture::new(Key::None, Modifiers::CONTROL).display(Os::Windows), "");
    }

    #[test]
    fn display_parses_back_for_every_key() {
        let all = Modifiers::CONTROL | Modifiers::ALT | Modifiers::SHIFT | Modifiers::META;
        for &key in Key::ALL.iter().filter(|&&k| k != Key::None) {
            for os in [Os::Windows, Os::MacOS, Os::Linux] {
                let gesture = Gesture::new(key, all);
                assert_eq!(gesture.display(os).parse::<Gesture>(), Ok(gesture), "{key:?} {os:?}");
            }
        }
    }
}
