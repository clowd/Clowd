//! `ui/Prisc.Ui/Session/SessionContract.g.cs`: the `session.json` golden sample. `SessionInfo` is a
//! large mutable persisted C# object and stays hand-written; a C# test deserializes this sample
//! (serialized by the Rust writers' own serde shape) and checks every field arrives.

use std::fmt::Write as _;
use std::path::Path;

use crate::{cs, Output};

const SOURCE: &str = "prisc_rust_core::session::contract_sample() in crates/prisc_rust_core/src/session.rs";

/// The session.json golden sample.
pub static OUTPUTS: &[Output] = &[Output {
    name: "session",
    path: "ui/Prisc.Ui/Session/SessionContract.g.cs",
    source: SOURCE,
    render,
}];

fn render(_root: &Path) -> anyhow::Result<String> {
    // The line in the writers' own field order; the field list from the parsed value.
    let line = serde_json::to_string(&prisc_rust_core::session::contract_sample())?;
    let sample: serde_json::Value = serde_json::from_str(&line)?;
    let fields: Vec<String> = sample
        .as_object()
        .map(|o| o.keys().cloned().collect())
        .unwrap_or_default();
    let mut out = cs::header("session", SOURCE);
    out.push_str(
        r#"namespace Prisc
{
    /// <summary>The session.json the Rust capture helpers write, every field set, for the contract test.</summary>
    internal static class SessionContract
    {
"#,
    );
    let _ = writeln!(
        out,
        "        internal const string RustSample = {};\n",
        cs::raw_string_literal(&line)
    );
    let _ = writeln!(out, "        internal static readonly string[] RustFields =\n        [");
    for field in fields {
        let _ = writeln!(out, "            {},", cs::string_literal(&field));
    }
    out.push_str("        ];\n    }\n}\n");
    Ok(out)
}
