fn main() {
    // `telemetry::release` bakes this into the Sentry release name with
    // option_env!, so a version bump has to invalidate the cached build —
    // otherwise a stale core object file would keep reporting the previous
    // release while the binaries around it report the new one.
    println!("cargo:rerun-if-env-changed=CLOWD_VERSION");
}
