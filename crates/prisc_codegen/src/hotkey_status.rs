//! The hotkey status outputs:
//!
//! - `ui/Prisc.Shared/Hotkeys/HotkeyErrorCodes.g.cs`: prisc_hotkeys' machine-readable status codes
//!   (`ERROR_CODES`, the one code table), which the capturer's `settings_status` event carries and
//!   Prisc.Ui keys its refusal texts on.
//! - `ui/Prisc.Ui/Services/Hotkeys/HotkeysAbi.g.cs` (retiring): the constants, event struct and
//!   `AbiVersion` of the legacy `prisc_hotkeys_legacy_*` ABI that `HotkeysNative.cs` still
//!   P/Invokes. It goes when the hook moves behind prisc_interop's `prisc_hotkeys_*` exports.

use std::fmt::Write as _;
use std::path::Path;

use prisc_hotkeys::ffi::{abi_version, ABI_CONSTS, EVENT_FIELDS};
use prisc_hotkeys::ERROR_CODES;

use crate::{cs, Output};

const CODES_SOURCE: &str = "the status code table in crates/prisc_hotkeys/src/error.rs";
const ABI_SOURCE: &str = "the legacy C ABI in crates/prisc_hotkeys/src/ffi.rs";

/// The status codes and the retiring legacy ABI mirror.
pub static OUTPUTS: &[Output] = &[
    Output {
        name: "hotkey-status",
        path: "ui/Prisc.Shared/Hotkeys/HotkeyErrorCodes.g.cs",
        source: CODES_SOURCE,
        render: render_codes,
    },
    Output {
        name: "hotkeys-abi",
        path: "ui/Prisc.Ui/Services/Hotkeys/HotkeysAbi.g.cs",
        source: ABI_SOURCE,
        render: render_abi,
    },
];

fn render_codes(_root: &Path) -> anyhow::Result<String> {
    let mut out = cs::header("hotkey_status", CODES_SOURCE);
    out.push_str(
        r#"namespace Prisc
{
    /// <summary>The machine-readable hotkey status codes (prisc_hotkeys <c>Error::code()</c> plus the
    /// supervisor's <c>stale_grant</c>), as the capturer's <c>settings_status</c> event carries them.</summary>
    public static class HotkeyErrorCodes
    {
"#,
    );
    for (name, code) in ERROR_CODES {
        let _ = writeln!(out, "        public const string {name} = {};", cs::string_literal(code));
    }
    out.push_str("    }\n}\n");
    Ok(out)
}

fn render_abi(_root: &Path) -> anyhow::Result<String> {
    let mut out = cs::header("hotkey_status", ABI_SOURCE);
    out.push_str(
        r#"using System.Runtime.InteropServices;

namespace Prisc.UI
{
    /// <summary>prisc_hotkeys return codes and event kinds.</summary>
    internal static class HotkeysAbi
    {
"#,
    );
    let _ = writeln!(
        out,
        "        /// <summary>prisc_hotkeys_legacy_abi_version() of the native library this mirror was generated with.</summary>"
    );
    let _ = writeln!(out, "        public const uint AbiVersion = 0x{:08X}u;\n", abi_version());
    for (name, value) in ABI_CONSTS {
        let _ = writeln!(out, "        public const int {name} = {value};");
    }
    out.push_str(
        r#"    }

    /// <summary>PriscHotkeysEvent, as prisc_hotkeys_legacy_next_event fills it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct HotkeyEventNative
    {
"#,
    );
    for (name, c_type) in EVENT_FIELDS {
        let cs_type = match *c_type {
            "uint32_t" => "uint",
            "int32_t" => "int",
            other => anyhow::bail!("no C# type for {other}"),
        };
        let _ = writeln!(out, "        public {cs_type} {};", cs::pascal(name));
    }
    out.push_str("    }\n}\n");
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rendered_shapes() {
        let codes = render_codes(Path::new(".")).unwrap();
        assert!(codes.contains("        public const string Permission = \"permission\";\n"));
        assert!(codes.contains("        public const string StaleGrant = \"stale_grant\";\n"));
        let abi = render_abi(Path::new(".")).unwrap();
        assert!(abi.contains("        public const int CH_ERR_STOPPED = -20;\n"));
        assert!(abi.contains("        public uint RawCode;\n        public uint Ch;\n        public uint Flags;\n"));
        assert!(abi.contains("        public int Key;\n"));
        assert!(abi.contains(&format!("public const uint AbiVersion = 0x{:08X}u;", abi_version())));
    }
}
