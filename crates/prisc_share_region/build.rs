use std::env;
use std::path::{Path, PathBuf};

// Two jobs, one per platform. Windows: compile the app icon into the exe (see
// `build_windows`). macOS: rpaths so the produced binary (and the unit-test harness — the
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
    if target_os == "windows" {
        build_windows();
    }
}

/// Compiles `assets/clowd.ico` into the executable as icon resource 1.
///
/// Two things read it: the shell, which shows the lowest-numbered RT_GROUP_ICON
/// as the file's icon in Explorer, the taskbar and Alt+Tab; and `ui/win32.rs`,
/// which loads the same id into the window class so the caption and the
/// window's taskbar button carry it too. The share prompt is a window the user
/// has to FIND in a meeting app's picker, and pickers list a window by its icon
/// and title, so the icon is functional here rather than decorative.
///
/// Deliberately this crate's build script and no other: clowd_share_region is the
/// only binary in the workspace that is a Clowd-branded user-facing window.
/// `rustc-link-arg-bins` keeps the .res on this crate's own bin link line.
///
/// Hand-rolled rather than via a build dependency: this is one invocation of
/// the Windows SDK's `rc.exe`, which the MSVC toolchain already implies.
fn build_windows() {
    println!("cargo:rerun-if-changed=assets/clowd.ico");

    // The GNU toolchain has no rc.exe (its equivalent is windres, with a
    // different command line). Nothing else here is msvc-specific, so rather
    // than fail a gnu build, skip the icon and say so.
    if env::var("CARGO_CFG_TARGET_ENV").unwrap_or_default() != "msvc" {
        println!("cargo:warning=app icon skipped: not an msvc target");
        return;
    }

    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let icon = manifest_dir.join("assets/clowd.ico");
    assert!(icon.exists(), "missing app icon: {}", icon.display());

    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    let rc_path = out_dir.join("icon.rc");
    let res_path = out_dir.join("icon.res");
    // Resource id 1: the id `LoadIconW` asks for, and — being the lowest —
    // the one the shell picks for the file itself.
    // A .rc string is C-like, so the path's backslashes have to be doubled.
    let rc_source = format!(
        "1 ICON \"{}\"\n",
        icon.display().to_string().replace('\\', "\\\\")
    );
    std::fs::write(&rc_path, rc_source).expect("write icon.rc");

    let rc_exe = find_rc_exe().expect(
        "rc.exe not found: the Windows SDK is required to build clowd_share_region's app icon",
    );
    let status = std::process::Command::new(&rc_exe)
        .arg("/nologo")
        .arg("/fo")
        .arg(&res_path)
        .arg(&rc_path)
        .status()
        .expect("failed to run rc.exe");
    assert!(status.success(), "rc.exe failed: {status}");

    println!("cargo:rustc-link-arg-bins={}", res_path.display());
}

/// The newest x64 `rc.exe` in the installed Windows 10/11 SDKs. The SDK's bin
/// directory is versioned (`.../bin/10.0.22621.0/x64/rc.exe`) and several
/// versions are usually installed side by side; the highest sorts last.
fn find_rc_exe() -> Option<PathBuf> {
    let roots = [
        env::var("ProgramFiles(x86)").unwrap_or_else(|_| "C:/Program Files (x86)".into()),
        env::var("ProgramFiles").unwrap_or_else(|_| "C:/Program Files".into()),
    ];
    let mut found: Vec<PathBuf> = Vec::new();
    for root in roots {
        let bin = Path::new(&root).join("Windows Kits/10/bin");
        let Ok(entries) = std::fs::read_dir(&bin) else {
            continue;
        };
        for entry in entries.flatten() {
            let rc = entry.path().join("x64/rc.exe");
            if rc.exists() {
                found.push(rc);
            }
        }
    }
    found.sort();
    found.pop()
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
