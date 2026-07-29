//! vid2gif — convert a video (mkv/mp4/…) into an optimized GIF using the
//! FFmpeg binaries bundled next to obs-express.
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
//! ffmpeg stage is killed, temp files and any partial output are removed, and
//! the final message is `cancelled`.
//!
//! The conversion runs as three ffmpeg passes rather than the classic two so
//! that progress streams smoothly and the source is decoded only once:
//!
//! A. input → small lossless intermediate with fps/scale baked in (progress
//!    streams; a palette output in the same run would suppress ffmpeg's
//!    periodic `-progress` reports until its encoder initializes at EOF);
//! B. intermediate → 256-color palette (cheap: runs at output size);
//! C. intermediate + palette → GIF (progress streams).

mod cancel;
mod presets;
mod probe;
mod progress;
mod tools;

use std::ffi::OsString;
use std::io::{BufRead, Write};
use std::path::{Path, PathBuf};

use anyhow::{bail, Context, Result};
use clap::Parser;

use cancel::{CancelToken, Cancelled};
use presets::Quality;
use progress::StageProgress;
use tools::Tools;

/// Overall-percent slices per stage: (base, span). Rough wall-time weights
/// measured on a 1080p input; B reports only on completion either way.
const STAGE_A: (u32, u32) = (0, 45);
const STAGE_B: (u32, u32) = (45, 20);
const STAGE_C: (u32, u32) = (65, 35);

/// Convert a video file to an optimized GIF.
///
/// Uses the ffmpeg/ffprobe bundled next to this executable (override the
/// directory with the VID2GIF_TOOLS_DIR environment variable). Progress is
/// reported on stdout as `progress <percent>` lines followed by a final
/// `done <path> <bytes>` or `error <message>` line.
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
    let cancel = CancelToken::new();
    spawn_stdin_watcher(cancel.clone());
    let mut emitter = Emitter::new();
    match run(&args, &mut emitter, &cancel) {
        Ok((path, bytes)) => {
            println!("done {} {bytes}", path.display());
        }
        Err(e) if e.downcast_ref::<Cancelled>().is_some() => {
            // Remove any partial output (stage C may have been mid-write).
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
    let tools = Tools::locate()?;
    let output = derive_output(&args.input, args.output.as_deref())?;
    let info = tools
        .probe(&args.input)
        .with_context(|| format!("could not read {}", args.input.display()))?;

    let fps = args.fps.unwrap_or(args.quality.fps());
    let scale_width =
        presets::clamp_width(info.width, info.height, args.max_width, args.max_height);

    if let Some(parent) = output.parent().filter(|p| !p.as_os_str().is_empty()) {
        std::fs::create_dir_all(parent)
            .with_context(|| format!("could not create {}", parent.display()))?;
    }

    let work = WorkDir::create()?;
    let mid = work.join("intermediate.mkv");
    let pal = work.join("palette.png");

    emitter.emit(0);

    // Stage A: decode the source once, bake in fps + scale, keep it lossless
    // (x264 qp 0; 4:4:4 so chroma survives for palette generation).
    let mut stage = StageProgress::new(STAGE_A.0, STAGE_A.1, info.duration_us);
    tools
        .run_ffmpeg(
            args_a(&args.input, fps, scale_width, &mid),
            &mut stage,
            &mut |p| emitter.emit(p),
            cancel,
        )
        .context("transcode stage failed")?;
    emitter.emit(STAGE_A.0 + STAGE_A.1);

    // Stage B: one global palette from the (small) intermediate.
    let mut stage = StageProgress::new(STAGE_B.0, STAGE_B.1, info.duration_us);
    tools
        .run_ffmpeg(
            args_b(&mid, &pal),
            &mut stage,
            &mut |p| emitter.emit(p),
            cancel,
        )
        .context("palette stage failed")?;
    emitter.emit(STAGE_B.0 + STAGE_B.1);

    // Stage C: dither the intermediate through the palette into the GIF.
    let mut stage = StageProgress::new(STAGE_C.0, STAGE_C.1, info.duration_us);
    tools
        .run_ffmpeg(
            args_c(&mid, &pal, args.quality, &output),
            &mut stage,
            &mut |p| emitter.emit(p),
            cancel,
        )
        .context("gif stage failed")?;
    emitter.emit(100);

    let bytes = std::fs::metadata(&output)
        .with_context(|| format!("output missing after conversion: {}", output.display()))?
        .len();
    Ok((output, bytes))
}

fn args_a(input: &Path, fps: u32, scale_width: Option<u32>, mid: &Path) -> Vec<OsString> {
    vec![
        "-i".into(),
        input.into(),
        "-vf".into(),
        presets::intermediate_vf(fps, scale_width).into(),
        "-c:v".into(),
        "libx264".into(),
        "-preset".into(),
        "ultrafast".into(),
        "-qp".into(),
        "0".into(),
        "-pix_fmt".into(),
        "yuv444p".into(),
        "-y".into(),
        mid.into(),
    ]
}

fn args_b(mid: &Path, pal: &Path) -> Vec<OsString> {
    vec![
        "-i".into(),
        mid.into(),
        "-vf".into(),
        presets::palettegen_vf().into(),
        "-update".into(),
        "1".into(),
        "-y".into(),
        pal.into(),
    ]
}

fn args_c(mid: &Path, pal: &Path, quality: Quality, output: &Path) -> Vec<OsString> {
    vec![
        "-i".into(),
        mid.into(),
        "-i".into(),
        pal.into(),
        "-lavfi".into(),
        presets::paletteuse_lavfi(quality).into(),
        "-f".into(),
        "gif".into(),
        "-y".into(),
        output.into(),
    ]
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

/// Collapses a (possibly multi-line ffmpeg stderr) message onto one bounded
/// line so it can't break the line-oriented stdout protocol.
fn single_line(msg: &str) -> String {
    let mut out = msg.split_whitespace().collect::<Vec<_>>().join(" ");
    const MAX: usize = 500;
    if out.chars().count() > MAX {
        out = out.chars().take(MAX).collect::<String>() + "…";
    }
    out
}

/// Temp dir for the intermediate + palette; removed on drop (including the
/// error path, since `run` returns before `main` prints).
struct WorkDir(PathBuf);

impl WorkDir {
    fn create() -> Result<WorkDir> {
        let dir = std::env::temp_dir().join(format!("vid2gif-{}", std::process::id()));
        std::fs::create_dir_all(&dir)
            .with_context(|| format!("could not create temp dir {}", dir.display()))?;
        Ok(WorkDir(dir))
    }

    fn join(&self, name: &str) -> PathBuf {
        self.0.join(name)
    }
}

impl Drop for WorkDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
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
