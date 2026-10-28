use std::env;
use std::path::PathBuf;

use prisc_build_support::copy_if_newer;

/// Runtime plumbing for the FFmpeg libraries prisc_vid2gif links (via prisc_ffmpeg_sys):
///
/// - Windows: copy the FFmpeg DLLs (and their dependency DLLs) from the
///   obs-deps bundle next to the binary AND into the `deps` dir, so both
///   `prisc_vid2gif.exe` and the cargo test executables load without PATH setup.
/// - macOS: add rpaths so the dylibs resolve from the obs-deps bundle during
///   development and from `@executable_path/Frameworks` in the shipped
///   bundle (the release staging strips the absolute rpath).
/// - Linux: both of the above — copy the bundle's FFmpeg shared objects next
///   to the binary (under their SONAMEs, see prisc_build_support) and link with
///   RUNPATH `$ORIGIN` so the profile dir is self-contained, plus an absolute
///   RUNPATH into the bundle for the test executables in `deps/`.
///
/// The bundle location comes from prisc_ffmpeg_sys (`links = "ffmpeg"`), NOT from a
/// scan of `.deps` here: that ordering guarantee is the point. prisc_ffmpeg_sys
/// waits for the OBS build to download the bundle and picks the one matching
/// the target arch; a scan in this script could run first, find nothing, and
/// silently emit no rpath — leaving a test binary that aborts at startup with
/// `Library not loaded: @rpath/libavformat.dylib`.
fn main() {
    match env::var("CARGO_CFG_TARGET_OS").as_deref() {
        Ok("windows") => windows(),
        Ok("macos") => macos(),
        Ok("linux") => linux(),
        _ => {}
    }
}

fn windows() {
    let profile_dir = prisc_build_support::cargo_profile_dir();

    let Some(deps_bin) = env::var_os("DEP_FFMPEG_DEPS_BIN").map(PathBuf::from) else {
        println!("cargo:warning=prisc_vid2gif: obs-deps bundle not found; runtime DLLs not copied");
        return;
    };

    // The four linked FFmpeg DLLs plus everything they import.
    let dlls = [
        "avcodec-62.dll",
        "avformat-62.dll",
        "avutil-60.dll",
        "avfilter-11.dll",
        "swscale-9.dll",
        "swresample-6.dll",
        "zlib.dll",
        "libx264-164.dll",
        "libcurl.dll",
        "librist.dll",
        "srt.dll",
    ];
    for name in dlls {
        let src = deps_bin.join(name);
        // A missing path forces a re-run on the next build, self-healing the
        // fresh-checkout case where the deps download races this script.
        println!("cargo:rerun-if-changed={}", src.display());
        if src.exists() {
            copy_if_newer(&src, &profile_dir.join(name));
            copy_if_newer(&src, &profile_dir.join("deps").join(name));
        } else {
            println!(
                "cargo:warning=prisc_vid2gif: not found in obs-deps: {}",
                src.display()
            );
        }
    }
}

fn macos() {
    match env::var_os("DEP_FFMPEG_DEPS_LIB") {
        Some(deps_lib) => println!(
            "cargo:rustc-link-arg=-Wl,-rpath,{}",
            PathBuf::from(deps_lib).display()
        ),
        // Hard error, not a warning: a binary linked without this rpath builds
        // fine and then aborts the moment it runs.
        None => panic!(
            "prisc_vid2gif: DEP_FFMPEG_DEPS_LIB not set — prisc_ffmpeg_sys did not export the obs-deps \
             bundle path (is its `links = \"ffmpeg\"` key still present?)"
        ),
    }
    println!("cargo:rustc-link-arg=-Wl,-rpath,@executable_path/Frameworks");
}

fn linux() {
    let profile_dir = prisc_build_support::cargo_profile_dir();

    // Hard error, as on macOS: without it the binary links and then fails to
    // start.
    let root = env::var_os("DEP_FFMPEG_DEPS_ROOT").map(PathBuf::from).unwrap_or_else(|| {
        panic!(
            "prisc_vid2gif: DEP_FFMPEG_DEPS_ROOT not set — prisc_ffmpeg_sys did not export the FFmpeg              bundle path (is its `links = \"ffmpeg\"` key still present?)"
        )
    });
    let bundle = prisc_build_support::linux::FfmpegBundle { root };

    // $ORIGIN first, so a staged or relocated prisc_vid2gif always prefers the copies
    // beside it; the absolute bundle path serves the test executables in
    // `deps/` (the release staging strips it). `$ORIGIN` reaches the linker
    // verbatim: cargo passes link args without a shell.
    println!("cargo:rustc-link-arg=-Wl,-rpath,$ORIGIN");
    println!("cargo:rustc-link-arg=-Wl,-rpath,{}", bundle.lib().display());

    prisc_build_support::linux::stage_ffmpeg_runtime(&bundle, &profile_dir);
}
