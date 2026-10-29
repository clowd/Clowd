//! The C# mirror of prisc_hotkeys' key table (`ui/Prisc.Shared/Hotkeys/HotkeyKey.g.cs`, public, in
//! Prisc.Shared), moved here from prisc_hotkeys' `src/csharp.rs`, and the keystroke overlay's keycap
//! legends (`ui/Prisc.VideoSDK/Composition/KeycapLegends.g.cs`). The mirror carries the enums and the
//! canonical names only: gesture text is parsed and printed by prisc_hotkeys through the
//! `prisc_gesture_*` exports, so C# has no parser, alias table or label table of its own. The hook's
//! event and status shapes cross the C ABI as prisc_interop's `prisc_hotkeys_*` types (abi.rs).

use std::fmt::Write as _;
use std::path::Path;

use prisc_hotkeys::reserved::Os;
use prisc_hotkeys::{Key, Modifiers};

use crate::{cs, Output};

const SOURCE: &str = "the key table in crates/prisc_hotkeys/src/key.rs";

/// The key table mirror (Prisc.Shared) and the keystroke overlay's keycap legends (Prisc.VideoSDK,
/// which does not reference Prisc.Shared).
pub static OUTPUTS: &[Output] = &[
    Output {
        name: "hotkey-keys",
        path: "ui/Prisc.Shared/Hotkeys/HotkeyKey.g.cs",
        source: SOURCE,
        render: render_keys,
    },
    Output {
        name: "keycap-legends",
        path: "ui/Prisc.VideoSDK/Composition/KeycapLegends.g.cs",
        source: SOURCE,
        render: render_keycaps,
    },
];

/// `HotkeyModifiers` from the canonical entries of `Modifiers::NAMES` (the first name of each
/// value), by value.
fn modifiers_enum() -> cs::EnumDecl {
    let mut canonical: Vec<(&str, Modifiers)> = Vec::new();
    for &(name, modifier) in Modifiers::NAMES {
        if !canonical.iter().any(|(_, m)| *m == modifier) {
            canonical.push((name, modifier));
        }
    }
    canonical.sort_by_key(|(_, m)| m.bits());
    cs::EnumDecl {
        name: "HotkeyModifiers".into(),
        underlying: None,
        flags: true,
        access: "public",
        doc: cs::Doc::Lines(vec![
            "<summary>Global hotkey modifiers (Avalonia KeyModifiers values). Meta = Win on Windows, Command on macOS.</summary>".into(),
        ]),
        members: canonical
            .into_iter()
            .map(|(name, m)| cs::Member {
                name: name.into(),
                value: m.bits().to_string(),
                doc: String::new(),
            })
            .collect(),
    }
}

fn render_keys(_root: &Path) -> anyhow::Result<String> {
    let mut out = cs::header("hotkey_keys", SOURCE);
    out.push_str(
        r#"#nullable disable
using System;

namespace Prisc
{
"#,
    );
    let keys = cs::EnumDecl {
        name: "HotkeyKey".into(),
        underlying: None,
        flags: false,
        access: "public",
        doc: cs::Doc::Lines(vec![
            "<summary>A key a global hotkey can name: the prisc_hotkeys key table (values are Avalonia 12.1.2's,".into(),
            "so settings saved before this type existed load unchanged). Canonical names only; gesture text".into(),
            "(names, aliases, labels, modifiers) is parsed and printed by prisc_hotkeys through".into(),
            "<see cref=\"HotkeyGesture\"/>.</summary>".into(),
        ]),
        members: Key::ALL
            .iter()
            .map(|&key| cs::Member {
                name: key.name().into(),
                value: (key as i32).to_string(),
                doc: String::new(),
            })
            .collect(),
    };
    out.push_str(&cs::enum_decl(&keys, "    "));
    out.push('\n');
    out.push_str(&cs::enum_decl(&modifiers_enum(), "    "));
    out.push_str(
        r#"
    /// <summary>Generated name table for <see cref="HotkeyKey"/>.</summary>
    public static class HotkeyKeys
    {
        /// <summary>The canonical (serialized) name; the numeric value for an undefined one.</summary>
        public static string Name(HotkeyKey key) => key switch
        {
"#,
    );
    for &key in Key::ALL {
        let _ = writeln!(out, "            HotkeyKey.{0} => \"{0}\",", key.name());
    }
    out.push_str(
        r#"            _ => ((int)key).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
"#,
    );
    Ok(out)
}

/// The keystroke overlay's keycap legends per raw key code, one switch per OS numbering, from the
/// key table's `cap` column and its extra codes (`Key::keycap_for_code`). Generated rather than
/// exported: the overlay asks per key event while it composes frames.
fn render_keycaps(_root: &Path) -> anyhow::Result<String> {
    let mut out = cs::header("hotkey_keys", SOURCE);
    out.push_str(
        r#"#nullable disable

namespace Prisc.VideoSDK.Composition
{
    /// <summary>The legend on a key's keycap as the keystroke overlay draws it, per raw key code off the
    /// keyboard that recorded it: the prisc_hotkeys key table's keycap column. Null for a code with no
    /// key.</summary>
    internal static class KeycapLegends
    {
"#,
    );
    for (method, os, doc, last) in [
        (
            "Windows",
            Os::Windows,
            "a Windows virtual-key code (mouse buttons included)",
            0xFF_u16,
        ),
        ("MacOS", Os::MacOS, "a macOS CGKeyCode", 0x7F_u16),
    ] {
        let _ = write!(
            out,
            "        /// <summary>The legend for {doc}; null for a code with no key.</summary>\n        internal static string {method}(int code) => code switch\n        {{\n"
        );
        for code in 0..=last {
            if let Some(legend) = Key::keycap_for_code(code, os) {
                let _ = writeln!(out, "            {code} => {},", cs::string_literal(legend));
            }
        }
        out.push_str("            _ => null,\n        };\n\n");
    }
    render_modifier_roles(&mut out);
    out.push_str("    }\n}\n");
    Ok(out)
}

/// Fn's overlay role bit: the one above every `Modifiers` bit.
const ROLE_FN: u32 = 0x10;

/// The overlay's modifier roles: `Modifiers`' four (Meta is the Win key or Command) plus Fn
/// ([`ROLE_FN`]), which only a macOS keyboard reports (`Key::MAC_FN_KEYCODE`).
fn modifier_roles() -> Vec<(String, u32)> {
    assert!(
        Modifiers::from_bits(ROLE_FN).is_none(),
        "Fn's role bit must lie above every Modifiers bit"
    );
    let mut roles: Vec<(String, u32)> = modifiers_enum()
        .members
        .into_iter()
        .filter(|m| m.value != "0")
        .map(|m| (format!("Mod{}", m.name), m.value.parse().unwrap()))
        .collect();
    roles.push(("ModFn".into(), ROLE_FN));
    roles
}

/// The modifier role a raw key code plays on `os`'s keyboard, from the key table (either side's
/// key), or `None` for a key that is not a modifier.
fn modifier_role_for_code(code: u16, os: Os) -> Option<u32> {
    if os == Os::MacOS && code == Key::MAC_FN_KEYCODE {
        return Some(ROLE_FN);
    }
    Some(u32::from(Key::modifier_for_code(code, os).bits())).filter(|&bits| bits != 0)
}

/// The keycap legend of a modifier role on `os`: its keys' `cap` column (Fn's from the extras).
fn modifier_role_legend(role: u32, os: Os) -> Option<&'static str> {
    if role == ROLE_FN {
        return (os == Os::MacOS)
            .then(|| Key::keycap_for_code(Key::MAC_FN_KEYCODE, os))
            .flatten();
    }
    Key::ALL
        .iter()
        .find(|key| u32::from(key.modifier().bits()) == role)
        .map(|key| key.keycap(os))
}

/// `KeycapLegends`' modifier half: the role bits, the role of each raw key code per OS and the
/// legend of each role per OS, so the overlay's chord prefix reads what the modifier keycaps read.
fn render_modifier_roles(out: &mut String) {
    let roles = modifier_roles();
    let consts: Vec<String> = roles
        .iter()
        .map(|(name, bits)| format!("{name} = {bits}"))
        .collect();
    let _ = write!(
        out,
        "        /// <summary>The modifier roles <see cref=\"WindowsModifier\"/> and <see cref=\"MacOSModifier\"/> answer with:\n        /// prisc_hotkeys' side-agnostic modifier bits (Meta is the Win key or Command) and Fn, which only\n        /// a macOS keyboard reports.</summary>\n        internal const int {};\n",
        consts.join(", ")
    );
    let name_of = |bits: u32| {
        roles
            .iter()
            .find(|(_, b)| *b == bits)
            .map(|(n, _)| n.as_str())
            .unwrap()
    };
    for (method, os, doc, last) in [
        ("Windows", Os::Windows, "a Windows virtual-key code", 0xFF_u16),
        ("MacOS", Os::MacOS, "a macOS CGKeyCode", 0x7F_u16),
    ] {
        let _ = write!(
            out,
            "\n        /// <summary>The modifier role of {doc} (either side's key); 0 for a key that is not a modifier.</summary>\n        internal static int {method}Modifier(int code) => code switch\n        {{\n"
        );
        for code in 0..=last {
            if let Some(role) = modifier_role_for_code(code, os) {
                let _ = writeln!(out, "            {code} => {},", name_of(role));
            }
        }
        out.push_str("            _ => 0,\n        };\n");
    }
    for (method, os, board) in [("Windows", Os::Windows, "a Windows"), ("MacOS", Os::MacOS, "a macOS")] {
        let _ = write!(
            out,
            "\n        /// <summary>The keycap legend of a modifier role on {board} keyboard; null for a role it has no key for.</summary>\n        internal static string {method}ModifierLegend(int role) => role switch\n        {{\n"
        );
        for (name, bits) in &roles {
            if let Some(legend) = modifier_role_legend(*bits, os) {
                let _ = writeln!(out, "            {name} => {},", cs::string_literal(legend));
            }
        }
        out.push_str("            _ => null,\n        };\n");
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rendered_shapes() {
        let keys = render_keys(Path::new(".")).unwrap();
        assert!(keys.contains("        PrintScreen = 30,\n"));
        assert!(keys.contains("            HotkeyKey.Oem3 => \"Oem3\",\n"));
        assert!(keys.contains("    public enum HotkeyModifiers\n"));
        // no C# gesture grammar: no parser, aliases or labels
        for gone in ["TryParse", "Snapshot", "PrtScr", "Label(", "Dictionary"] {
            assert!(!keys.contains(gone), "{gone}");
        }
        assert!(keys.ends_with("    }\n}\n") && !keys.contains('\r'));

        let caps = render_keycaps(Path::new(".")).unwrap();
        assert!(caps.contains("    internal static class KeycapLegends\n"));
        assert!(caps.contains("            8 => \"Bksp\",\n"));
        assert!(caps.contains("            1 => \"LMouse\",\n"));
        assert!(caps.contains("            220 => \"\\\\\",\n"));
        assert!(caps.contains("            51 => \"Delete\",\n"));
        assert!(caps.contains("            76 => \"Enter\",\n"));
        assert!(caps.contains("            10 => \"\u{a7}\",\n"));
        assert_eq!(
            caps.matches("            _ => null,\n")
                .count(),
            4
        );
        assert!(caps.contains("        internal const int ModAlt = 1, ModControl = 2, ModShift = 4, ModMeta = 8, ModFn = 16;\n"));
        assert!(caps.contains("            163 => ModControl,\n"));
        assert!(caps.contains("            63 => ModFn,\n"));
        assert!(caps.contains("            ModMeta => \"Cmd\",\n"));
        assert!(caps.ends_with("    }\n}\n") && !caps.contains('\r'));
    }

    /// The overlay's hand tables before the key table drove them (KeyboardLayout.ModifierBit and
    /// ModifierNames): the same codes play the same roles, and the roles read the same words.
    #[test]
    fn modifier_roles_are_the_overlay_tables() {
        let (alt, control, shift, meta, fun) = (1, 2, 4, 8, 16);
        let windows: Vec<(u16, u32)> = (0..=0xFF)
            .filter_map(|code| modifier_role_for_code(code, Os::Windows).map(|role| (code, role)))
            .collect();
        let mut expected = vec![
            (16, shift),
            (160, shift),
            (161, shift),
            (17, control),
            (162, control),
            (163, control),
            (18, alt),
            (164, alt),
            (165, alt),
            (91, meta),
            (92, meta),
        ];
        expected.sort();
        assert_eq!(windows, expected);

        let mac: Vec<(u16, u32)> = (0..=0x7F)
            .filter_map(|code| modifier_role_for_code(code, Os::MacOS).map(|role| (code, role)))
            .collect();
        let mut expected = vec![
            (56, shift),
            (60, shift),
            (59, control),
            (62, control),
            (58, alt),
            (61, alt),
            (54, meta),
            (55, meta),
            (63, fun),
        ];
        expected.sort();
        assert_eq!(mac, expected);
        // Caps Lock is a plain key to the overlay, not a modifier.
        assert_eq!(modifier_role_for_code(57, Os::MacOS), None);
        assert_eq!(modifier_role_for_code(20, Os::Windows), None);

        for (role, win, mac) in [
            (control, Some("Ctrl"), Some("Ctrl")),
            (alt, Some("Alt"), Some("Option")),
            (shift, Some("Shift"), Some("Shift")),
            (meta, Some("Win"), Some("Cmd")),
            (fun, None, Some("Fn")),
        ] {
            assert_eq!(modifier_role_legend(role, Os::Windows), win, "role {role}");
            assert_eq!(modifier_role_legend(role, Os::MacOS), mac, "role {role}");
        }
    }
}
