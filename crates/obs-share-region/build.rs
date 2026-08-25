use std::env;
use std::path::{Path, PathBuf};

// macOS-only: rpaths so the produced binary (and the unit-test harness — the
// geometry tests link libobs via obs-sys, whose references are all @rpath) can
// launch. cargo does not propagate `cargo:rustc-link-arg` between packages, so
// each executable-producing crate repeats these; see obs-express's build.rs
// for the canonical set.
//
// Deliberately NOT repeated here: the obs-ffmpeg-mux patch and the dep-dylib
// staging (obs-express's build script owns those, and this binary neither
// muxes nor is dlopen'd), and the Windows runtime copy — on Windows the DLLs
// obs-express stages into the shared cargo profile dir sit next to this exe
// too. The plugin path needs no baking either: default_obs_paths and its
// env!("OBS_PLUGIN_DIR") live in obs-platform, which bakes it itself.
fn main() {
    let target_os = env::var("CARGO_CFG_TARGET_OS").unwrap_or_default();
    if target_os == "macos" {
        build_macos();
    }
}

fn build_macos() {
    let build_dir = env::var("DEP_OBS_OBS_BUILD_DIR").expect("DEP_OBS_OBS_BUILD_DIR not set");
    let config = env::var("DEP_OBS_OBS_BUILD_CONFIG").expect("DEP_OBS_OBS_BUILD_CONFIG not set");
    let framework_path =
        env::var("DEP_OBS_FRAMEWORK_SEARCH").expect("DEP_OBS_FRAMEWORK_SEARCH not set");

    // libobs.framework itself, then the graphics module libobs dlopens by bare
    // name at obs_reset_video time (GRAPHICS_MODULE = "libobs-metal.dylib"),
    // then the prebuilt FFmpeg/x264 dylibs everything above references.
    println!("cargo:rustc-link-arg=-Wl,-rpath,{framework_path}");
    println!("cargo:rustc-link-arg=-Wl,-rpath,{build_dir}/libobs-metal/{config}");
    println!("cargo:rustc-link-arg=-Wl,-rpath,{build_dir}/libobs-opengl/{config}");

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
