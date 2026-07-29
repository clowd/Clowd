use std::env;
use std::fs;
use std::path::{Path, PathBuf};

/// Copies the obs-deps FFmpeg CLI binaries (small shims over the av*.dll
/// runtime that obs-express already bundles) next to vid2gif.exe.
fn main() {
    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return; // macOS bundling is handled by the release packaging, not here
    }

    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    // OUT_DIR = target/{debug,release}/build/vid2gif-<hash>/out
    let profile_dir = out_dir
        .ancestors()
        .nth(3)
        .expect("could not resolve the cargo profile dir from OUT_DIR")
        .to_path_buf();

    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let repo_root = manifest_dir
        .ancestors()
        .find(|p| p.join("obs-studio").exists())
        .expect("could not find repo root (no obs-studio dir in ancestors)")
        .to_path_buf();

    let Some(deps_bin) = find_deps_bin(&repo_root.join("obs-studio").join(".deps")) else {
        println!(
            "cargo:warning=vid2gif: obs-deps bundle not found; ffmpeg.exe/ffprobe.exe not copied"
        );
        return;
    };

    for name in ["ffmpeg.exe", "ffprobe.exe"] {
        let src = deps_bin.join(name);
        println!("cargo:rerun-if-changed={}", src.display());
        if src.exists() {
            copy_if_newer(&src, &profile_dir.join(name));
        } else {
            println!(
                "cargo:warning=vid2gif: not found in obs-deps: {}",
                src.display()
            );
        }
    }
}

fn find_deps_bin(deps_dir: &Path) -> Option<PathBuf> {
    for entry in fs::read_dir(deps_dir).ok()?.flatten() {
        let name = entry.file_name().to_string_lossy().to_string();
        if name.starts_with("obs-deps-") && !name.contains("qt6") {
            let bin = entry.path().join("bin");
            if bin.exists() {
                return Some(bin);
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
        if let Err(e) = fs::copy(src, dst) {
            println!(
                "cargo:warning=vid2gif: failed to copy {} -> {}: {e}",
                src.display(),
                dst.display()
            );
        }
    }
}
