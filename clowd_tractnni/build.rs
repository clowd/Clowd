/// The application manifest is shared with the other Clowd binaries and
/// lives in `clowd_rust_core/` beside the Rust they share (see that crate's
/// manifest note). It cannot be embedded *by* that crate — `/MANIFEST:EMBED`
/// is a link argument, and only the crate producing the executable links one
/// — so each binary's build script points at the single copy instead of
/// keeping its own. What it buys us is per-monitor DPI awareness; a copy
/// that silently drifted would make one binary read virtualised coordinates
/// and photograph the wrong pixels.
const SHARED_MANIFEST: &str = "../clowd_rust_core/app.manifest";

fn main() {
    println!("cargo:rerun-if-changed={SHARED_MANIFEST}");
    // baked into the Sentry release name via option_env! (clowd_rust_core's
    // telemetry::release), so a version bump has to invalidate the cached build
    println!("cargo:rerun-if-env-changed=CLOWD_VERSION");
    println!("cargo:rerun-if-env-changed=CLOWD_ORT_SKIP_DOWNLOAD");

    stage_ort_dylib();

    #[cfg(windows)]
    {
        let manifest_dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();
        let manifest_path = std::path::Path::new(&manifest_dir).join(SHARED_MANIFEST);
        // Fail loudly rather than link an unmanifested exe: without the
        // manifest the process is DPI-virtualised, which is a subtle
        // wrong-pixels bug rather than an obvious one.
        assert!(
            manifest_path.is_file(),
            "shared app manifest not found at {}",
            manifest_path.display()
        );
        println!("cargo:rustc-link-arg-bins=/MANIFEST:EMBED");
        println!("cargo:rustc-link-arg-bins=/MANIFESTINPUT:{}", manifest_path.display());
    }
}

/// The binary is useless without an ONNX Runtime dylib beside it (its runtime
/// probe is `--ort-dylib` > `ORT_DYLIB_PATH` > sibling-of-exe), so the build
/// stages one into the cargo profile directory the exe lands in — the same
/// build-script-fetches-a-prebuilt precedent ocr-rs set for MNN, with the same
/// tooling (curl plus tar, which is bsdtar on Windows 10+ and unzips too).
/// Versions/assets mirror ci.yml's packaging matrix: 1.29.0 everywhere except
/// macOS x86_64, pinned to 1.23.2 — the last upstream release for that arch.
/// The download is keyed off the cargo TARGET (never the host: CI cross-builds
/// windows-arm64 and mac universal slices on x64 hosts), cached in the target
/// dir across profiles, and a failure is a `cargo:warning` rather than a build
/// break — offline builds still link, and the exe reports INFERENCE_UNAVAILABLE
/// with a clear stderr message at runtime.
fn stage_ort_dylib() {
    if std::env::var_os("CLOWD_ORT_SKIP_DOWNLOAD").is_some_and(|v| v == "1") {
        return;
    }

    let os = std::env::var("CARGO_CFG_TARGET_OS").unwrap();
    let arch = std::env::var("CARGO_CFG_TARGET_ARCH").unwrap();
    let (version, asset, dylib) = match (os.as_str(), arch.as_str()) {
        ("windows", "x86_64") => ("1.29.0", "onnxruntime-win-x64-1.29.0.zip", "onnxruntime.dll"),
        ("windows", "aarch64") => ("1.29.0", "onnxruntime-win-arm64-1.29.0.zip", "onnxruntime.dll"),
        ("macos", "x86_64") => ("1.23.2", "onnxruntime-osx-x86_64-1.23.2.tgz", "libonnxruntime.dylib"),
        ("macos", "aarch64") => ("1.29.0", "onnxruntime-osx-arm64-1.29.0.tgz", "libonnxruntime.dylib"),
        _ => return, // no prebuilt for this target; the runtime probe explains itself
    };

    // OUT_DIR = <target>[/<triple>]/<profile>/build/<crate>-<hash>/out — the
    // profile dir the exe lands in is three levels up, and the cache lives
    // beside the profile dirs so debug and release share one download.
    let out_dir = std::path::PathBuf::from(std::env::var("OUT_DIR").unwrap());
    let profile_dir = out_dir
        .ancestors()
        .nth(3)
        .unwrap()
        .to_path_buf();
    let staged = profile_dir.join(dylib);

    let cache_dir = profile_dir
        .parent()
        .unwrap()
        .join("ort-dylib-cache")
        .join(format!("{os}-{arch}-{version}"));
    let cached = cache_dir.join(dylib);

    if !cached.is_file() {
        if let Err(err) = download_ort(version, asset, dylib, &cache_dir) {
            println!("cargo:warning=clowd_tractnni: could not stage the ONNX Runtime dylib ({err}); the binary will exit INFERENCE_UNAVAILABLE until one is placed beside it or ORT_DYLIB_PATH is set");
            return;
        }
    }

    // refresh the profile-dir copy only when missing or a different build
    // (cheap length check; the cache path already pins the exact version)
    let stale = match (std::fs::metadata(&staged), std::fs::metadata(&cached)) {
        (Ok(a), Ok(b)) => a.len() != b.len(),
        _ => true,
    };
    if stale {
        if let Err(err) = std::fs::copy(&cached, &staged) {
            println!("cargo:warning=clowd_tractnni: staging {} failed: {err}", staged.display());
        }
    }

    // Windows also ships the providers-shared shim when the release carries it.
    if os == "windows" {
        let shim = cache_dir.join("onnxruntime_providers_shared.dll");
        if shim.is_file() {
            let _ = std::fs::copy(&shim, profile_dir.join("onnxruntime_providers_shared.dll"));
        }
    }
}

fn download_ort(version: &str, asset: &str, dylib: &str, cache_dir: &std::path::Path) -> Result<(), String> {
    let url = format!("https://github.com/microsoft/onnxruntime/releases/download/v{version}/{asset}");
    let extract_dir = cache_dir.join("extract");
    std::fs::create_dir_all(&extract_dir).map_err(|e| e.to_string())?;
    let archive = cache_dir.join(asset);

    let run = |mut cmd: std::process::Command| -> Result<(), String> {
        let out = cmd.output().map_err(|e| e.to_string())?;
        if out.status.success() {
            Ok(())
        } else {
            Err(String::from_utf8_lossy(&out.stderr)
                .trim()
                .to_string())
        }
    };

    let mut curl = std::process::Command::new("curl");
    curl.args(["-fsSL", "--retry", "2", "-o"])
        .arg(&archive)
        .arg(&url);
    run(curl).map_err(|e| format!("download {url}: {e}"))?;

    // bsdtar reads zip and tgz alike, on both platforms we build on
    let mut tar = std::process::Command::new("tar");
    tar.arg("-xf")
        .arg(&archive)
        .arg("-C")
        .arg(&extract_dir);
    run(tar).map_err(|e| format!("extract {asset}: {e}"))?;

    // archives contain onnxruntime-<target>-<version>/lib/<dylib>; the macOS
    // lib dir names the real file with the version and symlinks the bare name,
    // so resolve through symlinks when copying out.
    let lib_dir = std::fs::read_dir(&extract_dir)
        .map_err(|e| e.to_string())?
        .filter_map(Result::ok)
        .map(|e| e.path().join("lib"))
        .find(|p| p.is_dir())
        .ok_or_else(|| format!("no lib/ dir inside {asset}"))?;

    let src = lib_dir.join(dylib);
    let real = std::fs::canonicalize(&src).map_err(|e| format!("{}: {e}", src.display()))?;
    std::fs::copy(&real, cache_dir.join(dylib)).map_err(|e| e.to_string())?;
    let shim = lib_dir.join("onnxruntime_providers_shared.dll");
    if shim.is_file() {
        std::fs::copy(&shim, cache_dir.join("onnxruntime_providers_shared.dll")).map_err(|e| e.to_string())?;
    }

    let _ = std::fs::remove_file(&archive);
    let _ = std::fs::remove_dir_all(&extract_dir);
    Ok(())
}
