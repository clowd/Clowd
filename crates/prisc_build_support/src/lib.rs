//! Helpers shared by the workspace's build scripts.
//!
//! Two groups live here. The obs-deps helpers serve Windows and macOS, which
//! get FFmpeg from the prebuilt obs-deps archives that OBS's own CMake
//! configure downloads: finding the bundle for the target arch, the rpaths
//! every executable that links libobs needs, and the incremental runtime copy.
//! The [`linux`] module is the Linux runtime bundle: Linux has no obs-deps
//! bundle, so the workspace fetches a pinned FFmpeg itself, and because
//! prisc_obs_sys (libobs + plugins), prisc_ffmpeg_sys (prisc_vid2gif) and the
//! staging scripts must all agree on the *same* copy, the pin and the code
//! that materialises it live here once rather than in four `build.rs` files
//! that could drift apart.
//!
//! The crate compiles on every host (it is a plain build-dependency); only
//! Linux build scripts call into [`linux`].

pub mod linux;

use std::env;
use std::fs;
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
/// version: one per arch on Windows (`-x64`, `-arm64`, `-x86`), a single
/// `-universal` on macOS. [`target_obs_deps_bundle`] picks the one to use.
fn obs_deps_bundles(obs_src: &Path) -> Vec<PathBuf> {
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

/// The `crates/obs-studio` submodule checkout. Every crate that calls this is a
/// sibling of it under `crates/`.
pub fn obs_src() -> PathBuf {
    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    manifest_dir.parent().unwrap().join("obs-studio")
}

/// The obs-deps bundle for the **target** architecture that holds every path
/// in `required` (relative to the bundle root), or `None` while it is missing.
///
/// `.deps` can hold several bundles: a Windows build also downloads the x86
/// one (and an ARM64 build the x64 one) for the child CMake configures OBS
/// spawns for its helpers. Directory order is arbitrary, so taking the first
/// `obs-deps-*` would ship x64 runtime DLLs in an ARM64 bundle, or link x64
/// import libraries into an ARM64 binary (LNK4272 plus an unresolved external
/// for every symbol). A bundle for a different arch is never returned.
pub fn target_obs_deps_bundle(obs_src: &Path, required: &[&str]) -> Option<PathBuf> {
    obs_deps_bundles(obs_src).into_iter().find(|dir| {
        let name = dir.file_name().unwrap_or_default().to_string_lossy();
        is_target_arch_bundle(&name) && required.iter().all(|r| dir.join(r).exists())
    })
}

/// True for a bundle name built for the target arch. macOS ships one
/// `-universal` bundle covering both slices; Windows names them `-x64` /
/// `-arm64` / `-x86`.
fn is_target_arch_bundle(name: &str) -> bool {
    if name.contains("universal") {
        return true;
    }
    match env::var("CARGO_CFG_TARGET_ARCH").unwrap_or_default().as_str() {
        "x86_64" => name.ends_with("-x64"),
        "aarch64" => name.ends_with("-arm64"),
        _ => false,
    }
}

/// The rpaths an executable that links libobs (through prisc_obs_sys) needs to
/// launch from the build tree, read from prisc_obs_sys's `DEP_OBS_*` exports.
/// Only a direct dependent of prisc_obs_sys sees those, so only its build
/// script can call this.
///
/// `cargo:rustc-link-arg` is package-scoped: prisc_obs_sys's own link args do
/// not reach its dependents, so every crate that produces an executable
/// (including a test harness) repeats them, and they are defined here once.
///
/// - macOS: libobs.framework's dir, then the graphics modules libobs dlopens
///   by bare name at obs_reset_video time (`libobs-metal.dylib`,
///   `libobs-opengl.dylib`), then the prebuilt FFmpeg/x264 dylibs everything
///   above references. libobs's install name and its dependencies' are all
///   `@rpath`, so even an empty test harness dies in dyld without these.
/// - Linux: libobs.so.30's dir and the pinned FFmpeg bundle.
///
/// Windows needs none: the runtime DLLs sit next to the executables in the
/// cargo profile dir (prisc_recorder's build script stages them).
pub fn link_obs_rpaths() {
    let rpath = |dir: &str| println!("cargo:rustc-link-arg=-Wl,-rpath,{dir}");
    let dep = |key: &str| env::var(format!("DEP_OBS_{key}")).unwrap_or_else(|_| panic!("DEP_OBS_{key} not set"));
    match env::var("CARGO_CFG_TARGET_OS").unwrap_or_default().as_str() {
        "macos" => {
            let build_dir = dep("OBS_BUILD_DIR");
            let config = dep("OBS_BUILD_CONFIG");
            rpath(&dep("FRAMEWORK_SEARCH"));
            rpath(&format!("{build_dir}/libobs-metal/{config}"));
            rpath(&format!("{build_dir}/libobs-opengl/{config}"));
            if let Ok(deps_lib) = env::var("DEP_OBS_DEPS_LIB") {
                rpath(&deps_lib);
            }
        }
        "linux" => {
            rpath(&dep("OBS_LIB_DIR"));
            rpath(&dep("DEPS_LIB"));
        }
        _ => {}
    }
}

/// The cargo profile dir (`target[/<triple>]/{debug,release}`) a build script
/// stages runtime files into, beside the executables.
pub fn cargo_profile_dir() -> PathBuf {
    // OUT_DIR = <profile>/build/<crate>-<hash>/out
    PathBuf::from(env::var("OUT_DIR").unwrap())
        .ancestors()
        .nth(3)
        .expect("could not resolve the cargo profile dir from OUT_DIR")
        .to_path_buf()
}

/// Copies `src` to `dst` when `dst` is missing, older, or a different size.
///
/// The copy lands under a temporary name and is renamed over `dst`, because
/// several build scripts stage the same FFmpeg files into the shared profile
/// directory concurrently (prisc_vid2gif and prisc_recorder both do, as on Windows): a
/// plain `fs::copy` from each would interleave into a corrupt file.
pub fn copy_if_newer(src: &Path, dst: &Path) {
    let should_copy = match (fs::metadata(src), fs::metadata(dst)) {
        (Ok(s), Ok(d)) => {
            s.len() != d.len()
                || match (s.modified(), d.modified()) {
                    (Ok(sm), Ok(dm)) => sm > dm,
                    _ => true,
                }
        }
        (Ok(_), Err(_)) => true,
        _ => false,
    };
    if should_copy {
        atomic_copy(src, dst, |_| true);
    }
}

/// Copy `src` to a temporary sibling of `dst`, run `finish` on it, and rename
/// it into place if `finish` succeeds.
pub(crate) fn atomic_copy(src: &Path, dst: &Path, finish: impl FnOnce(&Path) -> bool) {
    if let Some(parent) = dst.parent() {
        let _ = fs::create_dir_all(parent);
    }
    let tmp = dst.with_file_name(format!(
        ".{}.{}.tmp",
        dst.file_name().unwrap().to_string_lossy(),
        std::process::id()
    ));
    // The io::Error matters most on Windows, where a running recorder holding
    // the destination DLL shows up as a sharing violation.
    let result = match fs::copy(src, &tmp) {
        Err(e) => Err(e.to_string()),
        Ok(_) if !finish(&tmp) => Err("post-copy step failed".to_string()),
        Ok(_) => fs::rename(&tmp, dst).map_err(|e| e.to_string()),
    };
    if let Err(e) = result {
        let _ = fs::remove_file(&tmp);
        println!(
            "cargo:warning=failed to stage {} -> {}: {e}",
            src.display(),
            dst.display()
        );
    }
}
