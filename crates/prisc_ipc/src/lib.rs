//! The helper protocol: the one stdio contract between a parent (Prisc.Ui, or prisc_capture
//! spawning prisc_ai) and every child process Prisc ships. The spec is
//! `crates/prisc_capture/HELPER_PROTOCOL.md`.
//!
//! The message catalogs in [`catalog`] are the single source of truth: the C# records, codecs
//! and golden samples in `ui/Prisc.Ipc/Generated/HelperMessages.g.cs` are generated from them
//! (`src/csharp.rs`).

/// The envelope version (framing, `hello`, `error`, `quit`), sent in every `hello`. Bumped by
/// hand and should almost never change; catalog changes are caught by `catalog_hash` instead.
pub const PROTOCOL: u32 = 1;

pub mod catalog;
pub mod exit;

#[cfg(test)]
mod csharp;
