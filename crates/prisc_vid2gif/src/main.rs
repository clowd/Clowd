//! prisc_vid2gif — convert a video (mkv/mp4/…) into an optimized GIF by linking the
//! FFmpeg libraries bundled next to prisc_recorder (no subprocesses, no separate
//! ffmpeg binary).
//!
//! It speaks the `job` catalog of Prisc's helper protocol
//! (crates/prisc_capture/HELPER_PROTOCOL.md §4.3): `hello`, `progress` lines,
//! then exactly one of `done{result:"ok",path,bytes}`, `done{result:"cancelled"}`
//! (exit 0) or a fatal `error` (exit 1, or 2 for unusable arguments). `quit`,
//! stdin EOF, a signal or a dead stdout cancel: the in-flight pass stops within
//! one packet and any partial output is removed.
//!
//! The conversion is the classic two-pass palette pipeline, in process:
//! pass 1 (`fps[,scale],palettegen`) keeps the single palette frame in
//! memory; pass 2 re-decodes the input through `paletteuse` into the GIF
//! encoder. Progress derives from input frame timestamps, so it streams
//! smoothly through both passes.

mod cancel;
mod convert;
mod presets;
mod progress;

use std::ops::ControlFlow;
use std::path::{Path, PathBuf};

use anyhow::{bail, Context};
use clap::Parser;
use prisc_ipc::catalog::job::{self, Event, JobResult};
use prisc_ipc::catalog::ErrorCode;
use prisc_ipc::Inbound;

use cancel::{CancelToken, Cancelled};
use presets::Quality;

/// Convert a video file to an optimized GIF.
///
/// Speaks Prisc's helper protocol on stdout (`progress`, then `done` or a
/// fatal `error`). `{"type":"quit"}` on stdin, or closing stdin, cancels.
#[derive(Parser)]
#[command(version)]
struct Args {
    /// Input video file (anything the bundled FFmpeg can decode).
    input: PathBuf,

    /// Output GIF path [default: the input path with a .gif extension]
    output: Option<PathBuf>,

    /// Quality preset; trades frame rate and dithering fidelity for file size.
    #[arg(long, value_enum, default_value_t = Quality::Good)]
    quality: Quality,

    /// Cap the output width in pixels, preserving aspect (never upscales).
    /// With --max-height, the more restrictive of the two wins.
    #[arg(long, value_parser = clap::value_parser!(u32).range(1..))]
    max_width: Option<u32>,

    /// Cap the output height in pixels, preserving aspect (never upscales).
    #[arg(long, value_parser = clap::value_parser!(u32).range(1..))]
    max_height: Option<u32>,

    /// Override the preset's output frame rate.
    #[arg(long, value_parser = clap::value_parser!(u32).range(1..=120))]
    fps: Option<u32>,
}

fn main() {
    // `hello` first, before FFmpeg is initialised or called, with stdout
    // claimed so nothing FFmpeg prints can reach the protocol pipe.
    let out = prisc_ipc::open::<Event>("prisc_vid2gif");
    let _sentry = prisc_rust_core::telemetry::init_job("prisc_vid2gif");
    prisc_ipc::lifetime::on_exit(prisc_rust_core::telemetry::shutdown_hook);

    let args = Args::parse();
    convert::silence_info_logging();

    let cancel = CancelToken::new();
    let quit = cancel.clone();
    prisc_ipc::lifetime::install(move |_| quit.cancel(), job::QUIT_GRACE + prisc_ipc::SELF_EXIT_MARGIN);
    // The job catalog has no commands: anything but `quit` is a bad command.
    // Answered from the reader thread, since there are no acks to stay in
    // order with.
    let errors = out.clone();
    prisc_ipc::read_commands::<job::Command>(move |inbound| {
        match inbound {
            Inbound::Command(command) => match command {},
            Inbound::Malformed { reason } => errors.error(ErrorCode::BadCommand, reason, false),
        }
        ControlFlow::Continue(())
    });

    let mut progress = Progress::new();
    let mut emit = |percent| {
        if let Some(percent) = progress.update(percent) {
            out.emit(&Event::Progress { percent });
        }
    };
    let mut written = None;
    let result = run(&args, &mut emit, &cancel, &mut written);
    if result.is_err() {
        // Remove any partial output (pass 2 may have been mid-write), but only
        // a file this run started writing: an output path that already existed
        // is left alone when the run never got that far.
        if let Some(output) = &written {
            let _ = std::fs::remove_file(output);
        }
    }
    match result {
        Ok((path, bytes)) => {
            out.emit(&Event::Done {
                result: JobResult::Ok,
                path: Some(path.to_string_lossy().into_owned()),
                bytes: Some(bytes),
            });
        }
        Err(failure) if failure.error.downcast_ref::<Cancelled>().is_some() => {
            out.emit(&Event::Done {
                result: JobResult::Cancelled,
                path: None,
                bytes: None,
            });
        }
        Err(failure) => out.fatal(failure.code, format!("{:#}", failure.error)),
    }
    prisc_ipc::lifetime::exit(prisc_ipc::exit::OK)
}

/// A failed run, with the protocol error code it is reported under.
struct Failure {
    code: ErrorCode,
    error: anyhow::Error,
}

impl From<anyhow::Error> for Failure {
    fn from(error: anyhow::Error) -> Self {
        Self { code: ErrorCode::Failed, error }
    }
}

/// Tags an error with the code it is reported under.
fn coded(code: ErrorCode) -> impl FnOnce(anyhow::Error) -> Failure {
    move |error| Failure { code, error }
}

/// The conversion. `written` is set to the output path just before the first
/// byte of it can be written, so a failure knows what to clean up.
fn run(
    args: &Args,
    emit: &mut dyn FnMut(u32),
    cancel: &CancelToken,
    written: &mut Option<PathBuf>,
) -> Result<(PathBuf, u64), Failure> {
    if !args.input.is_file() {
        return Err(Failure {
            code: ErrorCode::IoFailed,
            error: anyhow::anyhow!("input file not found: {}", args.input.display()),
        });
    }
    let output = derive_output(&args.input, args.output.as_deref()).map_err(coded(ErrorCode::Usage))?;
    let info = convert::probe(&args.input)
        .with_context(|| format!("could not read {}", args.input.display()))
        .map_err(coded(ErrorCode::InitFailed))?;

    let fps = args.fps.unwrap_or(args.quality.fps());
    let scale_width =
        presets::clamp_width(info.width, info.height, args.max_width, args.max_height);

    if let Some(parent) = output.parent().filter(|p| !p.as_os_str().is_empty()) {
        std::fs::create_dir_all(parent)
            .with_context(|| format!("could not create {}", parent.display()))
            .map_err(coded(ErrorCode::IoFailed))?;
    }

    emit(0);
    *written = Some(output.clone());
    convert::run(
        &args.input,
        &output,
        &presets::pass1_graph(fps, scale_width),
        &presets::pass2_graph(fps, scale_width, args.quality),
        cancel,
        emit,
    )?;
    emit(100);

    let bytes = std::fs::metadata(&output)
        .with_context(|| format!("output missing after conversion: {}", output.display()))
        .map_err(coded(ErrorCode::IoFailed))?
        .len();
    Ok((output, bytes))
}

fn derive_output(input: &Path, explicit: Option<&Path>) -> anyhow::Result<PathBuf> {
    let output = match explicit {
        Some(p) => p.to_path_buf(),
        None => input.with_extension("gif"),
    };
    if output == input {
        bail!(
            "output path equals input path ({}); pass an explicit output",
            input.display()
        );
    }
    Ok(output)
}

/// Which `progress` values go out: deduplicated, monotonic, clamped to 100.
struct Progress {
    last: i64,
}

impl Progress {
    fn new() -> Progress {
        Progress { last: -1 }
    }

    /// The percent to emit, or `None` when it would repeat or go backwards.
    fn update(&mut self, percent: u32) -> Option<u32> {
        let p = percent.min(100) as i64;
        if p <= self.last {
            return None;
        }
        self.last = p;
        Some(p as u32)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn derive_output_swaps_extension() {
        let out = derive_output(Path::new("C:/x/clip.mkv"), None).unwrap();
        assert_eq!(out, Path::new("C:/x/clip.gif"));
    }

    #[test]
    fn derive_output_honors_explicit() {
        let out = derive_output(Path::new("in.mkv"), Some(Path::new("out/o.gif"))).unwrap();
        assert_eq!(out, Path::new("out/o.gif"));
    }

    #[test]
    fn derive_output_rejects_gif_input_without_output() {
        let err = derive_output(Path::new("clip.gif"), None).unwrap_err();
        assert!(err.to_string().contains("equals input"), "{err}");
    }

    #[test]
    fn progress_is_monotonic_and_deduplicated() {
        let mut e = Progress::new();
        assert_eq!(e.update(0), Some(0));
        assert_eq!(e.update(0), None);
        assert_eq!(e.update(7), Some(7));
        assert_eq!(e.update(5), None); // never goes backwards
        assert_eq!(e.update(200), Some(100)); // clamped
        assert_eq!(e.update(100), None);
    }
}
