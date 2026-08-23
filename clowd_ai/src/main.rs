//! On-device AI inference — the out-of-process half of Clowd's text
//! recognition, video matting and audio denoising features.
//!
//! One binary, one subcommand per job kind, one process per job:
//!
//! * `ocr` — the capture overlay (`clowd_capture_wgpu`) spawns it per OCR
//!   press, writes a [`RequestHeader`](clowd_rust_core::ocr::RequestHeader)
//!   line and the selected region's raw BGRA down stdin, and reads the
//!   PaddleOCR result from the `--out` file after it exits (see
//!   `clowd_rust_core::ocr` for the wire format and [`ocr`] for the pipeline).
//! * `matte` / `denoise` — `Clowd.VideoSDK`'s sidecar generators
//!   (`AiClient.cs`) spawn it per job and stream the raw payload down stdin:
//!   `matte` takes tightly packed RGB24 frames and answers each with a
//!   RobustVideoMatting alpha (or RGBA foreground) frame; `denoise` takes f32le
//!   interleaved 48 kHz PCM and answers with the same number of
//!   DPDFNet-denoised samples.
//!
//! One job, one process, then gone — canceling a job is killing us, and a
//! crash in native inference costs a per-job child rather than the editor or
//! the overlay mid-capture. The process boundary is also the license boundary:
//! the embedded RobustVideoMatting weights make this one binary GPL-3.0 in an
//! MIT repo (see Cargo.toml).
//!
//! **stdout carries only the binary payload** of `matte`/`denoise`, one output
//! frame per input frame, flushed per frame so the C# side can stream results
//! as they arrive; `ocr` writes nothing to it. Everything human-readable goes
//! to stderr, which every spawner pumps into its own diagnostics. That rule
//! binds the native libraries too, and they do not honour it on their own, so
//! [`claim_payload_stdout`] takes the pipe away from them before any model is
//! built (see there).
//!
//! Inference runs on ONNX Runtime, statically linked by the `ort` crate
//! (pyke's prebuilt binaries, per target). The video/audio effects register the
//! platform's hardware execution provider ([`ep_session_builder`]); a provider
//! that fails to register (no DX12 GPU, say) falls back to the CPU, so
//! inference always works somewhere. OCR deliberately stays on the CPU (see
//! `ocr.rs`).
//!
//! There is no Sentry client here, by design: a process spawned per job would
//! report release-health sessions that measure key presses and jobs rather
//! than app runs, and everything worth reporting is visible to the spawner
//! anyway, which sees our exit code and our stderr and reports on our behalf.
//! What the OCR spawner cannot see by itself is *why* we panicked, so
//! [`ocr::install_panic_reporter`] leaves that in the response file for it to
//! pick up.

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod denoise;
mod matte;
mod ocr;

use std::io::Read;
use std::path::PathBuf;

use anyhow::bail;
use clap::{Parser, Subcommand};

#[derive(Parser, Debug)]
#[command(about = "Runs Clowd's on-device AI models (OCR, video matting, audio denoising) as a child process")]
struct CliArgs {
    /// Optional log-file mirror. Terminal logging always goes to stderr,
    /// which the spawner pumps into its diagnostics; this puts the same
    /// lines (per-job timings, det/rec timings and the OCR tier choice)
    /// somewhere a "denoise was slow" or "OCR was slow" report can be
    /// diagnosed after the fact.
    #[arg(long, global = true)]
    log_file: Option<PathBuf>,

    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand, Debug)]
enum Command {
    /// PaddleOCR text recognition: one request (JSON header line + raw BGRA)
    /// on stdin, one JSON response in the `--out` file.
    Ocr {
        /// Where to write the JSON response. The capturer puts this in the
        /// capture's session directory when there is one (OCR also works
        /// standalone, with no session), and reads it only after this process
        /// exits 0.
        #[arg(long)]
        out: PathBuf,
    },
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

    // The payload subcommands take the pipe for themselves first; `ocr` has no
    // payload stream, and leaving stdout alone keeps its diagnostics where a
    // developer running it by hand expects them.
    let result = match args.command {
        Command::Ocr {
            out,
        } => ocr::run(out),
        Command::Matte {
            width,
            height,
            format,
        } => claim_payload_stdout().and_then(|payload| matte::run(width, height, format, payload)),
        Command::Denoise {
            channels,
        } => claim_payload_stdout().and_then(|payload| denoise::run(channels as usize, payload)),
    };
    if let Err(err) = &result {
        // Logged rather than reported: the spawner sees the non-zero exit and
        // does the reporting, and stderr is pumped so this line reaches it.
        log::error!("{err:#}");
    }
    result
}

/// Hands the caller a private duplicate of stdout and points this process's
/// stdout at stderr, so `matte`/`denoise` own their pipe outright.
///
/// The rule that stdout carries payload bytes and nothing else is one our own
/// code keeps easily and the native inference stack does not keep at all.
/// CoreML's runtime writes its model-compilation complaints straight to fd 1
/// ("E5RT encountered an STL exception. msg = ..."), a kilobyte and a half of
/// them for RVM on macOS 26, and they land in the C runtime's block buffer to
/// be flushed at some arbitrary later point — which is to say, spliced into
/// the middle of a matte frame. The C# reader counts bytes, so every frame
/// after the splice is torn: it reports "clowd_ai's output ended N bytes into
/// a matte frame" and the whole analysis fails, however healthy the inference
/// was. A log line is not worth a failed job, and there is no way to ask a
/// native library to stop, so the pipe simply stops being reachable by fd 1.
///
/// Duplicate first, redirect second, and the returned [`File`] writes to the
/// original pipe by its own descriptor while everything reaching fd 1 — ours,
/// ONNX Runtime's, CoreML's, DirectML's — goes to stderr, which the spawner
/// already pumps into its diagnostic ring. Must run before the session is
/// built: CoreML writes while it compiles the model, not while it infers.
///
/// The descriptor is what matters rather than the std handle: native code
/// prints through the C runtime, whose fd 1 was bound at its own start-up, so
/// only `dup2` moves it (on Windows the CRT's `_dup2`, for the same reason —
/// and the duplicate there is a handle rather than a CRT fd, so the payload
/// never passes through text-mode newline translation).
fn claim_payload_stdout() -> anyhow::Result<std::fs::File> {
    #[cfg(unix)]
    let payload = {
        use std::os::fd::AsFd;
        let stdout = std::io::stdout();
        std::fs::File::from(stdout.as_fd().try_clone_to_owned()?)
    };
    #[cfg(windows)]
    let payload = {
        use std::os::windows::io::AsHandle;
        let stdout = std::io::stdout();
        std::fs::File::from(stdout.as_handle().try_clone_to_owned()?)
    };

    // SAFETY: a plain descriptor-table edit. `dup2` closes the old fd 1 — the
    // pipe — but the duplicate above already holds it open, so nothing the
    // spawner reads from is released here.
    //
    // A failure is logged rather than raised: fd 1 then still points at the
    // pipe, which is exactly where it pointed before this function existed, so
    // the job runs as it always did, merely unguarded.
    if unsafe { libc::dup2(2, 1) } < 0 {
        log::warn!(
            "could not point stdout at stderr ({}) — a native library printing to stdout \
             would corrupt the payload stream",
            std::io::Error::last_os_error()
        );
    }
    Ok(payload)
}

/// Whether CoreML may take a model.
///
/// Worth knowing before reading either variant, because it is not what the
/// name "hardware execution provider" suggests: on macOS CoreML is *not* a
/// route to the Neural Engine for anything this crate runs. Asking CoreML for
/// its own compute plan (`ProfileComputePlan`) reports every operation of
/// RVM, of DPDFNet and of the PP-OCRv6 detector on `MLCPUComputeDevice`, under
/// every `MLComputeUnits` setting including `CPUAndNeuralEngine`. Why it
/// refuses is inferred rather than measured — the ANE wants static shapes,
/// and all three graphs are exported with dynamic dimensions — but that it
/// refuses is not. So the real choice here is between two CPU
/// implementations: CoreML's (Accelerate/AMX, ~1.2 cores busy) and ONNX
/// Runtime's own (portable SIMD, as many threads as there are cores). Which
/// one wins is a per-machine race between efficiency and core count, and the
/// answer below was measured on an M2 Pro (8 P + 4 E) — see each variant.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum CoreMl {
    /// Register CoreML on macOS like any other platform provider.
    Allowed,
    /// Keep this model on ONNX Runtime's own CPU provider.
    ///
    /// The 48 kHz speech enhancer is the one that asks for this, for two
    /// reasons, in this order:
    ///
    /// 1. It is *slower* on CoreML. DPDFNet is a small recurrent graph run
    ///    once per 10 ms audio frame — ~100 dispatches a second, split across
    ///    six CoreML partitions with the recurrent state crossing the boundary
    ///    every time — and per-dispatch overhead swamps a graph that size. 10 s
    ///    of mono measured 1.98 s of inference on the CPU against 2.54 s on
    ///    CoreML, plus ~0.9 s more to build the session. There is no hardware
    ///    being left on the table (see the type's own docs); it is simply the
    ///    slower of two CPU paths here.
    /// 2. On CoreML's default `NeuralNetwork` model format it does not merely
    ///    run slowly, it fails outright: that format pads every tensor to
    ///    rank 5, so DPDFNet's rank-3 recurrent state comes back as
    ///    `{1,1,1,96,96}` where the graph inferred `{1,1,96}` and every
    ///    inference dies in `GetStaticOutputShape` with "different ranks".
    ///    [`ep_session_builder`] asks for `MLProgram` instead, which compiles
    ///    the same graph correctly and to within 1.5e-6 of the CPU — so this
    ///    variant is a performance decision now, not a crash workaround, and
    ///    flipping it back to [`Allowed`](CoreMl::Allowed) would produce
    ///    correct output, just less of it per second.
    ///
    /// Windows is unaffected either way: DirectML takes this model there.
    Declined,
}

/// Builds a session builder with the platform's hardware execution provider
/// registered: DirectML on Windows (any DX12 GPU; DirectML.dll ships beside
/// this exe — chosen over CUDA, which would need a user-installed CUDA
/// toolkit no end user has), CoreML on macOS unless the caller passes
/// [`CoreMl::Declined`]. A provider that fails to register is skipped and
/// the CPU takes over, so registration can never make inference unavailable,
/// only faster.
///
/// Registration is explicit per provider rather than through ort's own
/// fallback (`with_execution_providers`): that path logs failures through the
/// `tracing` feature this build turns off, which made "why is this running on
/// the CPU?" undiagnosable. Here every provider's outcome lands on stderr,
/// which the spawner pumps into its diagnostic ring.
/// The hardware execution providers to try on this build's target, most
/// preferred first.
///
/// One function per platform rather than one function with `cfg` attributes
/// inside it: the providers differ per target, so the *list* is what is
/// per-target, and writing each as its own literal keeps every arm honest —
/// no unused parameter to silence on the targets that ignore it, and no
/// `Vec::new()` that only some targets go on to push to (clippy reads that
/// last shape as `vec_init_then_push`, which it is, on Windows).
#[cfg(windows)]
fn platform_providers(_coreml: CoreMl) -> Vec<Box<dyn ort::ep::ExecutionProvider>> {
    // DirectML takes both models here, so nothing declines it — the parameter
    // exists for the macOS arm and is deliberately unread.
    vec![Box::new(ort::ep::DirectML::default())]
}

#[cfg(target_os = "macos")]
fn platform_providers(coreml: CoreMl) -> Vec<Box<dyn ort::ep::ExecutionProvider>> {
    if coreml != CoreMl::Allowed {
        log::info!("CoreML skipped: this model measured faster on the CPU provider");
        return Vec::new();
    }

    vec![Box::new(
        ort::ep::CoreML::default()
            // MLProgram, not the `NeuralNetwork` default. That default is an
            // fp16 format, and the precision is not academic: RVM's first
            // frames come back visibly wrong on it — mean |Δalpha| 37.8/255
            // against the fp32 reference on frame 0, decaying over the next
            // ~10 frames as the recurrent state warms, i.e. a wrong matte at
            // the head of every clip. MLProgram matches the CPU to within one
            // 8-bit step everywhere. It also rank-pads nothing, which is what
            // makes the speech enhancer merely slow rather than broken (see
            // [`CoreMl::Declined`]). The cost is session build time: ~1.0 s
            // against ~0.37 s, paid once per job.
            .with_model_format(ort::ep::coreml::ModelFormat::MLProgram)
            // Not the `ALL` default, which measured 10-30% slower and far
            // noisier (48-50 ms/frame against 53-67 for RVM at 960x540).
            // `ALL` lets CoreML's planner shop partitions around the GPU;
            // narrowing the choice skips that, and costs nothing real, because
            // the ANE does not take these graphs anyway and the work lands on
            // the CPU under either setting.
            .with_compute_units(ort::ep::coreml::ComputeUnits::CPUAndNeuralEngine),
    )]
}

/// No hardware provider we ship for; ONNX Runtime's own CPU provider takes
/// everything.
#[cfg(not(any(windows, target_os = "macos")))]
fn platform_providers(_coreml: CoreMl) -> Vec<Box<dyn ort::ep::ExecutionProvider>> {
    Vec::new()
}

pub fn ep_session_builder(coreml: CoreMl) -> anyhow::Result<ort::session::builder::SessionBuilder> {
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

    let eps = platform_providers(coreml);
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
