//! PaddleOCR (PP-OCRv6 small) backend via the `ocr-rs` crate / MNN runtime.
//! Fully self-contained and platform-independent: models are embedded, MNN
//! runs on the CPU, and recognition covers Chinese/Japanese/Latin scripts
//! without any OS engine or language pack.

use std::sync::{Mutex, OnceLock};

use image::DynamicImage;
use ocr_rs::{DetOptions, OcrEngine, OcrEngineConfig};

use super::{OcrError, OcrLine, OcrOutcome, OcrRequest};
use clowd_rust_core::geometry::{RectExt, ScreenRectF};

/// The lift render pass hard-caps its instance buffer at 512 lines; past
/// 256 a selection is degenerate (a whole spreadsheet) where extra lines
/// only add latency.
const MAX_LINES: usize = 256;

/// Detection-stage resolution ceiling (the image is downscaled to this long
/// side before the DB detector runs; recognition always crops from the
/// full-resolution input). The crate default of 960 loses small screen text
/// on large selections — 1920 keeps ~12px UI text detectable on a 4K-wide
/// crop. This is THE accuracy-vs-latency knob to revisit if either suffers.
const DET_MAX_SIDE_LEN: u32 = 1920;

/// Detection inflates every box by this many pixels per side before
/// recognition (`DetOptions::box_border`, crate default 5) — the context
/// helps the recognizer, but the returned rects carry the same inflation,
/// so the geometry mapping below deflates them back for tight lift quads.
const BOX_BORDER: u32 = 5;

// PP-OCRv6 small, converted to MNN, vendored from
// github.com/zibo-chen/rust-paddle-ocr (models/). ~16 MB embedded — the
// deliberate cost of a zero-install, no-language-pack OCR. The _medium_ set
// (~70 MB, same repo, same charset coverage) measured noticeably beyond the
// size budget for a marginal accuracy gain on screen text; it remains the
// fallback if small's accuracy disappoints in real use.
static DET_MODEL: &[u8] = include_bytes!("../../assets/ocr/PP-OCRv6_small_det.mnn");
static REC_MODEL: &[u8] = include_bytes!("../../assets/ocr/PP-OCRv6_small_rec.mnn");
static CHARSET: &[u8] = include_bytes!("../../assets/ocr/ppocr_keys_v6_small.txt");

/// Process-global engine, created on first use: MNN session setup parses both
/// models (~70 MB) and is far too slow to repeat per capture. `Option` caches
/// a failed init too — an MNN that cannot start once will not start later, so
/// every recognize then reports `Unavailable` without retrying.
///
/// `Mutex` (not bare `OcrEngine`): `recognize` takes `&self` but MNN inference
/// sessions are not documented thread-safe, and the OCR worker threads are
/// spawned per capture cycle — serialize them defensively. Recognitions never
/// overlap in practice (one selection at a time), so the lock is uncontended.
fn engine() -> Option<&'static Mutex<OcrEngine>> {
    static ENGINE: OnceLock<Option<Mutex<OcrEngine>>> = OnceLock::new();
    ENGINE
        .get_or_init(|| match create_engine() {
            Ok(engine) => Some(Mutex::new(engine)),
            Err(e) => {
                log::error!("PaddleOCR engine init failed: {e}");
                None
            }
        })
        .as_ref()
}

fn create_engine() -> Result<OcrEngine, ocr_rs::OcrError> {
    let config = OcrEngineConfig::new()
        .with_det_options(
            DetOptions::new()
                .with_max_side_len(DET_MAX_SIDE_LEN)
                .with_box_border(BOX_BORDER),
        )
        // 0.5 (crate default) drops too much real UI text; screen captures
        // are clean enough that low-confidence reads are usually right.
        .with_min_result_confidence(0.35);
    OcrEngine::from_bytes(DET_MODEL, REC_MODEL, CHARSET, Some(config))
}

pub fn recognize(req: &OcrRequest) -> Result<OcrOutcome, OcrError> {
    let Some(engine) = engine() else {
        return Err(OcrError::Unavailable);
    };

    // BGRA -> RGB up front (alpha dropped: BitBlt'd desktop pixels routinely
    // carry a == 0, and the crate's preprocess calls to_rgb8() on whatever it
    // receives — handing it RGB directly makes that a no-op instead of a
    // second full-image conversion).
    let mut rgb = Vec::with_capacity(req.width as usize * req.height as usize * 3);
    for px in req.bgra.chunks_exact(4) {
        rgb.extend_from_slice(&[px[2], px[1], px[0]]);
    }
    let img = image::RgbImage::from_raw(req.width, req.height, rgb)
        .expect("OcrRequest.bgra must be width * height * 4 bytes");
    let img = DynamicImage::ImageRgb8(img);

    let mut results = engine
        .lock()
        .expect("OCR engine lock poisoned")
        .recognize(&img)
        .map_err(|e| OcrError::Failed(e.to_string()))?;

    // The single-pass pipeline returns boxes in raw detection order, NOT
    // reading order (only the Robust rotated-text mode sorts) — order them
    // ourselves so full_text reads top-to-bottom, left-to-right. Boxes on
    // one visual row can differ by a few pixels of top; bucket by
    // half-line-height so columns on the same row sort left-to-right.
    results.sort_by(|a, b| {
        let (ar, br) = (&a.bbox.rect, &b.bbox.rect);
        let tolerance = (ar.height().min(br.height()) / 2) as i32;
        if (ar.top() - br.top()).abs() <= tolerance {
            ar.left().cmp(&br.left())
        } else {
            ar.top().cmp(&br.top())
        }
    });

    let total = results.len();
    let ox = req.origin.left() as f32;
    let oy = req.origin.top() as f32;
    let mut lines: Vec<OcrLine> = Vec::with_capacity(total.min(MAX_LINES));
    for result in results {
        if lines.len() >= MAX_LINES {
            log::warn!("OCR result truncated to {MAX_LINES} lines (engine reported {total})");
            break;
        }
        // Deflate the box_border inflation back out, but never below 1px.
        // (At image edges expand() clamped instead of inflating, so this
        // over-tightens by up to BOX_BORDER px there — invisible in practice
        // and not worth tracking the clamp per side.)
        let b = BOX_BORDER as f32;
        let r = &result.bbox.rect;
        let (mut left, mut top) = (r.left() as f32 + b, r.top() as f32 + b);
        let (mut right, mut bottom) = (
            r.left() as f32 + r.width() as f32 - b,
            r.top() as f32 + r.height() as f32 - b,
        );
        if right - left < 1.0 {
            left = r.left() as f32;
            right = left + r.width() as f32;
        }
        if bottom - top < 1.0 {
            top = r.top() as f32;
            bottom = top + r.height() as f32;
        }
        lines.push(OcrLine {
            text: result.text,
            // Boxes come back in input-image coordinates (detection's
            // internal downscale is already mapped back by the crate), so
            // only the crop-origin offset applies here.
            rect: ScreenRectF::from_exact(ox + left, oy + top, ox + right, oy + bottom),
        });
    }

    let full_text = lines
        .iter()
        .map(|l| l.text.as_str())
        .collect::<Vec<_>>()
        .join("\n");

    Ok(OcrOutcome {
        lines,
        full_text,
        // PaddleOCR reports no skew estimate; the field is diagnostic-only.
        text_angle: 0.0,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use clowd_rust_core::geometry::ScreenRect;

    fn request(bgra: Vec<u8>, w: u32, h: u32) -> OcrRequest {
        OcrRequest {
            bgra,
            width: w,
            height: h,
            origin: ScreenRect::from_xy_size(0, 0, w as i32, h as i32),
        }
    }

    /// A blank image must come back as a clean empty success.
    #[test]
    fn blank_image_recognizes_to_empty() {
        let req = request(vec![255u8; 200 * 100 * 4], 200, 100);
        let outcome = recognize(&req).expect("blank image must not error");
        assert!(outcome.lines.is_empty());
        assert_eq!(outcome.full_text, "");
    }

    /// Whatever the engine hallucinates out of noise, every returned rect
    /// must land inside the request's origin rect — pins the crop-origin
    /// offset (zero lines passes trivially; the property is geometry).
    #[test]
    fn noise_line_rects_stay_within_origin() {
        let (w, h) = (320u32, 240u32);
        // Deterministic LCG so failures reproduce; no rand dependency.
        let mut state = 0x12345678u32;
        let bgra: Vec<u8> = (0..w * h * 4)
            .map(|_| {
                state = state.wrapping_mul(1664525).wrapping_add(1013904223);
                (state >> 24) as u8
            })
            .collect();
        let mut req = request(bgra, w, h);
        // Negative origin: the offset math must hold on multi-monitor
        // layouts where the virtual desktop starts left of zero.
        req.origin = ScreenRect::from_xy_size(-500, -300, w as i32, h as i32);
        let outcome = recognize(&req).expect("noise must not error");
        let bounds = req.origin.to_f32();
        for line in &outcome.lines {
            assert!(
                line.rect.left() >= bounds.left() - 1.0
                    && line.rect.top() >= bounds.top() - 1.0
                    && line.rect.right() <= bounds.right() + 1.0
                    && line.rect.bottom() <= bounds.bottom() + 1.0,
                "line {:?} outside origin {:?}",
                line.rect,
                bounds
            );
        }
    }

    /// Two back-to-back calls through the cached engine: pins the OnceLock
    /// reuse and that an inference leaves the session reusable.
    #[test]
    fn consecutive_recognize_calls_succeed() {
        let req = request(vec![255u8; 64 * 64 * 4], 64, 64);
        recognize(&req).expect("first call");
        recognize(&req).expect("second call");
    }

    /// Opt-in end-to-end check against a real screenshot: set
    /// CLOWD_OCR_TEST_IMAGE to a file path and CLOWD_OCR_TEST_EXPECT to a
    /// substring the recognized text must contain.
    #[test]
    fn env_image_contains_expected_text() {
        let (Ok(path), Ok(expect)) = (std::env::var("CLOWD_OCR_TEST_IMAGE"), std::env::var("CLOWD_OCR_TEST_EXPECT")) else {
            eprintln!("SKIP {}: CLOWD_OCR_TEST_IMAGE/CLOWD_OCR_TEST_EXPECT not set", module_path!());
            return;
        };
        let img = image::open(&path)
            .expect("CLOWD_OCR_TEST_IMAGE must decode")
            .to_rgba8();
        let (w, h) = img.dimensions();
        // The request wants BGRA; image gives RGBA — swap R and B.
        let mut bgra = img.into_raw();
        for px in bgra.chunks_exact_mut(4) {
            px.swap(0, 2);
        }
        let outcome = recognize(&request(bgra, w, h)).expect("test image must recognize");
        assert!(
            outcome.full_text.contains(&expect),
            "expected {:?} within recognized text:\n{}",
            expect,
            outcome.full_text
        );
    }
}
