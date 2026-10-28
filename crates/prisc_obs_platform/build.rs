use std::env;

// macOS and Linux concerns; on Windows the moved code needs nothing beyond the
// windows-sys import libs, and the runtime DLLs are staged into the shared
// cargo profile dir by prisc_recorder's build script.
fn main() {
    let target_os = env::var("CARGO_CFG_TARGET_OS").unwrap_or_default();
    if target_os == "macos" {
        build_macos();
    }
    if target_os == "linux" {
        build_linux();
    }
}

fn build_macos() {
    // enumerate_monitors talks straight to CoreGraphics/CoreFoundation via
    // hand-rolled extern decls (macos.rs), so anything that links this crate's
    // objects — including this crate's own test binary — needs the frameworks.
    println!("cargo:rustc-link-lib=framework=CoreGraphics");
    println!("cargo:rustc-link-lib=framework=CoreFoundation");

    // Bake the plugin path into the crate so no env var is needed at runtime
    // (default_obs_paths's env!("OBS_PLUGIN_DIR") — the dev-build fallback that
    // points at the OBS build tree in place). Consumers (prisc_recorder) used to
    // bake this themselves; the env! moved here with the code that reads it.
    let build_dir = env::var("DEP_OBS_OBS_BUILD_DIR").expect("DEP_OBS_OBS_BUILD_DIR not set");
    println!("cargo:rustc-env=OBS_PLUGIN_DIR={build_dir}/plugins");

    // rpaths so this crate's *test binary* can actually launch: it links
    // libobs.framework (via prisc_obs_sys), which in turn references the prebuilt
    // FFmpeg/x264/... dylibs by @rpath.
    prisc_build_support::link_obs_rpaths();
}

/// Linux: the system libraries the Linux platform module calls directly, plus
/// the test-harness RUNPATHs.
///
/// - X11 + Xrandr: session display handle for libobs (XOpenDisplay) and the
///   monitor list (XRRGetMonitors, in the order xshm_input_v2's `screen`
///   index uses).
/// - wayland-client: the wl_display libobs must be handed before obs_startup
///   under a Wayland session.
/// - glib-2.0: a GLib main loop. The PipeWire portal source's D-Bus replies
///   are dispatched on the default GMainContext, which nothing in libobs
///   iterates; without one the portal picker never gets past CreateSession.
///
/// `rustc-link-lib` (unlike `rustc-link-arg`) propagates to every dependent's
/// final link, and Rust links with --as-needed, so a library the code ends up
/// not using costs nothing. All four come from the -dev packages the OBS
/// build already requires (libx11-dev, libxrandr-dev, libwayland-dev,
/// libglib2.0-dev).
fn build_linux() {
    for lib in ["X11", "Xrandr", "wayland-client", "glib-2.0"] {
        println!("cargo:rustc-link-lib=dylib={lib}");
    }

    // Same as on macOS: absolute RUNPATHs so this crate's test binary finds
    // libobs.so.30 and the FFmpeg bundle (link args do not propagate).
    prisc_build_support::link_obs_rpaths();
}
