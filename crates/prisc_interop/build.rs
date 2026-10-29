//! Three things, and nothing written into the source tree:
//!
//! 1. Windows (MSVC): `/DEPENDENTLOADFLAG:0x800` (LOAD_LIBRARY_SEARCH_SYSTEM32), so the library's
//!    own imports (user32, gdi32, shcore, d3d12, ...) come from System32 and never from the working
//!    directory or PATH. It replaces the `[DefaultDllImportSearchPaths(System32)]` attributes of the
//!    P/Invokes this crate retires. A plain rustc-link-arg so a test binary that links the crate
//!    carries it too; the linker ignores it for anything with no import table.
//! 2. macOS: the dylib's install name is `@rpath/libprisc_interop.dylib`, which keeps CI paths out
//!    of the shipped binary (C# opens it by absolute path anyway).
//! 3. The ABI fingerprint (README section E): a 64-bit FNV-1a over the canonical rendering of every
//!    export signature, `#[repr(C)]` struct and `#[repr]` enum in `src/ffi`, plus the hotkey `Key`
//!    discriminants, computed by the same module prisc_codegen uses for `InteropAbi.g.cs`, and
//!    written to `OUT_DIR/abi_hash.rs`. An ffi edit rebuilt without `cargo codegen` therefore
//!    gives the library a hash the C# bindings do not have, and the loader refuses it.

use std::env;
use std::fs;
use std::path::PathBuf;

#[allow(dead_code)] // the canonical text is for prisc_codegen's diagnostics
mod abi_fingerprint {
    include!("../prisc_codegen/src/abi_fingerprint.rs");
}

fn main() {
    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR"));

    if env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc") {
        println!("cargo:rustc-link-arg=/DEPENDENTLOADFLAG:0x800");
    }
    if env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("macos") {
        println!("cargo:rustc-cdylib-link-arg=-Wl,-install_name,@rpath/libprisc_interop.dylib");
    }

    // `prisc_interop_version` bakes this in with option_env!, so a version bump has to invalidate
    // the cached build.
    println!("cargo:rerun-if-env-changed=PRISC_VERSION");
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-changed=../prisc_codegen/src/abi_fingerprint.rs");

    let ffi_dir = manifest_dir.join("src/ffi");
    let key_rs = manifest_dir.join("../prisc_hotkeys/src/key.rs");
    // The directory itself, so an added or removed ffi file reruns this script too.
    println!("cargo:rerun-if-changed={}", ffi_dir.display());
    let fingerprint = abi_fingerprint::compute_from_paths(&ffi_dir, &key_rs).unwrap_or_else(|e| panic!("ABI fingerprint: {e}"));
    for input in &fingerprint.inputs {
        println!("cargo:rerun-if-changed={}", input.display());
    }

    let out = PathBuf::from(env::var("OUT_DIR").expect("OUT_DIR")).join("abi_hash.rs");
    fs::write(&out, format!("0x{:016X}u64\n", fingerprint.hash)).expect("write OUT_DIR/abi_hash.rs");
}
