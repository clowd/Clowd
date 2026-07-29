use std::env;
use std::fs;
use std::path::{Path, PathBuf};

/// Runtime plumbing for the FFmpeg libraries vid2gif links (via ffmpeg-sys):
///
/// - Windows: copy the FFmpeg DLLs (and their dependency DLLs) from the
///   obs-deps bundle next to the binary AND into the `deps` dir, so both
///   `vid2gif.exe` and the cargo test executables load without PATH setup.
/// - macOS: add rpaths so the dylibs resolve from the obs-deps bundle during
///   development and from `@executable_path/Frameworks` in the shipped
///   bundle (the release staging strips the absolute rpath).
fn main() {
    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let repo_root = manifest_dir
        .ancestors()
        .find(|p| p.join("obs-studio").exists())
        .expect("could not find repo root (no obs-studio dir in ancestors)")
        .to_path_buf();
    let deps_root = repo_root.join("obs-studio").join(".deps");

    match env::var("CARGO_CFG_TARGET_OS").as_deref() {
        Ok("windows") => windows(&deps_root),
        Ok("macos") => macos(&deps_root),
        _ => {}
    }
}

fn windows(deps_root: &Path) {
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    // OUT_DIR = target/{debug,release}/build/vid2gif-<hash>/out
    let profile_dir = out_dir
        .ancestors()
        .nth(3)
        .expect("could not resolve the cargo profile dir from OUT_DIR")
        .to_path_buf();

    let Some(deps_bin) = find_deps_subdir(deps_root, "bin") else {
        println!("cargo:warning=vid2gif: obs-deps bundle not found; runtime DLLs not copied");
        return;
    };

    // The four linked FFmpeg DLLs plus everything they import.
    let dlls = [
        "avcodec-61.dll",
        "avformat-61.dll",
        "avutil-59.dll",
        "avfilter-10.dll",
        "swscale-8.dll",
        "swresample-5.dll",
        "zlib.dll",
        "libx264-164.dll",
        "libcurl.dll",
        "librist.dll",
        "srt.dll",
    ];
    for name in dlls {
        let src = deps_bin.join(name);
        // A missing path forces a re-run on the next build, self-healing the
        // fresh-checkout case where the deps download races this script.
        println!("cargo:rerun-if-changed={}", src.display());
        if src.exists() {
            copy_if_newer(&src, &profile_dir.join(name));
            copy_if_newer(&src, &profile_dir.join("deps").join(name));
        } else {
            println!(
                "cargo:warning=vid2gif: not found in obs-deps: {}",
                src.display()
            );
        }
    }
}

fn macos(deps_root: &Path) {
    if let Some(deps_lib) = find_deps_subdir(deps_root, "lib") {
        println!("cargo:rustc-link-arg=-Wl,-rpath,{}", deps_lib.display());
    }
    println!("cargo:rustc-link-arg=-Wl,-rpath,@executable_path/Frameworks");
}

fn find_deps_subdir(deps_root: &Path, sub: &str) -> Option<PathBuf> {
    for entry in fs::read_dir(deps_root).ok()?.flatten() {
        let name = entry.file_name().to_string_lossy().to_string();
        if name.starts_with("obs-deps-") && !name.contains("qt6") {
            let dir = entry.path().join(sub);
            if dir.exists() {
                return Some(dir);
            }
        }
    }
    None
}

/// Copy `src` to `dst` only when it is newer or a different size, keeping
/// incremental builds cheap (same contract as the obs-express build script).
fn copy_if_newer(src: &Path, dst: &Path) {
    let should_copy = match (fs::metadata(src), fs::metadata(dst)) {
        (Ok(s), Ok(d)) => {
            let size_changed = s.len() != d.len();
            let newer = match (s.modified(), d.modified()) {
                (Ok(sm), Ok(dm)) => sm > dm,
                _ => true,
            };
            size_changed || newer
        }
        (Ok(_), Err(_)) => true,
        _ => false,
    };

    if should_copy {
        if let Some(parent) = dst.parent() {
            let _ = fs::create_dir_all(parent);
        }
        if let Err(e) = fs::copy(src, dst) {
            println!(
                "cargo:warning=vid2gif: failed to copy {} -> {}: {e}",
                src.display(),
                dst.display()
            );
        }
    }
}
