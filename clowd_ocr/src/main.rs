//! Text recognition — the out-of-process half of the OCR feature.
//!
//! By the time this binary runs the overlay (`clowd_capture_wgpu`) has
//! already done its part: the user captured a region, pressed OCR, and the
//! overlay extracted that region's pixels — compositing a click-locked peek
//! if one is up, so what is recognized is what the user can actually see. It
//! spawns this process per request, writes a
//! [`RequestHeader`](clowd_rust_core::ocr::RequestHeader) line and the raw
//! BGRA down our stdin, and waits for us to exit.
//!
//! One request, one process, one answer, then gone. That is the whole design:
//! recognition runs on a static C++ engine (MNN, via `ocr-rs`), and the
//! failures worth isolating from an in-flight capture — `abort`, segfault, a
//! refused allocation on a degenerate selection — are exactly the ones no
//! amount of in-process defensiveness catches. See `clowd_rust_core::ocr` for
//! the wire format and `paddle` for the pipeline.
//!
//! Nothing is written to stdout. MNN prints device capabilities there on
//! session creation, so the result goes to the `--out` file instead; the
//! capturer redirects our stdout to null because its *own* stdout is the
//! NDJSON host protocol `Clowd.Ui` parses, and MNN's chatter reaching it
//! would corrupt that.
//!
//! There is no Sentry client here, by design. A process spawned per key press
//! would report release-health sessions that measure key presses rather than
//! app runs, and everything worth reporting is visible to the capturer anyway:
//! it already turns our exit code into an `error!`. What it cannot see by
//! itself is *why* we panicked, so [`install_panic_reporter`] leaves that in
//! the response file for it to pick up and report on our behalf.

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod paddle;

use std::io::{BufRead, BufReader, Read};
use std::path::PathBuf;
use std::sync::OnceLock;

use anyhow::{bail, Context};
use clap::Parser;
use clowd_rust_core::ocr::{OcrError, OcrRequest, OcrResponse, RequestHeader};

#[derive(Parser, Debug)]
#[command(about = "Recognizes text in a screen region handed to it by the Clowd capture overlay")]
struct CliArgs {
    /// Where to write the JSON response. The capturer puts this in the
    /// capture's session directory when there is one (OCR also works
    /// standalone, with no session), and reads it only after this process
    /// exits 0.
    #[arg(long)]
    out: PathBuf,

    /// Optional log-file mirror. Terminal logging always goes to stderr,
    /// which the capturer inherits and the shell pumps into its diagnostics;
    /// this is what puts the same lines — the det/rec timings and the tier
    /// choice — in the session directory beside `capture.log`, where a
    /// user's "OCR was slow" report can be diagnosed after the fact.
    #[arg(long)]
    log_file: Option<PathBuf>,
}

fn main() -> anyhow::Result<()> {
    let args = CliArgs::parse();

    // stderr, never stdout: stdout belongs to MNN's chatter here (see the
    // module docs), and the capturer nulls it.
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

    // Before anything that could panic, and it needs the path, so it cannot
    // move above the argument parse.
    let _ = OUT_PATH.set(args.out.clone());
    install_panic_reporter();

    let result = run(args);
    if let Err(err) = &result {
        // Logged rather than reported: the capturer sees the non-zero exit and
        // does the reporting, and stderr is inherited so this line reaches it.
        log::error!("{err:#}");
    }
    result
}

/// Where a panic should leave its explanation. Set once, from `main`.
static OUT_PATH: OnceLock<PathBuf> = OnceLock::new();

/// Leave a panicking run's message in the response file on the way down.
///
/// The process still dies with Rust's exit code 101, so the capturer still
/// treats it as an abnormal exit and does not use the file as a result — it
/// reads it only for this message, and reports that instead of a bare exit
/// code. Without it, the most likely failure in this binary (a Rust panic, far
/// more likely than the MNN `abort` the process split exists for) would reach
/// Sentry as "exited with 101" and nothing else.
fn install_panic_reporter() {
    let previous = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        if let Some(path) = OUT_PATH.get() {
            // `info` Displays as "panicked at src/paddle.rs:279:14:\n<msg>" —
            // location and message, which is what identifies the bug. No
            // backtrace: this is an LTO'd release build, where the frames are
            // largely inlined away, and the response file is not the place for
            // a page of them.
            let response: OcrResponse = Err(OcrError::Failed(format!("recognizer {info}")));
            if let Ok(json) = serde_json::to_vec(&response) {
                let _ = std::fs::write(path, json);
            }
        }
        previous(info);
    }));
}

fn run(args: CliArgs) -> anyhow::Result<()> {
    let request = read_request().context("reading the OCR request from stdin")?;
    log::info!("recognizing {}x{} at {:?}", request.width, request.height, request.origin);

    // A recognition that runs and fails is part of the answer, not a failure
    // of this process: it goes in the response file and we still exit 0, so
    // the capturer can tell "the engine is unavailable on this machine" apart
    // from "the child died", which is all a non-zero exit can mean.
    let response: OcrResponse = paddle::recognize(&request);
    if let Err(e) = &response {
        log::warn!("recognition failed: {e:?}");
    }

    let json = serde_json::to_vec(&response).context("serializing the OCR response")?;
    std::fs::write(&args.out, &json).with_context(|| format!("writing the OCR response to {}", args.out.display()))?;
    Ok(())
}

/// Read one request: a single JSON header line, then exactly
/// `header.payload_len()` bytes of tightly packed BGRA through to EOF.
///
/// The length is checked rather than trusted. A short payload would otherwise
/// reach `RgbImage::from_raw` as a `None` and panic inside recognition, and a
/// long one means the two sides disagree about the format — both are bugs on
/// our own side of a private protocol, so they fail loudly here.
fn read_request() -> anyhow::Result<OcrRequest> {
    let mut stdin = BufReader::new(std::io::stdin().lock());

    let mut line = String::new();
    let read = stdin
        .read_line(&mut line)
        .context("reading the request header line")?;
    if read == 0 {
        bail!("stdin closed before the request header arrived");
    }
    let header: RequestHeader = serde_json::from_str(line.trim_end()).with_context(|| format!("parsing the request header {line:?}"))?;

    // read_to_end on the same BufReader, which drains what the header read
    // already buffered before touching the pipe again.
    let mut bgra = Vec::with_capacity(header.payload_len());
    stdin
        .read_to_end(&mut bgra)
        .context("reading the pixel payload")?;
    if bgra.len() != header.payload_len() {
        bail!(
            "pixel payload is {} bytes, expected {} for {}x{}",
            bgra.len(),
            header.payload_len(),
            header.width,
            header.height
        );
    }

    Ok(OcrRequest {
        bgra,
        width: header.width,
        height: header.height,
        origin: header.origin,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The panic path is the recognizer's only channel for explaining itself —
    /// it has no Sentry client, and the capturer reads this file for a message
    /// when we exit abnormally. Verified rather than assumed, because a hook
    /// that silently failed would degrade every future panic to "exited with
    /// code 101" with nothing to go on.
    #[test]
    fn panic_reporter_leaves_its_message_in_the_response_file() {
        let path = std::env::temp_dir().join(format!("clowd_ocr_panic_test_{}.json", std::process::id()));
        let _ = std::fs::remove_file(&path);
        OUT_PATH
            .set(path.clone())
            .expect("no other test sets the out path");
        install_panic_reporter();

        // The chained hook still prints to stderr, so the deliberate panic is
        // noisy in the test output. That is the real behaviour.
        let panicked = std::panic::catch_unwind(|| panic!("deliberate test panic"));
        assert!(panicked.is_err(), "the closure must actually panic");

        let bytes = std::fs::read(&path).expect("the hook wrote a response file");
        let response: OcrResponse = serde_json::from_slice(&bytes).expect("the response parses");
        let OcrError::Failed(message) = response.expect_err("a panic is reported as an error") else {
            panic!("a panic must report as Failed, not Unavailable");
        };
        // Message AND location — the location is what identifies the bug.
        assert!(message.contains("deliberate test panic"), "message lost: {message}");
        assert!(message.contains("main.rs"), "panic location lost: {message}");

        let _ = std::fs::remove_file(&path);
    }
}
