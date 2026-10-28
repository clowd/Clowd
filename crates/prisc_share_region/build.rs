use std::env;
use std::path::{Path, PathBuf};

// Two jobs, one per platform. Windows: compile the app icon into the exe (see
// `build_windows`). macOS: rpaths so the produced binary (and the unit-test harness — the
// geometry tests link libobs via prisc_obs_sys, whose references are all @rpath) can
// launch. cargo does not propagate `cargo:rustc-link-arg` between packages, so
// each executable-producing crate repeats these through
// prisc_build_support::link_obs_rpaths.
//
// Deliberately NOT repeated here: the obs-ffmpeg-mux patch and the dep-dylib
// staging (prisc_recorder's build script owns those, and this binary neither
// muxes nor is dlopen'd), and the Windows runtime copy — on Windows the DLLs
// prisc_recorder stages into the shared cargo profile dir sit next to this exe
// too. The plugin path needs no baking either: default_obs_paths and its
// env!("OBS_PLUGIN_DIR") live in prisc_obs_platform, which bakes it itself.
fn main() {
    let target_os = env::var("CARGO_CFG_TARGET_OS").unwrap_or_default();
    if target_os == "macos" {
        prisc_build_support::link_obs_rpaths();
    }
    if target_os == "windows" {
        build_windows();
    }
}

/// Compiles `assets/prisc.ico` into the executable as icon resource 1.
///
/// Two things read it: the shell, which shows the lowest-numbered RT_GROUP_ICON
/// as the file's icon in Explorer, the taskbar and Alt+Tab; and `ui/win32.rs`,
/// which loads the same id into the window class so the caption and the
/// window's taskbar button carry it too. The share prompt is a window the user
/// has to FIND in a meeting app's picker, and pickers list a window by its icon
/// and title, so the icon is functional here rather than decorative.
///
/// Deliberately this crate's build script and no other: prisc_share_region is the
/// only binary in the workspace that is a Prisc-branded user-facing window.
/// `rustc-link-arg-bins` keeps the .res on this crate's own bin link line.
///
/// Hand-rolled rather than via a build dependency: this is one invocation of
/// the Windows SDK's `rc.exe`, which the MSVC toolchain already implies.
fn build_windows() {
    println!("cargo:rerun-if-changed=assets/prisc.ico");

    // The GNU toolchain has no rc.exe (its equivalent is windres, with a
    // different command line). Nothing else here is msvc-specific, so rather
    // than fail a gnu build, skip the icon and say so.
    if env::var("CARGO_CFG_TARGET_ENV").unwrap_or_default() != "msvc" {
        println!("cargo:warning=app icon skipped: not an msvc target");
        return;
    }

    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let icon = manifest_dir.join("assets/prisc.ico");
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
        "rc.exe not found: the Windows SDK is required to build prisc_share_region's app icon",
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
