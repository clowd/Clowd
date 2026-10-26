/// The application manifest is shared with the other Clowd binaries and
/// lives in `clowd_rust_core/` beside the Rust they share (see that crate's
/// manifest note). It cannot be embedded *by* that crate — `/MANIFEST:EMBED`
/// is a link argument, and only the crate producing the executable links one
/// — so each binary's build script points at the single copy instead of
/// keeping its own. What it buys us is per-monitor DPI awareness; a copy
/// that silently drifted would make one binary read virtualized coordinates
/// and photograph the wrong pixels.
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
    }
}
