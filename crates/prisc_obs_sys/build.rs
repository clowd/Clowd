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
/// obs-express ships (Windows x64/ARM64, macOS x86_64/arm64).
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
    generate_bindings(&manifest_dir, &obs_src, &obs_build);
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
    let marker = obs_build.join(".build_complete");
    if marker.exists() {
        return;
    }

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
    ];

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

    std::fs::write(&marker, "").expect("Failed to write build marker");
}

fn mac_emit_link_directives(_obs_src: &Path, obs_build: &Path, config: &str) {
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

    // Export paths so downstream crates can locate frameworks and plugins at runtime
    println!("cargo:framework_search={}", framework_search.display());
    println!("cargo:obs_build_dir={}", obs_build.display());
    println!("cargo:obs_build_config={config}");
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
    generate_bindings(&manifest_dir, &obs_src, &build_dir);
    win_emit_exports(&obs_src, &build_dir, config);
}

/// MAX_PATH-safe CMake build dir: never under the deep cargo OUT_DIR (MSB3491).
/// `OBS_BUILD_DIR` override, else `<workspace_target>/obs-<arch>` (obs-x64 /
/// obs-arm64) where the target dir is `CARGO_TARGET_DIR` if set, else the
/// OUT_DIR ancestor named `target`. The arch suffix keeps the x64 and ARM64
/// build trees from colliding in a shared target dir.
fn win_build_dir() -> PathBuf {
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

    workspace_target.join(format!("obs-{}", win_vs_platform().to_lowercase()))
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
    let marker = build_dir.join(".build_complete");
    if marker.exists() {
        return;
    }

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

    std::fs::write(&marker, "").expect("Failed to write .build_complete marker");
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
    let deps_dir = obs_src.join(".deps");
    for entry in std::fs::read_dir(&deps_dir).ok()?.flatten() {
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

// ---------------------------------------------------------------------------
// Shared: bindgen + obs-deps include discovery (identical wrapper/allowlists)
// ---------------------------------------------------------------------------

fn generate_bindings(manifest_dir: &Path, obs_src: &Path, obs_build: &Path) {
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    let bindings_path = out_dir.join("bindings.rs");

    let libobs_include = obs_src.join("libobs");
    let config_include = obs_build.join("config");
    let deps_include = find_obs_deps_include(obs_src);

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
        .allowlist_type("obs_.*")
        .allowlist_type("signal_handler_t")
        .allowlist_type("calldata_t")
        .allowlist_type("video_.*")
        .allowlist_type("audio_.*")
        .allowlist_type("speaker_layout")
        .allowlist_var("OBS_.*")
        .allowlist_var("VIDEO_.*")
        .allowlist_var("AUDIO_.*")
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
    let deps_dir = obs_src.join(".deps");
    if !deps_dir.exists() {
        return None;
    }

    for entry in std::fs::read_dir(&deps_dir).ok()? {
        let entry = entry.ok()?;
        let name = entry.file_name().to_string_lossy().to_string();
        if name.starts_with("obs-deps-") && !name.contains("qt6") {
            let include = entry.path().join("include");
            if include.exists() {
                println!(
                    "cargo:warning=Using obs-deps include: {}",
                    include.display()
                );
                return Some(include);
            }
        }
    }
    None
}
