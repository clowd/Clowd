//! The hotkey status output, `ui/Prisc.Shared/Hotkeys/HotkeyErrorCodes.g.cs`: prisc_hotkeys'
//! machine-readable status codes (`ERROR_CODES`, the one code table), which the capturer's
//! `settings_status` event carries and Prisc.Ui keys its user-visible texts on, for its own hook
//! and the capturer's alike.

use std::fmt::Write as _;
use std::path::Path;

use prisc_hotkeys::ERROR_CODES;

use crate::{cs, Output};

const CODES_SOURCE: &str = "the status code table in crates/prisc_hotkeys/src/error.rs";

/// The status codes.
pub static OUTPUTS: &[Output] = &[Output {
    name: "hotkey-status",
    path: "ui/Prisc.Shared/Hotkeys/HotkeyErrorCodes.g.cs",
    source: CODES_SOURCE,
    render: render_codes,
}];

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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rendered_shapes() {
        let codes = render_codes(Path::new(".")).unwrap();
        assert!(codes.contains("        public const string Permission = \"permission\";\n"));
        assert!(codes.contains("        public const string StaleGrant = \"stale_grant\";\n"));
    }
}
