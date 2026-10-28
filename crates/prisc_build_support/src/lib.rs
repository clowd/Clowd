//! Helpers shared by the workspace's build scripts.
//!
//! Today this is only the Linux runtime bundle: Windows and macOS get FFmpeg
//! from the prebuilt obs-deps archives that OBS's own CMake configure
//! downloads, and their build scripts keep their original, self-contained
//! logic. Linux has no obs-deps bundle, so the workspace fetches a pinned
//! FFmpeg itself — and because prisc_obs_sys (libobs + plugins), prisc_ffmpeg_sys (prisc_vid2gif)
//! and the staging scripts must all agree on the *same* copy, the pin and the
//! code that materialises it live here once rather than in four `build.rs`
//! files that could drift apart.
//!
//! The crate compiles on every host (it is a plain build-dependency); only
//! Linux build scripts call into [`linux`]. The obs-deps helpers below are for
//! the Windows/macOS scripts that locate the prebuilt bundle.

pub mod linux;

use std::path::{Path, PathBuf};

/// Panics with the fix when the `crates/obs-studio` submodule is not checked
/// out, recursively: OBS's CMakeLists also insist that the (disabled)
/// `obs-browser` / `obs-websocket` plugin submodule dirs exist. Without this a
/// fresh checkout gets only an opaque CMake error, and only the recorder stack
/// needs the submodule at all. Registers the files with `rerun-if-changed`, so
/// the check runs again once the submodule is checked out.
pub fn require_obs_submodule(obs_src: &Path, crate_name: &str) {
    let required = [
        obs_src.join("CMakeLists.txt"),
        obs_src.join("plugins").join("obs-browser").join("CMakeLists.txt"),
        obs_src.join("plugins").join("obs-websocket").join("CMakeLists.txt"),
    ];
    for file in &required {
        println!("cargo:rerun-if-changed={}", file.display());
    }
    if let Some(missing) = required.iter().find(|f| !f.is_file()) {
        panic!(
            "{crate_name}: the crates/obs-studio submodule is not checked out ({} is missing). \
             Run `git submodule update --init --recursive`. Only the recorder stack (`cargo rec`) \
             needs it; a plain `cargo build` / `cargo test` does not build it. If your editor \
             triggered this build, see \"Editors\" in BUILDING.md.",
            missing.display()
        );
    }
}

/// The prebuilt obs-deps version (`YYYY-MM-DD`) the pinned obs-studio checkout
/// downloads, read from its `CMakePresets.json`. Registers that file with
/// `rerun-if-changed`, so an OBS bump re-runs every caller (rpaths, staging,
/// bindings) instead of leaving it pointed at the previous bundle.
///
/// `.deps` keeps every bundle it ever downloaded, so after an OBS bump a dev
/// tree holds two (e.g. FFmpeg 7 and FFmpeg 8). Matching this exact version is
/// what keeps libobs, prisc_ffmpeg_sys and the staged runtime on the same one;
/// "first `obs-deps-*` dir" picked whichever `read_dir` listed first.
pub fn obs_deps_version(obs_src: &Path) -> String {
    let presets = obs_src.join("CMakePresets.json");
    println!("cargo:rerun-if-changed={}", presets.display());
    let text = std::fs::read_to_string(&presets)
        .unwrap_or_else(|e| panic!("failed to read {}: {e}", presets.display()));
    // No JSON dependency (see Cargo.toml): the first "version" after the
    // "prebuilt" key is the obs-deps release.
    let version = text
        .find("\"prebuilt\"")
        .and_then(|i| text[i..].find("\"version\"").map(|j| &text[i + j..]))
        .and_then(|rest| rest.split('"').nth(3));
    match version {
        Some(v) => v.to_string(),
        None => panic!("no prebuilt obs-deps version in {}", presets.display()),
    }
}

/// The non-Qt obs-deps bundle dirs under `<obs_src>/.deps` for the pinned
/// version — one per arch on Windows (`-x64`, `-arm64`, `-x86`), a single
/// `-universal` on macOS. Callers filter by arch / contents.
pub fn obs_deps_bundles(obs_src: &Path) -> Vec<PathBuf> {
    let prefix = format!("obs-deps-{}-", obs_deps_version(obs_src));
    let Ok(entries) = std::fs::read_dir(obs_src.join(".deps")) else {
        return Vec::new();
    };
    entries
        .flatten()
        .map(|e| e.path())
        .filter(|p| {
            p.is_dir()
                && p.file_name()
                    .is_some_and(|n| n.to_string_lossy().starts_with(&prefix))
        })
        .collect()
}
