use std::env;
use std::path::{Path, PathBuf};
use std::process::Command;

fn main() {
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let repo_root = manifest_dir.parent().unwrap().parent().unwrap();
    let obs_src = repo_root.join("obs-studio");
    let obs_build = out_dir.join("obs-build");

    println!("cargo:rerun-if-changed=wrapper.h");
    println!("cargo:rerun-if-changed={}", obs_src.join("libobs").display());

    let config = "RelWithDebInfo";

    cmake_configure(&obs_src, &obs_build);
    cmake_build(&obs_build, config);
    emit_link_directives(&obs_build, config);
    generate_bindings(&manifest_dir, &obs_src, &obs_build);
}

fn cmake_configure(obs_src: &Path, obs_build: &Path) {
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
        .arg("-DCMAKE_OSX_DEPLOYMENT_TARGET=12.0")
        .arg("-DENABLE_UI=OFF")
        .arg("-DENABLE_SCRIPTING=OFF")
        .arg("-DENABLE_BROWSER=OFF")
        .arg("-DENABLE_WEBSOCKET=OFF")
        .arg("-DENABLE_AJA=OFF")
        .arg("-DENABLE_NEW_MPEGTS_OUTPUT=OFF")
        .arg("-DENABLE_VIRTUALCAM=OFF")
        .arg("-Wno-dev")
        .output()
        .expect("Failed to run cmake configure");

    let stdout = String::from_utf8_lossy(&output.stdout);
    let stderr = String::from_utf8_lossy(&output.stderr);

    // Print cmake output for cargo
    for line in stdout.lines() {
        println!("{line}");
    }
    for line in stderr.lines() {
        eprintln!("{line}");
    }

    // The Xcode generator may emit "cannot determine linker language" warnings for
    // Swift-only targets (libobs-metal), but still generates valid Xcode projects.
    // Check that the xcodeproj was actually created rather than relying on exit code.
    let xcodeproj = obs_build.join("obs-studio.xcodeproj");
    assert!(
        xcodeproj.exists(),
        "cmake configure failed — no Xcode project generated.\nstderr: {stderr}"
    );
}

fn cmake_build(obs_build: &Path, config: &str) {
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
        "obs-x264",
        "coreaudio-encoder",
    ];

    let mut cmd = Command::new("cmake");
    cmd.arg("--build").arg(obs_build);
    for target in &targets {
        cmd.arg("--target").arg(target);
    }
    cmd.arg("--config").arg(config)
        .arg("--")
        .arg("-parallelizeTargets");

    let status = cmd.status().expect("Failed to run cmake build");
    assert!(status.success(), "cmake build failed");

    std::fs::write(&marker, "").expect("Failed to write build marker");
}

fn emit_link_directives(obs_build: &Path, config: &str) {
    let framework_search = obs_build.join("libobs").join(config);
    assert!(
        framework_search.join("libobs.framework").exists(),
        "libobs.framework not found at {}",
        framework_search.display()
    );

    println!("cargo:rustc-link-search=framework={}", framework_search.display());
    println!("cargo:rustc-link-lib=framework=libobs");

    // Export paths so downstream crates can locate frameworks and plugins at runtime
    println!("cargo:framework_search={}", framework_search.display());
    println!("cargo:obs_build_dir={}", obs_build.display());
    println!("cargo:obs_build_config={config}");
}

fn generate_bindings(manifest_dir: &Path, obs_src: &Path, obs_build: &Path) {
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    let bindings_path = out_dir.join("bindings.rs");

    let libobs_include = obs_src.join("libobs");
    let config_include = obs_build.join("config");
    let deps_include = find_obs_deps_include(obs_src);

    let mut builder = bindgen::Builder::default()
        .header(manifest_dir.join("wrapper.h").to_str().unwrap())
        .clang_arg(format!("-I{}", libobs_include.display()))
        .clang_arg(format!("-I{}", config_include.display()));

    if let Some(deps_inc) = &deps_include {
        builder = builder.clang_arg(format!("-I{}", deps_inc.display()));
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

    bindings
        .write_to_file(&bindings_path)
        .expect("Failed to write bindings");
}

fn find_obs_deps_include(obs_src: &Path) -> Option<PathBuf> {
    let deps_dir = obs_src.join(".deps");
    if !deps_dir.exists() {
        return None;
    }

    // Look for obs-deps-* (not qt6) directories with an include/ subfolder
    for entry in std::fs::read_dir(&deps_dir).ok()? {
        let entry = entry.ok()?;
        let name = entry.file_name().to_string_lossy().to_string();
        if name.starts_with("obs-deps-") && !name.contains("qt6") {
            let include = entry.path().join("include");
            if include.exists() {
                println!("cargo:warning=Using obs-deps include: {}", include.display());
                return Some(include);
            }
        }
    }
    None
}
