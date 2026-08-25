use std::env;
use std::path::{Path, PathBuf};

// The obs crate is a plain safe wrapper: it needs no link inputs of its own
// (obs-sys emits those). This script exists only so the crate's *test harness*
// can launch on macOS — the harness links libobs.framework via obs-sys, whose
// install name and FFmpeg/x264 references are all @rpath, and cargo does not
// propagate `cargo:rustc-link-arg` from obs-sys to dependents. Same pattern as
// obs-platform's build script.
fn main() {
    let target_os = env::var("CARGO_CFG_TARGET_OS").unwrap_or_default();
    if target_os == "macos" {
        build_macos();
    }
}

fn build_macos() {
    let framework_path =
        env::var("DEP_OBS_FRAMEWORK_SEARCH").expect("DEP_OBS_FRAMEWORK_SEARCH not set");
    println!("cargo:rustc-link-arg=-Wl,-rpath,{framework_path}");

    let out_dir = env::var("OUT_DIR").unwrap();
    let repo_root = Path::new(&out_dir)
        .ancestors()
        .find(|p| p.join("obs-studio").exists())
        .expect("Could not find repo root")
        .to_path_buf();
    if let Some(deps_lib) = find_obs_deps_lib(&repo_root) {
        println!("cargo:rustc-link-arg=-Wl,-rpath,{}", deps_lib.display());
    }
}

/// The prebuilt obs-deps `lib` dir (FFmpeg et al.) — same probe as
/// obs-express's build script.
fn find_obs_deps_lib(repo_root: &Path) -> Option<PathBuf> {
    let deps_dir = repo_root.join("obs-studio/.deps");
    for entry in std::fs::read_dir(&deps_dir).ok()?.flatten() {
        let name = entry.file_name().to_string_lossy().to_string();
        if name.starts_with("obs-deps-") && !name.contains("qt6") {
            let lib = entry.path().join("lib");
            if lib.exists() {
                return Some(lib);
            }
        }
    }
    None
}
