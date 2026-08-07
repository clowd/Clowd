fn main() {
    println!("cargo:rerun-if-changed=app.manifest");
    // baked into the Sentry release name via option_env! (telemetry/crash.rs), so a
    // version bump has to invalidate the cached build
    println!("cargo:rerun-if-env-changed=CLOWD_VERSION");

    #[cfg(windows)]
    {
        let manifest_dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();
        let manifest_path = format!("{}/app.manifest", manifest_dir);
        println!("cargo:rustc-link-arg-bins=/MANIFEST:EMBED");
        println!("cargo:rustc-link-arg-bins=/MANIFESTINPUT:{}", manifest_path);
    }
}
