//! ONNX Runtime resolution and initialization.
//!
//! `ort` is built with `load-dynamic`: nothing links against the runtime, it
//! is found and loaded at startup. That is what lets the crate build with no
//! ORT SDK installed, lets CI ship the official Microsoft release archive
//! beside the executable unmodified, and turns "this machine has no usable
//! runtime" into a named exit code instead of a loader error dialog.
//!
//! Resolution order (first hit wins, and a hit that then fails to *load* is
//! an error rather than a fallthrough — a half-installed runtime should be
//! diagnosed, not silently shadowed by another copy):
//!
//! 1. `--ort-dylib <path>` — explicit, used by tests and dev harnesses;
//! 2. `ORT_DYLIB_PATH` — the env var `ort` itself honours, kept working so
//!    a developer's existing setup behaves as expected;
//! 3. the platform's dylib name beside this executable — the shipped layout
//!    (CI downloads the runtime into `publish/`, see ci.yml).

use std::path::{Path, PathBuf};

use anyhow::Context;

/// The runtime's file name in the shipped layout, beside the executable.
#[cfg(windows)]
const DYLIB_NAME: &str = "onnxruntime.dll";
#[cfg(target_os = "macos")]
const DYLIB_NAME: &str = "libonnxruntime.dylib";
#[cfg(not(any(windows, target_os = "macos")))]
const DYLIB_NAME: &str = "libonnxruntime.so";

/// Resolve and load the runtime. An `Err` here means inference is
/// unavailable on this machine; `main` turns it into
/// [`clowd_rust_core::exit::INFERENCE_UNAVAILABLE`].
pub fn init(explicit: Option<&Path>) -> anyhow::Result<()> {
    let path = resolve(explicit)?;
    log::info!("loading ONNX Runtime from {}", path.display());
    // init_from is where the dylib actually loads; commit only publishes the
    // (default) environment config and cannot fail.
    ort::init_from(&path)
        .map_err(|e| anyhow::anyhow!("loading the ONNX Runtime dylib at {}: {e}", path.display()))?
        .commit();
    Ok(())
}

fn resolve(explicit: Option<&Path>) -> anyhow::Result<PathBuf> {
    if let Some(path) = explicit {
        return Ok(path.to_path_buf());
    }
    if let Ok(path) = std::env::var("ORT_DYLIB_PATH") {
        return Ok(PathBuf::from(path));
    }
    let beside = std::env::current_exe()
        .context("locating this executable to look for the ONNX Runtime beside it")?
        .with_file_name(DYLIB_NAME);
    if beside.is_file() {
        return Ok(beside);
    }
    anyhow::bail!(
        "no ONNX Runtime found: pass --ort-dylib, set ORT_DYLIB_PATH, or place {DYLIB_NAME} beside the executable (looked at {})",
        beside.display()
    );
}

/// One-shot ort initialization for the env-gated tests, which run several
/// inference tests in one process while `ort`'s environment commits once.
/// Returns `false` — and the caller SKIPs — when no dev runtime is present.
#[cfg(test)]
pub fn init_for_tests() -> bool {
    use std::sync::OnceLock;
    static INIT: OnceLock<bool> = OnceLock::new();
    *INIT.get_or_init(|| {
        // CLOWD_TRACTNNI_ORT_DYLIB for the env-gated tests only; the fallback
        // is the dev machine's known runtime so a bare `cargo test` there
        // exercises the real models without any setup.
        let path = std::env::var("CLOWD_TRACTNNI_ORT_DYLIB").unwrap_or_else(|_| {
            if cfg!(windows) {
                r"C:\Users\Caelan\AppData\Local\Temp\ovenv\Lib\site-packages\onnxruntime\capi\onnxruntime.dll".into()
            } else {
                String::new()
            }
        });
        if path.is_empty() || !Path::new(&path).is_file() {
            return false;
        }
        match ort::init_from(&path) {
            Ok(builder) => {
                builder.commit();
                true
            }
            Err(e) => {
                eprintln!("ort init from {path} failed: {e}");
                false
            }
        }
    })
}
