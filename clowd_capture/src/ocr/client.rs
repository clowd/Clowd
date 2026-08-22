//! Drives the `clowd_ai ocr` child process: one spawn per recognition.
//!
//! The wire format lives in `clowd_rust_core::ocr`. What lives here is the
//! parent's half of it — finding the binary, feeding it pixels without ever
//! blocking on a full pipe, turning BACK into a kill, and mapping however the
//! child ended into an [`OcrError`].
//!
//! Everything in this module BLOCKS. It is called only from the detached
//! `ocr` worker thread `app.rs` spawns per request, which is also why the
//! 20 MB stdin write is safe to do inline: the winit thread never reaches
//! this code.

use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};

use clowd_rust_core::geometry::RectExt;
use clowd_rust_core::ocr::{OcrError, OcrOutcome, OcrRequest, OcrResponse, RequestHeader, RESULT_FILE_NAME};

/// Binary we spawn, expected beside our own executable — which is where CI
/// puts it (`publish/`, next to `clowd_capture_wgpu`) and where `cargo build`
/// puts it too (`target/<profile>/`), so one resolution rule covers both. It
/// is the one AI inference binary Clowd ships (its `ocr` subcommand; the
/// video editor spawns the same exe for matting and denoising), and it does
/// not exist on Intel macOS, where ONNX Runtime has no binaries — there the
/// spawn fails and OCR reports as unavailable.
#[cfg(windows)]
const AI_BINARY: &str = "clowd_ai.exe";
#[cfg(not(windows))]
const AI_BINARY: &str = "clowd_ai";

/// Overrides the resolved path. The C# side has the same hatch for finding
/// the capturer (`CaptureBinaryLocator.EnvVarName`); this one exists because
/// sibling resolution cannot work from a test binary, which cargo puts in
/// `target/<profile>/deps/` rather than beside the recognizer.
const BINARY_OVERRIDE_VAR: &str = "CLOWD_AI_BINARY";

/// How often the wait loop wakes to poll the cancel flag. Small enough that
/// BACK feels immediate and that the result is picked up the moment the child
/// exits (it costs one `try_wait` syscall per tick, ~200 over a one-second
/// recognition), large enough not to spin.
const POLL_INTERVAL: std::time::Duration = std::time::Duration::from_millis(5);

/// Hard ceiling on one recognition. Not a latency target — the measured
/// worst case is a ~512-line tiny-tier page at roughly 3 s, and a dense
/// 3440x1440 desktop is ~0.9 s — but a backstop against a child that hangs
/// instead of exiting, because the Scanning phase has no other way out and
/// the user would sit under the sweep animation indefinitely. Ten times the
/// worst case, so slow hardware never trips it.
const TIMEOUT: std::time::Duration = std::time::Duration::from_secs(30);

/// Windows: don't flash a console window over the overlay. The capturer is a
/// GUI-subsystem process with no console of its own, so a child that wants
/// one gets a brand new window — briefly, on top of a fullscreen capture.
#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

/// Recognize `req` out-of-process.
///
/// `cancel` is polled throughout — while the pixels upload, and every
/// [`POLL_INTERVAL`] while the child works. Once it reads true the child is
/// killed and an error returned; the caller re-checks the flag and discards
/// whatever comes back, so that error carries no user-facing meaning.
///
/// `session_dir` is where the response file goes when the capture has one.
/// OCR deliberately works without a session (COPY and SEARCH need no shell
/// round-trip), so the fallback is the temp directory.
pub fn recognize(req: &OcrRequest, cancel: &AtomicBool, session_dir: Option<&Path>) -> Result<OcrOutcome, OcrError> {
    let paths = ResponsePaths::new(session_dir);
    // Guarded from the moment it exists: every path below must reap the
    // child, and an early return that merely dropped it would leave a
    // detached process holding the models and an ONNX Runtime session while
    // it finished a page nobody wants.
    let mut child = ChildGuard(spawn(&paths).map_err(|e| {
        // A missing sibling binary is a packaging fault, not a bad capture —
        // report it so it cannot go unnoticed in the field.
        log::error!("failed to spawn {AI_BINARY}: {e}");
        OcrError::Unavailable
    })?);

    upload(&mut child.0, req, cancel)?;
    wait(&mut child.0, cancel, &paths)?;

    std::fs::read(&paths.response)
        .map_err(|e| OcrError::Failed(format!("response file unreadable: {e}")))
        .and_then(|bytes| {
            serde_json::from_slice::<OcrResponse>(&bytes).map_err(|e| OcrError::Failed(format!("response file unparseable: {e}")))
        })?
}

/// Pre-warm: spawn the child once on a 1x1 image and let it run to
/// completion, so the first real OCR press does not pay for a cold
/// executable (tens of MB of embedded models) coming off disk. Blocking, best-effort,
/// and silent about failures — a broken engine is reported at recognize
/// time, where there is a user waiting for an answer to put it in.
///
/// Called once per process from a background thread at capture-cycle start
/// (see `app.rs`), and only when the OCR button exists at all.
pub fn warm() {
    let req = OcrRequest {
        bgra: vec![0xFF; 4],
        width: 1,
        height: 1,
        origin: clowd_rust_core::geometry::ScreenRect::from_xy_size(0, 0, 1, 1),
    };
    let cancel = AtomicBool::new(false);
    match recognize(&req, &cancel, None) {
        Ok(_) => log::info!("OCR engine warmed"),
        Err(e) => log::info!("OCR warm-up did not complete: {e:?}"),
    }
}

/// Where this request's artifacts go. Deletes the response file when dropped,
/// which is what makes cleanup unconditional: a recognition canceled in the
/// window between the child writing its answer and us reading it would
/// otherwise leave an `ocr.json` behind in the session directory, to ship to
/// the editor alongside the screenshot. (`ocr.log` is deliberately kept — it
/// is the diagnostic artifact.)
struct ResponsePaths {
    response: PathBuf,
    /// `None` outside a session directory: an `ocr.log` per recognition in
    /// the temp directory would be litter nobody ever reads.
    log: Option<PathBuf>,
}

impl ResponsePaths {
    fn new(session_dir: Option<&Path>) -> Self {
        match session_dir {
            Some(dir) => Self {
                response: dir.join(RESULT_FILE_NAME),
                log: Some(dir.join("ocr.log")),
            },
            // pid + counter: two capturers (or a warm-up racing a real
            // request) must not collide on one temp path.
            None => {
                static SEQ: AtomicU64 = AtomicU64::new(0);
                let n = SEQ.fetch_add(1, Ordering::Relaxed);
                Self {
                    response: std::env::temp_dir().join(format!("clowd_ai_ocr_{}_{n}.json", std::process::id())),
                    log: None,
                }
            }
        }
    }
}

fn spawn(paths: &ResponsePaths) -> std::io::Result<Child> {
    let binary = match std::env::var_os(BINARY_OVERRIDE_VAR) {
        Some(path) => PathBuf::from(path),
        None => std::env::current_exe()?
            .parent()
            .ok_or_else(|| std::io::Error::other("our own executable has no parent directory"))?
            .join(AI_BINARY),
    };

    let mut command = Command::new(&binary);
    command
        .arg("ocr")
        .arg("--out")
        .arg(&paths.response)
        .stdin(Stdio::piped())
        // Null, never inherited: the `ocr` subcommand writes nothing to
        // stdout (its answer is the `--out` file, which doubles as the
        // session's `ocr.json`), and inheriting would let anything a native
        // runtime printed there be mistaken for output of our own — our
        // stdout is the NDJSON host protocol Clowd.Ui line-parses. A pipe
        // would work too, but one nobody drains can eventually block the
        // child — null cannot.
        .stdout(Stdio::null())
        // Inherited on purpose: the child's log lines (det/rec timings, the
        // tier choice) are chatter by the same convention our own stderr
        // follows, and the shell already pumps it into its diagnostics.
        .stderr(Stdio::inherit());
    if let Some(log) = &paths.log {
        command.arg("--log-file").arg(log);
    }
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(CREATE_NO_WINDOW);
    }
    command.spawn()
}

/// Write the header line and the pixels, then close stdin — the child reads
/// the payload to EOF, so it never finishes without this.
///
/// Chunked so `cancel` is polled during the upload: a 3440x1440 selection is
/// 19.8 MB, and BACK pressed while that is still going should not have to
/// wait for the whole of it.
fn upload(child: &mut Child, req: &OcrRequest, cancel: &AtomicBool) -> Result<(), OcrError> {
    /// 1 MB: ~20 cancel checks across the largest realistic payload, and far
    /// above the 64 KB pipe buffer, so the write syscall count stays low.
    const CHUNK: usize = 1 << 20;

    let mut stdin = child
        .stdin
        .take()
        .ok_or_else(|| OcrError::Failed("child stdin was not piped".into()))?;

    let header = RequestHeader {
        width: req.width,
        height: req.height,
        origin: req.origin,
    };
    let mut line = serde_json::to_vec(&header).map_err(|e| OcrError::Failed(format!("serializing the request header: {e}")))?;
    line.push(b'\n');

    let write = |dst: &mut std::process::ChildStdin, bytes: &[u8]| -> Result<(), OcrError> {
        dst.write_all(bytes)
            .map_err(|e| OcrError::Failed(format!("writing the request: {e}")))
    };
    write(&mut stdin, &line)?;
    for chunk in req.bgra.chunks(CHUNK) {
        if cancel.load(Ordering::Acquire) {
            return Err(canceled());
        }
        write(&mut stdin, chunk)?;
    }
    // Explicit, and the reason this is not just a `drop`: EOF is what starts
    // recognition, so a stdin left open would hang the child until timeout.
    drop(stdin);
    Ok(())
}

/// Wait for the child, polling `cancel` and enforcing [`TIMEOUT`].
fn wait(child: &mut Child, cancel: &AtomicBool, paths: &ResponsePaths) -> Result<(), OcrError> {
    let started = std::time::Instant::now();
    loop {
        if cancel.load(Ordering::Acquire) {
            return Err(canceled());
        }
        match child.try_wait() {
            Ok(Some(status)) => {
                if status.success() {
                    return Ok(());
                }
                // The whole point of the split: a C++ abort, a segfault or a
                // refused allocation inside ONNX Runtime lands here as an exit
                // code instead of taking the overlay down with it. The recognizer
                // has no Sentry client of its own — this error! is the one
                // that reports its death, which is why it works to get the
                // message out of the file rather than leaving it at a code.
                let detail = panic_detail(paths);
                log::error!("{AI_BINARY} exited abnormally: {status}{detail}");
                return Err(OcrError::Failed(format!("recognizer exited with {status}{detail}")));
            }
            Ok(None) => {}
            Err(e) => return Err(OcrError::Failed(format!("waiting for the recognizer: {e}"))),
        }
        if started.elapsed() > TIMEOUT {
            log::error!("{AI_BINARY} did not finish within {TIMEOUT:?}; killing it");
            return Err(OcrError::Failed("recognizer timed out".into()));
        }
        std::thread::sleep(POLL_INTERVAL);
    }
}

/// Pull a panicking child's message out of the response file, as `": <msg>"`
/// ready to append, or empty if there is nothing there.
///
/// A non-zero exit means the file is not a RESULT — but the recognizer's panic
/// hook writes its message there on the way down precisely so this can recover
/// it (see `clowd_ai`'s `ocr::install_panic_reporter`). Without it a panic reaches
/// Sentry as "exited with code 101" and nothing else; with it, the panic's file
/// and line come too.
fn panic_detail(paths: &ResponsePaths) -> String {
    let Ok(bytes) = std::fs::read(&paths.response) else {
        return String::new();
    };
    match serde_json::from_slice::<OcrResponse>(&bytes) {
        Ok(Err(OcrError::Failed(msg))) => format!(": {msg}"),
        _ => String::new(),
    }
}

/// The error a canceled recognition reports. Only the OCR worker thread ever
/// sees it — the worker re-checks its cancel flag after `recognize` returns
/// and drops the result without setting the latch, so no user-facing path
/// renders this string.
fn canceled() -> OcrError {
    OcrError::Failed("canceled".into())
}

impl Drop for ResponsePaths {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.response);
    }
}

/// Kills and reaps the child on every exit path, including the cancel and
/// timeout returns above.
///
/// `kill` on an already-exited process is a no-op that still needs its
/// `wait`, so both run unconditionally: skipping the `wait` would leave a
/// zombie on Unix, and skipping the `kill` would let a canceled recognition
/// run a full page to completion in the background, holding the engine while
/// the next request spawns its own copy of it.
struct ChildGuard(Child);

impl Drop for ChildGuard {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use clowd_rust_core::geometry::ScreenRect;

    /// A capture with a session directory keeps both artifacts there; one
    /// without gets a collision-proof temp path and no log.
    #[test]
    fn response_paths_follow_the_session_dir_or_fall_back_to_temp() {
        let dir = PathBuf::from("session-dir");
        let with_session = ResponsePaths::new(Some(&dir));
        assert_eq!(with_session.response, dir.join(RESULT_FILE_NAME));
        assert_eq!(with_session.log.as_deref(), Some(dir.join("ocr.log").as_path()));

        let a = ResponsePaths::new(None);
        let b = ResponsePaths::new(None);
        assert!(a.response.starts_with(std::env::temp_dir()));
        assert!(a.log.is_none());
        // The counter: a warm-up racing a real request must not collide.
        assert_ne!(a.response, b.response);
    }

    /// Dropping the paths removes the response file wherever it got to — the
    /// cancel-after-the-child-answered window depends on this.
    #[test]
    fn dropping_response_paths_deletes_the_response() {
        let paths = ResponsePaths::new(None);
        let path = paths.response.clone();
        std::fs::write(&path, b"{}").expect("write the stand-in response");
        drop(paths);
        assert!(!path.exists(), "{} survived the drop", path.display());
    }

    /// Opt-in round trip through the REAL recognizer: point
    /// `CLOWD_AI_BINARY` at a built `clowd_ai` and run. Exercises the half
    /// of this module that unit tests cannot reach — header framing, the
    /// pixel upload, the wait, and parsing the response file — against the
    /// actual child rather than a stand-in.
    #[test]
    fn env_round_trip_through_the_real_recognizer() {
        if std::env::var_os(BINARY_OVERRIDE_VAR).is_none() {
            eprintln!("SKIP {}: {BINARY_OVERRIDE_VAR} not set", module_path!());
            return;
        }
        let blank = |w: u32, h: u32| OcrRequest {
            bgra: vec![0xFF; (w * h * 4) as usize],
            width: w,
            height: h,
            origin: ScreenRect::from_xy_size(0, 0, w as i32, h as i32),
        };

        // A blank page recognizes to nothing, which is still a full round
        // trip: anything wrong with the framing shows up as an error here.
        let outcome = recognize(&blank(160, 120), &AtomicBool::new(false), None).expect("a blank image must round-trip");
        assert!(outcome.lines.is_empty(), "blank image produced {:?}", outcome.lines);
        assert_eq!(outcome.full_text, "");

        // An already-canceled request must come back as an error rather than
        // a result, and must not leave the child running.
        let err = recognize(&blank(160, 120), &AtomicBool::new(true), None).expect_err("a canceled request must not produce an outcome");
        assert!(matches!(err, OcrError::Failed(_)), "unexpected error {err:?}");
    }
}
