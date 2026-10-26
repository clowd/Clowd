//! vid2gif — convert a video (mkv/mp4/…) into an optimized GIF by linking the
//! FFmpeg libraries bundled next to obs-express (no subprocesses, no separate
//! ffmpeg binary).
//!
//! Stdout protocol (one message per line, nothing else is ever printed):
//!
//! ```text
//! progress <0-100>       monotonically increasing integer percent
//! done <path> <bytes>    conversion finished successfully
//! error <message>        conversion failed (single line; exit code 1)
//! cancelled              stdin cancellation honored (exit code 0)
//! ```
//!
//! Stdin protocol: writing `quit\n` cancels the conversion — the in-flight
//! pass stops within one packet, any partial output is removed, and the final
//! message is `cancelled`.
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

use std::io::{BufRead, Write};
use std::path::{Path, PathBuf};

use anyhow::{bail, Context, Result};
use clap::Parser;

use cancel::{CancelToken, Cancelled};
use presets::Quality;

/// Convert a video file to an optimized GIF.
///
/// Progress is reported on stdout as `progress <percent>` lines followed by a
/// final `done <path> <bytes>` or `error <message>` line. Writing `quit` to
/// stdin cancels.
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
    let args = Args::parse();
    convert::silence_info_logging();
    let cancel = CancelToken::new();
    spawn_stdin_watcher(cancel.clone());
    let mut emitter = Emitter::new();
    match run(&args, &mut emitter, &cancel) {
        Ok((path, bytes)) => {
            println!("done {} {bytes}", path.display());
        }
        Err(e) if e.downcast_ref::<Cancelled>().is_some() => {
            // Remove any partial output (pass 2 may have been mid-write).
            if let Ok(output) = derive_output(&args.input, args.output.as_deref()) {
                let _ = std::fs::remove_file(output);
            }
            println!("cancelled");
        }
        Err(e) => {
            println!("error {}", single_line(&format!("{e:#}")));
            std::process::exit(1);
        }
    }
}

/// Watches stdin for a `quit` line and trips the cancel token. The thread
/// blocks in read for the process lifetime; it dies with the process.
fn spawn_stdin_watcher(cancel: CancelToken) {
    std::thread::spawn(move || {
        let stdin = std::io::stdin();
        for line in stdin.lock().lines() {
            let Ok(line) = line else { break };
            if line.trim().eq_ignore_ascii_case("quit") {
                cancel.cancel();
                break;
            }
        }
    });
}

fn run(args: &Args, emitter: &mut Emitter, cancel: &CancelToken) -> Result<(PathBuf, u64)> {
    if !args.input.is_file() {
        bail!("input file not found: {}", args.input.display());
    }
    let output = derive_output(&args.input, args.output.as_deref())?;
    let info = convert::probe(&args.input)
        .with_context(|| format!("could not read {}", args.input.display()))?;

    let fps = args.fps.unwrap_or(args.quality.fps());
    let scale_width =
        presets::clamp_width(info.width, info.height, args.max_width, args.max_height);

    if let Some(parent) = output.parent().filter(|p| !p.as_os_str().is_empty()) {
        std::fs::create_dir_all(parent)
            .with_context(|| format!("could not create {}", parent.display()))?;
    }

    emitter.emit(0);
    convert::run(
        &args.input,
        &output,
        &presets::pass1_graph(fps, scale_width),
        &presets::pass2_graph(fps, scale_width, args.quality),
        cancel,
        &mut |p| emitter.emit(p),
    )?;
    emitter.emit(100);

    let bytes = std::fs::metadata(&output)
        .with_context(|| format!("output missing after conversion: {}", output.display()))?
        .len();
    Ok((output, bytes))
}

fn derive_output(input: &Path, explicit: Option<&Path>) -> Result<PathBuf> {
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

/// Prints `progress <n>` lines: deduplicated, monotonic, clamped to 100.
struct Emitter {
    last: i64,
}

impl Emitter {
    fn new() -> Emitter {
        Emitter { last: -1 }
    }

    fn emit(&mut self, percent: u32) {
        if let Some(p) = self.update(percent) {
            println!("progress {p}");
            let _ = std::io::stdout().flush();
        }
    }

    /// The printing decision, separated for testability.
    fn update(&mut self, percent: u32) -> Option<u32> {
        let p = percent.min(100) as i64;
        if p <= self.last {
            return None;
        }
        self.last = p;
        Some(p as u32)
    }
}

/// Collapses a possibly multi-line error message onto one bounded line so it
/// can't break the line-oriented stdout protocol.
fn single_line(msg: &str) -> String {
    let mut out = msg.split_whitespace().collect::<Vec<_>>().join(" ");
    const MAX: usize = 500;
    if out.chars().count() > MAX {
        out = out.chars().take(MAX).collect::<String>() + "…";
    }
    out
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
    fn emitter_is_monotonic_and_deduplicated() {
        let mut e = Emitter::new();
        assert_eq!(e.update(0), Some(0));
        assert_eq!(e.update(0), None);
        assert_eq!(e.update(7), Some(7));
        assert_eq!(e.update(5), None); // never goes backwards
        assert_eq!(e.update(200), Some(100)); // clamped
        assert_eq!(e.update(100), None);
    }

    #[test]
    fn single_line_collapses_and_bounds() {
        assert_eq!(single_line("a\r\nb\n  c"), "a b c");
        let long = "x".repeat(2000);
        let s = single_line(&long);
        assert!(s.chars().count() <= 501);
        assert!(s.ends_with('…'));
    }
}
