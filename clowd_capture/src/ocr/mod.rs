//! On-device text recognition, PaddleOCR (PP-OCRv6 small via MNN) on every
//! platform — the models are embedded in the binary, so there is no OS
//! engine, language pack, or download involved. (Windows.Media.Ocr was the
//! original Windows backend and proved too inaccurate; it garbled small or
//! dark-mode text that PaddleOCR reads character-perfectly.)

mod paddle;

pub mod anim;
pub mod search;

use clowd_rust_core::geometry::{ScreenRect, ScreenRectF};

// Everything below is Clone because the result travels through
// crate::sync::Latch<T: Clone> from the OCR worker thread to the app thread.

#[derive(Debug, Clone)]
pub struct OcrLine {
    pub text: String,
    /// The line's approximate glyph-ink rect, in virtual-desktop screen
    /// coordinates — the bubble renderer sizes and places its pill from
    /// this, so it must track the SOURCE text's visual extent, not the
    /// detector's padded box (see `paddle::UNCLIP_TIGHTEN`). Already offset
    /// by the crop origin the extractor actually used.
    pub rect: ScreenRectF,
}

#[derive(Debug, Clone)]
pub struct OcrOutcome {
    pub lines: Vec<OcrLine>,
    /// Newline-joined line texts — what COPY/SEARCH/UPLOAD act on.
    pub full_text: String,
    /// Detected skew in degrees, 0.0 when none reported. Logged when a result
    /// is accepted and otherwise unused: the lift pass draws axis-aligned
    /// quads, so the angle informs diagnosis of a bad-looking lift but never
    /// the geometry. A de-skewed lift would be its first real consumer.
    /// (PaddleOCR reports no skew estimate, so this is currently always 0.0.)
    pub text_angle: f32,
}

#[derive(Debug, Clone)]
pub enum OcrError {
    /// The MNN engine failed to initialize (cause logged at error level by
    /// `paddle`); cached, so every later recognize reports it too.
    Unavailable,
    Failed(String),
}

/// One BGRA8 image plus where it lives on the virtual desktop.
/// `bgra` is tightly packed at `width * 4` bytes per row.
pub struct OcrRequest {
    pub bgra: Vec<u8>,
    pub width: u32,
    pub height: u32,
    /// Screen rect the crop ACTUALLY covers (extract_selection_bgra clamps).
    pub origin: ScreenRect,
}

/// Recognize text. BLOCKING — call only from a dedicated worker thread.
///
/// `cancel` is polled at every expensive internal boundary (lock
/// acquisition, post-detection, between recognition batches); once it
/// reads true the call returns an error promptly instead of finishing the
/// page. The caller is expected to re-check the flag and discard whatever
/// comes back — the error itself carries no user-facing meaning.
pub fn recognize(req: &OcrRequest, cancel: &std::sync::atomic::AtomicBool) -> Result<OcrOutcome, OcrError> {
    paddle::recognize(req, cancel)
}

/// Pre-initialize the recognition backend (embedded-model parse + MNN
/// session setup) from a background thread, so the first OCR press of the
/// process doesn't pay that one-time cost mid-scan. Blocking; idempotent;
/// failures are cached and surface later as `OcrError::Unavailable`.
pub fn warm() {
    paddle::warm();
}
