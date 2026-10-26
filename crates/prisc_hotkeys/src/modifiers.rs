use std::ops::{BitOr, BitOrAssign};

/// Side-agnostic modifiers, with Avalonia `KeyModifiers` bit values (also the C ABI's).
#[derive(Copy, Clone, Debug, Default, PartialEq, Eq, Hash)]
pub struct Modifiers(u8);

impl Modifiers {
    pub const NONE: Self = Self(0);
    pub const ALT: Self = Self(1);
    pub const CONTROL: Self = Self(2);
    pub const SHIFT: Self = Self(4);
    /// The Win key on Windows, Command on macOS.
    pub const META: Self = Self(8);

    pub const fn bits(self) -> u8 {
        self.0
    }

    /// `None` if any bit above 0xF is set.
    pub const fn from_bits(bits: u32) -> Option<Self> {
        if bits & !0xF == 0 {
            Some(Self(bits as u8))
        } else {
            None
        }
    }

    pub(crate) const fn contains(self, other: Self) -> bool {
        self.0 & other.0 == other.0
    }

    /// Every name a gesture's modifier part may use (ASCII case-insensitive): the canonical names
    /// `Gesture` prints first, then the shorthands people write. `None` adds nothing.
    pub(crate) const NAMES: &'static [(&'static str, Modifiers)] = &[
        ("None", Modifiers::NONE),
        ("Control", Modifiers::CONTROL),
        ("Alt", Modifiers::ALT),
        ("Shift", Modifiers::SHIFT),
        ("Meta", Modifiers::META),
        ("Ctrl", Modifiers::CONTROL),
        ("Option", Modifiers::ALT),
        ("Opt", Modifiers::ALT),
        ("Win", Modifiers::META),
        ("Windows", Modifiers::META),
        ("Cmd", Modifiers::META),
        ("Command", Modifiers::META),
        ("Super", Modifiers::META),
    ];

    /// The modifier with this name or shorthand (see `NAMES`).
    pub(crate) fn from_name(name: &str) -> Option<Modifiers> {
        let name = name.trim();
        Self::NAMES
            .iter()
            .find(|(candidate, _)| candidate.eq_ignore_ascii_case(name))
            .map(|&(_, modifier)| modifier)
    }
}

impl BitOr for Modifiers {
    type Output = Self;

    fn bitor(self, rhs: Self) -> Self {
        Self(self.0 | rhs.0)
    }
}

impl BitOrAssign for Modifiers {
    fn bitor_assign(&mut self, rhs: Self) {
        self.0 |= rhs.0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bits_round_trip_and_reject_high_bits() {
        assert_eq!(
            Modifiers::from_bits(0xF),
            Some(Modifiers::ALT | Modifiers::CONTROL | Modifiers::SHIFT | Modifiers::META)
        );
        assert_eq!(Modifiers::from_bits(0), Some(Modifiers::NONE));
        assert_eq!(Modifiers::from_bits(0x10), None);
        assert_eq!(Modifiers::from_bits(u32::MAX), None);
        let mut m = Modifiers::CONTROL;
        m |= Modifiers::SHIFT;
        assert_eq!(m.bits(), 6);
        assert!(m.contains(Modifiers::SHIFT));
        assert!(!m.contains(Modifiers::ALT));
    }
}
