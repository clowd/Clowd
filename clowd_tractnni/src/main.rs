//! AI effects inference — the out-of-process half of Clowd's video matting
//! and audio denoising features.
//!
//! `Clowd.VideoSDK`'s sidecar generators (`TractnniClient.cs`) spawn this
//! binary per job with one subcommand and stream the raw payload down stdin:
//! `matte` takes tightly packed RGB24 frames and answers each with a
//! RobustVideoMatting alpha (or RGBA foreground) frame; `denoise` takes f32le
//! interleaved 48 kHz PCM and answers with the same number of DPDFNet-denoised
//! samples. One job, one process, then gone — cancelling a job is killing us,
//! and a crash in native inference costs a per-job child rather than the
//! editor. The process boundary is also the licence boundary: the embedded
//! RobustVideoMatting weights make this one binary GPL-3.0 in an MIT repo
//! (see Cargo.toml).
//!
//! **stdout carries only the binary payload**, one output frame per input
//! frame, flushed per frame so the C# side can stream results as they arrive.
//! Everything human-readable goes to stderr, which the spawner pumps into a
//! bounded diagnostic ring.
//!
//! Inference runs on ONNX Runtime, statically linked by the `ort` crate
//! (pyke's prebuilt binaries, per target). Hardware execution providers are
//! registered per platform in [`register_execution_providers`]; a provider
//! that fails to register (no NVIDIA driver, say) falls back down the list
//! and ultimately to the CPU, so inference always works somewhere.
//!
//! There is no Sentry client here, by design — the same reasoning as
//! `clowd_ocr`: a process spawned per job would report release-health
//! sessions that measure jobs rather than app runs, and everything worth
//! reporting is visible to the spawner anyway, which sees our exit code and
//! our stderr and reports on our behalf.

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod denoise;
mod matte;

use std::io::Read;
use std::path::PathBuf;

use anyhow::bail;
use clap::{Parser, Subcommand};

#[derive(Parser, Debug)]
#[command(about = "Runs AI video/audio effect models over raw frames/samples piped through stdin/stdout")]
struct CliArgs {
    /// Optional log-file mirror. Terminal logging always goes to stderr,
    /// which the spawner pumps into its diagnostics; this puts the same
    /// lines (per-job timings, the resolved runtime path) somewhere a
    /// "denoise was slow" report can be diagnosed after the fact.
    #[arg(long, global = true)]
    log_file: Option<PathBuf>,

    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand, Debug)]
enum Command {
    /// RobustVideoMatting: RGB24 frames in, alpha (or RGBA foreground)
    /// frames out, one per input, in order.
    Matte {
        /// Frame width in pixels.
        #[arg(long)]
        width: u32,
        /// Frame height in pixels.
        #[arg(long)]
        height: u32,
        /// Output pixel format.
        #[arg(long, value_enum, default_value_t = matte::MatteFormat::Alpha)]
        format: matte::MatteFormat,
    },
    /// DPDFNet2 48 kHz speech denoising: f32le interleaved PCM in, the same
    /// channel layout and total sample count out, latency-compensated.
    Denoise {
        /// Interleaved channel count of the stdin PCM. Each channel runs
        /// through its own independent model state.
        #[arg(long)]
        channels: u32,
    },
}

fn main() -> anyhow::Result<()> {
    let args = CliArgs::parse();

    // stderr, never stdout: stdout is the binary result stream the C# side
    // parses byte-for-byte, so a single stray log line there corrupts a frame.
    let mut loggers: Vec<Box<dyn simplelog::SharedLogger>> = vec![simplelog::TermLogger::new(
        log::LevelFilter::Info,
        simplelog::Config::default(),
        simplelog::TerminalMode::Stderr,
        simplelog::ColorChoice::Auto,
    )];
    if let Some(path) = &args.log_file {
        if let Ok(file) = std::fs::File::create(path) {
            loggers.push(simplelog::WriteLogger::new(
                log::LevelFilter::Info,
                simplelog::Config::default(),
                std::io::LineWriter::new(file),
            ));
        }
    }
    // Plain simplelog, not `telemetry::install_logger`: that one bridges
    // `error!` into Sentry, and we have no Sentry client to bridge into.
    let _ = simplelog::CombinedLogger::init(loggers);

    let result = match args.command {
        Command::Matte {
            width,
            height,
            format,
        } => matte::run(width, height, format),
        Command::Denoise {
            channels,
        } => denoise::run(channels as usize),
    };
    if let Err(err) = &result {
        // Logged rather than reported: the spawner sees the non-zero exit and
        // does the reporting, and stderr is pumped so this line reaches it.
        log::error!("{err:#}");
    }
    result
}

/// Builds a session builder with the platform's hardware execution provider
/// registered: DirectML on Windows (any DX12 GPU; DirectML.dll ships beside
/// this exe — chosen over CUDA, which would need a user-installed CUDA
/// toolkit no end user has), CoreML on macOS. A provider that fails to
/// register is skipped and the CPU takes over, so this can never make
/// inference unavailable, only faster.
///
/// Registration is explicit per provider rather than through ort's own
/// fallback (`with_execution_providers`): that path logs failures through the
/// `tracing` feature this build turns off, which made "why is this running on
/// the CPU?" undiagnosable. Here every provider's outcome lands on stderr,
/// which the spawner pumps into its diagnostic ring.
pub fn ep_session_builder() -> anyhow::Result<ort::session::builder::SessionBuilder> {
    use ort::ep::ExecutionProvider;

    let mut builder = ort::session::Session::builder()?;
    // DirectML requires both of these (the DML allocator cannot serve the
    // memory-pattern planner, and parallel execution is CPU-only); they only
    // cost anything when every hardware provider fails, in which case CPU
    // inference is the least of the user's problems.
    // map_err rather than `?`: a builder-option error carries the builder
    // itself, which anyhow cannot hold (not Send/Sync).
    #[cfg(windows)]
    {
        builder = builder
            .with_memory_pattern(false)
            .map_err(|e| anyhow::anyhow!("disabling memory pattern: {e}"))?
            .with_parallel_execution(false)
            .map_err(|e| anyhow::anyhow!("selecting sequential execution: {e}"))?;
    }

    let eps: Vec<Box<dyn ExecutionProvider>> = vec![
        #[cfg(windows)]
        Box::new(ort::ep::DirectML::default()),
        #[cfg(target_os = "macos")]
        Box::new(ort::ep::CoreML::default()),
    ];
    for ep in &eps {
        match ep.register(&mut builder) {
            Ok(()) => log::info!("execution provider registered: {}", ep.name()),
            Err(e) => log::warn!("execution provider {} unavailable (trying the next one): {e}", ep.name()),
        }
    }
    Ok(builder)
}

/// Outcome of trying to fill a fixed-size buffer from a stream.
pub enum FrameRead {
    /// The buffer was filled completely.
    Frame,
    /// The stream ended cleanly at a frame boundary (zero bytes read).
    Eof,
}

/// Fill `buf` completely or report a clean EOF at a frame boundary. A stream
/// that ends *inside* a frame means the two sides disagree about the format —
/// a bug on our own side of a private protocol — so it fails loudly instead
/// of silently dropping a partial frame.
pub fn read_frame(reader: &mut impl Read, buf: &mut [u8]) -> anyhow::Result<FrameRead> {
    let mut filled = 0;
    while filled < buf.len() {
        match reader.read(&mut buf[filled..]) {
            Ok(0) if filled == 0 => return Ok(FrameRead::Eof),
            Ok(0) => bail!("stdin ended mid-frame: got {filled} of {} bytes", buf.len()),
            Ok(n) => filled += n,
            Err(e) if e.kind() == std::io::ErrorKind::Interrupted => continue,
            Err(e) => return Err(e.into()),
        }
    }
    Ok(FrameRead::Frame)
}

/// Fill as much of `buf` as the stream still carries; the final chunk before
/// EOF may be short. Used by `denoise`, whose input is a sample stream with
/// no inherent framing rather than fixed-size frames.
pub fn read_up_to(reader: &mut impl Read, buf: &mut [u8]) -> anyhow::Result<usize> {
    let mut filled = 0;
    while filled < buf.len() {
        match reader.read(&mut buf[filled..]) {
            Ok(0) => break,
            Ok(n) => filled += n,
            Err(e) if e.kind() == std::io::ErrorKind::Interrupted => continue,
            Err(e) => return Err(e.into()),
        }
    }
    Ok(filled)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A stream that ends exactly at a frame boundary is a clean EOF; one
    /// that ends inside a frame is a protocol bug and must error. Pins the
    /// distinction the matte loop's termination depends on.
    #[test]
    fn read_frame_distinguishes_eof_from_truncation() {
        let mut buf = [0u8; 4];

        let mut exact: &[u8] = &[1, 2, 3, 4];
        assert!(matches!(read_frame(&mut exact, &mut buf), Ok(FrameRead::Frame)));
        assert_eq!(buf, [1, 2, 3, 4]);
        assert!(matches!(read_frame(&mut exact, &mut buf), Ok(FrameRead::Eof)));

        let mut short: &[u8] = &[9, 9];
        assert!(read_frame(&mut short, &mut buf).is_err());
    }

    /// The tail chunk of a sample stream is allowed to be short.
    #[test]
    fn read_up_to_returns_short_final_chunk() {
        let mut data: &[u8] = &[1, 2, 3, 4, 5];
        let mut buf = [0u8; 4];
        assert_eq!(read_up_to(&mut data, &mut buf).unwrap(), 4);
        assert_eq!(read_up_to(&mut data, &mut buf).unwrap(), 1);
        assert_eq!(read_up_to(&mut data, &mut buf).unwrap(), 0);
    }
}
