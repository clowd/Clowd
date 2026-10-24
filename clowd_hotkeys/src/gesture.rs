use std::str::FromStr;

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

    /// What `Hotkeys::register` checks before touching the hook, in the same order.
    pub fn validate(self) -> Result<(), Error> {
        if self.key == Key::None {
            return Err(Error::NoKey);
        }
        if self.key.is_modifier() {
            return Err(Error::ModifierKey);
        }
        if !seen_on_this_os(self.key) {
            return Err(Error::UnsupportedKey(self.key));
        }
        Ok(())
    }
}

#[cfg(windows)]
fn seen_on_this_os(key: Key) -> bool {
    key.to_vk().is_some()
}

/// CapsLock only ever arrives as FlagsChanged, so it would register but never fire.
#[cfg(target_os = "macos")]
fn seen_on_this_os(key: Key) -> bool {
    key != Key::CapsLock && (key.to_mac().is_some() || key.to_nx().is_some())
}

/// No backend: `Hotkeys::start` already failed, so there is nothing to refuse here.
#[cfg(not(any(windows, target_os = "macos")))]
fn seen_on_this_os(_key: Key) -> bool {
    true
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

    #[cfg(windows)]
    #[test]
    fn validate_refuses_keys_windows_never_reports() {
        assert_eq!(Gesture::new(Key::PrintScreen, Modifiers::NONE).validate(), Ok(()));
        assert_eq!(Gesture::new(Key::MediaPlayPause, Modifiers::NONE).validate(), Ok(()));
        assert_eq!(
            Gesture::new(Key::LineFeed, Modifiers::NONE).validate(),
            Err(Error::UnsupportedKey(Key::LineFeed))
        );
        assert_eq!(
            Gesture::new(Key::MediaRecord, Modifiers::NONE).validate(),
            Err(Error::UnsupportedKey(Key::MediaRecord))
        );
        assert_eq!(
            Error::UnsupportedKey(Key::LineFeed).to_string(),
            "the key 'LineFeed' cannot be used as a global hotkey on this system"
        );
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn validate_refuses_keys_macos_never_reports() {
        assert_eq!(Gesture::new(Key::F13, Modifiers::NONE).validate(), Ok(()));
        assert_eq!(Gesture::new(Key::MediaPlayPause, Modifiers::NONE).validate(), Ok(()));
        assert_eq!(
            Gesture::new(Key::PrintScreen, Modifiers::NONE).validate(),
            Err(Error::UnsupportedKey(Key::PrintScreen))
        );
        assert_eq!(
            Gesture::new(Key::CapsLock, Modifiers::NONE).validate(),
            Err(Error::UnsupportedKey(Key::CapsLock))
        );
    }
}
