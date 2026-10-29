//! The C# mirror of prisc_hotkeys' key table (`ui/Prisc.Shared/Hotkeys/HotkeyKey.g.cs`, public, in
//! Prisc.Shared), moved here from prisc_hotkeys' `src/csharp.rs`. The legacy hook ABI's constants
//! and `AbiVersion` live in hotkey_status.rs's `HotkeysAbi.g.cs` now, so this generator reads only
//! the key table.

use std::fmt::Write as _;
use std::path::Path;

use prisc_hotkeys::{Key, Modifiers};

use crate::{cs, Output};

const SOURCE: &str = "the key table in crates/prisc_hotkeys/src/key.rs";

/// The key table mirror.
pub static OUTPUTS: &[Output] = &[Output {
    name: "hotkey-keys",
    path: "ui/Prisc.Shared/Hotkeys/HotkeyKey.g.cs",
    source: SOURCE,
    render: render_keys,
}];

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
using System.Collections.Generic;

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
            "so settings saved before this type existed load unchanged). Canonical names only; aliases and labels".into(),
            "parse via <see cref=\"HotkeyKeys.TryParse\"/>.</summary>".into(),
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
    /// <summary>Generated name tables for <see cref="HotkeyKey"/>.</summary>
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

        /// <summary>The short display name ("PrtScr", "Del", "1", "Num7", "="); the canonical name when
        /// there is no shorter one. For labels only; settings store <see cref="Name"/>.</summary>
        public static string Label(HotkeyKey key) => key switch
        {
"#,
    );
    for &key in Key::ALL {
        if key.label() != key.name() {
            let _ = writeln!(out, "            HotkeyKey.{} => \"{}\",", key.name(), cs::escape(key.label()));
        }
    }
    out.push_str(
        r#"            _ => Name(key),
        };

        /// <summary>Canonical names, aliases and labels, case-insensitive. No numbers beyond the
        /// top-row digits' labels, no comma lists.</summary>
        public static bool TryParse(string name, out HotkeyKey key) => Names.TryGetValue(name?.Trim() ?? "", out key);

        /// <summary>A gesture's modifier part: the canonical names and the shorthands (Ctrl, Cmd, Win,
        /// Option, Super, …), case-insensitive. "None" adds nothing.</summary>
        public static bool TryParseModifier(string name, out HotkeyModifiers modifier) =>
            ModifierNames.TryGetValue(name?.Trim() ?? "", out modifier);

        private static readonly Dictionary<string, HotkeyModifiers> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
        {
"#,
    );
    for &(name, modifier) in Modifiers::NAMES {
        let _ = writeln!(out, "            [\"{name}\"] = {},", cs_modifier(modifier));
    }
    out.push_str(
        r#"        };

        private static readonly Dictionary<string, HotkeyKey> Names = new(StringComparer.OrdinalIgnoreCase)
        {
"#,
    );
    for &key in Key::ALL {
        let _ = writeln!(out, "            [\"{0}\"] = HotkeyKey.{0},", key.name());
    }
    for &key in Key::ALL {
        for alias in key.aliases() {
            let _ = writeln!(out, "            [\"{alias}\"] = HotkeyKey.{},", key.name());
        }
    }
    for &key in Key::ALL {
        // a label that repeats the key's own name or alias (Return's "Enter") is already listed
        let label = key.label();
        let listed = std::iter::once(key.name())
            .chain(key.aliases().iter().copied())
            .any(|name| name.eq_ignore_ascii_case(label));
        if !listed {
            let _ = writeln!(out, "            [\"{}\"] = HotkeyKey.{},", cs::escape(label), key.name());
        }
    }
    out.push_str("        };\n    }\n}\n");
    Ok(out)
}

/// The C# spelling of one modifier flag (or none).
fn cs_modifier(modifier: Modifiers) -> &'static str {
    match modifier {
        Modifiers::NONE => "HotkeyModifiers.None",
        Modifiers::ALT => "HotkeyModifiers.Alt",
        Modifiers::CONTROL => "HotkeyModifiers.Control",
        Modifiers::SHIFT => "HotkeyModifiers.Shift",
        Modifiers::META => "HotkeyModifiers.Meta",
        other => panic!("no C# name for the modifier combination {other:?}"),
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
        assert!(keys.contains("            [\"Snapshot\"] = HotkeyKey.PrintScreen,\n"));
        assert!(keys.contains("            [\"PrtScr\"] = HotkeyKey.PrintScreen,\n"));
        assert!(keys.contains("            HotkeyKey.D1 => \"1\",\n"));
        assert!(keys.contains("            HotkeyKey.OemPipe => \"\\\\\",\n"));
        assert!(keys.contains("            [\"\\\\\"] = HotkeyKey.OemPipe,\n"));
        assert!(keys.contains("            [\"Ctrl\"] = HotkeyModifiers.Control,\n"));
        // Return's label is its own alias: listed twice, the C# initializer would throw at type init
        assert_eq!(keys.matches("[\"Enter\"]").count(), 1);
        assert!(keys.ends_with("    }\n}\n") && !keys.contains('\r'));
    }
}
