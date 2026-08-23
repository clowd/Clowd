/// The application manifest is shared with the other Clowd binaries and
/// lives in `clowd_rust_core/` beside the Rust they share (see that crate's
/// manifest note). It cannot be embedded *by* that crate — `/MANIFEST:EMBED`
/// is a link argument, and only the crate producing the executable links one
/// — so each binary's build script points at the single copy instead of
/// keeping its own. What it buys us is per-monitor DPI awareness; a copy
/// that silently drifted would make one binary read virtualized coordinates
/// and photograph the wrong pixels.
///
/// The ONNX Runtime itself needs nothing from this script: `ort-sys` (the
/// `ort` crate's build) downloads pyke's prebuilt runtime for the cargo
/// TARGET and links it statically, and its `copy-dylibs` feature stages the
/// DirectML EP's helper dylib (DirectML.dll) beside the built exe.
const SHARED_MANIFEST: &str = "../clowd_rust_core/app.manifest";

fn main() {
    println!("cargo:rerun-if-changed={SHARED_MANIFEST}");
    // baked into the Sentry release name via option_env! (clowd_rust_core's
    // telemetry::release), so a version bump has to invalidate the cached build
    println!("cargo:rerun-if-env-changed=CLOWD_VERSION");

    #[cfg(windows)]
    {
        let manifest_dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();
        let manifest_path = std::path::Path::new(&manifest_dir).join(SHARED_MANIFEST);
        // Fail loudly rather than link an unmanifested exe: without the
        // manifest the process is DPI-virtualized, which is a subtle
        // wrong-pixels bug rather than an obvious one.
        assert!(
            manifest_path.is_file(),
            "shared app manifest not found at {}",
            manifest_path.display()
        );
        println!("cargo:rustc-link-arg-bins=/MANIFEST:EMBED");
        println!("cargo:rustc-link-arg-bins=/MANIFESTINPUT:{}", manifest_path.display());

        // The one binary in the workspace that must NOT be `+crt-static`:
        // pyke's prebuilt ONNX Runtime static libs are compiled /MD, and their
        // `__imp_*` CRT references cannot resolve against the static CRT the
        // workspace's .cargo/config.toml selects (rustc treats any occurrence
        // of +crt-static as final, so a config-level opt-out is impossible).
        // rustc contributes the static CRT only as `/defaultlib:libcmt`, which
        // NODEFAULTLIB suppresses cleanly — then the whole link resolves
        // against the dynamic CRT import libs instead, exactly one CRT in the
        // image. The exe then imports msvcp140/vcruntime140 at runtime; CI
        // ships those app-local in the Windows packages. rustc-link-arg (not
        // -bins) so the crate's test binaries link the same way.
        for lib in [
            "libcmt",
            "libcmtd",
            "libucrt",
            "libucrtd",
            "libvcruntime",
            "libvcruntimed",
            "libcpmt",
            "libcpmtd",
        ] {
            println!("cargo:rustc-link-arg=/NODEFAULTLIB:{lib}.lib");
        }
        for lib in ["msvcrt", "msvcprt", "ucrt", "vcruntime"] {
            println!("cargo:rustc-link-arg=/DEFAULTLIB:{lib}.lib");
        }
    }
}
