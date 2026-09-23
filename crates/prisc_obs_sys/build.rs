use std::env;
use std::path::{Path, PathBuf};
use std::process::Command;

fn main() {
    // Build scripts must branch on the *target* OS, not the host cfg — the
    // build script itself is compiled for the host, so `#[cfg(target_os)]`
    // would be wrong when cross-compiling.
    let target_os = env::var("CARGO_CFG_TARGET_OS").unwrap_or_default();
    match target_os.as_str() {
        "macos" => build_macos(),
        "windows" => build_windows(),
        "linux" => build_linux(),
        other => panic!("obs-sys: unsupported CARGO_CFG_TARGET_OS `{other}`"),
    }
}

/// The version stamped into the OBS build.
///
/// OBS's `versionconfig.cmake` derives its version from `git describe --tags`.
/// That works in a full local clone, but CI checkouts of the submodule have no
/// tags, so `git describe --always` falls back to the short commit hash (e.g.
/// `fb4d98b`). That is not a valid `MAJOR.MINOR.PATCH`, so OBS's version parsing
/// (and the top-level `project(... VERSION ...)`) fails the configure step.
///
/// Passing `-DOBS_VERSION_OVERRIDE` sidesteps `git describe` entirely and makes
/// the build reproducible regardless of tag availability. The default matches
/// the pinned `obs-studio` submodule tag; override via the `OBS_VERSION_OVERRIDE`
/// env var if the submodule is bumped or a custom stamp is desired.
fn obs_version_override() -> String {
    println!("cargo:rerun-if-env-changed=OBS_VERSION_OVERRIDE");
    env::var("OBS_VERSION_OVERRIDE").unwrap_or_else(|_| "32.1.2".to_string())
}

/// Rust's target arch (`CARGO_CFG_TARGET_ARCH`) drives the native OBS build's
/// architecture, so a single `cargo build --target <triple>` yields a matching
/// native or cross build. `x86_64` and `aarch64` are the only architectures
/// obs-express ships (Windows x64/ARM64, macOS x86_64/arm64, Linux x86_64).
fn target_arch() -> String {
    env::var("CARGO_CFG_TARGET_ARCH").unwrap_or_default()
}

/// Visual Studio generator platform (`-A`) for the current target arch.
fn win_vs_platform() -> &'static str {
    match target_arch().as_str() {
        "x86_64" => "x64",
        "aarch64" => "ARM64",
        other => panic!("obs-sys: unsupported Windows target arch `{other}`"),
    }
}

/// `CMAKE_OSX_ARCHITECTURES` value for the current target arch. Setting it
/// explicitly (rather than defaulting to the host) is what lets the arm64 CI
/// runner cross-build the x86_64 slice — OBS's macOS prebuilt deps are
/// universal, so both arches link.
fn mac_osx_arch() -> &'static str {
    match target_arch().as_str() {
        "x86_64" => "x86_64",
        "aarch64" => "arm64",
        other => panic!("obs-sys: unsupported macOS target arch `{other}`"),
    }
}

// ---------------------------------------------------------------------------
// macOS (unchanged behavior — Xcode generator, framework link, source watch)
// ---------------------------------------------------------------------------

fn build_macos() {
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let repo_root = manifest_dir.parent().unwrap().parent().unwrap();
    let obs_src = repo_root.join("obs-studio");
    let obs_build = out_dir.join("obs-build");

    println!("cargo:rerun-if-changed=wrapper.h");
    println!(
        "cargo:rerun-if-changed={}",
        obs_src.join("libobs").display()
    );

    let config = "RelWithDebInfo";

    mac_cmake_configure(&obs_src, &obs_build);
    mac_cmake_build(&obs_build, config);
    mac_emit_link_directives(&obs_src, &obs_build, config);
    generate_bindings(
        &manifest_dir,
        &obs_src,
        &obs_build,
        find_obs_deps_include(&obs_src),
    );
}

fn mac_cmake_configure(obs_src: &Path, obs_build: &Path) {
    if obs_build.join("CMakeCache.txt").exists() {
        return;
    }

    let output = Command::new("cmake")
        .arg("-S")
        .arg(obs_src)
        .arg("-B")
        .arg(obs_build)
        .arg("-G")
        .arg("Xcode")
        .arg(format!("-DOBS_VERSION_OVERRIDE={}", obs_version_override()))
        // OBS builds itself with -Werror (via CMAKE_COMPILE_WARNING_AS_ERROR,
        // which it defaults ON). Newer toolchains — e.g. the Xcode 26 clang on
        // CI — enable warnings OBS 32.1.2 never saw (-Wimplicit-int-float-
        // conversion), turning them into hard build failures. OBS is a vendored
        // dependency, so opt out of warnings-as-errors for its tree.
        .arg("-DCMAKE_COMPILE_WARNING_AS_ERROR=OFF")
        .arg(format!("-DCMAKE_OSX_ARCHITECTURES={}", mac_osx_arch()))
        .arg("-DCMAKE_OSX_DEPLOYMENT_TARGET=12.0")
        .arg("-DENABLE_UI=OFF")
        .arg("-DENABLE_SCRIPTING=OFF")
        .arg("-DENABLE_BROWSER=OFF")
        .arg("-DENABLE_WEBSOCKET=OFF")
        .arg("-DENABLE_AJA=OFF")
        .arg("-DENABLE_NEW_MPEGTS_OUTPUT=OFF")
        .arg("-DENABLE_VIRTUALCAM=ON")
        // These UUIDs are needed so the camera-extension CMakeLists.txt reaches
        // its enable_language(Swift) call, which is required for libobs-metal.
        .arg("-DVIRTUALCAM_DEVICE_UUID=7626645E-4425-469E-9D8B-97E0FA59AC75")
        .arg("-DVIRTUALCAM_SOURCE_UUID=A8D7B8AA-65AD-4D21-9C42-66480DBFA8E1")
        .arg("-DVIRTUALCAM_SINK_UUID=A3F16177-7044-4DD8-B900-72E2419F7A9A")
        .arg("-Wno-dev")
        .output()
        .expect("Failed to run cmake configure");

    let stdout = String::from_utf8_lossy(&output.stdout);
    let stderr = String::from_utf8_lossy(&output.stderr);

    for line in stdout.lines() {
        println!("{line}");
    }
    for line in stderr.lines() {
        eprintln!("{line}");
    }

    let xcodeproj = obs_build.join("obs-studio.xcodeproj");
    assert!(
        xcodeproj.exists(),
        "cmake configure failed — no Xcode project generated.\nstderr: {stderr}"
    );
}

fn mac_cmake_build(obs_build: &Path, config: &str) {
    let targets = [
        "libobs",
        "libobs-metal",
        "libobs-opengl",
        "mac-capture",
        "mac-videotoolbox",
        "obs-ffmpeg",
        "obs-ffmpeg-mux",
        "obs-x264",
        "coreaudio-encoder",
        // image_source + color_filter: the mouse click tracker (--tracker).
        "image-source",
        "obs-filters",
        // mp4_output: the hybrid MP4 muxer behind --multi-track.
        "obs-outputs",
        // macos-avcapture: webcam capture (--webcam / --list-cameras).
        "mac-avcapture",
    ];

    let marker = obs_build.join(".build_complete");
    if build_is_current(&marker, &targets) {
        return;
    }

    let mut cmd = Command::new("cmake");
    cmd.arg("--build").arg(obs_build);
    for target in &targets {
        cmd.arg("--target").arg(target);
    }
    cmd.arg("--config")
        .arg(config)
        .arg("--")
        .arg("-parallelizeTargets");

    let status = cmd.status().expect("Failed to run cmake build");
    assert!(status.success(), "cmake build failed");

    write_build_marker(&marker, &targets);
}

/// The build marker records the target list it was written for, so adding a
/// target rebuilds even when a previous (or CI-cached) tree is already marked
/// complete. cmake itself makes the rebuild incremental — only the new targets
/// compile.
fn build_is_current(marker: &Path, targets: &[&str]) -> bool {
    std::fs::read_to_string(marker).is_ok_and(|s| s == targets.join(" "))
}

fn write_build_marker(marker: &Path, targets: &[&str]) {
    std::fs::write(marker, targets.join(" ")).expect("Failed to write build marker");
}

fn mac_emit_link_directives(obs_src: &Path, obs_build: &Path, config: &str) {
    let framework_search = obs_build.join("libobs").join(config);
    assert!(
        framework_search.join("libobs.framework").exists(),
        "libobs.framework not found at {}",
        framework_search.display()
    );

    println!(
        "cargo:rustc-link-search=framework={}",
        framework_search.display()
    );
    println!("cargo:rustc-link-lib=framework=libobs");

    // rpaths so this crate's own test harness can launch. `cargo:rustc-link-arg`
    // is package-scoped: it applies to obs-sys's tests/benches but does NOT
    // propagate to dependents, so every executable-producing consumer repeats
    // these in its own build script (obs-express, obs-platform, obs,
    // clowd_share_region). Even an empty harness dies in dyld without them:
    // libobs's install name and its FFmpeg/x264 references are all @rpath.
    println!(
        "cargo:rustc-link-arg=-Wl,-rpath,{}",
        framework_search.display()
    );
    if let Some(deps_lib) = mac_find_obs_deps_lib(obs_src) {
        println!("cargo:rustc-link-arg=-Wl,-rpath,{}", deps_lib.display());
    }

    // Export paths so downstream crates can locate frameworks and plugins at runtime
    println!("cargo:framework_search={}", framework_search.display());
    println!("cargo:obs_build_dir={}", obs_build.display());
    println!("cargo:obs_build_config={config}");
}

/// The prebuilt obs-deps `lib` dir (FFmpeg et al.) — same probe as the
/// downstream build scripts, but rooted at the obs-studio checkout we already
/// hold rather than walking up from OUT_DIR.
fn mac_find_obs_deps_lib(obs_src: &Path) -> Option<PathBuf> {
    let deps_dir = obs_src.join(".deps");
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
// Windows (Visual Studio generator, obs.lib import link, self-contained deps)
// ---------------------------------------------------------------------------

fn build_windows() {
    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let repo_root = manifest_dir.parent().unwrap().parent().unwrap();
    let obs_src = repo_root.join("obs-studio");
    let build_dir = win_build_dir();
    let config = "RelWithDebInfo";

    // The Windows branch does NOT watch the obs-studio source tree — idempotency
    // is provided by the CMakeCache.txt / .build_complete markers instead.
    println!("cargo:rerun-if-changed=wrapper.h");
    println!("cargo:rerun-if-env-changed=OBS_BUILD_DIR");
    println!("cargo:rerun-if-env-changed=CARGO_TARGET_DIR");

    let cmake = find_cmake();
    win_cmake_configure(&cmake, &obs_src, &build_dir);
    win_cmake_build(&cmake, &build_dir, config);
    win_emit_link_directives(&build_dir, config);
    generate_bindings(
        &manifest_dir,
        &obs_src,
        &build_dir,
        find_obs_deps_include(&obs_src),
    );
    win_emit_exports(&obs_src, &build_dir, config);
}

/// MAX_PATH-safe CMake build dir: never under the deep cargo OUT_DIR (MSB3491).
/// `OBS_BUILD_DIR` override, else `<workspace_target>/obs-<arch>` (obs-x64 /
/// obs-arm64) where the target dir is `CARGO_TARGET_DIR` if set, else the
/// OUT_DIR ancestor named `target`. The arch suffix keeps the x64 and ARM64
/// build trees from colliding in a shared target dir.
fn win_build_dir() -> PathBuf {
    workspace_obs_build_dir(&win_vs_platform().to_lowercase())
}

/// `OBS_BUILD_DIR`, else `<workspace_target>/obs-<arch_suffix>` — the layout
/// shared by the Windows and Linux branches (see [`win_build_dir`]). Keeping
/// the OBS tree outside cargo's OUT_DIR also means it survives `cargo clean -p
/// obs-sys` and can be shared across worktrees via `OBS_BUILD_DIR`.
fn workspace_obs_build_dir(arch_suffix: &str) -> PathBuf {
    if let Ok(dir) = env::var("OBS_BUILD_DIR") {
        return PathBuf::from(dir);
    }

    let workspace_target = if let Ok(t) = env::var("CARGO_TARGET_DIR") {
        PathBuf::from(t)
    } else {
        let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
        out_dir
            .ancestors()
            .find(|p| p.file_name().map(|n| n == "target").unwrap_or(false))
            .expect("could not find a `target` ancestor of OUT_DIR")
            .to_path_buf()
    };

    workspace_target.join(format!("obs-{arch_suffix}"))
}

/// cmake is frequently not on PATH on dev machines; fall back to the copy that
/// ships with Visual Studio 2022.
fn find_cmake() -> PathBuf {
    if Command::new("cmake")
        .arg("--version")
        .output()
        .map(|o| o.status.success())
        .unwrap_or(false)
    {
        return PathBuf::from("cmake");
    }

    let vs = PathBuf::from("C:/Program Files/Microsoft Visual Studio/2022/Community/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe");
    if vs.exists() {
        return vs;
    }

    panic!(
        "cmake not found on PATH and the Visual Studio fallback is missing at {}",
        vs.display()
    );
}

fn win_cmake_configure(cmake: &Path, obs_src: &Path, build_dir: &Path) {
    if build_dir.join("CMakeCache.txt").exists() {
        return;
    }

    // Deps auto-download into <src>/.deps at configure time (needs network on
    // first run). Configure also creates <src>/build_x86 (the 32-bit child) —
    // that is expected.
    let status = Command::new(cmake)
        .arg("-S")
        .arg(obs_src)
        .arg("-B")
        .arg(build_dir)
        .arg("-G")
        .arg("Visual Studio 17 2022")
        .arg("-A")
        .arg(win_vs_platform())
        .arg(format!("-DOBS_VERSION_OVERRIDE={}", obs_version_override()))
        // See the macOS branch: OBS defaults to -Werror / MSVC /WX. Disable
        // warnings-as-errors so a newer MSVC toolchain than OBS 32.1.2 was
        // tested against cannot break our build on a stray warning.
        .arg("-DCMAKE_COMPILE_WARNING_AS_ERROR=OFF")
        .arg("-DENABLE_FRONTEND=OFF")
        .arg("-DENABLE_UI=OFF")
        .arg("-DENABLE_SCRIPTING=OFF")
        .arg("-DENABLE_BROWSER=OFF")
        .arg("-DENABLE_WEBSOCKET=OFF")
        .arg("-DENABLE_VST=OFF")
        .arg("-DENABLE_AJA=OFF")
        .arg("-DENABLE_DECKLINK=OFF")
        .arg("-DENABLE_WEBRTC=OFF")
        .arg("-DENABLE_VIRTUALCAM=OFF")
        .arg("-DENABLE_NEW_MPEGTS_OUTPUT=OFF")
        .arg("-Wno-dev")
        .status()
        .expect("Failed to run cmake configure");

    assert!(status.success(), "cmake configure failed");
    assert!(
        build_dir.join("CMakeCache.txt").exists(),
        "cmake configure did not produce CMakeCache.txt at {}",
        build_dir.display()
    );
}

fn win_cmake_build(cmake: &Path, build_dir: &Path, config: &str) {
    // w32-pthreads and the win-capture helpers (graphics-hook, inject-helper,
    // get-graphics-offsets) come along via add_dependencies. Base targets are
    // built for every Windows arch.
    let mut targets = vec![
        "libobs",
        "libobs-d3d11",
        "libobs-winrt",
        "win-capture",
        "win-wasapi",
        "obs-ffmpeg",
        "obs-ffmpeg-mux",
        "obs-x264",
        "obs-outputs",
        "coreaudio-encoder",
        // image_source + color_filter: the mouse click tracker (--tracker).
        "image-source",
        "obs-filters",
        // dshow_input: webcam capture (--webcam / --list-cameras).
        "win-dshow",
    ];

    // GPU-vendor hardware encoders (NVIDIA NVENC, Intel QSV, AMD AMF) exist only
    // for x64/x86 — OBS does not generate these targets for ARM64 (Windows-on-ARM
    // has no such discrete encoders), so naming them there fails MSBuild with
    // MSB1009 (project file not found). Their standalone registration-test exes
    // have no dependency edge from the plugins, so they must be named explicitly.
    if target_arch() == "x86_64" {
        targets.extend_from_slice(&[
            "obs-nvenc",
            "obs-qsv11",
            "obs-nvenc-test",
            "obs-qsv-test",
            "obs-amf-test",
        ]);
    }

    let marker = build_dir.join(".build_complete");
    if build_is_current(&marker, &targets) {
        return;
    }

    let mut cmd = Command::new(cmake);
    cmd.arg("--build")
        .arg(build_dir)
        .arg("--config")
        .arg(config);
    for target in &targets {
        cmd.arg("--target").arg(target);
    }

    let status = cmd.status().expect("Failed to run cmake build");
    assert!(status.success(), "cmake build failed");

    write_build_marker(&marker, &targets);
}

fn win_emit_link_directives(build_dir: &Path, config: &str) {
    // obs.lib is a normal MSVC import library next to obs.dll (PREFIX "" =>
    // obs.lib / obs.dll, not libobs.*).
    let link_search = build_dir.join("libobs").join(config);
    assert!(
        link_search.join("obs.lib").exists(),
        "obs.lib not found at {} — did the libobs target build?",
        link_search.display()
    );

    println!("cargo:rustc-link-search=native={}", link_search.display());
    println!("cargo:rustc-link-lib=dylib=obs");
}

fn win_emit_exports(obs_src: &Path, build_dir: &Path, config: &str) {
    // Consumed downstream as DEP_OBS_OBS_BUILD_DIR / DEP_OBS_OBS_BUILD_CONFIG /
    // DEP_OBS_DEPS_BIN (links key is `obs`).
    println!("cargo:obs_build_dir={}", build_dir.display());
    println!("cargo:obs_build_config={config}");

    if let Some(bin) = find_obs_deps_bin(obs_src) {
        println!("cargo:deps_bin={}", bin.display());
    } else {
        println!(
            "cargo:warning=obs-sys: could not locate the obs-deps bin dir under {}",
            obs_src.join(".deps").display()
        );
    }
}

fn find_obs_deps_bin(obs_src: &Path) -> Option<PathBuf> {
    find_obs_deps_subdir(obs_src, "bin")
}

/// Locates `<obs-deps bundle>/<sub>` for the TARGET architecture.
///
/// `.deps` holds several bundles: a Windows build also downloads the x86 one
/// (and an ARM64 build the x64 one) for the child CMake configures OBS spawns
/// for its helpers. Directory order is arbitrary, so matching the first
/// `obs-deps-*` would ship x64 runtime DLLs in an ARM64 bundle, or generate
/// bindings against the wrong headers.
fn find_obs_deps_subdir(obs_src: &Path, sub: &str) -> Option<PathBuf> {
    let deps_dir = obs_src.join(".deps");
    std::fs::read_dir(&deps_dir)
        .ok()?
        .flatten()
        .map(|e| e.path())
        .find(|dir| {
            let name = dir.file_name().unwrap_or_default().to_string_lossy();
            name.starts_with("obs-deps-")
                && !name.contains("qt6")
                && is_target_arch_bundle(&name)
                && dir.join(sub).exists()
        })
        .map(|dir| dir.join(sub))
}

/// True for a bundle name built for the target arch. macOS ships one
/// `-universal` bundle covering both slices; Windows names them `-x64` /
/// `-arm64` / `-x86`.
fn is_target_arch_bundle(name: &str) -> bool {
    if name.contains("universal") {
        return true;
    }
    match target_arch().as_str() {
        "x86_64" => name.ends_with("-x64"),
        "aarch64" => name.ends_with("-arm64"),
        _ => false,
    }
}

// ---------------------------------------------------------------------------
// Linux (Ninja generator, pinned BtbN FFmpeg + source-built x264, libobs.so)
// ---------------------------------------------------------------------------
//
// Linux has no obs-deps bundle, so the two third-party pieces OBS cannot take
// from the distro are supplied here before CMake runs:
//
// - FFmpeg: the pinned BtbN build (obs-build-support), shared with ffmpeg-sys
//   so libobs, the plugins and vid2gif all load one FFmpeg. The system libav*
//   is never used — distro FFmpeg versions vary per release, and two copies in
//   one process would each carry their own codec registry and allocator.
// - x264: built from a pinned source commit into a static, PIC archive that
//   obs-x264.so embeds, so the runtime directory carries no libx264.so and the
//   encoder does not depend on whichever x264 ABI the distro ships.
//
// Everything else (X11/xcb, Wayland, EGL, PipeWire, PulseAudio, libv4l2, udev,
// jansson, curl, mbedTLS, glib, libva/libdrm/libpci) is linked from the
// system's -dev packages, as every Linux OBS build does.

/// x264 source: the head of the upstream `stable` branch (X264_BUILD 165) when
/// this was pinned. A commit hash rather than a tarball because
/// code.videolan.org generates archives on the fly and their bytes are not
/// stable; the checked-out commit is verified against this hash, which pins
/// the content as firmly as a SHA-256 of an archive would.
const X264_REPO: &str = "https://code.videolan.org/videolan/x264.git";
const X264_COMMIT: &str = "b35605ace3ddf7c1a5d67a2eb553f034aef41d55";

/// The OBS targets built on Linux, and why each is needed. Configure still
/// visits every plugin's CMakeLists (so their REQUIRED packages matter even
/// when not built — see `linux_cmake_configure`), but only these compile.
const LINUX_TARGETS: &[&str] = &[
    "libobs",
    // The only Linux graphics backend; libobs dlopens it at obs_reset_video
    // (by its soname, libobs-opengl.so.30).
    "libobs-opengl",
    // ffmpeg_muxer / ffmpeg_aac, and the out-of-process muxer it spawns.
    "obs-ffmpeg",
    "obs-ffmpeg-mux",
    "obs-x264",
    // mp4_output: the hybrid MP4 muxer behind --multi-track.
    "obs-outputs",
    // image_source + color_filter: the mouse click tracker (--tracker), and
    // color_source for `--webcam test`.
    "image-source",
    "obs-filters",
    // xshm_input_v2: X11 display capture.
    "linux-capture",
    // pipewire-screen-capture-source: Wayland capture via xdg-desktop-portal.
    "linux-pipewire",
    // pulse_output_capture / pulse_input_capture (these also serve PipeWire
    // hosts, through pipewire-pulse).
    "linux-pulseaudio",
    // v4l2_input: webcam capture (--webcam / --list-cameras).
    "linux-v4l2",
];

/// A path that will never exist, compiled into libobs as OBS_INSTALL_PREFIX.
///
/// libobs searches `OBS_INSTALL_PREFIX/lib/obs-plugins` and
/// `OBS_INSTALL_PREFIX/share/obs/libobs` at runtime in addition to the paths
/// we register. With the default `/usr/local` (or a distro-style `/usr`), a
/// system-wide OBS installation's plugins and effect files — built against a
/// different libobs — could be loaded next to ours. A bogus prefix makes
/// those two particular lookups miss harmlessly.
///
/// It is NOT the whole defence: libobs's `add_default_module_paths()` also
/// registers exe-relative (`<exe>/../lib/obs-plugins`) and CWD-relative plugin
/// dirs that no configure option removes. Plugin loading is therefore
/// restricted to our own registered directory at runtime instead
/// (`obs::ObsContext::load_all_modules`, Linux branch). libobs *data* files
/// still resolve CWD-relative `share/obs/libobs/` and exe-relative
/// `../share/obs/libobs/` before our `obs_add_data_path` entry
/// (`find_libobs_data_file`, libobs/obs-nix.c); that only matters if the
/// bundle is unpacked next to a system OBS's `share/obs`, and fixing it would
/// need a libobs patch.
const LINUX_INSTALL_PREFIX: &str = "/nonexistent/obs-express";

/// OBS build-dir suffix for the target arch (`<target>/obs-x64`), matching the
/// Windows naming. Only x86_64 is supported for now; the FFmpeg asset lookup
/// (obs-build-support) panics with instructions for anything else, and calling
/// it first makes sure that happens before any other work.
fn linux_arch_suffix() -> &'static str {
    let arch = target_arch();
    let _ = obs_build_support::linux::ffmpeg_asset(&arch);
    match arch.as_str() {
        "x86_64" => "x64",
        "aarch64" => "arm64",
        other => unreachable!("ffmpeg_asset accepted unsupported arch `{other}`"),
    }
}

fn build_linux() {
    use obs_build_support::linux;

    let arch_suffix = linux_arch_suffix();
    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let repo_root = manifest_dir.parent().unwrap().parent().unwrap();
    let obs_src = repo_root.join("obs-studio");
    let deps_dir = obs_src.join(".deps");
    let build_dir = workspace_obs_build_dir(arch_suffix);
    let config = "RelWithDebInfo";

    // Like Windows: idempotency comes from the CMakeCache / .build_complete
    // markers and the bundle markers under .deps, not from watching sources.
    println!("cargo:rerun-if-changed=wrapper.h");
    println!("cargo:rerun-if-env-changed=OBS_BUILD_DIR");
    println!("cargo:rerun-if-env-changed=CARGO_TARGET_DIR");

    for tool in [
        "cmake",
        "ninja",
        "pkg-config",
        "git",
        "make",
        "nasm",
        "patchelf",
    ] {
        linux::require_tool(tool);
    }

    let ffmpeg = linux::ensure_ffmpeg(&deps_dir, &target_arch());
    let x264 = linux_ensure_x264(&deps_dir, arch_suffix);

    linux_cmake_configure(&obs_src, &build_dir, &ffmpeg, &x264);
    linux_cmake_build(&build_dir, &ffmpeg, &x264);
    let lib_dir = linux_emit_link_directives(&build_dir, config, &ffmpeg);
    generate_bindings(&manifest_dir, &obs_src, &build_dir, Some(ffmpeg.include()));

    // Consumed downstream as DEP_OBS_*: the build dir/config (same keys as the
    // other platforms), the rundir lib dir holding libobs.so.30,
    // libobs-opengl.so.30 and obs-plugins/, and the FFmpeg bundle (the same
    // one ffmpeg-sys exports as DEP_FFMPEG_DEPS_*).
    println!("cargo:obs_build_dir={}", build_dir.display());
    println!("cargo:obs_build_config={config}");
    println!("cargo:obs_lib_dir={}", lib_dir.display());
    println!("cargo:deps_root={}", ffmpeg.root.display());
    println!("cargo:deps_lib={}", ffmpeg.lib().display());
}

/// Builds x264 from [`X264_COMMIT`] into `<.deps>/x264-<commit>-<arch>` (once)
/// and returns that prefix.
///
/// `--enable-static --enable-pic`: the archive is linked into obs-x264.so, a
/// shared object, so it must be position independent. `--disable-cli`: only
/// the library is wanted. `--disable-opencl`: OBS never enables the OpenCL
/// lookahead, and disabling it drops x264's runtime dlopen of libOpenCL.
///
/// Lives under `.deps` next to the FFmpeg bundle, not in the OBS build dir, so
/// CI's `.deps` cache and worktrees sharing `.deps` reuse it; the file lock is
/// for the same sharing. The prefix name carries the commit, so a pin bump
/// builds side by side instead of reusing a stale archive.
fn linux_ensure_x264(deps_dir: &Path, arch_suffix: &str) -> PathBuf {
    use obs_build_support::linux::run;

    let prefix = deps_dir.join(format!("x264-{}-{arch_suffix}", &X264_COMMIT[..12]));
    let marker = prefix.join(".obs-express-complete");
    let is_complete = || std::fs::read_to_string(&marker).is_ok_and(|s| s.trim() == X264_COMMIT);
    if is_complete() {
        return prefix;
    }

    std::fs::create_dir_all(deps_dir).unwrap();
    let lock = std::fs::File::create(deps_dir.join(".x264.lock")).expect("create x264 lock");
    lock.lock().expect("lock x264 build");
    if is_complete() {
        return prefix;
    }

    let src = deps_dir.join(format!(".x264-src-{arch_suffix}"));
    let _ = std::fs::remove_dir_all(&src);
    let _ = std::fs::remove_dir_all(&prefix);
    std::fs::create_dir_all(&src).unwrap();
    let git = |args: &[&str]| {
        let mut cmd = Command::new("git");
        cmd.arg("-C").arg(&src).args(args);
        run(&mut cmd);
    };
    git(&["init", "-q"]);
    git(&["fetch", "-q", "--depth", "1", X264_REPO, X264_COMMIT]);
    git(&[
        "-c",
        "advice.detachedHead=false",
        "checkout",
        "-q",
        "FETCH_HEAD",
    ]);
    let head = Command::new("git")
        .arg("-C")
        .arg(&src)
        .args(["rev-parse", "HEAD"])
        .output()
        .expect("git rev-parse");
    let head = String::from_utf8_lossy(&head.stdout).trim().to_string();
    assert_eq!(
        head, X264_COMMIT,
        "x264 checkout is at {head}, expected the pinned {X264_COMMIT}"
    );

    run(Command::new("./configure")
        .current_dir(&src)
        .arg(format!("--prefix={}", prefix.display()))
        .args([
            "--enable-static",
            "--enable-pic",
            "--disable-cli",
            "--disable-opencl",
        ]));
    let jobs = env::var("NUM_JOBS").unwrap_or_else(|_| "4".into());
    run(Command::new("make")
        .current_dir(&src)
        .arg(format!("-j{jobs}")));
    run(Command::new("make").current_dir(&src).arg("install"));
    assert!(
        prefix.join("lib/libx264.a").exists() && prefix.join("include/x264.h").exists(),
        "x264 install did not produce lib/libx264.a + include/x264.h under {}",
        prefix.display()
    );

    std::fs::write(&marker, X264_COMMIT).unwrap();
    let _ = std::fs::remove_dir_all(&src);
    prefix
}

/// `PKG_CONFIG_PATH` with the bundled FFmpeg and x264 first.
///
/// OBS's FindFFmpeg / FindLibx264 consult pkg-config for versions and extra
/// cflags even when the include/library cache variables are preset; putting
/// our `.pc` files first guarantees they describe our copies (BtbN's are
/// `${pcfiledir}`-relative, so they are valid wherever the bundle sits). The
/// system path is kept, not replaced: PipeWire, Gio, libva and the rest are
/// found through it.
fn linux_pkg_config_path(ffmpeg: &obs_build_support::linux::FfmpegBundle, x264: &Path) -> String {
    let mut parts = vec![
        ffmpeg.pkgconfig().display().to_string(),
        x264.join("lib/pkgconfig").display().to_string(),
    ];
    if let Ok(existing) = env::var("PKG_CONFIG_PATH") {
        if !existing.is_empty() {
            parts.push(existing);
        }
    }
    parts.join(":")
}

fn linux_cmake_configure(
    obs_src: &Path,
    build_dir: &Path,
    ffmpeg: &obs_build_support::linux::FfmpegBundle,
    x264: &Path,
) {
    let mut args: Vec<String> = vec![
        "-G".into(),
        "Ninja".into(),
        "-DCMAKE_BUILD_TYPE=RelWithDebInfo".into(),
        format!("-DOBS_VERSION_OVERRIDE={}", obs_version_override()),
        // See the macOS branch: no warnings-as-errors for the vendored tree.
        "-DCMAKE_COMPILE_WARNING_AS_ERROR=OFF".into(),
        format!("-DCMAKE_INSTALL_PREFIX={LINUX_INSTALL_PREFIX}"),
        // GNUInstallDirs picks lib/x86_64-linux-gnu for a /usr prefix; pin the
        // flat `lib` the staging code expects under rundir.
        "-DCMAKE_INSTALL_LIBDIR=lib".into(),
        // The same set Windows disables...
        "-DENABLE_FRONTEND=OFF".into(),
        "-DENABLE_UI=OFF".into(),
        "-DENABLE_SCRIPTING=OFF".into(),
        "-DENABLE_BROWSER=OFF".into(),
        "-DENABLE_WEBSOCKET=OFF".into(),
        "-DENABLE_VST=OFF".into(),
        "-DENABLE_AJA=OFF".into(),
        "-DENABLE_DECKLINK=OFF".into(),
        "-DENABLE_WEBRTC=OFF".into(),
        "-DENABLE_NEW_MPEGTS_OUTPUT=OFF".into(),
        // ...plus Linux plugins whose configure hard-requires packages we
        // neither ship nor use: VLC (vlc/libvlc.h), text-freetype2 (Freetype +
        // Fontconfig), ALSA (PulseAudio covers audio capture), and the speexdsp
        // noise suppressor in obs-filters (libspeexdsp).
        "-DENABLE_VLC=OFF".into(),
        "-DENABLE_FREETYPE=OFF".into(),
        "-DENABLE_ALSA=OFF".into(),
        "-DENABLE_SPEEXDSP=OFF".into(),
        // Hardware encoding is out of scope on Linux (--hw-accel falls back to
        // x264): no NVENC (FFnvcodec) or QSV (libvpl). The VAAPI encoder inside
        // obs-ffmpeg has no switch and is compiled regardless; it is simply
        // never selected.
        "-DENABLE_NVENC=OFF".into(),
        "-DENABLE_QSV11=OFF".into(),
        // rtmp-services is always built; stop it from fetching service lists.
        "-DENABLE_SERVICE_UPDATES=OFF".into(),
        // The capture backends this port exists for, explicitly ON so that a
        // missing -dev package fails configure rather than silently dropping
        // Wayland or webcam support.
        "-DENABLE_PIPEWIRE=ON".into(),
        "-DENABLE_WAYLAND=ON".into(),
        "-DENABLE_PULSEAUDIO=ON".into(),
        "-DENABLE_V4L2=ON".into(),
        // $ORIGIN-relative INSTALL_RPATHs. The build tree itself keeps absolute
        // RUNPATHs into rundir (so it runs in place), and the copies that
        // obs-express's build script stages are re-pointed at $ORIGIN with
        // patchelf, so neither relies on this; it only makes a `cmake
        // --install` of this tree relocatable as well.
        "-DENABLE_RELOCATABLE=ON".into(),
        // Search our prefixes before the system's.
        format!(
            "-DCMAKE_PREFIX_PATH={};{}",
            ffmpeg.root.display(),
            x264.display()
        ),
        // Static x264 inside obs-x264.so: keep its symbols local. Otherwise the
        // module exports x264_* and, ELF symbol interposition being
        // process-global, another x264 in the process (a distro libx264 pulled
        // in by some system library) could satisfy obs-x264's own calls with a
        // different ABI, or ours could satisfy theirs.
        "-DCMAKE_MODULE_LINKER_FLAGS=-Wl,--exclude-libs,libx264.a".into(),
        // ...and give obs-x264.so its own DT_NEEDED on libm. The static archive
        // calls exp/exp2/log etc., but obs-x264 links only libobs, so without
        // this those references stay unversioned and resolve only by luck of
        // libm already being loaded. The standard-libraries slot goes at the
        // END of each link line, where --as-needed keeps it only for targets
        // that actually use libm.
        "-DCMAKE_C_STANDARD_LIBRARIES=-lm".into(),
        format!("-DLibx264_INCLUDE_DIR={}", x264.join("include").display()),
        format!("-DLibx264_LIBRARY={}", x264.join("lib/libx264.a").display()),
        "-Wno-dev".into(),
    ];
    // Preset FindFFmpeg's per-component cache variables so every consumer
    // (libobs, obs-ffmpeg, ffmpeg-mux, linux-v4l2, media-playback) resolves to
    // the bundle, never to a system copy found through the default paths.
    for c in [
        "avcodec",
        "avdevice",
        "avfilter",
        "avformat",
        "avutil",
        "swscale",
        "swresample",
    ] {
        args.push(format!(
            "-DFFmpeg_{c}_INCLUDE_DIR={}",
            ffmpeg.include().display()
        ));
        args.push(format!(
            "-DFFmpeg_{c}_LIBRARY={}",
            ffmpeg.lib().join(format!("lib{c}.so")).display()
        ));
    }

    // Unlike Windows (configure once, never again), re-run configure when the
    // argument list changes: a bumped FFmpeg or x264 pin changes paths in it,
    // and a stale cache would keep linking the old copies. cmake on an
    // existing cache only updates what changed, so this is cheap.
    let stamp = build_dir.join(".configure_args");
    let signature = args.join("\n");
    if build_dir.join("CMakeCache.txt").exists()
        && std::fs::read_to_string(&stamp).is_ok_and(|s| s == signature)
    {
        return;
    }

    let status = Command::new("cmake")
        .arg("-S")
        .arg(obs_src)
        .arg("-B")
        .arg(build_dir)
        .args(&args)
        .env("PKG_CONFIG_PATH", linux_pkg_config_path(ffmpeg, x264))
        .status()
        .expect("Failed to run cmake configure");
    assert!(
        status.success(),
        "cmake configure failed. On Debian/Ubuntu the build needs the OBS -dev packages \
         listed in the Linux CI job (notably extra-cmake-modules, uuid-dev, \
         libpipewire-0.3-dev, libwayland-dev, libxkbcommon-dev, libv4l-dev, libva-dev, \
         libpci-dev, libdrm-dev, libmbedtls-dev, libcurl4-openssl-dev, libjansson-dev)."
    );
    std::fs::write(&stamp, signature).expect("write configure stamp");
    // A (re)configure can change how existing targets link, so the target
    // list alone no longer proves the build is current: drop the marker so
    // `linux_cmake_build` runs (ninja rebuilds only what the change touched).
    let _ = std::fs::remove_file(build_dir.join(".build_complete"));
}

fn linux_cmake_build(
    build_dir: &Path,
    ffmpeg: &obs_build_support::linux::FfmpegBundle,
    x264: &Path,
) {
    let marker = build_dir.join(".build_complete");
    if build_is_current(&marker, LINUX_TARGETS) {
        return;
    }

    let mut cmd = Command::new("cmake");
    cmd.arg("--build").arg(build_dir);
    for target in LINUX_TARGETS {
        cmd.arg("--target").arg(target);
    }
    // A build can re-run configure (a CMakeLists changed); keep it resolving
    // the same FFmpeg/x264 as the initial configure did.
    cmd.env("PKG_CONFIG_PATH", linux_pkg_config_path(ffmpeg, x264));
    let status = cmd.status().expect("Failed to run cmake build");
    assert!(status.success(), "cmake build failed");

    write_build_marker(&marker, LINUX_TARGETS);
}

/// Links `libobs.so` and returns the rundir `lib` dir (libobs.so.30,
/// libobs-opengl.so.30, obs-plugins/).
///
/// The link-search dir is the libobs target's own output dir, not rundir:
/// rundir only receives the real file and its SONAME name (libobs.so.30), not
/// the unversioned `libobs.so` development link `-lobs` needs. Binaries record
/// `libobs.so.30` (the SONAME) either way.
fn linux_emit_link_directives(
    build_dir: &Path,
    config: &str,
    ffmpeg: &obs_build_support::linux::FfmpegBundle,
) -> PathBuf {
    let link_search = build_dir.join("libobs");
    assert!(
        link_search.join("libobs.so").exists(),
        "libobs.so not found at {} — did the libobs target build?",
        link_search.display()
    );
    let lib_dir = build_dir.join("rundir").join(config).join("lib");
    assert!(
        lib_dir.join("libobs.so.30").exists(),
        "libobs.so.30 not found in the OBS rundir at {}",
        lib_dir.display()
    );

    println!("cargo:rustc-link-search=native={}", link_search.display());
    println!("cargo:rustc-link-lib=dylib=obs");

    // Absolute RUNPATHs for this crate's own test harness. As on macOS,
    // `rustc-link-arg` is package-scoped, so every executable-producing
    // consumer repeats these from DEP_OBS_OBS_LIB_DIR / DEP_OBS_DEPS_LIB.
    println!("cargo:rustc-link-arg=-Wl,-rpath,{}", lib_dir.display());
    println!("cargo:rustc-link-arg=-Wl,-rpath,{}", ffmpeg.lib().display());
    lib_dir
}

// ---------------------------------------------------------------------------
// Shared: bindgen + obs-deps include discovery (identical wrapper/allowlists)
// ---------------------------------------------------------------------------

/// `deps_include` is the FFmpeg header dir: the obs-deps bundle's `include`
/// on Windows/macOS, the pinned BtbN bundle's on Linux.
fn generate_bindings(
    manifest_dir: &Path,
    obs_src: &Path,
    obs_build: &Path,
    deps_include: Option<PathBuf>,
) {
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    let bindings_path = out_dir.join("bindings.rs");

    let libobs_include = obs_src.join("libobs");
    let config_include = obs_build.join("config");

    let mut builder = bindgen::Builder::default()
        .header(manifest_dir.join("wrapper.h").to_str().unwrap())
        .clang_arg(format!("-I{}", libobs_include.display()))
        .clang_arg(format!("-I{}", config_include.display()))
        // OBS's util_uint64.h calls the MSVC intrinsic `_udiv128` (guarded by
        // _MSC_VER/_M_X64). libclang parses in MSVC mode but does not resolve
        // that intrinsic's declaration, and clang 16+ promotes an implicit
        // function declaration to a hard error — which bindgen treats as fatal.
        // The offending helper (util_mul_div64) is not in our allowlist, so its
        // body is irrelevant to the generated bindings: silence the diagnostic
        // and let generation proceed. Harmless on macOS (no such intrinsic).
        .clang_arg("-Wno-implicit-function-declaration");

    if let Some(deps_inc) = &deps_include {
        builder = builder.clang_arg(format!("-I{}", deps_inc.display()));
    }

    // On ARM64, OBS's prebuilt deps ship SIMDE to emulate x86 SSE intrinsics.
    // SIMDE's sse.h references C11 <stdatomic.h> names (memory_order_seq_cst)
    // that libclang leaves undeclared while parsing in MSVC mode, which bindgen
    // treats as a fatal error. Force-include stdatomic.h so those names exist.
    // x64 uses native intrinsics and never pulls in SIMDE, so this is arm64-only.
    if target_arch() == "aarch64" {
        builder = builder.clang_arg("-include").clang_arg("stdatomic.h");
    }

    let bindings = builder
        .allowlist_function("obs_.*")
        .allowlist_function("signal_handler_.*")
        .allowlist_function("calldata_.*")
        .allowlist_function("video_output_.*")
        .allowlist_function("audio_output_.*")
        // The libobs monotonic clock (util/platform.h): input-capture event
        // timestamps must share obs_get_video_frame_time's timebase.
        .allowlist_function("os_gettime_ns")
        // Graphics API (graphics/graphics.h): effects, texrenders and sprite
        // draws, used by clowd_share_region's obscure renderer to post-process
        // the composited canvas inside the display draw callback.
        .allowlist_function("gs_.*")
        .allowlist_type("obs_.*")
        // Graphics types (graphics/graphics.h): gs_init_data / gs_window and
        // the GS_* enums, needed by obs_display_create (crates/obs display.rs).
        .allowlist_type("gs_.*")
        .allowlist_type("signal_handler_t")
        .allowlist_type("calldata_t")
        .allowlist_type("video_.*")
        .allowlist_type("audio_.*")
        .allowlist_type("speaker_layout")
        .allowlist_var("OBS_.*")
        .allowlist_var("GS_.*")
        .allowlist_var("VIDEO_.*")
        .allowlist_var("AUDIO_.*")
        // Track-count limits: obs-express asserts its own constants against
        // these at compile time, so a libobs bump that changes them fails the
        // build instead of silently dropping tracks.
        .allowlist_var("MAX_AUDIO_MIXES")
        .allowlist_var("MAX_OUTPUT_AUDIO_ENCODERS")
        .allowlist_var("MAX_OUTPUT_VIDEO_ENCODERS")
        .derive_default(true)
        .generate()
        .expect("Failed to generate bindings");

    let generated = bindings.to_string();
    assert_no_opaque_regressions(&generated);

    std::fs::write(&bindings_path, generated).expect("Failed to write bindings");
}

/// bindgen 0.71 + libclang 22 intermittently emitted these structs as opaque
/// 1-byte `{ _address: u8 }` bodies (while still emitting the real-size layout
/// asserts, which then fail downstream with E0080). bindgen 0.72 fixed it on
/// this machine, but fail fast here with a clear message if it ever recurs —
/// the alternative is six baffling layout-assert errors in generated code.
fn assert_no_opaque_regressions(generated: &str) {
    let critical = [
        "vec2",
        "vec3",
        "vec4",
        "obs_transform_info",
        "obs_audio_data",
        "obs_source_frame",
    ];
    for name in critical {
        let opaque = format!("pub struct {name} {{\n    pub _address: u8,");
        assert!(
            !generated.contains(&opaque),
            "bindgen emitted `{name}` as an opaque 1-byte struct — this is the \
             bindgen/libclang layout bug (seen with bindgen 0.71 + libclang 22). \
             Check the installed LLVM version against the bindgen version in \
             crates/obs-sys/Cargo.toml."
        );
    }
}

fn find_obs_deps_include(obs_src: &Path) -> Option<PathBuf> {
    let include = find_obs_deps_subdir(obs_src, "include")?;
    println!(
        "cargo:warning=Using obs-deps include: {}",
        include.display()
    );
    Some(include)
}
