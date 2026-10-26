//! On-device text recognition — the capturer's side of it.
//!
//! The recognition itself does not happen here. It happens in the `clowd_ai`
//! binary that ships beside this one (its `ocr` subcommand), which the
//! [`client`] spawns per request; this module is the seam. (Windows.Media.Ocr
//! was the original Windows backend and proved too inaccurate — it garbled
//! small and dark-mode text that PaddleOCR reads character-perfectly — so
//! what runs there now is PP-OCRv6 on ONNX Runtime, on every platform that
//! has an ONNX Runtime build (not Intel macOS), with the models embedded in
//! that binary. No OS engine, language pack or download is involved.)
//!
//! Out-of-process because the engine is a large native library: a Rust panic
//! in it would unwind harmlessly, but an `abort`, a segfault or a refused
//! allocation on a degenerate selection kills its process — which in-process
//! meant the overlay, mid-capture, with the user's selection already framed.
//! The request/response contract is `clowd_rust_core::ocr`.

mod client;

pub mod anim;
pub mod search;

// The result types are the process boundary's contract, so they live in the
// crate both sides share. Re-exported under their original path because
// everything downstream — `interaction`, `app`, the bubble renderer — refers
// to them as `crate::ocr::*` and none of it cares where recognition runs.
pub use clowd_rust_core::ocr::{OcrError, OcrLine, OcrOutcome, OcrRequest};

use std::path::Path;
use std::sync::atomic::AtomicBool;

/// Recognize text. BLOCKING — call only from a dedicated worker thread.
///
/// `cancel` is polled throughout: while the pixels upload to the child, and
/// every few milliseconds while it works. Once it reads true the child is
/// killed and an error returned promptly instead of a finished page. The
/// caller is expected to re-check the flag and discard whatever comes back —
/// the error itself carries no user-facing meaning.
///
/// `session_dir` is where the child leaves its response and its `ocr.log`;
/// `None` (OCR runs without a session — COPY and SEARCH need no shell
/// round-trip) puts the response in the temp directory and skips the log.
pub fn recognize(req: &OcrRequest, cancel: &AtomicBool, session_dir: Option<&Path>) -> Result<OcrOutcome, OcrError> {
    client::recognize(req, cancel, session_dir)
}
