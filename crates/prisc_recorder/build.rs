use std::env;
use std::fs;
use std::path::{Path, PathBuf};

fn main() {
    // Branch on the target OS at runtime of the build script (not #[cfg]).
    let target_os = env::var("CARGO_CFG_TARGET_OS").unwrap_or_default();
    match target_os.as_str() {
        "macos" => build_macos(),
        "windows" => build_windows(),
        _ => {}
    }
}

// ---------------------------------------------------------------------------
// macOS (unchanged behavior — framework rpaths + obs-ffmpeg-mux patch)
// ---------------------------------------------------------------------------

fn build_macos() {
    println!("cargo:rustc-link-lib=framework=CoreGraphics");
    println!("cargo:rustc-link-lib=framework=CoreFoundation");
    println!("cargo:rustc-link-lib=framework=ColorSync");

    let build_dir = env::var("DEP_OBS_OBS_BUILD_DIR").expect("DEP_OBS_OBS_BUILD_DIR not set");
    let config = env::var("DEP_OBS_OBS_BUILD_CONFIG").expect("DEP_OBS_OBS_BUILD_CONFIG not set");
    let framework_path =
        env::var("DEP_OBS_FRAMEWORK_SEARCH").expect("DEP_OBS_FRAMEWORK_SEARCH not set");
    let out_dir = env::var("OUT_DIR").unwrap();

    let obs_build = PathBuf::from(&build_dir);
    let repo_root = Path::new(&out_dir)
        .ancestors()
        .find(|p| p.join("obs-studio").exists())
        .expect("Could not find repo root")
        .to_path_buf();
    let deps_lib = find_obs_deps_lib(&repo_root);

    // Bake the plugin path into the binary so no env var is needed at runtime
    println!(
        "cargo:rustc-env=OBS_PLUGIN_DIR={}/plugins",
        obs_build.display()
    );

    // Set rpaths for libobs.framework, libobs-metal.dylib, and FFmpeg dylibs
    println!("cargo:rustc-link-arg=-Wl,-rpath,{framework_path}");
    println!(
        "cargo:rustc-link-arg=-Wl,-rpath,{}/libobs-metal/{config}",
        obs_build.display()
    );
    println!(
        "cargo:rustc-link-arg=-Wl,-rpath,{}/libobs-opengl/{config}",
        obs_build.display()
    );
    if let Some(ref lib) = deps_lib {
        println!("cargo:rustc-link-arg=-Wl,-rpath,{}", lib.display());
    }

    // Copy and patch obs-ffmpeg-mux helper binary
    let target_dir = Path::new(&out_dir)
        .ancestors()
        .find(|p| p.ends_with("debug") || p.ends_with("release"))
        .map(|p| p.to_path_buf());

    if let Some(target) = target_dir {
        let mux_src = obs_build
            .join("plugins/obs-ffmpeg/ffmpeg-mux")
            .join(&config)
            .join("obs-ffmpeg-mux");
        let mux_dst = target.join("obs-ffmpeg-mux");

        if mux_src.exists() {
            let _ = std::fs::copy(&mux_src, &mux_dst);
            let mux_path = mux_dst.to_str().unwrap();

            run(
                "install_name_tool",
                &["-add_rpath", &framework_path, mux_path],
            );
            if let Some(ref lib) = deps_lib {
                run(
                    "install_name_tool",
                    &["-add_rpath", lib.to_str().unwrap(), mux_path],
                );
            }
            run("codesign", &["--force", "--sign", "-", mux_path]);
        }
    }
}

fn run(cmd: &str, args: &[&str]) {
    let _ = std::process::Command::new(cmd).args(args).output();
}

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

// ---------------------------------------------------------------------------
// Windows (assemble a self-contained runtime next to obs-express.exe)
// ---------------------------------------------------------------------------

fn build_windows() {
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    // OUT_DIR = target/{debug,release}/build/obs-express-<hash>/out
    // ancestors: [0]=out [1]=obs-express-<hash> [2]=build [3]={debug,release}
    let profile_dir = out_dir
        .ancestors()
        .nth(3)
        .expect("could not resolve the cargo profile dir from OUT_DIR")
        .to_path_buf();

    let obs_build =
        PathBuf::from(env::var("DEP_OBS_OBS_BUILD_DIR").expect("DEP_OBS_OBS_BUILD_DIR not set"));
    let config =
        env::var("DEP_OBS_OBS_BUILD_CONFIG").unwrap_or_else(|_| "RelWithDebInfo".to_string());
    let deps_bin = PathBuf::from(env::var("DEP_OBS_DEPS_BIN").expect("DEP_OBS_DEPS_BIN not set"));

    // Re-run the runtime copy after a manual OBS rebuild recreates the marker;
    // without this the profile dir silently keeps stale DLLs.
    println!(
        "cargo:rerun-if-changed={}",
        obs_build.join(".build_complete").display()
    );

    let rundir = obs_build.join("rundir").join(&config);

    // bin/64bit/* (dll + exe) -> profile dir. Gives obs.dll, w32-pthreads.dll,
    // libobs-d3d11.dll, libobs-winrt.dll, obs-ffmpeg-mux.exe and the three
    // obs-*-test.exe probes — all must sit next to obs-express.exe.
    copy_dir_filtered(
        &rundir.join("bin").join("64bit"),
        &profile_dir,
        &["dll", "exe"],
    );

    // obs-plugins/64bit/* -> profile/obs-plugins/64bit/
    copy_dir_all(
        &rundir.join("obs-plugins").join("64bit"),
        &profile_dir.join("obs-plugins").join("64bit"),
    );

    // data/* -> profile/data/ (contains libobs/ effects required by obs_reset_video)
    copy_dir_all(&rundir.join("data"), &profile_dir.join("data"));

    // Third-party runtime DLLs the (unbuilt) frontend target would have bundled.
    let deps_dlls = [
        "avcodec-61.dll",
        "avformat-61.dll",
        "avutil-59.dll",
        "avfilter-10.dll",
        "avdevice-61.dll",
        "swscale-8.dll",
        "swresample-5.dll",
        "zlib.dll",
        "libx264-164.dll",
        "libcurl.dll",
        // avformat-61.dll imports these two; without them obs-ffmpeg fails to
        // load with STATUS_DLL_NOT_FOUND.
        "librist.dll",
        "srt.dll",
    ];
    for name in deps_dlls {
        let src = deps_bin.join(name);
        if src.exists() {
            copy_if_newer(&src, &profile_dir.join(name));
        } else {
            println!(
                "cargo:warning=obs-express: deps DLL not found: {}",
                src.display()
            );
        }
    }
}

/// Copy `src` to `dst` only when it is newer or a different size (the runtime
/// tree is ~150 MB — incremental builds must stay cheap).
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
        (Ok(_), Err(_)) => true, // dst missing
        _ => false,              // src missing — nothing to do
    };

    if should_copy {
        if let Some(parent) = dst.parent() {
            let _ = fs::create_dir_all(parent);
        }
        if let Err(e) = fs::copy(src, dst) {
            println!(
                "cargo:warning=obs-express: failed to copy {} -> {}: {e}",
                src.display(),
                dst.display()
            );
        }
    }
}

/// Copy only top-level files in `src_dir` whose extension matches `exts`.
fn copy_dir_filtered(src_dir: &Path, dst_dir: &Path, exts: &[&str]) {
    let entries = match fs::read_dir(src_dir) {
        Ok(e) => e,
        Err(_) => {
            println!(
                "cargo:warning=obs-express: missing dir {}",
                src_dir.display()
            );
            return;
        }
    };
    let _ = fs::create_dir_all(dst_dir);

    for entry in entries.flatten() {
        let path = entry.path();
        if !path.is_file() {
            continue;
        }
        let ext_ok = path
            .extension()
            .and_then(|e| e.to_str())
            .map(|e| exts.iter().any(|x| x.eq_ignore_ascii_case(e)))
            .unwrap_or(false);
        if ext_ok {
            copy_if_newer(&path, &dst_dir.join(entry.file_name()));
        }
    }
}

/// Recursively copy `src_dir` into `dst_dir` (incremental via `copy_if_newer`).
fn copy_dir_all(src_dir: &Path, dst_dir: &Path) {
    let entries = match fs::read_dir(src_dir) {
        Ok(e) => e,
        Err(_) => {
            println!(
                "cargo:warning=obs-express: missing dir {}",
                src_dir.display()
            );
            return;
        }
    };
    let _ = fs::create_dir_all(dst_dir);

    for entry in entries.flatten() {
        let path = entry.path();
        let dst = dst_dir.join(entry.file_name());
        if path.is_dir() {
            copy_dir_all(&path, &dst);
        } else {
            copy_if_newer(&path, &dst);
        }
    }
}
