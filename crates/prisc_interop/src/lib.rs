//! `prisc_interop`: the one native library Prisc's C# hosts load in-process (Prisc.Ui,
//! Prisc.VideoRender and their tests). A thin C ABI over the shared Rust crates (prisc_platform,
//! prisc_rust_core, prisc_hotkeys, prisc_ipc) plus the OS code that has no other Rust consumer.
//!
//! - `src/ffi/*`: the ABI, one file per area. crates/prisc_codegen reads these files and
//!   generates `ui/Prisc.Interop/Generated/NativeMethods.g.cs` (DllImports, structs, handles) and
//!   `InteropTypes.g.cs` (the public C# enums); `cargo codegen` regenerates, `cargo test -p
//!   prisc_codegen` fails when the bindings are stale or a crate rule is broken.
//! - `src/platform/{windows,macos}`: the OS halves the exports call.
//! - `src/panic.rs`, `src/error.rs`, `src/util.rs`, `src/convert.rs`: the boundary machinery.
//!
//! README.md is the contract: export shape, status codes, types, string modes, handles,
//! callbacks, threads and the crate rules.

// A `# Safety` heading on an export would land in the generated C# XML docs as a stray markdown
// heading, so each export's pointer contract is written into the prose of its doc comment.
#![allow(clippy::missing_safety_doc)]

pub mod ffi;

mod convert;
mod error;
mod hotkeys_host;
mod panic;
mod platform;
mod util;

/// The ABI fingerprint of this build (README E1), computed by build.rs from `src/ffi` and the
/// hotkey key table. The C# loader compares it with the generated `InteropAbi.Hash`.
pub(crate) const ABI_HASH: u64 = include!(concat!(env!("OUT_DIR"), "/abi_hash.rs"));
