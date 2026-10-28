fn main() {
    // `hello.version` bakes this in with option_env!, so a version bump has to invalidate the
    // cached build (the same reason prisc_rust_core's build.rs exists).
    println!("cargo:rerun-if-env-changed=PRISC_VERSION");
}
