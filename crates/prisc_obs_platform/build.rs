use std::env;
use std::path::{Path, PathBuf};

// macOS-only concerns; on Windows the moved code needs nothing beyond the
// windows-sys import libs, and the runtime DLLs are staged into the shared
// cargo profile dir by obs-express's build script.
fn main() {
    let target_os = env::var("CARGO_CFG_TARGET_OS").unwrap_or_default();
    if target_os == "macos" {
        build_macos();
    }
}

fn build_macos() {
    // enumerate_monitors talks straight to CoreGraphics/CoreFoundation via
    // hand-rolled extern decls (macos.rs), so anything that links this crate's
    // objects — including this crate's own test binary — needs the frameworks.
    println!("cargo:rustc-link-lib=framework=CoreGraphics");
    println!("cargo:rustc-link-lib=framework=CoreFoundation");

    // Bake the plugin path into the crate so no env var is needed at runtime
    // (default_obs_paths's env!("OBS_PLUGIN_DIR") — the dev-build fallback that
    // points at the OBS build tree in place). Consumers (obs-express) used to
    // bake this themselves; the env! moved here with the code that reads it.
    let build_dir = env::var("DEP_OBS_OBS_BUILD_DIR").expect("DEP_OBS_OBS_BUILD_DIR not set");
    println!("cargo:rustc-env=OBS_PLUGIN_DIR={build_dir}/plugins");

    // rpaths so this crate's *test binary* can actually launch: it links
    // libobs.framework (via obs-sys), which in turn references the prebuilt
    // FFmpeg/x264/... dylibs by @rpath. The consuming executables set their own
    // rpaths in their build scripts; cargo does not propagate link-args, so the
    // test target must repeat the two search roots here.
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
