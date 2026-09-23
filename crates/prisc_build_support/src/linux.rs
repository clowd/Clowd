//! The Linux FFmpeg bundle and the RUNPATH-correct staging of shared objects.
//!
//! # One FFmpeg for everything
//!
//! libobs, every OBS plugin that touches FFmpeg (obs-ffmpeg, the linux-v4l2
//! MJPEG decoder, libobs's media helpers), obs-ffmpeg-mux and vid2gif (through
//! ffmpeg-sys) must load the *same* libavcodec. Two copies in one process would
//! each carry their own codec registry and allocator state, and a distro
//! FFmpeg of a different major version would not even be ABI compatible with
//! headers we generated bindings from. So the system libav* is never used:
//! every consumer links the pinned BtbN build below, which is FFmpeg 7.1 — the
//! same ABI line (libavcodec 61) the Windows and macOS obs-deps ship, so the
//! `avcodec_version()` assertion in ffmpeg-sys holds on every OS.
//!
//! BtbN's `gpl-shared` builds link all external codec libraries statically
//! into the eight FFmpeg `.so` files and depend only on glibc, which is what
//! makes them relocatable into our self-contained runtime directory. Their
//! `.pc` files are `${pcfiledir}`-relative, so pkg-config works from wherever
//! the archive is extracted.
//!
//! # RUNPATH
//!
//! The archive's libraries carry no RUNPATH at all. That is fine when they sit
//! in `/usr/lib`, but not here: an executable's DT_RUNPATH only applies to its
//! *direct* dependencies, so `libavfilter.so.10` asking for
//! `libpostproc.so.58` would be looked up in the system paths and fail (or,
//! worse, find a distro copy). [`ensure_ffmpeg`] therefore rewrites every
//! library's RUNPATH to `$ORIGIN` once, at extraction time, so the siblings
//! resolve each other wherever the set is copied to — the Linux analogue of
//! the `@loader_path` rpath obs-express's macOS staging adds.

use std::fs::{self, File};
use std::path::{Path, PathBuf};
use std::process::Command;

/// Where the FFmpeg bundle comes from, for one target architecture.
///
/// The URL is the single place to change when the archive is mirrored to our
/// own release: the SHA-256 pins the *content*, so a mirror of the identical
/// file needs no other edit.
pub struct FfmpegAsset {
    pub url: &'static str,
    /// SHA-256 of the archive at `url`, lowercase hex.
    pub sha256: &'static str,
    /// The single top-level directory the archive extracts to. It is also the
    /// directory name under `obs-studio/.deps`, so the version is visible in
    /// the tree and a pin bump extracts side by side instead of over the top.
    pub dir_name: &'static str,
}

/// BtbN FFmpeg-Builds, release `autobuild-2026-07-31-14-10`, FFmpeg n7.1.5,
/// GPL, shared libraries.
pub const FFMPEG_LINUX_X64: FfmpegAsset = FfmpegAsset {
    url: "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-07-31-14-10/ffmpeg-n7.1.5-12-g1fdbca85aa-linux64-gpl-shared-7.1.tar.xz",
    sha256: "df7e15a2d2fe0ee15ae36e0e3b83596dd088ee0a0767874241d9a54380df0add",
    dir_name: "ffmpeg-n7.1.5-12-g1fdbca85aa-linux64-gpl-shared-7.1",
};

/// The asset for a Rust target arch (`CARGO_CFG_TARGET_ARCH`).
///
/// Only x86_64 is wired up. aarch64 needs nothing structural — BtbN publishes
/// a matching `linuxarm64-gpl-shared-7.1` archive in the same release — just a
/// second constant with its own verified hash, and the x264 configure in
/// obs-sys already builds natively for whatever the host is.
pub fn ffmpeg_asset(target_arch: &str) -> &'static FfmpegAsset {
    match target_arch {
        "x86_64" => &FFMPEG_LINUX_X64,
        other => panic!(
            "obs-express supports Linux on x86_64 only for now (target arch `{other}`). \
             To add it, pin the BtbN linuxarm64-gpl-shared-7.1 archive (URL + SHA-256) in \
             crates/build-support/src/linux.rs::ffmpeg_asset."
        ),
    }
}

/// An extracted, verified and RUNPATH-patched FFmpeg bundle.
pub struct FfmpegBundle {
    pub root: PathBuf,
}

impl FfmpegBundle {
    pub fn include(&self) -> PathBuf {
        self.root.join("include")
    }
    pub fn lib(&self) -> PathBuf {
        self.root.join("lib")
    }
    pub fn bin(&self) -> PathBuf {
        self.root.join("bin")
    }
    pub fn pkgconfig(&self) -> PathBuf {
        self.lib().join("pkgconfig")
    }
}

/// Name of the marker written inside the bundle once it is complete. It holds
/// the archive hash, so a bundle extracted from a different pin (same dir name,
/// re-uploaded archive) is redone rather than trusted.
const COMPLETE_MARKER: &str = ".obs-express-complete";

/// Makes sure the pinned FFmpeg bundle for `target_arch` exists under
/// `deps_dir` (the `obs-studio/.deps` directory), downloading, verifying and
/// extracting it if needed, and returns it.
///
/// Both obs-sys and ffmpeg-sys call this, and cargo runs their build scripts
/// concurrently, so the whole check-or-fetch is serialised on an exclusive
/// file lock: the second caller blocks until the first has finished and then
/// sees the completion marker. Extraction goes to a scratch directory that is
/// renamed into place only after the hash check and the RUNPATH patch, so an
/// interrupted build never leaves a half-populated bundle that a later run
/// would mistake for a good one.
pub fn ensure_ffmpeg(deps_dir: &Path, target_arch: &str) -> FfmpegBundle {
    let asset = ffmpeg_asset(target_arch);
    fs::create_dir_all(deps_dir)
        .unwrap_or_else(|e| panic!("cannot create {}: {e}", deps_dir.display()));

    let root = deps_dir.join(asset.dir_name);
    let bundle = FfmpegBundle { root: root.clone() };
    let marker = root.join(COMPLETE_MARKER);
    let is_complete = || fs::read_to_string(&marker).is_ok_and(|s| s.trim() == asset.sha256);

    // Fast path without the lock: incremental builds never contend.
    if is_complete() {
        return bundle;
    }

    let lock_path = deps_dir.join(".ffmpeg-bundle.lock");
    let lock = File::create(&lock_path)
        .unwrap_or_else(|e| panic!("cannot create {}: {e}", lock_path.display()));
    lock.lock()
        .unwrap_or_else(|e| panic!("cannot lock {}: {e}", lock_path.display()));

    // Re-check under the lock: another build script may have just finished.
    if is_complete() {
        return bundle;
    }

    for tool in ["curl", "tar", "sha256sum", "patchelf"] {
        require_tool(tool);
    }

    let archive = deps_dir.join(format!("{}.tar.xz.part", asset.dir_name));
    eprintln!("obs-build-support: downloading {}", asset.url);
    run(Command::new("curl")
        .args(["-fL", "--retry", "3", "--retry-delay", "2", "-sS", "-o"])
        .arg(&archive)
        .arg(asset.url));

    let actual = sha256_of(&archive);
    if actual != asset.sha256 {
        let _ = fs::remove_file(&archive);
        panic!(
            "FFmpeg bundle hash mismatch for {}\n  expected {}\n  actual   {actual}\n\
             The download was deleted. If the archive was deliberately replaced, update \
             FfmpegAsset::sha256 in crates/build-support/src/linux.rs; otherwise the file \
             at that URL has changed and must not be trusted.",
            asset.url, asset.sha256
        );
    }

    let scratch = deps_dir.join(format!(".{}.extract", asset.dir_name));
    let _ = fs::remove_dir_all(&scratch);
    fs::create_dir_all(&scratch).unwrap();
    run(Command::new("tar")
        .arg("-xJf")
        .arg(&archive)
        .arg("-C")
        .arg(&scratch));
    let extracted = scratch.join(asset.dir_name);
    assert!(
        extracted.join("include/libavcodec/avcodec.h").exists(),
        "FFmpeg archive did not extract to the expected {}/ layout",
        asset.dir_name
    );

    // See the module docs: siblings must resolve each other via $ORIGIN.
    for entry in fs::read_dir(extracted.join("lib")).unwrap().flatten() {
        let path = entry.path();
        let is_real_so = fs::symlink_metadata(&path).is_ok_and(|m| m.file_type().is_file())
            && is_shared_object_name(&entry.file_name().to_string_lossy());
        if is_real_so {
            set_runpath(&path, "$ORIGIN");
        }
    }
    // The CLI tools are never shipped, but keep them runnable for debugging.
    if let Ok(entries) = fs::read_dir(extracted.join("bin")) {
        for entry in entries.flatten() {
            set_runpath(&entry.path(), "$ORIGIN/../lib");
        }
    }

    let _ = fs::remove_dir_all(&root);
    fs::rename(&extracted, &root).unwrap_or_else(|e| {
        panic!(
            "cannot move {} into place at {}: {e}",
            extracted.display(),
            root.display()
        )
    });
    fs::write(&marker, asset.sha256).unwrap();
    let _ = fs::remove_dir_all(&scratch);
    let _ = fs::remove_file(&archive);
    bundle
}

/// `libfoo.so` or `libfoo.so.1.2.3`.
fn is_shared_object_name(name: &str) -> bool {
    name.starts_with("lib") && (name.ends_with(".so") || name.contains(".so."))
}

/// The SONAME-named entries of a library directory, resolved to their real
/// files: `(libavcodec.so.61, <dir>/libavcodec.so.61.19.101)`.
///
/// The runtime loader asks for exactly the SONAME, so that is the only name a
/// shipped directory needs. The development `libfoo.so` link and the fully
/// versioned real-file name are both dropped: copying the real file under its
/// SONAME yields one plain file per library and no symlinks, which survive any
/// archive format and cannot dangle.
pub fn soname_entries(lib_dir: &Path) -> Vec<(String, PathBuf)> {
    let mut out = Vec::new();
    let Ok(entries) = fs::read_dir(lib_dir) else {
        return out;
    };
    for entry in entries.flatten() {
        let name = entry.file_name().to_string_lossy().into_owned();
        // libfoo.so.<major>, and nothing after the major number.
        let Some((stem, major)) = name.rsplit_once(".so.") else {
            continue;
        };
        if !stem.starts_with("lib")
            || major.is_empty()
            || !major.bytes().all(|b| b.is_ascii_digit())
        {
            continue;
        }
        if let Ok(real) = fs::canonicalize(entry.path()) {
            out.push((name, real));
        }
    }
    out.sort();
    out
}

/// Copies every FFmpeg library of `bundle` into `dst_dir` under its SONAME
/// (see [`soname_entries`]). The copies keep the `$ORIGIN` RUNPATH
/// [`ensure_ffmpeg`] gave them, so they resolve each other in `dst_dir`.
pub fn stage_ffmpeg_runtime(bundle: &FfmpegBundle, dst_dir: &Path) {
    let entries = soname_entries(&bundle.lib());
    assert!(
        !entries.is_empty(),
        "no FFmpeg libraries found in {}",
        bundle.lib().display()
    );
    for (name, real) in entries {
        copy_if_newer(&real, &dst_dir.join(name));
    }
}

/// Copies `src` to `dst` when `dst` is missing, older, or a different size.
///
/// The copy lands under a temporary name and is renamed over `dst`, because
/// several build scripts stage the same FFmpeg files into the shared profile
/// directory concurrently (vid2gif and obs-express both do, as on Windows): a
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

/// Copies a shared object or executable into a staging directory and sets its
/// RUNPATH to `runpath` (e.g. `$ORIGIN`, or `$ORIGIN/..` for plugins).
///
/// The OBS build tree links everything with absolute RUNPATHs into that tree,
/// which is right for running in place but wrong for a relocatable directory;
/// patching the staged copy (not the original) keeps both correct. Up-to-date
/// is judged by mtime alone — the patch changes the size, so a size check
/// would recopy every build — and the patch runs on the temporary copy before
/// the rename, so an unpatched file is never visible under the real name.
pub fn stage_with_runpath(src: &Path, dst: &Path, runpath: &str) {
    let up_to_date = match (fs::metadata(src), fs::symlink_metadata(dst)) {
        (Ok(s), Ok(d)) if d.file_type().is_file() => match (s.modified(), d.modified()) {
            (Ok(sm), Ok(dm)) => dm >= sm,
            _ => false,
        },
        _ => false,
    };
    if !up_to_date {
        atomic_copy(src, dst, |tmp| set_runpath_checked(tmp, runpath));
    }
}

/// Copy `src` to a temporary sibling of `dst`, run `finish` on it, and rename
/// it into place if `finish` succeeds.
fn atomic_copy(src: &Path, dst: &Path, finish: impl FnOnce(&Path) -> bool) {
    if let Some(parent) = dst.parent() {
        let _ = fs::create_dir_all(parent);
    }
    let tmp = dst.with_file_name(format!(
        ".{}.{}.tmp",
        dst.file_name().unwrap().to_string_lossy(),
        std::process::id()
    ));
    let ok = fs::copy(src, &tmp).is_ok() && finish(&tmp) && fs::rename(&tmp, dst).is_ok();
    if !ok {
        let _ = fs::remove_file(&tmp);
        println!(
            "cargo:warning=failed to stage {} -> {}",
            src.display(),
            dst.display()
        );
    }
}

/// `patchelf --set-rpath`, panicking on failure (used where a wrong RUNPATH
/// would ship).
pub fn set_runpath(file: &Path, runpath: &str) {
    assert!(
        set_runpath_checked(file, runpath),
        "patchelf --set-rpath {runpath} {} failed",
        file.display()
    );
}

fn set_runpath_checked(file: &Path, runpath: &str) -> bool {
    match Command::new("patchelf")
        .arg("--set-rpath")
        .arg(runpath)
        .arg(file)
        .output()
    {
        Ok(out) if out.status.success() => true,
        Ok(out) => {
            println!(
                "cargo:warning=patchelf --set-rpath {runpath} {}: {}",
                file.display(),
                String::from_utf8_lossy(&out.stderr).trim()
            );
            false
        }
        Err(e) => {
            println!("cargo:warning=failed to run patchelf: {e}");
            false
        }
    }
}

/// Panics with an install hint when a build-time tool is missing. A missing
/// tool must stop the build here, with a name attached, rather than surface
/// later as a confusing CMake or loader failure.
pub fn require_tool(tool: &str) {
    let found = Command::new(tool)
        .arg("--version")
        .output()
        .is_ok_and(|o| o.status.success());
    if !found {
        let package = match tool {
            "sha256sum" | "tar" => "coreutils / tar",
            "ninja" => "ninja-build",
            other => other,
        };
        panic!(
            "`{tool}` is required to build obs-express on Linux but was not found on PATH \
             (Debian/Ubuntu package: {package}). See the apt list in the Linux CI job."
        );
    }
}

fn sha256_of(file: &Path) -> String {
    let out = Command::new("sha256sum")
        .arg(file)
        .output()
        .expect("failed to run sha256sum");
    assert!(out.status.success(), "sha256sum {} failed", file.display());
    String::from_utf8_lossy(&out.stdout)
        .split_whitespace()
        .next()
        .unwrap_or_default()
        .to_ascii_lowercase()
}

/// Runs a command, panicking with its command line if it fails.
pub fn run(cmd: &mut Command) {
    let status = cmd
        .status()
        .unwrap_or_else(|e| panic!("failed to start {cmd:?}: {e}"));
    assert!(status.success(), "{cmd:?} failed with {status}");
}
