//! prisc_codegen: every generated C# file in the repo, and the crate rules of prisc_interop.
//!
//! - `cargo codegen` writes every output whose bytes changed (always LF) and prints the paths.
//! - `cargo codegen --check` writes nothing and exits 1 listing stale, missing or orphaned files.
//! - `cargo codegen --list` prints every output path (for `git add`, DESIGN 3.5).
//! - `cargo test -p prisc_codegen` is the gate: every output current, no orphan, every lint rule.
//!
//! Each module owns its outputs (`OUTPUTS`), so a work package that adds an output edits only
//! its own module.

use std::path::{Path, PathBuf};

/// The ABI fingerprint, shared with crates/prisc_interop/build.rs (README E1).
pub mod abi_fingerprint;

pub mod abi;
pub mod contracts;
pub mod cs;
pub mod env;
pub mod hotkey_keys;
pub mod hotkey_status;
pub mod ipc;
pub mod lint;
pub mod session;

/// One generated file.
pub struct Output {
    /// A short stable name: `interop-native`, `ipc`, `hotkey-keys`, ...
    pub name: &'static str,
    /// Repo-relative, forward slashes.
    pub path: &'static str,
    /// What it is generated from, for the header.
    pub source: &'static str,
    /// Renders the file's full text (LF) from the repo root.
    pub render: fn(&Path) -> anyhow::Result<String>,
}

/// How an output on disk compares with its render.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum State {
    Current,
    Stale,
    Missing,
}

/// Every output, in a stable order.
pub fn outputs() -> Vec<&'static Output> {
    [
        abi::OUTPUTS,
        env::OUTPUTS,
        ipc::OUTPUTS,
        hotkey_keys::OUTPUTS,
        hotkey_status::OUTPUTS,
        contracts::OUTPUTS,
        session::OUTPUTS,
    ]
    .iter()
    .flat_map(|outputs| outputs.iter())
    .collect()
}

/// The repo root (this crate is `crates/prisc_codegen`).
pub fn repo_root() -> PathBuf {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    root.canonicalize().unwrap_or(root)
}

/// Compares every output with its render (CRLF on disk reads as LF, so a CRLF checkout is current).
pub fn check(root: &Path) -> anyhow::Result<Vec<(&'static Output, State)>> {
    let mut states = Vec::new();
    for output in outputs() {
        let rendered = (output.render)(root).map_err(|e| anyhow::anyhow!("{}: {e:#}", output.path))?;
        let state = match std::fs::read_to_string(root.join(output.path)) {
            Ok(on_disk) if on_disk.replace("\r\n", "\n") == rendered => State::Current,
            Ok(_) => State::Stale,
            Err(_) => State::Missing,
        };
        states.push((output, state));
    }
    Ok(states)
}

/// Writes every output whose bytes differ (LF), returning those it wrote.
pub fn write(root: &Path) -> anyhow::Result<Vec<&'static Output>> {
    let mut written = Vec::new();
    for output in outputs() {
        let rendered = (output.render)(root).map_err(|e| anyhow::anyhow!("{}: {e:#}", output.path))?;
        let path = root.join(output.path);
        if std::fs::read(&path).ok().as_deref() == Some(rendered.as_bytes()) {
            continue;
        }
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(&path, rendered)?;
        written.push(output);
    }
    Ok(written)
}

/// Every `*.g.cs` under `ui/` that carries this generator's header but is no output any more (a
/// retired output must not linger).
pub fn orphans(root: &Path) -> anyhow::Result<Vec<PathBuf>> {
    let listed: Vec<PathBuf> = outputs()
        .iter()
        .map(|output| root.join(output.path))
        .collect();
    let mut found = Vec::new();
    let mut stack = vec![root.join("ui")];
    while let Some(dir) = stack.pop() {
        let Ok(entries) = std::fs::read_dir(&dir) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            let name = entry
                .file_name()
                .to_string_lossy()
                .into_owned();
            if path.is_dir() {
                if name != "bin" && name != "obj" && !name.starts_with('.') {
                    stack.push(path);
                }
            } else if name.ends_with(".g.cs") && !listed.contains(&path) {
                let text = std::fs::read_to_string(&path)
                    .unwrap_or_default()
                    .replace("\r\n", "\n");
                if text.starts_with(cs::HEADER_PREFIX) {
                    found.push(path);
                }
            }
        }
    }
    found.sort();
    Ok(found)
}
