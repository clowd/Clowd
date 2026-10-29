//! The C# mirror of prisc_hotkeys' key table (`ui/Prisc.Shared/Hotkeys/HotkeyKey.g.cs`, public, in
//! Prisc.Shared), moved here from prisc_hotkeys' `src/csharp.rs`, and the keystroke overlay's keycap
//! legends (`ui/Prisc.VideoSDK/Composition/KeycapLegends.g.cs`). The mirror carries the enums and the
//! canonical names only: gesture text is parsed and printed by prisc_hotkeys through the
//! `prisc_gesture_*` exports, so C# has no parser, alias table or label table of its own. The legacy
//! hook ABI's constants and `AbiVersion` live in hotkey_status.rs's `HotkeysAbi.g.cs`.

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
        out.push_str("            _ => null,\n        };\n");
        if method == "Windows" {
            out.push('\n');
        }
    }
    out.push_str("    }\n}\n");
    Ok(out)
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
            2
        );
        assert!(caps.ends_with("    }\n}\n") && !caps.contains('\r'));
    }
}
