use std::env;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

/// Links the FFmpeg libraries from the obs-deps bundle and generates bindings
/// from its headers. Import libraries (Windows) and dylibs (macOS) both live
/// in the bundle's `lib` dir; headers in `include`.
fn main() {
    let target_os = env::var("CARGO_CFG_TARGET_OS").unwrap_or_default();
    if target_os != "windows" && target_os != "macos" {
        panic!("ffmpeg-sys: unsupported CARGO_CFG_TARGET_OS `{target_os}`");
    }

    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let repo_root = manifest_dir.parent().unwrap().parent().unwrap();
    let deps = wait_for_deps(&repo_root.join("obs-studio").join(".deps"));
    let include = deps.join("include");
    let lib = deps.join("lib");

    println!("cargo:rerun-if-changed=wrapper.h");
    println!("cargo:rustc-link-search=native={}", lib.display());
    for name in ["avformat", "avcodec", "avfilter", "avutil"] {
        println!("cargo:rustc-link-lib=dylib={name}");
    }
    if target_os == "macos" {
        // For this crate's own test binaries. (Downstream binaries emit their
        // own rpaths — link-args do not propagate across crates.)
        println!("cargo:rustc-link-arg=-Wl,-rpath,{}", lib.display());
        println!("cargo:rustc-link-arg=-Wl,-rpath,@executable_path/Frameworks");
    }

    generate_bindings(&manifest_dir, &include);
}

/// Locates the non-qt6 obs-deps bundle containing the FFmpeg headers.
///
/// The bundle is downloaded by the OBS CMake configure that obs-sys runs, and
/// cargo may schedule this build script before (or concurrently with) that
/// download on a fresh checkout — so poll briefly instead of failing
/// immediately. Incremental builds hit the happy path instantly.
fn wait_for_deps(deps_dir: &Path) -> PathBuf {
    let deadline = Instant::now() + Duration::from_secs(600);
    let mut announced = false;
    loop {
        if let Some(found) = find_deps(deps_dir) {
            return found;
        }
        if Instant::now() >= deadline {
            panic!(
                "ffmpeg-sys: obs-deps bundle with FFmpeg headers not found under {} \
                 after waiting 10 minutes. The bundle is downloaded by the OBS build \
                 (obs-sys); run a full workspace `cargo build` at least once before \
                 building vid2gif/ffmpeg-sys on their own.",
                deps_dir.display()
            );
        }
        if !announced {
            eprintln!(
                "ffmpeg-sys: waiting for the obs-deps download (obs-sys CMake configure) \
                 to populate {}...",
                deps_dir.display()
            );
            announced = true;
        }
        std::thread::sleep(Duration::from_secs(5));
    }
}

fn find_deps(deps_dir: &Path) -> Option<PathBuf> {
    for entry in std::fs::read_dir(deps_dir).ok()?.flatten() {
        let name = entry.file_name().to_string_lossy().to_string();
        if name.starts_with("obs-deps-") && !name.contains("qt6") {
            let dir = entry.path();
            if dir.join("include").join("libavcodec").exists() && dir.join("lib").exists() {
                return Some(dir);
            }
        }
    }
    None
}

fn generate_bindings(manifest_dir: &Path, include: &Path) {
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());

    let bindings = bindgen::Builder::default()
        .header(manifest_dir.join("wrapper.h").to_str().unwrap())
        .clang_arg(format!("-I{}", include.display()))
        .allowlist_function("av_.*")
        .allowlist_function("avformat_.*")
        .allowlist_function("avcodec_.*")
        .allowlist_function("avfilter_.*")
        .allowlist_function("avio_.*")
        .allowlist_type("AV.*")
        .allowlist_var("AV_.*")
        .allowlist_var("AVFMT_.*")
        .allowlist_var("AVIO_.*")
        .allowlist_var("FF_.*")
        .allowlist_var("LIBAV.*")
        // Defined by hand in lib.rs: these expand through casts/function-like
        // macros bindgen's evaluator cannot fold, and hand definitions must
        // not collide if a future bindgen learns to.
        .blocklist_item("AV_NOPTS_VALUE")
        .blocklist_item("AVERROR_.*")
        .derive_default(true)
        .generate()
        .expect("ffmpeg-sys: failed to generate bindings");

    bindings
        .write_to_file(out_dir.join("bindings.rs"))
        .expect("ffmpeg-sys: failed to write bindings");
}
